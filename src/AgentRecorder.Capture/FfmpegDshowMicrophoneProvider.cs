using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Enumerates microphone devices using FFmpeg's dshow device list.
/// Runs <c>ffmpeg -list_devices true -f dshow -i dummy</c> with a bounded timeout,
/// captures a limited stderr excerpt, and parses both the classic
/// "DirectShow audio devices" section and the FFmpeg 8.x tagged
/// <c>[in#0 ...] "Name" (audio)</c> format.
/// </summary>
public sealed class FfmpegDshowMicrophoneProvider : IMicrophoneDeviceProvider
{
    private readonly IExternalProcessRunner _runner;
    private readonly Func<string> _ffmpegPathProvider;
    private readonly TimeSpan _timeout;

    public FfmpegDshowMicrophoneProvider(
        IExternalProcessRunner? runner = null,
        Func<string>? ffmpegPathProvider = null,
        TimeSpan? timeout = null)
    {
        _runner = runner ?? new ExternalProcessRunner();
        _ffmpegPathProvider = ffmpegPathProvider ?? (() => FfmpegLocator.FfmpegPath);
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var ffmpegPath = _ffmpegPathProvider();
        var args = new[] { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" };

        ExternalProcessResult result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);
            result = await _runner.RunAsync(ffmpegPath, args, _timeout, captureStderr: true,
                stderrEncoding: Encoding.UTF8, cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MicrophoneEnumerationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new MicrophoneEnumerationException("device_enumeration_unavailable",
                "Microphone device enumeration failed.");
        }

        // Honor external cancellation even if the runner completed or timed out.
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        if (result.TimedOut)
            throw new MicrophoneEnumerationException("device_enumeration_timeout", "Microphone device enumeration timed out.");

        // Evidence-first, fail-safe: trust the parse result, not a single exit code.
        var parseResult = DshowAudioDeviceParser.Parse(result.Stderr);
        if (parseResult.Conclusion == DshowParseConclusion.Unrecognized)
            throw new MicrophoneEnumerationException("device_enumeration_unavailable", "Microphone device enumeration failed.");

        return parseResult.Devices;
    }
}

/// <summary>
/// Thrown when microphone device enumeration fails in a way that should be surfaced
/// to the API as a stable error code.
/// </summary>
public sealed class MicrophoneEnumerationException : Exception
{
    public string ErrorCode { get; }

    public MicrophoneEnumerationException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Result of parsing an FFmpeg dshow stderr listing.
/// Distinguishes "format recognized and complete" (with or without devices)
/// from "unrecognized or incomplete", so callers never treat a truncated
/// or unknown listing as an empty device list.
/// </summary>
internal enum DshowParseConclusion
{
    RecognizedWithDevices,
    RecognizedNoDevices,
    Unrecognized
}

/// <summary>
/// Structured result of <see cref="DshowAudioDeviceParser.Parse"/>.
/// </summary>
internal sealed class DshowParseResult
{
    public DshowParseConclusion Conclusion { get; }
    public IReadOnlyList<MicrophoneDeviceInfo> Devices { get; }

    private DshowParseResult(DshowParseConclusion conclusion, IReadOnlyList<MicrophoneDeviceInfo> devices)
    {
        Conclusion = conclusion;
        Devices = devices;
    }

    public static DshowParseResult WithDevices(IReadOnlyList<MicrophoneDeviceInfo> devices)
        => new(DshowParseConclusion.RecognizedWithDevices, devices);

    public static DshowParseResult NoDevices()
        => new(DshowParseConclusion.RecognizedNoDevices, Array.Empty<MicrophoneDeviceInfo>());

    public static DshowParseResult Unrecognized()
        => new(DshowParseConclusion.Unrecognized, Array.Empty<MicrophoneDeviceInfo>());
}

/// <summary>
/// Parses FFmpeg dshow stderr to extract audio input devices.
/// Supports both the classic "DirectShow audio devices" section and the
/// FFmpeg 8.x tagged <c>[in#0 ...] "Name" (audio)</c> format.
/// </summary>
internal static class DshowAudioDeviceParser
{
    private const string NoDevicesMarker = "Could not enumerate audio only devices (or none found).";

