using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRecorder.Mcp;

internal sealed partial class RecorderTools(BindingStore store)
{
    public async Task<Result> CallAsync(string name, IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
    {
        try
        {
            arguments ??= new Dictionary<string, JsonElement>();
            switch (name)
            {
                case "recorder_binding_get":
                    RequireArguments(arguments, []);
                    return Get();
                case "recorder_binding_set":
                    RequireArguments(arguments, ["package_root", "data_dir", "initialize_new_data_directory"]);
                    if (!arguments.TryGetValue("package_root", out var root) || root.ValueKind != JsonValueKind.String ||
                        !arguments.TryGetValue("data_dir", out var data) || data.ValueKind != JsonValueKind.String)
                        throw new ContractException("invalid_arguments");
                    var initialize = false;
                    if (arguments.TryGetValue("initialize_new_data_directory", out var intent))
                    {
                        if (intent.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw new ContractException("invalid_arguments");
                        initialize = intent.GetBoolean();
                    }
                    return await SetAsync(root.GetString()!, data.GetString()!, initialize, cancellationToken);
                case "recorder_host_ensure":
                    RequireArguments(arguments, []);
                    return await EnsureAsync(cancellationToken);
                default: throw new ContractException("unknown_tool");
            }
        }
        catch (ContractException error) { return Result.Error(error.Code); }
        catch (OperationCanceledException) { return Result.Error("request_cancelled", "unknown"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Result.Error("local_io_failed"); }
        catch { return Result.Error("metadata_invalid"); }
    }

    private static void RequireArguments(IDictionary<string, JsonElement> arguments, string[] allowed)
    {
        if (arguments.Keys.Except(allowed, StringComparer.Ordinal).Any()) throw new ContractException("invalid_arguments");
    }

    private Result Get()
    {
        var binding = store.Read();
        if (binding == null) return new(true, "unconfigured", "binding_unconfigured");
        try
        {
            store.ValidateLocation(binding.PackageRoot, binding.DataDir);
            BindingStore.Validate(binding);
            return new(true, "valid", "binding_valid", binding);
        }
        catch (ContractException error) { return Result.Error(error.Code, binding: binding); }
    }

    private async Task<Result> SetAsync(string rootValue, string dataValue, bool initialize, CancellationToken cancellationToken)
    {
        var root = LocalPaths.Normalize(rootValue);
        var data = LocalPaths.Normalize(dataValue);
        // Binding, lock and temp share this parent. Reject overlap before any mkdir/lock/write.
        store.ValidateLocation(root, data);
        var package = PackageValidator.Validate(root);
        if (LocalPaths.Equal(root, data) || data.StartsWith(root + "\\AgentRecorder.", StringComparison.OrdinalIgnoreCase) ||
            root.StartsWith(data + "\\", StringComparison.OrdinalIgnoreCase))
            throw new ContractException("data_package_overlap");
        using var lease = await store.AcquireAsync(cancellationToken);
        var previous = store.Read(); // Corrupt/unknown schemas must never be overwritten.
        if (initialize)
        {
            // Explicit new means truly new: no adoption of empty/replaced existing directories,
            // and no resetting a lost/unknown binding's initialization by repeating this flag.
            if (Directory.Exists(data) || File.Exists(data) || previous != null && LocalPaths.Equal(previous.DataDir, data))
                throw new ContractException("new_data_directory_required");
        }
        var databaseIdentity = initialize ? null : BindingStore.RequireInitializedData(data);
        if (initialize) Directory.CreateDirectory(data);
        var binding = new Binding { SchemaVersion = 1, PackageRoot = root, AppPath = package.App,
            CliPath = package.Cli, DataDir = data, ProductVersion = PackageValidator.SupportedVersion,
            InitializationPolicy = initialize ? "initialize_new" : "existing",
            InitializationState = initialize ? "pending" : "initialized", HostEnsureState = "idle",
            DataDirectoryIdentity = FileIdentity.Read(data), DatabaseIdentity = databaseIdentity };
        store.Write(binding);
        return new(true, "valid", initialize ? "binding_saved_pending_initialization" : "binding_saved", binding);
    }

    private async Task<Result> EnsureAsync(CancellationToken cancellationToken)
    {
        // Read before locking: an unconfigured ensure is also free of filesystem writes.
        var check = Get();
        if (!check.Ok) return check;
        if (check.Binding == null) return Result.Error("binding_unconfigured", "unconfigured");
        using var lease = await store.AcquireAsync(cancellationToken);
        var binding = store.Read() ?? throw new ContractException("binding_unconfigured");
        store.ValidateLocation(binding.PackageRoot, binding.DataDir);
        BindingStore.Validate(binding);
        cancellationToken.ThrowIfCancellationRequested();
        var uncertain = binding with { HostEnsureState = "unknown",
            InitializationState = binding.InitializationState == "pending" ? "unknown" : binding.InitializationState };
        // Durable intent precedes process submission, including a crash/EOF in this window.
        store.Write(uncertain);
        var outcome = await CliRunner.RunAsync(binding, cancellationToken);
        if (outcome.ReasonCode == "cli_not_started")
        {
            store.Write(binding);
            return Result.Error("cli_not_started", binding: binding);
        }
        if (outcome.ReasonCode != "cli_exited") return Result.Error(outcome.ReasonCode, "unknown", uncertain);
        try
        {
            using var document = Json.Read(outcome.Output!);
            var response = document.RootElement;
            if (!response.GetProperty("ok").GetBoolean())
            {
                var code = response.TryGetProperty("code", out var productCode) ? productCode.GetString() : null;
                return new(false, "unknown", "product_cli_rejected", uncertain, ProductErrorCode:
                    code != null && ProductCode().IsMatch(code) ? code : "UNRECOGNIZED_PRODUCT_ERROR",
                    ProductErrorSource: "recorder_cli");
            }
            if (outcome.ExitCode != 0 || response.GetProperty("status").GetString() != "ready" ||
                response.GetProperty("mode").GetString() != "tray" ||
                response.GetProperty("desktop_status").GetString() != "interactive" ||
                response.GetProperty("api_version").GetString() != "v1" ||
                response.GetProperty("pid").GetInt32() <= 0 || response.GetProperty("port").GetInt32() is < 1 or > 65535 ||
                !LocalPaths.Equal(response.GetProperty("ready_file").GetString()!, Path.Combine(binding.DataDir, "runtime", "ready.json")) ||
                !LocalPaths.Equal(response.GetProperty("data_dir").GetString()!, binding.DataDir) ||
                !LocalPaths.Equal(response.GetProperty("api_key_file").GetString()!, Path.Combine(binding.DataDir, "config", "api-key.txt")))
                return Result.Error("cli_evidence_invalid_unknown", "unknown", uncertain);
            var started = response.GetProperty("started").GetBoolean();
            if (FileIdentity.Read(binding.DataDir) != binding.DataDirectoryIdentity)
                return Result.Error("data_directory_replaced", "unknown", uncertain);
            var databaseIdentity = BindingStore.RequireInitializedData(binding.DataDir);
            if (binding.DatabaseIdentity != null && binding.DatabaseIdentity != databaseIdentity)
                return Result.Error("initialized_data_replaced", "unknown", uncertain);
            var initialized = binding with { InitializationState = "initialized", HostEnsureState = "idle", DatabaseIdentity = databaseIdentity };
            store.Write(initialized);
            return new(true, "ready", "host_ready_cli_verified", initialized, new
            {
                pid = response.GetProperty("pid").GetInt32(),
                port = response.GetProperty("port").GetInt32(),
                started, mode = "tray",
                readiness_api_version = "v1", product_version = PackageValidator.SupportedVersion,
                evidence_source = "bound_cli_identity_and_capabilities_validation",
                desktop_status = "interactive", human_window_visibility = "not_verified",
                capabilities = new { api_readiness = "cli_verified", capture = "not_checked", recording_tools = false }
            });
        }
        catch { return Result.Error("cli_evidence_invalid_unknown", "unknown", uncertain); }
    }

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,79}$")]
    private static partial Regex ProductCode();
}
