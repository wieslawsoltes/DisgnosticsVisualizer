// Copyright (c) Wiesław Šoltés. MIT licensed.
#include "control.h"
#include <algorithm>
#include <chrono>
#include <cstring>
#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#else
#include <arpa/inet.h>
#include <fcntl.h>
#include <sys/socket.h>
#include <unistd.h>
#include <cerrno>
#endif
namespace dv {
namespace {
#ifdef _WIN32
using Socket=SOCKET; constexpr Socket Invalid=INVALID_SOCKET;
void Close(Socket s){if(s!=Invalid)closesocket(s);}
bool Again(){const auto e=WSAGetLastError();return e==WSAEWOULDBLOCK || e==WSAEINTR;}
bool Nonblocking(Socket s){u_long v=1;return ioctlsocket(s,FIONBIO,&v)==0;}
#else
using Socket=int; constexpr Socket Invalid=-1;
void Close(Socket s){if(s!=Invalid)close(s);}
bool Again(){return errno==EAGAIN || errno==EWOULDBLOCK || errno==EINTR;}
bool Nonblocking(Socket s){return fcntl(s,F_SETFL,O_NONBLOCK)==0 && fcntl(s,F_SETFD,FD_CLOEXEC)==0;}
#endif
uint32_t Read32(const uint8_t* p){uint32_t v=0;for(unsigned n=0;n<4;++n)v|=uint32_t(p[n])<<(8*n);return v;}
void Write32(uint8_t* p,uint32_t v){for(unsigned n=0;n<4;++n)p[n]=uint8_t(v>>(8*n));}
void Write64(uint8_t* p,uint64_t v){for(unsigned n=0;n<8;++n)p[n]=uint8_t(v>>(8*n));}
uint64_t Now(){return uint64_t(std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());}
}
struct ControlServer::Impl {
    Socket listener=Invalid,client=Invalid;
    std::array<uint8_t,16> session{};
    std::array<uint8_t,32> token{};
    std::array<uint8_t,73> request{};
    std::array<uint8_t,64> response{};
    size_t received=0,sent=0;uint64_t deadline=0;
    bool replying=false,terminal=false;
#ifdef _WIN32
    bool winsock=false;
#endif
    void Drop(){Close(client);client=Invalid;received=sent=0;replying=false;}
    ~Impl(){Drop();Close(listener);
#ifdef _WIN32
        if(winsock)WSACleanup();
#endif
    }
};
ControlServer::ControlServer():impl_(std::make_unique<Impl>()){}
ControlServer::~ControlServer()=default;
bool ControlServer::Open(uint16_t port,const std::array<uint8_t,16>& session,const std::array<uint8_t,32>& token){
    auto& p=*impl_;if(port==0 || p.listener!=Invalid)return false;
#ifdef _WIN32
    WSADATA data{};if(WSAStartup(MAKEWORD(2,2),&data))return false;p.winsock=true;
#endif
    p.listener=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);if(p.listener==Invalid)return false;
#ifdef _WIN32
    BOOL exclusive=TRUE;if(setsockopt(p.listener,SOL_SOCKET,SO_EXCLUSIVEADDRUSE,reinterpret_cast<const char*>(&exclusive),sizeof(exclusive)))return false;
#endif
    sockaddr_in address{};address.sin_family=AF_INET;address.sin_addr.s_addr=htonl(INADDR_LOOPBACK);address.sin_port=htons(port);
    if(!Nonblocking(p.listener) || bind(p.listener,reinterpret_cast<sockaddr*>(&address),sizeof(address)) || listen(p.listener,4))return false;
    p.session=session;p.token=token;return true;
}
bool ControlServer::Pending()const noexcept{return impl_->client!=Invalid;}
void ControlServer::Poll(const std::function<ControlResult(uint32_t)>& apply) noexcept {
    auto& p=*impl_;
    try {
        if(p.client==Invalid && !p.terminal){
            p.client=accept(p.listener,nullptr,nullptr);if(p.client==Invalid)return;
            if(!Nonblocking(p.client)){p.Drop();return;}
#ifdef __APPLE__
            int one=1;setsockopt(p.client,SOL_SOCKET,SO_NOSIGPIPE,&one,sizeof(one));
#endif
            p.deadline=Now()+250000000; // A partial unauthenticated request cannot occupy the worker indefinitely.
        }
        if(p.client==Invalid)return;
        if(Now()>=p.deadline){p.Drop();return;}
        if(!p.replying){
            const auto n=recv(p.client,reinterpret_cast<char*>(p.request.data()+p.received),int(p.request.size()-p.received),0);
            if(n<=0){if(n==0 || !Again())p.Drop();return;}
            p.received+=size_t(n);if(p.received<72)return;
            uint8_t difference=0;for(size_t i=0;i<32;++i)difference|=p.request[24+i]^p.token[i];
            if(p.received!=72 || difference || std::memcmp(p.request.data(),"DVNCMD01",8) || std::memcmp(p.request.data()+8,p.session.data(),16) || Read32(p.request.data()+68)!=0){p.Drop();return;}
            const auto operation=Read32(p.request.data()+64);
            const auto result=operation<=4?apply(operation):ControlResult{0,0,0,-1};
            p.response.fill(0);std::memcpy(p.response.data(),"DVNACK01",8);std::memcpy(p.response.data()+8,p.session.data(),16);
            std::memcpy(p.response.data()+24,p.request.data()+56,8);Write32(p.response.data()+32,operation);
            Write32(p.response.data()+36,uint32_t(result.result));Write32(p.response.data()+40,result.state);
            Write32(p.response.data()+44,result.methods);Write32(p.response.data()+48,result.failed);Write64(p.response.data()+56,Now());
            p.replying=true;p.deadline=Now()+250000000; // Bound response I/O separately from an authorized CLR operation.
            if(operation==3 || operation==4){p.terminal=true;Close(p.listener);p.listener=Invalid;}
        }
#ifdef __linux__
        constexpr int flags=MSG_NOSIGNAL;
#else
        constexpr int flags=0;
#endif
        const auto n=send(p.client,reinterpret_cast<const char*>(p.response.data()+p.sent),int(p.response.size()-p.sent),flags);
        if(n<=0){if(n==0 || !Again())p.Drop();return;}
        p.sent+=size_t(n);if(p.sent==p.response.size())p.Drop();
    }catch(...){p.Drop();}
}
}