    public static DshowParseResult Parse(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return DshowParseResult.Unrecognized();

        var devices = new List<MicrophoneDeviceInfo>();
        var lines = stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        bool sawClassicHeader = false;
        bool sawTaggedAudio = false;
        bool sawNoAudioMarker = false;
        bool inAudioSection = false;
        bool sawIncompleteDeviceRecord = false;
        PendingName? pending = null;
        PendingName? pendingVideo = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            if (TrySplitLoggerPrefix(line, out var loggerLine))
            {
                ProcessTrustedLoggerLine(
                    loggerLine,
                    devices,
                    ref sawClassicHeader,
                    ref sawTaggedAudio,
                    ref sawNoAudioMarker,
                    ref inAudioSection,
                    ref sawIncompleteDeviceRecord,
                    ref pending,
                    ref pendingVideo);
            }
            else if (LooksLikeMalformedDeviceLoggerPrefix(line))
            {
                // A line that looks like it wanted to be [in#...] or [dshow...]
                // but has an invalid prefix. If its payload also looks like it
                // is attempting to express a device/alternative/section/no-devices
                // marker, fail the whole listing closed.
                var content = ExtractContentAfterLoggerBracket(line);
                if (LooksLikeDeviceRelatedPayload(content))
                {
                    MarkInterruptedAudioPending(ref sawIncompleteDeviceRecord, pending);
                    sawIncompleteDeviceRecord = true;
                    pending = null;
                    pendingVideo = null;
                }
            }
            // Lines without a recognized or malformed device logger prefix are
            // completely unrelated stderr and are ignored.
        }

        // End of input: any pending audio candidate without a matching alternative is incomplete.
        // A pending video candidate may be silently ignored because video records are not part
        // of the microphone result.
        if (pending != null)
            sawIncompleteDeviceRecord = true;

        if (sawIncompleteDeviceRecord)
            return DshowParseResult.Unrecognized();

        if (devices.Count > 0)
            return DshowParseResult.WithDevices(Deduplicate(devices));

        if (sawClassicHeader || sawTaggedAudio || sawNoAudioMarker)
            return DshowParseResult.NoDevices();

        return DshowParseResult.Unrecognized();
    }

    private static void ProcessTrustedLoggerLine(
        LoggerLine loggerLine,
        List<MicrophoneDeviceInfo> devices,
        ref bool sawClassicHeader,
        ref bool sawTaggedAudio,
        ref bool sawNoAudioMarker,
        ref bool inAudioSection,
        ref bool sawIncompleteDeviceRecord,
        ref PendingName? pending,
        ref PendingName? pendingVideo)
    {
        if (loggerLine.IsDshow)
        {
            ProcessDshowContent(loggerLine.Content, devices, ref sawClassicHeader, ref sawNoAudioMarker,
                ref inAudioSection, ref sawIncompleteDeviceRecord, ref pending, ref pendingVideo);
        }
        else if (loggerLine.IsInput)
        {
            ProcessInputContent(loggerLine.Content, loggerLine.InputKey, devices, ref sawTaggedAudio,
                ref sawNoAudioMarker, ref sawIncompleteDeviceRecord, ref pending, ref pendingVideo);
        }
    }

