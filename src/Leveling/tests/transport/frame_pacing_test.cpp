#include "../../Multiverse.Core/FramePacer.h"
#include "../../Multiverse.Core/RemoteFrameDeadlineScheduler.h"
#include <cassert>
#include <chrono>
#include <cstdint>
#include <iostream>

using namespace multiverse;
using Clock = RemoteFrameDeadlineScheduler::Clock;
using namespace std::chrono;

int main()
{
    FramePacer pacer;
    int accepted = 0;
    for (std::int64_t i = 0; i < 600; ++i)
        accepted += pacer.ShouldRun(i * 16667, 60) ? 1 : 0;
    assert(accepted == 600);
    pacer.Reset();
    accepted = 0;
    for (std::int64_t i = 0; i < 600; ++i)
        accepted += pacer.ShouldRun(i * 8333, 60) ? 1 : 0;
    assert(accepted >= 300 && accepted <= 302);
    pacer.Reset();
    accepted = 0;
    for (std::int64_t i = 0; i < 300; ++i)
        accepted += pacer.ShouldRun(i * 33334, 30) ? 1 : 0;
    assert(accepted == 300);
    pacer.Reset();
    accepted = 0;
    for (std::int64_t i = 0; i < 600; ++i)
        accepted += pacer.ShouldRun(i * 16667 + (i % 4 == 0 ? 4000 : 0), 60) ? 1 : 0;
    assert(accepted >= 597 && accepted <= 600);
    pacer.Reset();
    assert(pacer.ShouldRun(1000000, 60));
    assert(pacer.ShouldRun(900000, 60)); // clock discontinuity resets safely
    assert(!pacer.ShouldRun(900000, 0));

    RemoteFrameDeadlineScheduler scheduler;
    auto t0 = Clock::time_point{} + seconds(1);
    auto period = duration_cast<Clock::duration>(duration<double>(1.0 / 60.0));
    assert(scheduler.GetOrInitializeDeadline(t0) == t0);
    scheduler.CommitSuccessfulFrame(t0, period);
    assert(scheduler.GetOrInitializeDeadline(t0 + milliseconds(4)) == t0 + period);
    // A missed ring acquisition must never advance the deadline.
    assert(scheduler.GetOrInitializeDeadline(t0 + milliseconds(18)) == t0 + period);
    auto late = t0 + milliseconds(20);
    scheduler.CommitSuccessfulFrame(late, period);
    assert(scheduler.GetOrInitializeDeadline(late) == t0 + 2 * period);
    // A long stall reanchors rather than producing a burst of old frames.
    auto stalled = t0 + seconds(5);
    scheduler.CommitSuccessfulFrame(stalled, period);
    assert(scheduler.GetOrInitializeDeadline(stalled) == stalled + period);
    scheduler.Reset();
    assert(scheduler.GetOrInitializeDeadline(t0) == t0);
    std::cout << "frame pacing regression: PASS\n";
}
