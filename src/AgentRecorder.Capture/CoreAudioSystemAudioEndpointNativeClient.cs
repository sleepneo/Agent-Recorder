using System;
using System.Runtime.InteropServices;

namespace AgentRecorder.Capture;

internal sealed class CoreAudioSystemAudioEndpointNativeClient : ISystemAudioEndpointNativeClient
{
    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid PKeyDeviceFriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private const uint PKeyDeviceFriendlyNameId = 14;
    internal const int ErrorNotFound = unchecked((int)0x80070490);

    public SystemAudioEndpointInfo? GetDefaultMultimediaRenderEndpoint()
    {
        IMMDeviceEnumeratorSystemAudio? enumerator = null;
        IMMDeviceSystemAudio? endpoint = null;
        try
        {
            enumerator = CreateEnumerator();
            if (enumerator == null)
                throw new SystemAudioEndpointEnumerationException(
                    "system_audio_endpoint_enumeration_unavailable",
                    "MMDeviceEnumerator is unavailable.");

            var hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out endpoint);
            if (hr == ErrorNotFound)
                return null;
            if (hr != 0)
                throw new SystemAudioEndpointEnumerationException(
                    "system_audio_default_endpoint_unavailable",
                    $"GetDefaultAudioEndpoint failed with HRESULT 0x{hr:X8}.");
            if (endpoint == null)
                throw new SystemAudioEndpointEnumerationException(
                    "system_audio_default_endpoint_unavailable",
                    "GetDefaultAudioEndpoint returned S_OK without an endpoint.");

            return ReadEndpoint(endpoint, expectedDirection: "render", isDefault: true);
        }
        finally
        {
            Release(endpoint);
            Release(enumerator);
        }
    }

    public SystemAudioEndpointInfo? GetEndpoint(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
            return null;

        IMMDeviceEnumeratorSystemAudio? enumerator = null;
        IMMDeviceSystemAudio? endpoint = null;
        IMMDeviceSystemAudio? defaultEndpoint = null;
        try
        {
            enumerator = CreateEnumerator();
            if (enumerator == null)
                throw new SystemAudioEndpointEnumerationException(
                    "system_audio_endpoint_enumeration_unavailable",
                    "MMDeviceEnumerator is unavailable.");

            var hr = enumerator.GetDevice(endpointId, out endpoint);
            if (hr == ErrorNotFound)
                return null;
            if (hr != 0)
                throw new SystemAudioEndpointEnumerationException(
                    "system_audio_endpoint_enumeration_unavailable",
                    $"GetDevice failed with HRESULT 0x{hr:X8}.");
            if (endpoint == null)
                throw new SystemAudioEndpointEnumerationException(
                    "system_audio_endpoint_enumeration_unavailable",
                    "GetDevice returned S_OK without an endpoint.");

            var defaultHr = enumerator.GetDefaultAudioEndpoint(
                EDataFlow.eRender,
                ERole.eMultimedia,
                out defaultEndpoint);
            string? defaultId = null;
            var defaultIdHr = -1;
            if (defaultEndpoint != null)
            {
                defaultIdHr = defaultEndpoint.GetId(out var queriedDefaultId);
                defaultId = queriedDefaultId;
            }

            var isDefault = DetermineIsDefaultMultimedia(
                endpointId,
                defaultHr,
                defaultEndpoint != null,
                defaultIdHr,
                defaultId);

            return ReadEndpoint(endpoint, expectedDirection: null, isDefault: isDefault);
        }
        finally
        {
            Release(defaultEndpoint);
            Release(endpoint);
            Release(enumerator);
        }
    }

    internal static bool DetermineIsDefaultMultimedia(
        string requestedEndpointId,
        int defaultHr,
        bool defaultEndpointPresent,
        int defaultIdHr,
        string? defaultEndpointId)
    {
        if (defaultHr == ErrorNotFound)
            return false;

        if (defaultHr != 0)
            throw new SystemAudioEndpointEnumerationException(
                "system_audio_default_endpoint_unavailable",
                $"GetDefaultAudioEndpoint failed with HRESULT 0x{defaultHr:X8}.");

        if (!defaultEndpointPresent)
            throw new SystemAudioEndpointEnumerationException(
                "system_audio_default_endpoint_unavailable",
                "GetDefaultAudioEndpoint returned S_OK without an endpoint.");

        if (defaultIdHr != 0 || string.IsNullOrWhiteSpace(defaultEndpointId))
            throw new SystemAudioEndpointEnumerationException(
                "system_audio_default_endpoint_unavailable",
                "The default render endpoint did not return an id.");

        return string.Equals(defaultEndpointId, requestedEndpointId, StringComparison.Ordinal);
    }

    private static SystemAudioEndpointInfo ReadEndpoint(
        IMMDeviceSystemAudio endpoint,
        string? expectedDirection,
        bool isDefault)
    {
        if (endpoint.GetId(out var id) != 0 || string.IsNullOrWhiteSpace(id))
            throw new SystemAudioEndpointEnumerationException(
                "system_audio_endpoint_enumeration_unavailable",
                "The render endpoint did not return an id.");

        var state = endpoint.GetState(out var rawState) == 0 &&
                    rawState == DeviceState.Active
            ? "active"
            : "inactive";

        string direction = "unknown";
        if (endpoint is IMMEndpointSystemAudio endpointFlow && endpointFlow.GetDataFlow(out var flow) == 0)
            direction = flow == EDataFlow.eRender ? "render" : flow == EDataFlow.eCapture ? "capture" : "unknown";

        if (expectedDirection != null && direction == "unknown")
            direction = expectedDirection;

        var name = ReadFriendlyName(endpoint) ?? string.Empty;
        return new SystemAudioEndpointInfo(id, name, direction, state, isDefault);
    }

    private static string? ReadFriendlyName(IMMDeviceSystemAudio endpoint)
    {
        object? storeObject = null;
        try
        {
            if (endpoint.OpenPropertyStore(0, out storeObject) != 0 || storeObject is not IPropertyStoreSystemAudio store)
                return null;

            var key = new PropertyKey(PKeyDeviceFriendlyName, PKeyDeviceFriendlyNameId);
            if (store.GetValue(ref key, out var value) != 0)
                return null;

            try
            {
                // VT_LPWSTR and VT_BSTR both carry a UTF-16 pointer at offset 8.
                if (value.VariantType is 31 or 8 && value.PointerValue != IntPtr.Zero)
                    return Marshal.PtrToStringUni(value.PointerValue);
                return null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Release(storeObject);
        }
    }

    private static IMMDeviceEnumeratorSystemAudio? CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid);
        return type == null ? null : Activator.CreateInstance(type) as IMMDeviceEnumeratorSystemAudio;
    }

    private static void Release(object? value)
    {
        try
        {
            if (value != null && Marshal.IsComObject(value))
                Marshal.ReleaseComObject(value);
        }
        catch { }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumeratorSystemAudio
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDeviceSystemAudio endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDeviceSystemAudio endpoint);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceSystemAudio
{
    [PreserveSig] int Activate(ref Guid iid, CLSCTX clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    [PreserveSig] int OpenPropertyStore(uint stgmAccess, [MarshalAs(UnmanagedType.Interface)] out object properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out DeviceState state);
}

[ComImport, Guid("1BE09788-6894-4089-8586-9A2A6C265AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMEndpointSystemAudio
{
    [PreserveSig] int GetDataFlow(out EDataFlow dataFlow);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStoreSystemAudio
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;

    public PropertyKey(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariantBlob
{
    public uint Size;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariantUnion
{
    // BLOB is 8 bytes on x86 and 16 bytes on x64, matching the largest
    // architecture-dependent members needed by the SDK union here.
    [FieldOffset(0)] public IntPtr PointerValue;
    [FieldOffset(0)] public PropVariantBlob Blob;
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort VariantType;
    [FieldOffset(8)] public IntPtr PointerValue;
    [FieldOffset(8)] public PropVariantUnion Value;
}
