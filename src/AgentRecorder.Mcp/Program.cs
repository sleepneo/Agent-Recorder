using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AgentRecorder.Mcp;

try
{
    string bindingFile;
    if (args.Length == 0)
        bindingFile = BindingStore.DefaultFilename(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    else if (args.Length == 2 && args[0] == "--binding-file") bindingFile = args[1];
    else throw new ContractException("invalid_process_arguments");
    var bindingStore = new BindingStore(bindingFile);
    var tools = new RecorderTools(bindingStore);
    await using var operations = new OperationManager(bindingStore, tools);
    var definitions = new List<Tool>
    {
        new() { Name = "recorder_binding_get", Description = "Read the stable binding without writing or starting Recorder.",
            InputSchema = Schema("{}", "[]"), Annotations = new() { ReadOnlyHint = true, OpenWorldHint = false } },
        new() { Name = "recorder_binding_set", Description = "Bind an explicitly supplied external Recorder 0.1.25 portable package and data directory. New data preparation requires explicit user intent. Does not start Recorder.",
            InputSchema = Schema("""
            {"package_root":{"type":"string","minLength":4},"data_dir":{"type":"string","minLength":4},
             "initialize_new_data_directory":{"type":"boolean","default":false}}
            """, "[\"package_root\",\"data_dir\"]"),
            Annotations = new() { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = false, OpenWorldHint = false } },
        new() { Name = "recorder_host_ensure", Description = "Explicitly start/reuse the bound tray App through its CLI; fixed 10s CLI/15s observation budgets. Unknown outcomes must not be automatically retried. No recording, approval or shutdown.",
            InputSchema = Schema("{}", "[]"),
            Annotations = new() { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = false, OpenWorldHint = false } },
        new() { Name = "recorder_record_region", Description = "Admit one silent selected-region short recording with an explicit caller-generated operation UUID. Local selection/approval remain required. Returns local acceptance promptly; query the same operation_id, never auto-resubmit an unknown outcome. Startup observation has a separate 15s budget.",
            InputSchema = Schema("""
            {"operation_id":{"type":"string","format":"uuid","pattern":"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"},
             "duration_seconds":{"type":"integer","minimum":1,"maximum":30,"default":10}}
            """, "[\"operation_id\"]"),
            Annotations = new() { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false } },
        new() { Name = "recorder_operation_get", Description = "Read one operation by its original UUID and query only its mapped native recording/output. Never starts a host or submits selection. Unknown/not_found is not permission to create another operation.",
            InputSchema = Schema("""{"operation_id":{"type":"string","format":"uuid","pattern":"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"}}""", "[\"operation_id\"]"),
            Annotations = new() { ReadOnlyHint = true, OpenWorldHint = false } },
        new() { Name = "recorder_recording_stop", Description = "Only on explicit user stop intent: stop exactly the native recording_id already mapped to this operation_id on the same bound host generation. Nine-second observation budget, durable at-most-once submission. Unknown outcomes are queried, never resubmitted. Acceptance is not saved-video completion.",
            InputSchema = Schema("""
            {"operation_id":{"type":"string","format":"uuid","pattern":"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"},
             "recording_id":{"type":"string","pattern":"^rec_[0-9a-f]{12}$"}}
            """, "[\"operation_id\",\"recording_id\"]"),
            Annotations = new() { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false } }
    };
    var options = new McpServerOptions
    {
        ServerInfo = new() { Name = "agent-recorder-bootstrap", Version = "0.3.2" },
        Capabilities = new() { Tools = new() },
        Handlers = new()
        {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = definitions }),
            CallToolHandler = async (request, cancellationToken) =>
            {
                var name = request.Params!.Name;
                IToolResult result = name == "recorder_recording_stop" ? await operations.StopCallAsync(request.Params.Arguments, cancellationToken)
                    : name is "recorder_record_region" or "recorder_operation_get"
                    ? await operations.CallAsync(name, request.Params.Arguments, cancellationToken)
                    : await tools.CallAsync(name, request.Params.Arguments, cancellationToken);
                return new CallToolResult
                {
                    IsError = !result.Ok,
                    StructuredContent = JsonSerializer.SerializeToElement(result, result.GetType(), Json.Options),
                    Content = [new TextContentBlock { Text = result.ReasonCode }]
                };
            }
        }
    };
    await using var server = McpServer.Create(new StdioServerTransport("agent-recorder-bootstrap"), options);
    await server.RunAsync();
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error is ContractException contract ? contract.Code : "adapter_terminated");
    return 1;
}

static JsonElement Schema(string properties, string required) => JsonDocument.Parse(
    $$"""{"type":"object","properties":{{properties}},"required":{{required}},"additionalProperties":false}""").RootElement.Clone();