    private static void ProcessDshowContent(
        string content,
        List<MicrophoneDeviceInfo> devices,
        ref bool sawClassicHeader,
        ref bool sawNoAudioMarker,
        ref bool inAudioSection,
        ref bool sawIncompleteDeviceRecord,
        ref PendingName? pending,
        ref PendingName? pendingVideo)
    {
        // Classic section headers are only meaningful from the dshow logger.
        if (content.Equals("DirectShow audio devices", StringComparison.OrdinalIgnoreCase))
        {
            MarkInterruptedAudioPending(ref sawIncompleteDeviceRecord, pending);
            sawClassicHeader = true;
            inAudioSection = true;
            pending = null;
            pendingVideo = null;
            return;
        }

        if (content.Equals("DirectShow video devices", StringComparison.OrdinalIgnoreCase))
        {
            MarkInterruptedAudioPending(ref sawIncompleteDeviceRecord, pending);
            inAudioSection = false;
            pending = null;
            pendingVideo = null;
            return;
        }

        // Classic no-devices marker only counts when we are already inside a
        // classic audio section opened by a trusted dshow header.
        if (inAudioSection && content.Equals(NoDevicesMarker, StringComparison.OrdinalIgnoreCase))
        {
            MarkInterruptedAudioPending(ref sawIncompleteDeviceRecord, pending);
            if (devices.Count > 0)
                sawIncompleteDeviceRecord = true;
            sawNoAudioMarker = true;
            pending = null;
            pendingVideo = null;
            return;
        }

        if (!inAudioSection)
            return;

        var trimmed = content.TrimStart();

        // Classic friendly name: a quoted value and nothing else.
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            if (TryParseClassicFriendly(content, out var friendly))
            {
                MarkInterruptedAudioPending(ref sawIncompleteDeviceRecord, pending);
                pending = new PendingName(friendly, PendingSource.Classic, null);
            }
            else
            {
                MarkInterruptedAudioPending(ref sawIncompleteDeviceRecord, pending);
                sawIncompleteDeviceRecord = true;
                pending = null;
            }
            pendingVideo = null;
            return;
        }

        // Classic alternative name.
        if (trimmed.StartsWith("Alternative name", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseAlternative(content, out var alternative))
            {
                if (pending != null && pending.Source == PendingSource.Classic)
                {
                    devices.Add(new MicrophoneDeviceInfo(alternative, pending.Name, null, null));
                }
                else
                {
                    // An orphan alternative in a recognized listing indicates an incomplete record.
                    if (sawClassicHeader || sawNoAudioMarker)
                        sawIncompleteDeviceRecord = true;
                }
            }
            else
            {
                sawIncompleteDeviceRecord = true;
            }
            pending = null;
            pendingVideo = null;
            return;
        }

        // Any other dshow content inside the audio section that still contains
        // a quote or an audio/video tag looks like a malformed record attempt.
        if (content.Contains('\"') ||
            content.Contains("(audio)", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("(video)", StringComparison.OrdinalIgnoreCase))
        {
            sawIncompleteDeviceRecord = true;
            pending = null;
            pendingVideo = null;
            return;
        }

        // Remaining dshow content inside the audio section is ignored as
        // unrelated log noise (e.g. "dummy: Immediate exit requested").
    }

    private static void ProcessInputContent(
        string content,
        string inputKey,
        List<MicrophoneDeviceInfo> devices,
        ref bool sawTaggedAudio,
        ref bool sawNoAudioMarker,
        ref bool sawIncompleteDeviceRecord,
        ref PendingName? pending,
        ref PendingName? pendingVideo)
    {
        // Input logger no-devices marker from any trusted input logger.
        if (content.Equals(NoDevicesMarker, StringComparison.OrdinalIgnoreCase))
        {
            MarkInterruptedAudioPending(ref sawIncompleteDeviceRecord, pending);
            if (devices.Count > 0)
                sawIncompleteDeviceRecord = true;
            sawNoAudioMarker = true;
            pending = null;
            pendingVideo = null;
            return;
        }

        var trimmed = content.TrimStart();

        // Tagged friendly name: quoted value followed by exact (audio) or (video).
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            if (TryParseTaggedFriendly(content, out var name, out var isAudio))
            {
                if (isAudio)
                {
                    MarkInterruptedAudioPending(ref sawIncompleteDeviceRecord, pending);
                    MarkInterruptedAudioPending(ref sawIncompleteDeviceRecord, pendingVideo);
                    sawTaggedAudio = true;
                    pending = new PendingName(name, PendingSource.Tagged, inputKey);
                    pendingVideo = null;
                }
                else
                {
                    // A video friendly line replaces any previous video pending silently.
                    // If an audio candidate was still pending, the audio record is interrupted.
                    MarkInterruptedAudioPending(ref sawIncompleteDeviceRecord, pending);
                    pendingVideo = new PendingName(name, PendingSource.TaggedVideo, inputKey);
                    pending = null;
                }
            }
            else
            {
                MarkInterruptedAudioPending(ref sawIncompleteDeviceRecord, pending);
                sawIncompleteDeviceRecord = true;
                pending = null;
                pendingVideo = null;
            }
            return;
        }

