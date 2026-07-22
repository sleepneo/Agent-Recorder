#include "probe.h"

#include <windows.h>
#include <d3d11.h>
#include <mfapi.h>
#include <mferror.h>
#include <mftransform.h>
#include <codecapi.h>
#include <wrl/client.h>

#include <format>
#include <string>

namespace wgc {

namespace {

std::string HresultToString(HRESULT hr) {
    return std::format("0x{:08X}", static_cast<unsigned>(hr));
}

using RtlGetVersionFn = LONG (WINAPI*)(RTL_OSVERSIONINFOW*);

bool IsWgcSupported() {
    // Windows Graphics Capture requires Windows 10 version 1903 (build 18362).
    // Use RtlGetVersion so the check is not dependent on an application manifest.
    HMODULE ntdll = ::GetModuleHandleW(L"ntdll.dll");
    if (!ntdll) return false;
    auto rtlGetVersion = reinterpret_cast<RtlGetVersionFn>(::GetProcAddress(ntdll, "RtlGetVersion"));
    if (!rtlGetVersion) return false;
    RTL_OSVERSIONINFOW osi = {};
    osi.dwOSVersionInfoSize = sizeof(osi);
    if (rtlGetVersion(&osi) != 0) return false;
    if (osi.dwMajorVersion > 10) return true;
    if (osi.dwMajorVersion == 10 && osi.dwMinorVersion == 0 && osi.dwBuildNumber >= 18362) return true;
    return false;
}

HRESULT CreateD3D11Device(Microsoft::WRL::ComPtr<ID3D11Device>& device) {
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
#if defined(_DEBUG)
    flags |= D3D11_CREATE_DEVICE_DEBUG;
#endif
    D3D_FEATURE_LEVEL featureLevels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;
    return D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
                             featureLevels, static_cast<UINT>(std::size(featureLevels)),
                             D3D11_SDK_VERSION, device.GetAddressOf(), nullptr, &context);
}

HRESULT CreateSoftwareH264Encoder() {
    // Enumerate software H.264 encoder MFTs. Avoids a hard-coded CLSID that may not
    // be registered on all Windows SKUs/editions.
    MFT_REGISTER_TYPE_INFO outputType = {};
    outputType.guidMajorType = MFMediaType_Video;
    outputType.guidSubtype = MFVideoFormat_H264;

    IMFActivate** activates = nullptr;
    UINT32 count = 0;
    HRESULT hr = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER,
                           MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_ASYNCMFT,
                           nullptr, &outputType, &activates, &count);
    if (FAILED(hr)) return hr;
    if (count == 0) {
        ::CoTaskMemFree(activates);
        return REGDB_E_CLASSNOTREG;
    }

    bool created = false;
    for (UINT32 i = 0; i < count; ++i) {
        Microsoft::WRL::ComPtr<IMFTransform> transform;
        hr = activates[i]->ActivateObject(__uuidof(IMFTransform),
                                          reinterpret_cast<void**>(transform.GetAddressOf()));
        activates[i]->Release();
        if (SUCCEEDED(hr)) {
            created = true;
            // Release any remaining activate objects that were not examined.
            for (UINT32 j = i + 1; j < count; ++j) {
                activates[j]->Release();
            }
            break;
        }
    }
    ::CoTaskMemFree(activates);
    return created ? S_OK : hr;
}

} // namespace

ProbeResult RunProbe() {
    ProbeResult result;

    // Initialize COM for this thread. MFTEnumEx/ActivateObject require an
    // apartment, and MFStartup must be paired with MFShutdown.
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool comInitialized = SUCCEEDED(hr);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) {
        result.error = std::format("COM initialization failed: {}", HresultToString(hr));
        return result;
    }
    struct ComGuard {
        bool needUninit;
        ~ComGuard() { if (needUninit) CoUninitialize(); }
    } comGuard{comInitialized};

    if (!IsWgcSupported()) {
        result.error = "Windows Graphics Capture requires Windows 10 version 1903 or later";
        return result;
    }
    result.wgcSupported = true;

    hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) {
        result.error = std::format("MFStartup failed: {}", HresultToString(hr));
        return result;
    }
    struct MfGuard {
        bool needShutdown = true;
        ~MfGuard() { if (needShutdown) MFShutdown(); }
    } mfGuard;

    Microsoft::WRL::ComPtr<ID3D11Device> d3dDevice;
    hr = CreateD3D11Device(d3dDevice);
    if (FAILED(hr)) {
        // Try WARP fallback.
        hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr,
                               D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                               nullptr, 0, D3D11_SDK_VERSION,
                               &d3dDevice, nullptr, nullptr);
        if (FAILED(hr)) {
            result.error = std::format("D3D11 device creation failed: {}", HresultToString(hr));
            return result;
        }
    }
    result.d3d11Initialized = true;

    hr = CreateSoftwareH264Encoder();
    if (FAILED(hr)) {
        result.error = std::format("Software H.264 encoder creation failed: {}", HresultToString(hr));
        return result;
    }
    result.encoderCreated = true;

    mfGuard.needShutdown = false;
    MFShutdown();
    result.ok = true;
    return result;
}

} // namespace wgc