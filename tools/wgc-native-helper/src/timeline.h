#pragma once

#include <cstdint>

namespace wgc {

// Normalizes WGC SystemRelativeTime (QPC-based 100-ns ticks) to a monotonic
// media timeline relative to the first accepted frame.
class FrameTimeline {
public:
    explicit FrameTimeline(int fps);

    // Feeds a raw SystemRelativeTime tick. Returns true if the frame is accepted
    // and populates *mediaTimeHns. For the first frame, *durationHns is nominal;
    // for later frames it is the interval from the previous accepted timestamp
    // to this one. CaptureSession uses that interval to close the previous
    // sample, not as the current sample's final duration.
    bool SubmitFrame(int64_t systemRelativeTimeHns,
                     int64_t* mediaTimeHns,
                     int64_t* durationHns,
                     int64_t maxMediaTimeHns = -1);

    // Closes the last accepted sample at an authorized capture end expressed
    // on the normalized media timeline. Returns false when there is no pending
    // sample or the requested end is not after its timestamp.
    bool FinalizeAt(int64_t captureEndHns,
                    int64_t* mediaTimeHns,
                    int64_t* durationHns) const;

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
