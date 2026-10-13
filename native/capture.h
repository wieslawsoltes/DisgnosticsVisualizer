// Copyright (c) Wiesław Šoltés. MIT licensed.
#pragma once
#include <array>
#include <bit>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

namespace dv {
constexpr uint32_t ThreadLimit = 64, RingCapacity = 4096, StackCapacity = 256, SiteLimit = 4096;
struct Configuration {
    uint32_t durationMs = 0, sampleEvery = 1;
    uint64_t utcTicks = 0;
    std::array<uint8_t,16> session{};
    std::string path;
    uint16_t controlPort = 0;
    std::array<uint8_t,32> controlToken{};
    std::vector<std::string> rules;
    static Configuration Parse(const uint8_t* data, size_t length);
};
struct Event {
    uint64_t cookie = 0, parent = 0, timestamp = 0, duration = 0;
    uint32_t site = 0, thread = 0, kind = 0, flags = 0;
};
static_assert(sizeof(Event) == 48);
static_assert(std::endian::native == std::endian::little && sizeof(void*) == 8);
struct StackEntry { uint64_t cookie, parent, start; uint32_t site; };
struct ThreadBuffer {
    std::atomic<uint64_t> write{0}, read{0};
    std::atomic<uint32_t> ready{0};
    uint64_t osId = 0, sequence = 0, samples = 0;
    uint32_t ordinal = 0, depth = 0, overflow = 0;
    std::array<StackEntry, StackCapacity> stack{};
    std::array<Event,RingCapacity> events{};
};
// Storage intentionally lasts until process exit: already compiled calli sites can outlive CLR Shutdown.
class Recorder {
    FILE* file_ = nullptr;
    uint64_t bytes_ = 0;
    bool budgetExceeded_ = false;
    bool Write(const void* p, size_t n) noexcept;
public:
    Configuration config;
    uint64_t origin = 0;
    std::atomic<bool> accepting{false}, paused{false};
    std::atomic<uint32_t> inFlight{0}, nextThread{0};
    std::atomic<uint64_t> dropped{0}, faults{0}, ioFaults{0}, methodsRequested{0}, methodsInstrumented{0}, compilations{0}, rejitErrors{0};
    std::array<ThreadBuffer,ThreadLimit> threads{};
    explicit Recorder(Configuration cfg);
    ~Recorder() { if (file_) fclose(file_); }
    bool Open();
    bool Frame(uint32_t kind, const std::vector<uint8_t>& payload) noexcept;
    bool BudgetExceeded() const noexcept { return budgetExceeded_; }
    void Drain() noexcept;
    void Finish(uint32_t reason) noexcept;
    void Put(ThreadBuffer& buffer, const Event& event) noexcept;
    ThreadBuffer* Thread() noexcept;
};
Recorder* ActiveRecorder() noexcept;
void PublishRecorder(Recorder*) noexcept;
uint64_t Clock() noexcept;
uint64_t OsThread() noexcept;
void Append32(std::vector<uint8_t>&,uint32_t);
void Append64(std::vector<uint8_t>&,uint64_t);
void AppendString(std::vector<uint8_t>&,const std::string&);
void Enter(uint32_t site) noexcept;
void Leave(uint32_t site, uint32_t outcome) noexcept;
} // namespace dv
