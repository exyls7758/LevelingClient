#include "../../src/Leveling/Multiverse.Core/UnlockedPresentation.h"
#include <future>
#include <iostream>
#include <stdexcept>
#include <chrono>
int main() {
    using namespace std::chrono_literals;
    std::mutex state;
    std::unique_lock lock(state);
    std::promise<void> copyCompleted;
    auto copied=copyCompleted.get_future();
    auto producer=std::async(std::launch::async,[&]{std::lock_guard guard(state);copyCompleted.set_value();});
    bool advanced=multiverse::InvokePresentation(lock,[&]{return copied.wait_for(500ms)==std::future_status::ready;});
    bool relocked=lock.owns_lock();
    lock.unlock();producer.get();
    if(!advanced || !relocked){std::cerr<<"FAIL decoder copy blocked by stalled Present\n";return 1;}
    lock.lock();
    try {multiverse::InvokePresentation(lock,[]{throw std::runtime_error("present failed");return 0;});} catch(const std::runtime_error&){}
    if(!lock.owns_lock()){std::cerr<<"FAIL exception lost resource lock\n";return 2;}
    std::cout<<"PASS decoder advances during stalled presentation; resource lock restored on success/error\n";
}
