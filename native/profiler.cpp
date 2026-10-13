// Copyright (c) Wiesław Šoltés. MIT licensed.
#include <cassert>
#include "signature.h"
// Uses the pinned .NET Foundation profiling SDK and its MIT-licensed no-op callback base/IL list rewriter.
#include "capture.h"
#include <profiler.h>
#include <ilrewriter.h>
#include <algorithm>
#include <array>
#include <cstring>
#include <memory>
#include <string>
#include <thread>
#include <unordered_set>

namespace {
const GUID ProfilerId={0x6eadfa34,0x8f22,0x4e0b,{0x96,0x85,0x72,0x9b,0x11,0x24,0xd3,0x60}};
constexpr uint32_t FunctionCapacity=65536;
static_assert(std::atomic<uint64_t>::is_always_lock_free);
template<class T> struct Com {
    T* value=nullptr;
    ~Com(){if(value)value->Release();}
    T* operator->()const{return value;}
    T** Address(){return &value;}
};
std::string Utf8(const WCHAR* p) {
    std::string result;
    for(size_t i=0;p[i];++i) {
        uint32_t c=uint16_t(p[i]);
        if(c>=0xd800 && c<=0xdbff && p[i+1]>=0xdc00 && p[i+1]<=0xdfff) {c=0x10000+((c-0xd800)<<10)+(uint16_t(p[++i])-0xdc00);}
        else if(c>=0xd800 && c<=0xdfff) c=0xfffd;
        if(c<0x80) result.push_back(char(c));
        else if(c<0x800) {result.push_back(char(0xc0|(c>>6)));result.push_back(char(0x80|(c&63)));}
        else if(c<0x10000) {result.push_back(char(0xe0|(c>>12)));result.push_back(char(0x80|((c>>6)&63)));result.push_back(char(0x80|(c&63)));}
        else {result.push_back(char(0xf0|(c>>18)));result.push_back(char(0x80|((c>>12)&63)));result.push_back(char(0x80|((c>>6)&63)));result.push_back(char(0x80|(c&63)));}
    }
    return result;
}
struct Site {
    ModuleID module=0; mdMethodDef token=0; GUID mvid{};
    std::atomic<bool> active{true};
    std::atomic<int32_t> status{0}, error{0};
    std::atomic<uint32_t> compilations{0};
    int32_t writtenStatus=INT32_MIN, writtenError=0; uint32_t writtenCompilations=0;
};
struct FunctionSlot { std::atomic<FunctionID> function{0}; std::atomic<uint32_t> site{0}; };
// No managed callbacks, allocation, exceptions or file/network I/O in these probes.
extern "C" void DVNativeEnter(uint64_t site) noexcept { dv::Enter(uint32_t(site)); }
extern "C" void DVNativeLeave(uint64_t site,uint32_t outcome) noexcept { dv::Leave(uint32_t(site),outcome); }

class NativeProfiler final : public Profiler {
    Com<ICorProfilerInfo10> info_;
    dv::Recorder* recorder_=nullptr;
    std::atomic<bool> stopping_{false}, workerDone_{false};
    std::atomic<uint32_t> count_{0};
    std::array<Site,dv::SiteLimit> sites_{};
    std::array<FunctionSlot,FunctionCapacity> functions_{};
    std::unordered_set<ModuleID> scanned_;
    std::atomic<uint64_t> moduleEpoch_{0};
    uint64_t scannedEpoch_ = 0; // Worker-only; module callbacks only publish atomic invalidations.
    uint32_t Find(ModuleID module,mdMethodDef token) noexcept {
        const auto count=count_.load(std::memory_order_acquire);
        for(uint32_t i=0;i<count;++i) if(sites_[i].module==module && sites_[i].token==token && sites_[i].active.load()) return i+1;
        return 0;
    }
    uint32_t Function(FunctionID function) noexcept {
        for(uint32_t step=0;step<FunctionCapacity;++step) {
            auto& entry=functions_[(uint64_t(function)*11400714819323198485ull+step)&(FunctionCapacity-1)];
            const auto f=entry.function.load(std::memory_order_acquire); if(!f)return 0;
            if(f==function) return entry.site.load(std::memory_order_acquire);
        }
        return 0;
    }
    uint32_t RegisterFunction(FunctionID function) noexcept {
        ClassID type=0;ModuleID module=0;mdToken token=0;
        if(stopping_.load() || FAILED(info_->GetFunctionInfo(function,&type,&module,&token)))return 0;
        const auto site=Find(module,token);if(!site)return 0;
        for(uint32_t step=0;step<FunctionCapacity;++step) {
            auto& entry=functions_[(uint64_t(function)*11400714819323198485ull+step)&(FunctionCapacity-1)];
            FunctionID expected=0;
            if(entry.function.compare_exchange_strong(expected,function,std::memory_order_acq_rel) || expected==function) {entry.site.store(site,std::memory_order_release);return site;}
        }
        recorder_->faults.fetch_add(1);return 0;
    }
    void Statuses() {
        for(uint32_t i=0;i<count_.load(std::memory_order_acquire);++i) {
            auto& site=sites_[i];const auto status=site.status.load(),error=site.error.load();const auto compiled=site.compilations.load();
            if(status==site.writtenStatus && error==site.writtenError && compiled==site.writtenCompilations)continue;
            std::vector<uint8_t> payload;dv::Append32(payload,i+1);dv::Append32(payload,uint32_t(status));dv::Append32(payload,uint32_t(error));dv::Append32(payload,compiled);
            recorder_->Frame(4,payload);site.writtenStatus=status;site.writtenError=error;site.writtenCompilations=compiled;
        }
    }
    std::string TypeName(IMetaDataImport* metadata,mdTypeDef token,unsigned depth=0) {
        if(depth>32)return "<type nesting limit>";
        WCHAR name[2048]{};ULONG length=0;DWORD flags=0;mdToken parent=0;
        if(FAILED(metadata->GetTypeDefProps(token,name,2048,&length,&flags,&parent)) || length>2048)return "<unavailable type>";
        mdTypeDef outer=0;
        if(SUCCEEDED(metadata->GetNestedClassProps(token,&outer)))return TypeName(metadata,outer,depth+1)+"+"+Utf8(name);
        return Utf8(name);
    }
    void ScanModule(ModuleID module) {
        Com<IMetaDataImport> metadata;
        if(FAILED(info_->GetModuleMetaData(module,ofRead,IID_IMetaDataImport,reinterpret_cast<IUnknown**>(metadata.Address()))))return;
        WCHAR scope[2048]{};ULONG scopeLength=0;GUID mvid{};
        if(FAILED(metadata->GetScopeProps(scope,2048,&scopeLength,&mvid)) || scopeLength>2048)return;
        std::string assembly=Utf8(scope);if(assembly.ends_with(".dll") || assembly.ends_with(".exe"))assembly.resize(assembly.size()-4);
        // Scope name is not always the assembly name (renamed outputs/netmodules).
        AssemblyID assemblyId=0;ULONG nameLength=0;WCHAR assemblyName[2048]{};
        if(SUCCEEDED(info_->GetModuleInfo(module,nullptr,0,nullptr,nullptr,&assemblyId)) &&
            SUCCEEDED(info_->GetAssemblyInfo(assemblyId,2048,&nameLength,assemblyName,nullptr,nullptr)) && nameLength<=2048)assembly=Utf8(assemblyName);
        if(std::none_of(recorder_->config.rules.begin(),recorder_->config.rules.end(),[&](const auto& rule){return rule.starts_with(assembly+"!");}))return;
        HCORENUM enumeration=nullptr;mdTypeDef types[64];ULONG typeCount=0;
        std::vector<ModuleID> modules;std::vector<mdMethodDef> methods;
        struct EnumCloser {IMetaDataImport* m;HCORENUM* e;~EnumCloser(){if(*e)m->CloseEnum(*e);}} typesCloser{metadata.value,&enumeration};
        while(SUCCEEDED(metadata->EnumTypeDefs(&enumeration,types,64,&typeCount)) && typeCount && !stopping_.load()) {
            for(ULONG t=0;t<typeCount;++t) {
                const auto type=TypeName(metadata.value,types[t]);HCORENUM methodEnum=nullptr;mdMethodDef tokens[64];ULONG methodCount=0;
                EnumCloser methodCloser{metadata.value,&methodEnum};
                while(SUCCEEDED(metadata->EnumMethods(&methodEnum,types[t],tokens,64,&methodCount)) && methodCount) {
                    for(ULONG i=0;i<methodCount;++i) {
                        WCHAR name[2048]{};ULONG length=0;DWORD attributes=0,impl=0;mdTypeDef owner=0;PCCOR_SIGNATURE signature=nullptr;ULONG signatureLength=0,rva=0;
                        if(FAILED(metadata->GetMethodProps(tokens[i],&owner,name,2048,&length,&attributes,&signature,&signatureLength,&rva,&impl)) || length>2048)continue;
                        const auto selector=assembly+"!"+type+"::"+Utf8(name);
                        if(std::find(recorder_->config.rules.begin(),recorder_->config.rules.end(),selector)==recorder_->config.rules.end())continue;
                        if(Find(module,tokens[i]))continue;
                        const auto index=count_.load();if(index>=dv::SiteLimit){recorder_->faults.fetch_add(1);return;}
                        auto& site=sites_[index];site.module=module;site.token=tokens[i];site.mvid=mvid;
                        std::vector<uint8_t> payload;dv::Append32(payload,index+1);payload.insert(payload.end(),reinterpret_cast<uint8_t*>(&mvid),reinterpret_cast<uint8_t*>(&mvid)+16);
                        dv::Append32(payload,tokens[i]);dv::AppendString(payload,assembly);dv::AppendString(payload,type+"::"+Utf8(name));
                        recorder_->Frame(1,payload);count_.store(index+1,std::memory_order_release);
                        if(IsMdAbstract(attributes) || IsMdPinvokeImpl(attributes) || !IsMiIL(impl) || !rva || signatureLength==0) {site.status.store(-1);continue;}
                        modules.push_back(module);methods.push_back(tokens[i]);site.status.store(1);
                    }
                }
            }
        }
        if(!methods.empty() && !stopping_.load()) {
            recorder_->methodsRequested.fetch_add(methods.size());
            const auto result=info_->RequestReJITWithInliners(COR_PRF_REJIT_BLOCK_INLINING,ULONG(methods.size()),modules.data(),methods.data());
            if(FAILED(result)) {
                recorder_->rejitErrors.fetch_add(methods.size());
                for(auto token:methods)if(auto id=Find(module,token)){sites_[id-1].error.store(result);sites_[id-1].status.store(-2);}
            }
        }
    }
    void Scan() {
        const auto epoch=moduleEpoch_.load(); if (epoch!=scannedEpoch_) { scanned_.clear(); scannedEpoch_=epoch; }
        Com<ICorProfilerModuleEnum> modules;if(FAILED(info_->EnumModules(modules.Address())))return;
        ModuleID values[64];ULONG count=0;
        while(SUCCEEDED(modules->Next(64,values,&count)) && count && !stopping_.load())for(ULONG i=0;i<count;++i) {
            if(scanned_.contains(values[i]))continue;
            if(scanned_.size()>=4096){recorder_->faults.fetch_add(1);recorder_->accepting.store(false);return;}
            scanned_.insert(values[i]);ScanModule(values[i]);
        }
    }
    void Worker() noexcept {
        try {
            const auto initialized=info_->InitializeCurrentThread();
            if(FAILED(initialized)) {recorder_->faults.fetch_add(1);stopping_.store(true);}
            const auto deadline=dv::Clock()+uint64_t(recorder_->config.durationMs)*1000000;
            uint64_t nextScan=0;
            while(!stopping_.load() && recorder_->accepting.load() && dv::Clock()<deadline) {
                const auto now=dv::Clock();if(now>=nextScan){Scan();nextScan=now+250000000;}
                Statuses();recorder_->Drain();std::this_thread::sleep_for(std::chrono::milliseconds(10));
            }
            recorder_->accepting.store(false);
            Statuses();recorder_->Finish(stopping_.load()?1:0);
        } catch(...) {recorder_->faults.fetch_add(1);recorder_->Finish(2);}
        workerDone_.store(true,std::memory_order_release);Release();
    }
public:
    HRESULT STDMETHODCALLTYPE Initialize(IUnknown*) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE InitializeForAttach(IUnknown* runtime,void* client,UINT length) override {
        try {
            if(dv::ActiveRecorder())return E_FAIL;
            auto config=dv::Configuration::Parse(static_cast<const uint8_t*>(client),length);
            HRESULT hr=runtime->QueryInterface(IID_ICorProfilerInfo10,reinterpret_cast<void**>(info_.Address()));if(FAILED(hr))return hr;
            hr=info_->SetEventMask(COR_PRF_ENABLE_REJIT|COR_PRF_MONITOR_MODULE_LOADS|COR_PRF_MONITOR_JIT_COMPILATION);
            if(FAILED(hr))return hr;
            auto record=std::make_unique<dv::Recorder>(std::move(config));if(!record->Open())return E_FAIL;
            recorder_=record.release();dv::PublishRecorder(recorder_);return S_OK;
        } catch(...) {return E_FAIL;}
    }
    HRESULT STDMETHODCALLTYPE ProfilerAttachComplete() override {
        try {recorder_->accepting.store(true);AddRef();std::thread([this]{Worker();}).detach();return S_OK;}
        catch(...) {recorder_->accepting.store(false);recorder_->faults.fetch_add(1);recorder_->Finish(2);Release();return E_FAIL;}
    }
    HRESULT STDMETHODCALLTYPE Shutdown() override {
        stopping_.store(true);if(recorder_)recorder_->accepting.store(false);
        // Never join a CLR-API worker while executing a runtime shutdown callback.
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE ModuleUnloadStarted(ModuleID module) override {
        for(uint32_t i=0;i<count_.load(std::memory_order_acquire);++i)if(sites_[i].module==module)sites_[i].active.store(false);moduleEpoch_.fetch_add(1);return S_OK;
    }
    HRESULT STDMETHODCALLTYPE JITCompilationStarted(FunctionID function,BOOL) override {RegisterFunction(function);return S_OK;}
    HRESULT STDMETHODCALLTYPE ReJITCompilationStarted(FunctionID function,ReJITID,BOOL) override {RegisterFunction(function);return S_OK;}
    HRESULT STDMETHODCALLTYPE JITInlining(FunctionID,FunctionID callee,BOOL* inlineMethod) override {
        if(inlineMethod)*inlineMethod=Function(callee)==0;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE ReJITCompilationFinished(FunctionID function,ReJITID,HRESULT result,BOOL) override {
        if(const auto id=Function(function)) {if(SUCCEEDED(result)){sites_[id-1].compilations.fetch_add(1);recorder_->compilations.fetch_add(1);}else{sites_[id-1].error.store(result);recorder_->rejitErrors.fetch_add(1);}}return S_OK;
    }
    HRESULT STDMETHODCALLTYPE ReJITError(ModuleID module,mdMethodDef method,FunctionID,HRESULT result) override {
        if(const auto id=Find(module,method)){sites_[id-1].error.store(result);sites_[id-1].status.store(-3);}if(recorder_)recorder_->rejitErrors.fetch_add(1);return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetReJITParameters(ModuleID module,mdMethodDef method,ICorProfilerFunctionControl* control) override {
        const auto id=Find(module,method);if(!id || stopping_.load() || !recorder_->accepting.load())return S_OK;
        auto& site=sites_[id-1];
        try {
            auto fail=[&](HRESULT hr,int state=-4) {site.error.store(hr);site.status.store(state);return hr;};
            LPCBYTE originalBody=nullptr;ULONG bodyBytes=0;
            HRESULT hr=info_->GetILFunctionBody(module,method,&originalBody,&bodyBytes);
            if(FAILED(hr))return fail(hr);
            if(!originalBody||bodyBytes>1024*1024){site.status.store(-5);return S_OK;}
            ILRewriter rewriter(info_.value,control,module,method);hr=rewriter.Initialize();if(FAILED(hr))return fail(hr);
            hr=rewriter.Import();if(FAILED(hr))return fail(hr);
            if(rewriter.ExceptionCount()>1024){site.status.store(-5);return S_OK;}
            std::vector<ILInstr*> returns;std::vector<std::pair<ULONG32,ILInstr*>> originals;
            for(auto* p=rewriter.GetILList()->m_pNext;p!=rewriter.GetILList();p=p->m_pNext) {
                if(p->m_opcode==CEE_TAILCALL || p->m_opcode==CEE_JMP){site.status.store(-5);return S_OK;}
                if(p->m_opcode==CEE_RET)returns.push_back(p);
                if(p->m_opcode!=CEE_SWITCH_ARG)originals.emplace_back(p->m_offset,p);
            }
            Com<IMetaDataImport> metadata;hr=info_->GetModuleMetaData(module,ofRead|ofWrite,IID_IMetaDataImport,reinterpret_cast<IUnknown**>(metadata.Address()));if(FAILED(hr))return fail(hr);
            Com<IMetaDataEmit> emit;hr=metadata->QueryInterface(IID_IMetaDataEmit,reinterpret_cast<void**>(emit.Address()));if(FAILED(hr))return fail(hr);
            PCCOR_SIGNATURE methodSignature=nullptr;ULONG methodLength=0;
            hr=metadata->GetMethodProps(method,nullptr,nullptr,0,nullptr,nullptr,&methodSignature,&methodLength,nullptr,nullptr);if(FAILED(hr))return fail(hr);
            bool isVoid=false;auto returnType=dv::SignatureReader({methodSignature,methodLength}).ReturnType(isVoid);
            std::vector<uint8_t> locals={IMAGE_CEE_CS_CALLCONV_LOCAL_SIG};uint32_t localCount=0;PCCOR_SIGNATURE oldLocals=nullptr;ULONG oldLength=0;size_t localStart=0;
            if(rewriter.LocalSignature()){
                hr=metadata->GetSigFromToken(rewriter.LocalSignature(),&oldLocals,&oldLength);if(FAILED(hr))return fail(hr);
                dv::SignatureReader reader({oldLocals,oldLength});if(reader.Byte()!=IMAGE_CEE_CS_CALLCONV_LOCAL_SIG)return fail(E_INVALIDARG);
                localCount=reader.Number();localStart=reader.position;
            }
            if(localCount>65533){site.status.store(-5);return S_OK;}
            dv::SignatureReader::Encode(locals,localCount+(isVoid?1:2));
            if(oldLocals)locals.insert(locals.end(),oldLocals+localStart,oldLocals+oldLength);
            const uint32_t outcomeLocal=localCount,returnLocal=localCount+1;
            locals.push_back(ELEMENT_TYPE_U4);if(!isVoid)locals.insert(locals.end(),returnType.begin(),returnType.end());
            mdSignature localsToken=0;hr=emit->GetTokenFromSig(locals.data(),ULONG(locals.size()),&localsToken);if(FAILED(hr))return fail(hr);rewriter.LocalSignature(localsToken);
            const COR_SIGNATURE beginSig[]={IMAGE_CEE_CS_CALLCONV_C,1,ELEMENT_TYPE_VOID,ELEMENT_TYPE_U8};
            const COR_SIGNATURE endSig[]={IMAGE_CEE_CS_CALLCONV_C,2,ELEMENT_TYPE_VOID,ELEMENT_TYPE_U8,ELEMENT_TYPE_U4};
            mdSignature beginToken=0,endToken=0;
            hr=emit->GetTokenFromSig(beginSig,sizeof(beginSig),&beginToken);if(FAILED(hr))return fail(hr);
            hr=emit->GetTokenFromSig(endSig,sizeof(endSig),&endToken);if(FAILED(hr))return fail(hr);
            auto instruction=[&](unsigned opcode,uint64_t value=0) {auto* p=rewriter.NewILInstr();p->m_opcode=opcode;p->m_Arg64=int64_t(value);return p;};
            auto* sentinel=rewriter.GetILList();auto* first=sentinel->m_pNext;
            rewriter.InsertBefore(first,instruction(CEE_LDC_I4_2));rewriter.InsertBefore(first,instruction(CEE_STLOC,outcomeLocal));
            rewriter.InsertBefore(first,instruction(CEE_LDC_I8,id));rewriter.InsertBefore(first,instruction(CEE_LDC_I8,uint64_t(reinterpret_cast<uintptr_t>(&DVNativeEnter))));
            rewriter.InsertBefore(first,instruction(CEE_CONV_I));rewriter.InsertBefore(first,instruction(CEE_CALLI,beginToken));
            auto* complete=instruction(CEE_LDC_I4_1);rewriter.SealOriginalEnd(complete);
            rewriter.InsertBefore(sentinel,complete);rewriter.InsertBefore(sentinel,instruction(CEE_STLOC,outcomeLocal));
            auto* leave=instruction(CEE_LEAVE);rewriter.InsertBefore(sentinel,leave);
            auto* cleanup=instruction(CEE_LDC_I8,id);rewriter.InsertBefore(sentinel,cleanup);
            rewriter.InsertBefore(sentinel,instruction(CEE_LDLOC,outcomeLocal));
            rewriter.InsertBefore(sentinel,instruction(CEE_LDC_I8,uint64_t(reinterpret_cast<uintptr_t>(&DVNativeLeave))));
            rewriter.InsertBefore(sentinel,instruction(CEE_CONV_I));rewriter.InsertBefore(sentinel,instruction(CEE_CALLI,endToken));
            auto* finallyEnd=instruction(CEE_ENDFINALLY);rewriter.InsertBefore(sentinel,finallyEnd);
            auto* exit=instruction(CEE_NOP);rewriter.InsertBefore(sentinel,exit);leave->m_pTarget=exit;
            if(!isVoid)rewriter.InsertBefore(sentinel,instruction(CEE_LDLOC,returnLocal));rewriter.InsertBefore(sentinel,instruction(CEE_RET));
            for(auto* original:returns){
                if(isVoid){original->m_opcode=CEE_LEAVE;original->m_pTarget=complete;}
                else {original->m_opcode=CEE_STLOC;original->m_Arg32=returnLocal;auto* branch=instruction(CEE_LEAVE);branch->m_pTarget=complete;rewriter.InsertAfter(original,branch);}
            }
            rewriter.AddFinally(first,cleanup,cleanup,finallyEnd);
            if(rewriter.EstimatedMaxStack()>65535){site.status.store(-5);return S_OK;}
            hr=control->SetCodegenFlags(COR_PRF_CODEGEN_DISABLE_INLINING);if(FAILED(hr))return fail(hr);
            hr=rewriter.Export();if(FAILED(hr))return fail(hr,-6);
            std::vector<COR_IL_MAP> map;map.reserve(originals.size());
            for(const auto& [offset,node]:originals)map.push_back({offset,node->m_offset,TRUE});
            hr=control->SetILInstrumentedCodeMap(ULONG(map.size()),map.data());if(FAILED(hr))return fail(hr,-6);
            site.status.store(2);recorder_->methodsInstrumented.fetch_add(1);return S_OK;
        } catch(...) {site.status.store(-7);recorder_->faults.fetch_add(1);return E_FAIL;}
    }
};
class Factory final : public IClassFactory {
    std::atomic<ULONG> refs_{1};
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** value) override {if(!value)return E_POINTER;*value=nullptr;if(id!=IID_IUnknown && id!=IID_IClassFactory)return E_NOINTERFACE;*value=this;AddRef();return S_OK;}
    ULONG STDMETHODCALLTYPE AddRef() override{return refs_.fetch_add(1)+1;}
    ULONG STDMETHODCALLTYPE Release() override{auto count=refs_.fetch_sub(1)-1;if(!count)delete this;return count;}
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer,REFIID id,void** value) override {
        if(!value)return E_POINTER;*value=nullptr;if(outer)return CLASS_E_NOAGGREGATION;
        try {auto* profiler=new NativeProfiler();profiler->AddRef();auto result=profiler->QueryInterface(id,value);profiler->Release();return result;}catch(...){return E_OUTOFMEMORY;}
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL) override{return S_OK;}
};
}
#ifdef _WIN32
#define DV_EXPORT // Export COM entry points through the module definition, matching the Windows SDK declarations.
#else
#define DV_EXPORT __attribute__((visibility("default")))
#endif
extern "C" DV_EXPORT HRESULT STDMETHODCALLTYPE DllGetClassObject(REFCLSID clsid,REFIID iid,void** value) {
    if(!value)return E_POINTER;*value=nullptr;if(clsid!=ProfilerId)return CLASS_E_CLASSNOTAVAILABLE;
    try{auto* factory=new Factory();auto result=factory->QueryInterface(iid,value);factory->Release();return result;}catch(...){return E_OUTOFMEMORY;}
}
extern "C" DV_EXPORT HRESULT STDMETHODCALLTYPE DllCanUnloadNow(){return S_FALSE;}
