using System.Text.Json;
namespace AgentRecorder.Api;
public static class ApiResponse
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public static string Ok(object data, string reqId) =>
        JsonSerializer.Serialize(new { ok = true, data, request_id = reqId }, Json);

    public static string Err(string code, string message, object? details, string reqId) =>
        JsonSerializer.Serialize(new
        {
            ok = false,
            error = new { code, message, details = details ?? new { } },
            request_id = reqId
        }, Json);
}