        // Tagged alternative name.
        if (trimmed.StartsWith("Alternative name", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseAlternative(content, out var alternative))
            {
                if (pending != null && pending.Source == PendingSource.Tagged &&
                    pending.InputKey == inputKey)
                {
                    devices.Add(new MicrophoneDeviceInfo(alternative, pending.Name, null, null));
                }
                else if (pendingVideo != null && pendingVideo.Source == PendingSource.TaggedVideo &&
                    pendingVideo.InputKey == inputKey)
                {
                    // Complete and discard the tagged video record; it is not part of the result.
                }
                else
                {
                    // A true orphan alternative in a recognized tagged listing is incomplete.
                    if (sawTaggedAudio || sawNoAudioMarker)
                        sawIncompleteDeviceRecord = true;
                }
            }
            else
            {
                sawIncompleteDeviceRecord = true;
            }
            pending = null;
            pendingVideo = null;
            return;
        }

        // A trusted input logger line that contains an (audio)/(video) tag but
        // is not a well-formed friendly name is a malformed record attempt.
        if (content.Contains("(audio)", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("(video)", StringComparison.OrdinalIgnoreCase))
        {
            MarkInterruptedAudioPending(ref sawIncompleteDeviceRecord, pending);
            sawIncompleteDeviceRecord = true;
            pending = null;
            pendingVideo = null;
            return;
        }

        // Other input logger content is ignored.
    }

    private static void MarkInterruptedAudioPending(ref bool sawIncompleteDeviceRecord, PendingName? pending)
    {
        // Only an interrupted audio candidate is considered incomplete.
        // A pending video candidate may be replaced or dropped silently.
        if (pending != null && pending.Source != PendingSource.TaggedVideo)
            sawIncompleteDeviceRecord = true;
    }

    // ------------------------------------------------------------------
    // Logger prefix parsing
    // ------------------------------------------------------------------

    private readonly struct LoggerLine
    {
        public LoggerKind Kind { get; }
        public string InputKey { get; }
        public string Content { get; }

        public bool IsInput => Kind == LoggerKind.Input;
        public bool IsDshow => Kind == LoggerKind.Dshow;

        public LoggerLine(LoggerKind kind, string inputKey, string content)
        {
            Kind = kind;
            InputKey = inputKey;
            Content = content;
        }
    }

    private enum LoggerKind
    {
        None,
        Input,
        Dshow
    }

