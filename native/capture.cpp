// Copyright (c) Wiesław Šoltés. MIT licensed.
#include "capture.h"
#include <algorithm>
#include <cerrno>
#include <cstring>
#include <limits>
#include <stdexcept>
#include <thread>
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <io.h>
#include <fcntl.h>
#include <share.h>
#include <sys/stat.h>
#else
#include <fcntl.h>
#include <unistd.h>
#include <sys/stat.h>
#ifdef __APPLE__
#include <pthread.h>
#else
#include <sys/syscall.h>
#endif
#endif
namespace dv {
namespace {
std::atomic<Recorder*> current{nullptr};
thread_local ThreadBuffer* local = nullptr;
thread_local bool rejectedThread = false;
struct Guard {
    Recorder* recorder;
    int error = errno;
#ifdef _WIN32
    DWORD lastError = GetLastError();
#endif
    bool enabled = false;
    Guard() noexcept : recorder(ActiveRecorder()) {
        if (recorder) { recorder->inFlight.fetch_add(1); enabled = recorder->accepting.load(); }
    }
    ~Guard() {
        if (recorder) recorder->inFlight.fetch_sub(1);
        errno = error;
#ifdef _WIN32
        SetLastError(lastError);
#endif
    }
};
uint32_t Crc(const uint8_t* bytes, size_t n, uint32_t prior = 0) noexcept {
    uint32_t crc = ~prior;
    for (size_t i=0;i<n;++i) { crc ^= bytes[i]; for (int b=0;b<8;++b) crc=(crc>>1)^(0xedb88320u & (0u-(crc&1u))); }
    return ~crc;
}
uint32_t Read32(const uint8_t*& p,const uint8_t* end) {
    if (end-p<4) throw std::invalid_argument("Truncated native configuration");
    uint32_t value=0; for (unsigned i=0;i<4;++i) value|=uint32_t(*p++)<<(8*i); return value;
}
uint64_t Read64(const uint8_t*& p,const uint8_t* end) {
    auto low=Read32(p,end); auto high=Read32(p,end); return low | (uint64_t(high)<<32);
}
std::string ReadString(const uint8_t*& p,const uint8_t* end,size_t maximum) {
    const auto count=Read32(p,end);
    if (count==0 || count>maximum || size_t(end-p)<count || std::memchr(p,0,count)) throw std::invalid_argument("Invalid native string");
    std::string result(reinterpret_cast<const char*>(p),count); p+=count; return result;
}
}
Recorder* ActiveRecorder() noexcept { return current.load(std::memory_order_acquire); }
void PublishRecorder(Recorder* recorder) noexcept { current.store(recorder,std::memory_order_release); }
uint64_t Clock() noexcept { return uint64_t(std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count()); }
uint64_t OsThread() noexcept {
#ifdef _WIN32
    return GetCurrentThreadId();
#elif defined(__APPLE__)
    uint64_t value=0; pthread_threadid_np(nullptr,&value); return value;
#else
    return uint64_t(syscall(SYS_gettid));
#endif
}
void Append32(std::vector<uint8_t>& p,uint32_t value) { for (unsigned n=0;n<4;++n) p.push_back(uint8_t(value>>(8*n))); }
void Append64(std::vector<uint8_t>& p,uint64_t value) { for (unsigned n=0;n<8;++n) p.push_back(uint8_t(value>>(8*n))); }
void AppendString(std::vector<uint8_t>& p,const std::string& s) { Append32(p,uint32_t(s.size())); p.insert(p.end(),s.begin(),s.end()); }
Configuration Configuration::Parse(const uint8_t* data,size_t length) {
    if (!data || length<48 || length>60000 || (std::memcmp(data,"DVNP0001",8) && std::memcmp(data,"DVNP0002",8))) throw std::invalid_argument("Invalid native configuration header");
    const auto* p=data+8; const auto* end=data+length; Configuration cfg;
    cfg.durationMs=Read32(p,end); cfg.sampleEvery=Read32(p,end); cfg.utcTicks=Read64(p,end);
    if (cfg.durationMs<100 || cfg.durationMs>3600000 || cfg.sampleEvery==0 || cfg.sampleEvery>1000000 || end-p<16) throw std::invalid_argument("Invalid capture budget");
    std::copy(p,p+16,cfg.session.begin()); p+=16;
    cfg.path=ReadString(p,end,4096);
    const auto count=Read32(p,end); if (count==0 || count>64) throw std::invalid_argument("Specify 1..64 exact method selectors");
    for (uint32_t i=0;i<count;++i) { auto rule=ReadString(p,end,1024); if (rule.find('!')==std::string::npos || rule.find("::")==std::string::npos || rule.find('*')!=std::string::npos) throw std::invalid_argument("Invalid exact selector"); cfg.rules.push_back(std::move(rule)); }
    if (data[7]=='2') {
        const auto port=Read32(p,end);if(port==0 || port>65535 || end-p!=32)throw std::invalid_argument("Invalid control capability");
        cfg.controlPort=uint16_t(port);std::copy(p,p+32,cfg.controlToken.begin());p+=32;
        if(std::all_of(cfg.controlToken.begin(),cfg.controlToken.end(),[](auto b){return b==0;}))throw std::invalid_argument("Empty control token");
    }
    if (p!=end) throw std::invalid_argument("Trailing native configuration bytes"); return cfg;
}
Recorder::Recorder(Configuration cfg) : config(std::move(cfg)), origin(Clock()) {}
bool Recorder::Open() {
    int fd=-1;
#ifdef _WIN32
    const int count=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,config.path.data(),int(config.path.size()),nullptr,0);
    if (count<=0) return false; std::wstring path(size_t(count),L'\0');
    MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,config.path.data(),int(config.path.size()),path.data(),count);
    if (_wsopen_s(&fd,path.c_str(),_O_WRONLY|_O_CREAT|_O_EXCL|_O_BINARY,_SH_DENYWR,_S_IREAD|_S_IWRITE)!=0) return false;
    file_=_fdopen(fd,"wb"); if (!file_) { _close(fd); return false; }
