using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Read-only description of a Windows audio endpoint. System-audio capture
/// accepts only an active render endpoint and never maps it to a microphone.
/// </summary>
public sealed record SystemAudioEndpointInfo(
    string Id,
    string Name,
    string Direction,
    string State,
    bool IsDefaultMultimedia);

public interface ISystemAudioEndpointProvider
{
    Task<SystemAudioEndpointInfo?> GetDefaultMultimediaRenderEndpointAsync(
        CancellationToken cancellationToken = default);

    Task<SystemAudioEndpointInfo?> GetEndpointAsync(
        string endpointId,
        CancellationToken cancellationToken = default);
}

public sealed class SystemAudioEndpointEnumerationException : Exception
{
    public SystemAudioEndpointEnumerationException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

/// <summary>
/// Windows CoreAudio provider used only by the controlled experiment. It reads
/// the current eRender/eMultimedia default and validates explicit ids through
/// the same endpoint reader. It does not open an audio client or capture data.
/// </summary>
public sealed class CoreAudioSystemAudioEndpointProvider : ISystemAudioEndpointProvider
{
    private readonly ISystemAudioEndpointNativeClient _nativeClient;

    public CoreAudioSystemAudioEndpointProvider(ISystemAudioEndpointNativeClient? nativeClient = null)
    {
        _nativeClient = nativeClient ?? new CoreAudioSystemAudioEndpointNativeClient();
    }

    public Task<SystemAudioEndpointInfo?> GetDefaultMultimediaRenderEndpointAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(_nativeClient.GetDefaultMultimediaRenderEndpoint());
        }
        catch (SystemAudioEndpointEnumerationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SystemAudioEndpointEnumerationException(
                "system_audio_endpoint_enumeration_unavailable",
                $"Could not enumerate the default render endpoint: {ex.Message}");
        }
    }

    public Task<SystemAudioEndpointInfo?> GetEndpointAsync(
        string endpointId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(_nativeClient.GetEndpoint(endpointId));
        }
        catch (SystemAudioEndpointEnumerationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SystemAudioEndpointEnumerationException(
                "system_audio_endpoint_enumeration_unavailable",
                $"Could not enumerate the requested render endpoint: {ex.Message}");
        }
    }
}

public interface ISystemAudioEndpointNativeClient
{
    SystemAudioEndpointInfo? GetDefaultMultimediaRenderEndpoint();
    SystemAudioEndpointInfo? GetEndpoint(string endpointId);
}