    /// <summary>
    /// Splits a line into a trusted logger prefix and its content.
    /// Accepts only <c>[in#N @ identity]</c> and <c>[dshow]</c> or
    /// <c>[dshow @ identity]</c> prefixes.
    /// </summary>
    private static bool TrySplitLoggerPrefix(string line, out LoggerLine result)
    {
        result = default;
        if (string.IsNullOrEmpty(line))
            return false;

        if (line[0] != '[')
            return false;

        int closeBracket = line.IndexOf(']');
        if (closeBracket < 0)
            return false;

        string prefix = line.Substring(1, closeBracket - 1);
        string content = line.Substring(closeBracket + 1).TrimStart();

        if (TryParseInputLoggerPrefix(prefix, out var inputKey))
        {
            result = new LoggerLine(LoggerKind.Input, inputKey, content);
            return true;
        }

        if (TryParseDshowLoggerPrefix(prefix))
        {
            result = new LoggerLine(LoggerKind.Dshow, string.Empty, content);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates <c>in#N @ identity</c> where N is one or more digits and
    /// identity is non-empty.
    /// </summary>
    private static bool TryParseInputLoggerPrefix(string prefix, out string inputKey)
    {
        inputKey = string.Empty;
        if (!prefix.StartsWith("in#", StringComparison.Ordinal))
            return false;

        int i = 3;
        if (i >= prefix.Length || !char.IsDigit(prefix[i]))
            return false;

        while (i < prefix.Length && char.IsDigit(prefix[i]))
            i++;

        // Whitespace before @.
        if (i >= prefix.Length || !char.IsWhiteSpace(prefix[i]))
            return false;
        i++;
        while (i < prefix.Length && char.IsWhiteSpace(prefix[i]))
            i++;

        if (i >= prefix.Length || prefix[i] != '@')
            return false;
        i++;

        // Whitespace after @.
        if (i >= prefix.Length || !char.IsWhiteSpace(prefix[i]))
            return false;
        i++;
        while (i < prefix.Length && char.IsWhiteSpace(prefix[i]))
            i++;

        // Non-empty identity.
        if (i >= prefix.Length)
            return false;

        inputKey = $"[{prefix}]";
        return true;
    }

    /// <summary>
    /// Validates <c>dshow</c> or <c>dshow @ identity</c> where identity is
    /// non-empty. Rejects <c>dshow-garbage</c>.
    /// </summary>
    private static bool TryParseDshowLoggerPrefix(string prefix)
    {
        if (prefix.Equals("dshow", StringComparison.Ordinal))
            return true;

        if (!prefix.StartsWith("dshow", StringComparison.Ordinal))
            return false;

        int i = 5;
        if (i >= prefix.Length || !char.IsWhiteSpace(prefix[i]))
            return false;
        i++;
        while (i < prefix.Length && char.IsWhiteSpace(prefix[i]))
            i++;

        if (i >= prefix.Length || prefix[i] != '@')
            return false;
        i++;

        if (i >= prefix.Length || !char.IsWhiteSpace(prefix[i]))
            return false;
        i++;
        while (i < prefix.Length && char.IsWhiteSpace(prefix[i]))
            i++;

        return i < prefix.Length;
    }

    /// <summary>
    /// Detects lines that begin like a device logger prefix but do not satisfy
    /// the strict grammar. Used to fail closed when such a line is clearly
    /// attempting to express a device record.
    /// </summary>
    private static bool LooksLikeMalformedDeviceLoggerPrefix(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("[in#", StringComparison.Ordinal))
            return !TrySplitLoggerPrefix(line, out _);
        if (trimmed.StartsWith("[dshow", StringComparison.Ordinal))
            return !TrySplitLoggerPrefix(line, out _);
        return false;
    }

    private static string ExtractContentAfterLoggerBracket(string line)
    {
        int closeBracket = line.IndexOf(']');
        if (closeBracket < 0)
            return line;
        return line.Substring(closeBracket + 1).TrimStart();
    }

    private static bool LooksLikeDeviceRelatedPayload(string content)
    {
        var trimmed = content.TrimStart();
        return trimmed.StartsWith("\"", StringComparison.Ordinal) ||
               trimmed.StartsWith("Alternative name", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("(audio)", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("(video)", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("DirectShow", StringComparison.OrdinalIgnoreCase) ||
               content.Contains(NoDevicesMarker, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Content parsing
    // ------------------------------------------------------------------

    /// <summary>
    /// Parses a strict FFmpeg 8.x tagged friendly line. Returns true only when
    /// the content is <c>"Friendly Name" (audio|video)</c> with no extra text.
    /// </summary>
    private static bool TryParseTaggedFriendly(string content, out string name, out bool isAudio)
    {
        name = null!;
        isAudio = false;

        if (!TryParseQuotedAtStart(content, out name, out var consumed))
            return false;

        var after = content.Substring(consumed).Trim();
        if (after.Equals("(audio)", StringComparison.OrdinalIgnoreCase))
        {
            isAudio = true;
            return true;
        }

        if (after.Equals("(video)", StringComparison.OrdinalIgnoreCase))
        {
            isAudio = false;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses a strict alternative-name content. Works for both tagged and
    /// classic alternative lines: <c>Alternative name "..."</c> with nothing
    /// after the quoted value.
    /// </summary>
    private static bool TryParseAlternative(string content, out string alternative)
    {
        alternative = null!;

        var trimmed = content.TrimStart();
        const string token = "Alternative name";
        if (!trimmed.StartsWith(token, StringComparison.Ordinal))
            return false;

        int afterToken = token.Length;
        if (afterToken >= trimmed.Length || !char.IsWhiteSpace(trimmed[afterToken]))
            return false;

        var rest = trimmed.Substring(afterToken).TrimStart();
        if (!TryParseQuotedAtStart(rest, out alternative, out var consumed))
            return false;

        return string.IsNullOrWhiteSpace(rest.Substring(consumed));
    }

    /// <summary>
    /// Parses a strict classic friendly name: a quoted value with nothing after it.
    /// </summary>
    private static bool TryParseClassicFriendly(string content, out string name)
    {
        name = null!;
        if (!TryParseQuotedAtStart(content, out name, out var consumed))
            return false;
        return string.IsNullOrWhiteSpace(content.Substring(consumed));
    }

    /// <summary>
    /// Extracts the first quoted substring starting at the first non-whitespace
    /// position, decoding FFmpeg <c>\"</c> and <c>\\</c> escapes. Returns the
    /// decoded value and the index immediately after the closing quote.
    /// </summary>
    private static bool TryParseQuotedAtStart(string text, out string value, out int consumed)
    {
        value = null!;
        consumed = 0;

        if (string.IsNullOrEmpty(text))
            return false;

        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;

        if (i >= text.Length || text[i] != '\"')
            return false;

        int openingQuote = i;
        i++;
        int closingQuote = FindUnescapedQuote(text, i);
        if (closingQuote < 0)
            return false;

        value = DecodeQuoted(text.Substring(openingQuote + 1, closingQuote - openingQuote - 1));
        consumed = closingQuote + 1;
        return true;
    }

    private static int FindUnescapedQuote(string line, int startIndex)
    {
        for (int i = startIndex; i < line.Length; i++)
        {
            if (line[i] == '\\')
            {
                i++; // skip escaped character
                continue;
            }

            if (line[i] == '\"')
                return i;
        }

        return -1;
    }

    private static string DecodeQuoted(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length && (raw[i + 1] == '\"' || raw[i + 1] == '\\'))
            {
                sb.Append(raw[i + 1]);
                i++;
            }
            else
            {
                sb.Append(raw[i]);
            }
        }

        return sb.ToString();
    }

    private static IReadOnlyList<MicrophoneDeviceInfo> Deduplicate(List<MicrophoneDeviceInfo> devices)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenNames = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<MicrophoneDeviceInfo>();

        foreach (var d in devices)
        {
            // IDs are FFmpeg dshow alternative names and must remain stable so
            // subsequent recording requests can open the same device. They are
            // expected to be unique; if not, keep the first occurrence defensively.
            if (!seenIds.Add(d.Id))
                continue;

            // Duplicate display names are common (e.g. "Microphone"). Disambiguate
            // the user-visible name without changing the underlying device ID.
            var originalName = d.Name;
            seenNames.TryGetValue(originalName, out var count);
            count++;
            seenNames[originalName] = count;
            var name = count == 1 ? originalName : $"{originalName} ({count})";

            result.Add(d with { Name = name });
        }

        return result;
    }

    private sealed class PendingName
    {
        public string Name { get; }
        public PendingSource Source { get; }
        public string? InputKey { get; }

        public PendingName(string name, PendingSource source, string? inputKey = null)
        {
            Name = name;
            Source = source;
            InputKey = inputKey;
        }
    }

    private enum PendingSource
    {
        Classic,
        Tagged,
        TaggedVideo
    }
}
