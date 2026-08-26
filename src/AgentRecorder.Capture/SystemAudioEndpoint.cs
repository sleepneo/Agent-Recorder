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
    /// <summary>
    /// Enumerates the current active render endpoints. The result contains
    /// only safe, read-only metadata suitable for public capability/device
    /// responses; it never opens an audio client.
    /// </summary>
    Task<IReadOnlyList<SystemAudioEndpointInfo>> GetRenderEndpointsAsync(
        CancellationToken cancellationToken = default);

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
/// Windows CoreAudio provider for public system-audio capability and capture
/// flows. It reads active eRender endpoints and the eRender/eMultimedia default
/// and validates explicit ids through the same endpoint reader. It does not
/// open an audio client or capture data.
/// </summary>
public sealed class CoreAudioSystemAudioEndpointProvider : ISystemAudioEndpointProvider
{
    private readonly ISystemAudioEndpointNativeClient _nativeClient;

    public CoreAudioSystemAudioEndpointProvider(ISystemAudioEndpointNativeClient? nativeClient = null)
    {
        _nativeClient = nativeClient ?? new CoreAudioSystemAudioEndpointNativeClient();
    }

    public async Task<IReadOnlyList<SystemAudioEndpointInfo>> GetRenderEndpointsAsync(
        CancellationToken cancellationToken = default)
    {
        var endpoints = await ExecuteNativeAsync(
            ct => _nativeClient.GetRenderEndpoints(ct), cancellationToken).ConfigureAwait(false);
        return endpoints
            .OrderByDescending(endpoint => endpoint.IsDefaultMultimedia)
            .ThenBy(endpoint => endpoint.Name, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public Task<SystemAudioEndpointInfo?> GetDefaultMultimediaRenderEndpointAsync(
        CancellationToken cancellationToken = default)
        => ExecuteNativeAsync(ct => _nativeClient.GetDefaultMultimediaRenderEndpoint(ct), cancellationToken);

    public Task<SystemAudioEndpointInfo?> GetEndpointAsync(
        string endpointId,
        CancellationToken cancellationToken = default)
        => ExecuteNativeAsync(ct => _nativeClient.GetEndpoint(endpointId, ct), cancellationToken);

    private static async Task<T> ExecuteNativeAsync<T>(Func<CancellationToken, T> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // CoreAudio calls are synchronous COM calls. Run them off the API
            // request thread so the caller's WaitAsync timeout remains a real
            // response bound; cancellation is checked before and after the
            // native operation.
            var result = await Task.Run(() => operation(cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SystemAudioEndpointEnumerationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new SystemAudioEndpointEnumerationException(
                "system_audio_endpoint_enumeration_unavailable",
                "Could not enumerate the render endpoint.");
        }
    }
}

public interface ISystemAudioEndpointNativeClient
{
    IReadOnlyList<SystemAudioEndpointInfo> GetRenderEndpoints(CancellationToken cancellationToken = default);
    SystemAudioEndpointInfo? GetDefaultMultimediaRenderEndpoint(CancellationToken cancellationToken = default);
    SystemAudioEndpointInfo? GetEndpoint(string endpointId, CancellationToken cancellationToken = default);
}
