#include "test_framework.h"

#include "display_geometry.h"

#include <windows.h>

namespace wgc {
namespace test {

namespace {

RECT MakeRect(long left, long top, long right, long bottom) {
    RECT r = {};
    r.left = left;
    r.top = top;
    r.right = right;
    r.bottom = bottom;
    return r;
}

} // namespace

TEST_REGISTRAR(RectExactlyMatchesMonitor_PrimaryExactMatch, []() {
    Rect target{0, 0, 3840, 2160};
    RECT monitor = MakeRect(0, 0, 3840, 2160);
    ASSERT_TRUE(RectExactlyMatchesMonitor(target, monitor));
});

TEST_REGISTRAR(RectExactlyMatchesMonitor_NegativeLeftMatch, []() {
    Rect target{-1920, 0, 1920, 1080};
    RECT monitor = MakeRect(-1920, 0, 0, 1080);
    ASSERT_TRUE(RectExactlyMatchesMonitor(target, monitor));
});

TEST_REGISTRAR(RectExactlyMatchesMonitor_VerticalArrangementMatch, []() {
    Rect target{0, 2160, 1920, 1080};
    RECT monitor = MakeRect(0, 2160, 1920, 3240);
    ASSERT_TRUE(RectExactlyMatchesMonitor(target, monitor));
});

TEST_REGISTRAR(RectExactlyMatchesMonitor_SameSizeDifferentOriginMismatch, []() {
    Rect target{0, 0, 1920, 1080};
    RECT monitor = MakeRect(3840, 0, 5760, 1080);
    ASSERT_FALSE(RectExactlyMatchesMonitor(target, monitor));
});

TEST_REGISTRAR(RectExactlyMatchesMonitor_WidthOffByOneMismatch, []() {
    Rect target{0, 0, 1920, 1080};
    RECT monitor = MakeRect(0, 0, 1921, 1080);
    ASSERT_FALSE(RectExactlyMatchesMonitor(target, monitor));
});

TEST_REGISTRAR(RectExactlyMatchesMonitor_HeightOffByOneMismatch, []() {
    Rect target{0, 0, 1920, 1080};
    RECT monitor = MakeRect(0, 0, 1920, 1081);
    ASSERT_FALSE(RectExactlyMatchesMonitor(target, monitor));
});

TEST_REGISTRAR(RectExactlyMatchesMonitor_LeftOffByOneMismatch, []() {
    Rect target{0, 0, 1920, 1080};
    RECT monitor = MakeRect(1, 0, 1921, 1080);
    ASSERT_FALSE(RectExactlyMatchesMonitor(target, monitor));
});

TEST_REGISTRAR(RectExactlyMatchesMonitor_TopOffByOneMismatch, []() {
    Rect target{0, 0, 1920, 1080};
    RECT monitor = MakeRect(0, 1, 1920, 1081);
    ASSERT_FALSE(RectExactlyMatchesMonitor(target, monitor));
});

TEST_REGISTRAR(RectExactlyMatchesMonitor_NegativeOriginOffByOneMismatch, []() {
    Rect target{-1920, 0, 1920, 1080};
    RECT monitor = MakeRect(-1919, 0, 1, 1080);
    ASSERT_FALSE(RectExactlyMatchesMonitor(target, monitor));
});

} // namespace test
} // namespace wgc
