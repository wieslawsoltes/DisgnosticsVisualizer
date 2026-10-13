// Copyright (c) Wiesław Šoltés. MIT licensed.
#pragma once
#include <array>
#include <cstdint>
#include <functional>
#include <memory>
namespace dv {
struct ControlResult { uint32_t state=0, methods=0, failed=0; int32_t result=0; };
// Optional loopback-only, capability-authenticated endpoint. All I/O runs on the profiler worker.
// No listening endpoint exists for the legacy/default attachment configuration.
class ControlServer {
    struct Impl;
    std::unique_ptr<Impl> impl_;
public:
    ControlServer();
    ~ControlServer();
    bool Open(uint16_t port,const std::array<uint8_t,16>& session,const std::array<uint8_t,32>& token);
    void Poll(const std::function<ControlResult(uint32_t)>& apply) noexcept;
    bool Pending() const noexcept;
};
}