#else
    if (config.path.empty() || config.path[0]!='/') return false;
    fd=open(config.path.c_str(),O_WRONLY|O_CREAT|O_EXCL|O_CLOEXEC|O_NOFOLLOW,0600);
    if (fd<0) return false; file_=fdopen(fd,"wb"); if (!file_) { close(fd); return false; }
#endif
    std::vector<uint8_t> header{'D','V','N','T','R','C','0','1'};
    Append32(header,config.controlPort?4:3); Append32(header,0); Append64(header,1000000000); Append64(header,origin); Append64(header,config.utcTicks);
    header.insert(header.end(),config.session.begin(),config.session.end());
#ifdef _WIN32
    Append32(header,GetCurrentProcessId());
#else
    Append32(header,uint32_t(getpid()));
#endif
    Append32(header,uint32_t(sizeof(void*))); Append32(header,Crc(header.data(),header.size()));
    return Write(header.data(),header.size());
}
bool Recorder::Write(const void* p,size_t n) noexcept {
    if (!file_ || fwrite(p,1,n,file_)!=n) { ioFaults.fetch_add(1); accepting.store(false); return false; }
    bytes_+=n; return true;
}
bool Recorder::Frame(uint32_t kind,const std::vector<uint8_t>& p) noexcept {
    // The sole writer reserves two complete method-status rounds plus the footer.
    // A stop can occur during Scan/Statuses, followed by final reversion statuses.
    const uint64_t reserve = (kind==4 || kind==6) && !accepting.load() ? 1024 : 1024+2ull*SiteLimit*28;
    if (kind!=5 && bytes_+p.size()+12>128ull*1024*1024-reserve) { budgetExceeded_=true; accepting.store(false); return false; }
    uint8_t header[12]; const uint32_t values[]={kind,uint32_t(p.size()),0};
    for (unsigned n=0;n<3;++n) for (unsigned i=0;i<4;++i) header[n*4+i]=uint8_t(values[n]>>(i*8));
    const auto crc=Crc(p.data(),p.size(),Crc(header,8));
    for (unsigned i=0;i<4;++i) header[8+i]=uint8_t(crc>>(8*i));
    return Write(header,sizeof(header)) && Write(p.data(),p.size());
}
ThreadBuffer* Recorder::Thread() noexcept {
    if (local) return local;
    if (rejectedThread) { dropped.fetch_add(1,std::memory_order_relaxed); return nullptr; }
    auto ordinal=nextThread.fetch_add(1,std::memory_order_relaxed);
    if (ordinal>=ThreadLimit) { rejectedThread=true; dropped.fetch_add(1,std::memory_order_relaxed); return nullptr; }
    auto& slot=threads[ordinal]; slot.ordinal=ordinal+1; slot.osId=OsThread(); slot.ready.store(1,std::memory_order_release); local=&slot; return local;
}
void Recorder::Put(ThreadBuffer& slot,const Event& e) noexcept {
    const auto write=slot.write.load(std::memory_order_relaxed);
    if (write-slot.read.load(std::memory_order_acquire)>=RingCapacity) { dropped.fetch_add(1,std::memory_order_relaxed); return; }
    slot.events[write%RingCapacity]=e; slot.write.store(write+1,std::memory_order_release);
}
void Recorder::Drain() noexcept {
    try {
        for (uint32_t n=0;n<std::min(nextThread.load(std::memory_order_acquire),ThreadLimit);++n) {
            auto& slot=threads[n]; if (!slot.ready.load(std::memory_order_acquire)) continue;
            if (slot.ready.load()==1) { std::vector<uint8_t> info; Append32(info,slot.ordinal); Append64(info,slot.osId); Frame(2,info); slot.ready.store(2); }
            auto read=slot.read.load(std::memory_order_relaxed); const auto write=slot.write.load(std::memory_order_acquire);
            while (read<write) {
                const auto count=std::min<uint64_t>(256,write-read); std::vector<uint8_t> payload; payload.reserve(4+size_t(count)*48); Append32(payload,uint32_t(count));
                for (uint64_t i=0;i<count;++i) {
                    const auto& e=slot.events[(read+i)%RingCapacity]; Append64(payload,e.cookie); Append64(payload,e.parent); Append64(payload,e.timestamp); Append64(payload,e.duration);
                    Append32(payload,e.site); Append32(payload,e.thread); Append32(payload,e.kind); Append32(payload,e.flags);
                }
                if (!Frame(3,payload)) dropped.fetch_add(count); read+=count; slot.read.store(read,std::memory_order_release);
            }
        }
        if (file_ && fflush(file_)!=0) { ioFaults.fetch_add(1); accepting.store(false); }
    } catch (...) { faults.fetch_add(1); accepting.store(false); }
}
void Recorder::Finish(uint32_t reason) noexcept {
    accepting.store(false);
    while (inFlight.load()!=0) std::this_thread::yield();
    Drain();
    try {
        uint64_t open=0;
        for (uint32_t i=0;i<std::min(nextThread.load(),ThreadLimit);++i) for(uint32_t n=0;n<threads[i].depth;++n) if(threads[i].stack[n].cookie) ++open;
        std::vector<uint8_t> footer; Append32(footer,budgetExceeded_ ? 3 : reason); Append32(footer,0);
        for (uint64_t value : {Clock(),dropped.load(),faults.load(),ioFaults.load(),methodsRequested.load(),methodsInstrumented.load(),compilations.load(),rejitErrors.load(),open}) Append64(footer,value);
        Frame(5,footer);
    } catch (...) { faults.fetch_add(1); }
    if (file_) { fclose(file_); file_=nullptr; }
}
void Enter(uint32_t site) noexcept {
    Guard guard; if (!guard.enabled) return; auto& recorder=*guard.recorder; auto* slot=recorder.Thread(); if (!slot) return;
    if (slot->depth>=StackCapacity || slot->overflow) { ++slot->overflow; recorder.dropped.fetch_add(1); return; }
    // Stamp before reading the admission gate: a root admitted before pause cannot
    // acquire an apparent start time after the subsequently recorded transition.
    const auto start=Clock();
    const auto parent=slot->depth ? slot->stack[slot->depth-1].cookie : 0;
    const bool sampled=slot->depth ? parent!=0 : !recorder.paused.load(std::memory_order_acquire) && (++slot->samples % recorder.config.sampleEvery)==0;
    if (sampled && slot->sequence==0xffffffffffffull) { recorder.faults.fetch_add(1); recorder.accepting.store(false); return; }
    const auto cookie=sampled ? ((uint64_t(slot->ordinal)<<48)|++slot->sequence) : 0;
    auto& entry=slot->stack[slot->depth++]; entry={cookie,parent,start,site};
    if (cookie) recorder.Put(*slot,{cookie,parent,entry.start,0,site,slot->ordinal,0,0});
}
void Leave(uint32_t site,uint32_t outcome) noexcept {
    Guard guard; if (!guard.enabled) return; auto& recorder=*guard.recorder; auto* slot=local; if (!slot) return;
    if (slot->overflow) { --slot->overflow; return; }
    if (slot->depth==0 || slot->stack[slot->depth-1].site!=site) { recorder.faults.fetch_add(1); return; }
    const auto entry=slot->stack[--slot->depth];
    if (entry.cookie) recorder.Put(*slot,{entry.cookie,entry.parent,entry.start,Clock()-entry.start,entry.site,slot->ordinal,outcome,0});
}
} // namespace dv
