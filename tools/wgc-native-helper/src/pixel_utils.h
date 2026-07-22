#pragma once

#include <cstdint>
#include <vector>

namespace wgc {

// Copies a BGRA top-down buffer to an MF RGB32 top-down buffer without channel
// swapping. The DXGI_FORMAT_B8G8R8A8_UNORM byte order (B, G, R, A) is already
// compatible with MFVideoFormat_RGB32 / D3DFMT_X8R8G8B8 (X, R, G, B) when
// treated as little-endian 0x00RRGGBB. Alpha is explicitly zeroed.
std::vector<uint8_t> CopyBgraToRgb32(const std::vector<uint8_t>& bgra,
                                     int width,
                                     int height);

} // namespace wgc
