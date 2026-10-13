// Copyright (c) Wiesław Šoltés. MIT licensed.
#include "capture.h"
#include <filesystem>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <thread>

namespace {
void Require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
struct TemporaryFile {
    std::filesystem::path path = std::filesystem::temp_directory_path() /
        ("dv-recorder-" + std::to_string(dv::Clock()) + "-" + std::to_string(dv::OsThread()) + ".dvntrace");
    bool created = false;
    ~TemporaryFile() { if (created) { std::error_code error; std::filesystem::remove(path, error); } }
};
void Budget() {
    TemporaryFile file;
    dv::Configuration config; config.path=file.path.string();
    auto recorder=std::make_unique<dv::Recorder>(std::move(config));
    Require(recorder->Open(), "Could not create unique recorder test file"); file.created=true;
    recorder->accepting.store(true);
    // Raw writer test, not a semantically valid event recording: saturate the file
    // with bounded payloads, then require both status rounds and the footer to fit.
    std::vector<uint8_t> payload(65536);
    unsigned frames=0;
    while (recorder->Frame(3,payload)) Require(++frames<2200, "File capacity was not enforced");
    Require(recorder->BudgetExceeded() && !recorder->accepting.load(), "Budget must stop admission");
    for (unsigned round=0;round<2;++round) for (uint32_t site=1;site<=dv::SiteLimit;++site) {
        std::vector<uint8_t> status; dv::Append32(status,site);dv::Append32(status,round==0?2:3);dv::Append32(status,0);dv::Append32(status,1);
        Require(recorder->Frame(4,status), "Final method status allowance was exhausted");
    }
    Require(recorder->Frame(6,std::vector<uint8_t>(32)), "Final control state allowance was exhausted");
    recorder->Finish(0);
    Require(recorder->ioFaults.load()==0 && recorder->faults.load()==0, "Writer fault at capacity");
    Require(std::filesystem::file_size(file.path)<=128ull*1024*1024, "Native file exceeded capacity");
    std::cout << "BUDGET_OK payloadFrames=" << frames << " statusFrames=" << 2*dv::SiteLimit << '\n';
}
void PauseAdmission() {
    TemporaryFile file;
    dv::Configuration config; config.path=file.path.string();
    auto recorder=std::make_unique<dv::Recorder>(std::move(config));
    Require(recorder->Open(), "Could not create unique pause fixture");file.created=true;
    dv::PublishRecorder(recorder.get());recorder->accepting.store(true);
    dv::Enter(1);recorder->paused.store(true);dv::Enter(2);dv::Leave(2,1);dv::Leave(1,1);
    dv::Enter(3);recorder->paused.store(false);dv::Enter(4);dv::Leave(4,1);dv::Leave(3,1);
    dv::Enter(5);dv::Leave(5,1);
    auto& thread=recorder->threads[0];
    Require(thread.write.load()==6 && thread.depth==0, "Pause/resume corrupted admission or nesting");
    Require(thread.events[1].parent==thread.events[0].cookie, "An admitted child lost its parent while paused");
    Require(thread.events[4].site==5 && thread.events[4].parent==0, "Suppressed tree acquired a recorded child after resume");
    recorder->Finish(0);
    Require(recorder->dropped.load()==0 && recorder->faults.load()==0 && recorder->ioFaults.load()==0, "Intentional paused exclusion became loss");
    dv::PublishRecorder(nullptr);std::cout << "PAUSE_ADMISSION_OK paired=3 excludedRoots=1\n";
}
void StopRace() {
    TemporaryFile file;
    dv::Configuration config; config.path=file.path.string();
    auto recorder=std::make_unique<dv::Recorder>(std::move(config));
    Require(recorder->Open(), "Could not create unique recorder test file"); file.created=true;
    dv::PublishRecorder(recorder.get()); recorder->accepting.store(true);
    std::atomic<unsigned> ready{0}; std::atomic<bool> start{false}, finish{false};
    std::vector<std::thread> workers;
    for (unsigned t=0;t<4;++t) workers.emplace_back([&] {
        ready.fetch_add(1); while (!start.load()) std::this_thread::yield();
        while (!finish.load()) { dv::Enter(1); dv::Leave(1,1); }
        // Old instrumented bodies may still call native addresses after stop.
        for (unsigned i=0;i<10000;++i) { dv::Enter(1); dv::Leave(1,1); }
    });
    while(ready.load()!=4) std::this_thread::yield(); start.store(true);
    for (unsigned i=0;i<10;++i) { recorder->Drain(); std::this_thread::sleep_for(std::chrono::milliseconds(1)); }
    recorder->accepting.store(false); finish.store(true);
    recorder->Finish(0);
    for (auto& worker:workers) worker.join();
    Require(recorder->inFlight.load()==0, "Native callback lease leaked");
    Require(recorder->faults.load()==0 && recorder->ioFaults.load()==0, "Concurrent stop corrupted recorder state");
    const auto size=std::filesystem::file_size(file.path);
    dv::Enter(1);dv::Leave(1,1);
    Require(size==std::filesystem::file_size(file.path), "Disabled callbacks appended after terminal frame");
    dv::PublishRecorder(nullptr);
    std::cout << "STOP_RACE_OK producerDrops=" << recorder->dropped.load() << '\n';
}
}
int main(int argc,char** argv) {
    try {
        Require(argc==2, "Specify budget or stop-race");
        const std::string test=argv[1];
        if(test=="budget") Budget(); else if(test=="stop-race") StopRace(); else if(test=="pause") PauseAdmission(); else throw std::invalid_argument("Unknown test");
        return 0;
    } catch(const std::exception& error) { std::cerr<<error.what()<<'\n';return 1; }
}
