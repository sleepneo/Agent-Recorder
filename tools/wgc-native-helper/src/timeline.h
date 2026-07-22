#pragma once

#include <cstdint>

namespace wgc {

// Normalizes WGC SystemRelativeTime (QPC-based 100-ns ticks) to a monotonic
// media timeline relative to the first accepted frame.
class FrameTimeline {
public:
    explicit FrameTimeline(int fps);

    // Feeds a raw SystemRelativeTime tick. Returns true if the frame is accepted
    // and populates *mediaTimeHns and *durationHns. Returns false if the frame
    // should be dropped (non-monotonic or invalid), in which case *mediaTimeHns
    // and *durationHns are unchanged.
    bool SubmitFrame(int64_t systemRelativeTimeHns,
                     int64_t* mediaTimeHns,
                     int64_t* durationHns);

    int64_t FramesSubmitted() const { return submitted_; }
    int64_t FramesDropped() const { return dropped_; }

    // Resets the timeline to its initial state. Used for testing.
    void Reset();

private:
    int fps_;
    bool hasAnchor_ = false;
    int64_t anchorHns_ = 0;
    int64_t lastMediaTimeHns_ = 0;
    int64_t nominalDurationHns_ = 0;
    int64_t submitted_ = 0;
    int64_t dropped_ = 0;
};

} // namespace wgc
