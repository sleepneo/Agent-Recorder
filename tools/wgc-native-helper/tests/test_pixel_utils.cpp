#include "test_framework.h"

#include "pixel_utils.h"

#include <cstdint>
#include <vector>

using namespace wgc;

TEST_REGISTRAR(CopyBgraToRgb32PreservesBlueChannel, []() {
    std::vector<uint8_t> bgra = { 0xFF, 0x00, 0x00, 0x00 }; // B=FF, G=0, R=0
    auto rgb32 = CopyBgraToRgb32(bgra, 1, 1);
    ASSERT_EQ(rgb32.size(), 4u);
    ASSERT_EQ(rgb32[0], 0xFF); // B
    ASSERT_EQ(rgb32[1], 0x00); // G
    ASSERT_EQ(rgb32[2], 0x00); // R
    ASSERT_EQ(rgb32[3], 0x00); // X (alpha zeroed)
});

TEST_REGISTRAR(CopyBgraToRgb32PreservesRedChannel, []() {
    std::vector<uint8_t> bgra = { 0x00, 0x00, 0xFF, 0x00 }; // B=0, G=0, R=FF
    auto rgb32 = CopyBgraToRgb32(bgra, 1, 1);
    ASSERT_EQ(rgb32.size(), 4u);
    ASSERT_EQ(rgb32[0], 0x00); // B
    ASSERT_EQ(rgb32[1], 0x00); // G
    ASSERT_EQ(rgb32[2], 0xFF); // R
    ASSERT_EQ(rgb32[3], 0x00); // X
});

TEST_REGISTRAR(CopyBgraToRgb32MixedColorZerosAlpha, []() {
    std::vector<uint8_t> bgra = { 0x12, 0x34, 0x56, 0xAA }; // B=12, G=34, R=56, A=AA
    auto rgb32 = CopyBgraToRgb32(bgra, 1, 1);
    ASSERT_EQ(rgb32.size(), 4u);
    ASSERT_EQ(rgb32[0], 0x12);
    ASSERT_EQ(rgb32[1], 0x34);
    ASSERT_EQ(rgb32[2], 0x56);
    ASSERT_EQ(rgb32[3], 0x00); // alpha explicitly zeroed
});

TEST_REGISTRAR(CopyBgraToRgb32RejectsShortBuffer, []() {
    std::vector<uint8_t> bgra = { 0xFF, 0x00, 0x00 }; // only 3 bytes
    auto rgb32 = CopyBgraToRgb32(bgra, 1, 1);
    ASSERT_TRUE(rgb32.empty());
});

TEST_REGISTRAR(CopyBgraToRgb32RejectsZeroDimensions, []() {
    std::vector<uint8_t> bgra(4, 0);
    auto rgb32 = CopyBgraToRgb32(bgra, 0, 1);
    ASSERT_TRUE(rgb32.empty());
    rgb32 = CopyBgraToRgb32(bgra, 1, 0);
    ASSERT_TRUE(rgb32.empty());
});

TEST_REGISTRAR(CopyBgraToRgb32TwoPixelRow, []() {
    // Two pixels: blue then red.
    std::vector<uint8_t> bgra = {
        0xFF, 0x00, 0x00, 0x00,
        0x00, 0x00, 0xFF, 0x00
    };
    auto rgb32 = CopyBgraToRgb32(bgra, 2, 1);
    ASSERT_EQ(rgb32.size(), 8u);
    ASSERT_EQ(rgb32[0], 0xFF);
    ASSERT_EQ(rgb32[1], 0x00);
    ASSERT_EQ(rgb32[2], 0x00);
    ASSERT_EQ(rgb32[3], 0x00);
    ASSERT_EQ(rgb32[4], 0x00);
    ASSERT_EQ(rgb32[5], 0x00);
    ASSERT_EQ(rgb32[6], 0xFF);
    ASSERT_EQ(rgb32[7], 0x00);
});
