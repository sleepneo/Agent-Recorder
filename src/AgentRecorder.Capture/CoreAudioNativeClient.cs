using System;
using System.Runtime.InteropServices;

namespace AgentRecorder.Capture;

/// <summary>
/// Windows-only implementation of <see cref="ICoreAudioNativeClient"/>.
/// Performs real CoreAudio COM calls with a correct IAudioEndpointVolume vtable
/// and releases COM objects on every path.
/// </summary>
public sealed class CoreAudioNativeClient : ICoreAudioNativeClient
{
    private static readonly Guid MMDeviceEnumeratorCLSID = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IAudioEndpointVolumeGUID = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    public string? GetDefaultCaptureEndpointId()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? endpoint = null;
        try
        {
            enumerator = CreateEnumerator();
            if (enumerator == null)
                return null;

            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eMultimedia, out endpoint);
            if (hr != 0 || endpoint == null)
                return null;

            hr = endpoint.GetId(out string? id);
            return hr == 0 ? id : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            SafeRelease(endpoint);
            SafeRelease(enumerator);
        }
    }

    public CoreAudioEndpointDetails GetEndpointDetails(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
            return Unknown();

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;

        try
        {
            enumerator = CreateEnumerator();
            if (enumerator == null)
                return Unknown();

            int hr = enumerator.GetDevice(endpointId, out device);
            if (hr == unchecked((int)0x80070490))
            {
                // ERROR_NOT_FOUND: the endpoint is definitively gone (e.g. the
                // Bluetooth device disconnected). This is distinct from an
                // inconclusive COM failure: callers may treat it as positive
                // evidence that a cached enumeration entry is stale.
                return new CoreAudioEndpointDetails(null, "not_present", null, null);
            }
            if (hr != 0 || device == null)
                return Unknown();

            // GetState failure must not be compressed into "inactive".
            DeviceState? rawState = null;
            try
            {
                hr = device.GetState(out DeviceState state);
                if (hr == 0)
                    rawState = state;
            }
            catch
            {
                rawState = null;
            }

            // Try to activate IAudioEndpointVolume. If activation fails, keep
            // the state we already know and report null for mute/volume.
            object? activated = null;
            try
            {
                var iid = IAudioEndpointVolumeGUID;
                hr = device.Activate(ref iid, CLSCTX.ALL, IntPtr.Zero, out activated);
            }
            catch
            {
                activated = null;
            }

            if (hr != 0 || activated == null)
                return CoreAudioEndpointDetailsAssembler.Assemble(rawState, null, _ => null, _ => null);

            volume = activated as IAudioEndpointVolume;
            if (volume == null)
            {
                SafeRelease(activated);
                return CoreAudioEndpointDetailsAssembler.Assemble(rawState, null, _ => null, _ => null);
            }

            // Mute and volume queries are isolated: a failure in one must not
            // wipe the result of the other, and neither must wipe state.
            return CoreAudioEndpointDetailsAssembler.Assemble(
                rawState,
                volume,
                vol => ReadMute((IAudioEndpointVolume)vol),
                vol => ReadVolumePercent((IAudioEndpointVolume)vol));
        }
        catch
        {
            return Unknown();
        }
        finally
        {
            SafeRelease(volume);
            SafeRelease(device);
            SafeRelease(enumerator);
        }
    }

    private static bool? ReadMute(IAudioEndpointVolume volume)
    {
        int hr = volume.GetMute(out bool muted);
        return hr == 0 ? muted : null;
    }

    private static int? ReadVolumePercent(IAudioEndpointVolume volume)
    {
        int hr = volume.GetMasterVolumeLevelScalar(out float scalar);
        if (hr != 0)
            return null;

        var percent = (int)Math.Round(scalar * 100f);
        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;
        return percent;
    }

    private static IMMDeviceEnumerator? CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(MMDeviceEnumeratorCLSID);
        if (type == null)
            return null;

        return Activator.CreateInstance(type) as IMMDeviceEnumerator;
    }

    private static CoreAudioEndpointDetails Unknown(string? state = null)
        => new(null, state, null, null);

    private static void SafeRelease(object? comObject)
    {
        if (comObject == null)
            return;

        try
        {
            while (Marshal.ReleaseComObject(comObject) > 0) { }
        }
        catch
        {
            // Object may already be detached; ignore.
        }
    }
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IntPtr devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, CLSCTX clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);

    [PreserveSig]
    int OpenPropertyStore(uint stgmAccess, out IntPtr properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out DeviceState state);
}

/// <summary>
/// Correct IAudioEndpointVolume vtable order for Windows CoreAudio.
/// All Set methods are declared only to keep the vtable slots aligned with the
/// native interface. Production code must NOT call them; only read-only Get
/// methods are used.
/// </summary>
[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IntPtr notify);

    [PreserveSig]
    int UnregisterControlChangeNotify(IntPtr notify);

    [PreserveSig]
    int GetChannelCount(out uint count);

    // SetMasterVolumeLevel: ABI uses float levelDB, LPCGUID eventContext.
    [PreserveSig]
    int SetMasterVolumeLevel(float levelDB, ref Guid eventContext);

    // SetMasterVolumeLevelScalar: ABI uses float level, LPCGUID eventContext.
    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolumeLevel(out float levelDB);

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    // SetChannelVolumeLevel: ABI uses uint channel, float levelDB, LPCGUID eventContext.
    [PreserveSig]
    int SetChannelVolumeLevel(uint channel, float levelDB, ref Guid eventContext);

    // SetChannelVolumeLevelScalar: ABI uses uint channel, float level, LPCGUID eventContext.
    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

    [PreserveSig]
    int GetChannelVolumeLevel(uint channel, out float levelDB);

    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint channel, out float level);

    // SetMute: ABI uses BOOL mute, LPCGUID eventContext.
    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

    [PreserveSig]
    int GetVolumeStepInfo(out uint step, out uint stepCount);

    // VolumeStepUp/Down: ABI uses LPCGUID eventContext.
    [PreserveSig]
    int VolumeStepUp(ref Guid eventContext);

    [PreserveSig]
    int VolumeStepDown(ref Guid eventContext);

    [PreserveSig]
    int QueryHardwareSupport(out uint hardwareSupportMask);

    [PreserveSig]
    int GetVolumeRange(out float volumeMinDB, out float volumeMaxDB, out float volumeStepDB);
}

internal enum EDataFlow
{
    eRender = 0,
    eCapture = 1,
    eAll = 2
}

internal enum ERole
{
    eConsole,
    eMultimedia,
    eCommunications
}

[Flags]
internal enum DeviceState : uint
{
    Active = 0x00000001,
    Disabled = 0x00000002,
    NotPresent = 0x00000004,
    Unplugged = 0x00000008,
    All = 0x0000000F
}

[Flags]
internal enum CLSCTX : uint
{
    INPROC_SERVER = 0x1,
    INPROC_HANDLER = 0x2,
    LOCAL_SERVER = 0x4,
    REMOTE_SERVER = 0x10,
    ALL = INPROC_SERVER | INPROC_HANDLER | LOCAL_SERVER | REMOTE_SERVER // 23
}
