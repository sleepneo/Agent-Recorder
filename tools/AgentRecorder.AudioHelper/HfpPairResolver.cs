using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AgentRecorder.AudioHelper;

/// <summary>
/// Owns endpoint wrappers acquired by one discovery operation. The resolver
/// must not rely on endpoint value equality or on the CoreAudio enumerator to
/// release individual MMDevice wrappers.
/// </summary>
internal sealed class HfpEndpointOwnership : IDisposable
{
    private readonly HashSet<IHfpEndpoint> _endpoints = new(ReferenceEqualityComparer.Instance);
    private int _disposed;

    public void Own(IHfpEndpoint? endpoint)
    {
        if (endpoint == null || Volatile.Read(ref _disposed) != 0)
            return;
        _endpoints.Add(endpoint);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var endpoint in _endpoints)
        {
            try { endpoint.Dispose(); } catch { }
        }
        _endpoints.Clear();
    }
}

internal enum HfpPairDiscoveryStatus
{
    NotApplicable,
    Paired,
    NoCandidate,
    Ambiguous,
    EvidenceFailure
}

internal enum HfpTransportClassification
{
    Unknown,
    NotHfp,
    HfpCandidate
}

/// <summary>
/// Bounded, machine-readable outcome of automatic HFP pair discovery. The
/// resolver never exposes COM exceptions or raw endpoint diagnostics to the
/// caller and never uses null to represent a blocking discovery failure.
/// </summary>
internal sealed class HfpPairDiscoveryResult
{
    private HfpPairDiscoveryResult(HfpPairDiscoveryStatus status, string pairEvidence,
        string reason, string? renderEndpointId, string? resultCode,
        HfpTransportClassification transportClassification)
    {
        Status = status;
        PairEvidence = pairEvidence;
        var sanitizedReason = HfpDuplexAudioInputFactory.SanitizeReason(reason);
        Reason = sanitizedReason.Contains('\\') || sanitizedReason.Contains('/')
            ? "Automatic HFP pair evidence failed"
            : sanitizedReason.Length <= 256 ? sanitizedReason : sanitizedReason[..256];
        RenderEndpointId = renderEndpointId;
        ResultCode = resultCode;
        TransportClassification = transportClassification;
    }

    public HfpPairDiscoveryStatus Status { get; }
    public string PairEvidence { get; }
    public string Reason { get; }
    public string? RenderEndpointId { get; }
    public string? ResultCode { get; }
    public HfpTransportClassification TransportClassification { get; }
    public bool IsPaired => Status == HfpPairDiscoveryStatus.Paired && !string.IsNullOrEmpty(RenderEndpointId);
    public bool IsBlockingFailure => Status is HfpPairDiscoveryStatus.NoCandidate
        or HfpPairDiscoveryStatus.Ambiguous
        or HfpPairDiscoveryStatus.EvidenceFailure;

    public static HfpPairDiscoveryResult NotApplicable(string reason,
        HfpTransportClassification transportClassification = HfpTransportClassification.Unknown)
        => new(HfpPairDiscoveryStatus.NotApplicable, "not_applicable", reason, null, "not_applicable",
            transportClassification);

    public static HfpPairDiscoveryResult Paired(string renderEndpointId)
        => new(HfpPairDiscoveryStatus.Paired, "same_container_id", "Unique active same-container render endpoint", renderEndpointId,
            "same_container_id", HfpTransportClassification.HfpCandidate);

    public static HfpPairDiscoveryResult NoCandidate(string reason)
        => new(HfpPairDiscoveryStatus.NoCandidate, "hfp_pair_discovery_failed", reason, null,
            "audio_hfp_pair_discovery_failed", HfpTransportClassification.HfpCandidate);

    public static HfpPairDiscoveryResult Ambiguous(int candidateCount)
        => new(HfpPairDiscoveryStatus.Ambiguous, "hfp_pair_discovery_failed",
            $"Automatic HFP pair discovery found {Math.Max(0, candidateCount)} active candidates; refusing to choose by name or order",
            null, "audio_hfp_pair_discovery_failed", HfpTransportClassification.HfpCandidate);

    public static HfpPairDiscoveryResult EvidenceFailure(string reason,
        HfpTransportClassification transportClassification = HfpTransportClassification.HfpCandidate)
        => new(HfpPairDiscoveryStatus.EvidenceFailure, "hfp_pair_discovery_failed", reason, null,
            "audio_hfp_pair_discovery_failed", transportClassification);
}

/// <summary>
/// Resolves the render half of an HFP pair from passive structural CoreAudio
/// evidence. Render MixFormat/AudioClient is intentionally never touched.
/// </summary>
internal interface IHfpPairResolver
{
    HfpPairDiscoveryResult Resolve(string captureEndpointId);
}

internal sealed class HfpPairResolver : IHfpPairResolver
{
    private const int MaxSampleRate = 32000;
    private readonly Func<IHfpEndpointEnumerator> _endpointEnumeratorFactory;

    public HfpPairResolver()
        : this(() => new NAudioHfpEndpointEnumerator())
    {
    }

    internal HfpPairResolver(Func<IHfpEndpointEnumerator> endpointEnumeratorFactory)
    {
        _endpointEnumeratorFactory = endpointEnumeratorFactory ?? throw new ArgumentNullException(nameof(endpointEnumeratorFactory));
    }

