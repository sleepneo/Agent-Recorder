using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRecorder.Mcp;

internal sealed class ContractException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static JsonDocument Read(string value)
    {
        var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 });
        try { RejectDuplicates(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}

internal sealed record Binding
{
    public required int SchemaVersion { get; init; }
    public required string PackageRoot { get; init; }
    public required string AppPath { get; init; }
    public required string CliPath { get; init; }
    public required string DataDir { get; init; }
    public required string ProductVersion { get; init; }
    public required string InitializationPolicy { get; init; }
    public required string InitializationState { get; init; }
    public required string HostEnsureState { get; init; }
    public required string DataDirectoryIdentity { get; init; }
    public required string? DatabaseIdentity { get; init; }
}

internal interface IToolResult { bool Ok { get; } string ReasonCode { get; } }

internal sealed record Result(bool Ok, string Status, string ReasonCode, Binding? Binding = null,
    object? Host = null, string? ProductErrorCode = null, string? ProductErrorSource = null) : IToolResult
{
    public static Result Error(string code, string? status = null, Binding? binding = null) => new(false,
        status ?? (code is "initialization_unknown" or "host_start_unknown" ? "unknown" : "invalid"), code, binding);
}