    public HfpPairDiscoveryResult Resolve(string captureEndpointId)
    {
        if (string.IsNullOrWhiteSpace(captureEndpointId))
            return HfpPairDiscoveryResult.NotApplicable("Capture endpoint id is empty");

        IHfpEndpoint? capture = null;
        IHfpEndpointEnumerator? enumerator = null;
        using var ownership = new HfpEndpointOwnership();
        try
        {
            enumerator = _endpointEnumeratorFactory();
            capture = enumerator.GetDevice(captureEndpointId);
            ownership.Own(capture);

            if (!string.Equals(capture.EndpointId, captureEndpointId, StringComparison.OrdinalIgnoreCase))
                return HfpPairDiscoveryResult.NotApplicable("Capture endpoint identity did not match the requested endpoint");
            if (!IsActiveCapture(capture))
                return HfpPairDiscoveryResult.NotApplicable("Capture endpoint is not an active capture endpoint");

            HfpTransportClassification transportClassification;
            try
            {
                if (!capture.TryGetTransportClassification(out transportClassification, out _))
                {
                    // An unreadable transport property is not enough to block
                    // an ordinary microphone from direct capture.
                    return HfpPairDiscoveryResult.NotApplicable(
                        "Automatic HFP pair discovery is not applicable",
                        HfpTransportClassification.Unknown);
                }
            }
            catch
            {
                return HfpPairDiscoveryResult.NotApplicable(
                    "Automatic HFP pair discovery is not applicable",
                    HfpTransportClassification.Unknown);
            }

            if (transportClassification != HfpTransportClassification.HfpCandidate)
            {
                // Mono/low-rate format is never a standalone HFP signal.
                return HfpPairDiscoveryResult.NotApplicable(
                    "Automatic HFP pair discovery is not applicable", transportClassification);
            }

            WaveFormat captureFormat;
            try
            {
                // Capture MixFormat is only used to recognize the existing
                // HFP-like mono/low-rate capture profile. It is never read on
                // a render endpoint.
                captureFormat = capture.MixFormat;
            }
            catch
            {
                return HfpPairDiscoveryResult.EvidenceFailure("Automatic HFP pair evidence failed");
            }
            if (!IsSupportedMonoFormat(captureFormat))
                return HfpPairDiscoveryResult.NotApplicable(
                    "Automatic HFP pair discovery is not applicable", transportClassification);

            Guid captureContainer;
            string captureContainerFailure;
            try
            {
                if (!capture.TryGetContainerId(out captureContainer, out captureContainerFailure))
                    return HfpPairDiscoveryResult.EvidenceFailure(
                        string.IsNullOrWhiteSpace(captureContainerFailure)
                            ? "Capture endpoint ContainerId evidence is unavailable"
                            : "Capture endpoint ContainerId query failed");
            }
            catch
            {
                return HfpPairDiscoveryResult.EvidenceFailure("Capture endpoint ContainerId query failed");
            }
            if (captureContainer == Guid.Empty)
                return HfpPairDiscoveryResult.EvidenceFailure("Capture endpoint ContainerId is empty");

            IReadOnlyList<IHfpEndpoint> renderEndpoints;
            try
            {
                renderEndpoints = enumerator.EnumerateRenderEndpoints();
            }
            catch
            {
                return HfpPairDiscoveryResult.EvidenceFailure("Active render endpoint enumeration failed");
            }
            foreach (var endpoint in renderEndpoints)
                ownership.Own(endpoint);

            var candidates = new List<string>();
            foreach (var candidate in renderEndpoints)
            {
                if (!IsActiveRender(candidate))
                    continue;

                Guid renderContainer;
                string renderContainerFailure;
                try
                {
                    if (!candidate.TryGetContainerId(out renderContainer, out renderContainerFailure))
                    {
                        return renderContainer == Guid.Empty
                            ? HfpPairDiscoveryResult.EvidenceFailure("Active render endpoint ContainerId is empty")
                            : HfpPairDiscoveryResult.EvidenceFailure("Active render endpoint ContainerId query failed");
                    }
                }
                catch
                {
                    return HfpPairDiscoveryResult.EvidenceFailure("Active render endpoint ContainerId query failed");
                }

                if (renderContainer == Guid.Empty)
                    return HfpPairDiscoveryResult.EvidenceFailure("Active render endpoint ContainerId is empty");

                if (renderContainer != captureContainer)
                    continue;

                if (string.IsNullOrWhiteSpace(candidate.EndpointId))
                    return HfpPairDiscoveryResult.EvidenceFailure("Matching render endpoint id is empty");

                candidates.Add(candidate.EndpointId);
            }

            return candidates.Count switch
            {
                0 => HfpPairDiscoveryResult.NoCandidate("No unique active render endpoint shared the capture ContainerId"),
                1 => HfpPairDiscoveryResult.Paired(candidates[0]),
                _ => HfpPairDiscoveryResult.Ambiguous(candidates.Count)
            };
        }
        catch
        {
            return HfpPairDiscoveryResult.EvidenceFailure(
                "Automatic HFP pair evidence failed", HfpTransportClassification.Unknown);
        }
        finally
        {
            ownership.Dispose();
            try { enumerator?.Dispose(); } catch { }
        }
    }

    private static bool IsActiveCapture(IHfpEndpoint endpoint)
        => endpoint.DataFlow == DataFlow.Capture && endpoint.State == DeviceState.Active;

    private static bool IsActiveRender(IHfpEndpoint endpoint)
        => endpoint.DataFlow == DataFlow.Render && endpoint.State == DeviceState.Active;

    private static bool IsSupportedMonoFormat(WaveFormat format)
        => format != null && format.Channels == 1 && format.SampleRate > 0 && format.SampleRate <= MaxSampleRate;

}
