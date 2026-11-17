using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgentRecorder.Mcp;

internal sealed class BindingStore(string filename)
{
    public string Filename { get; } = LocalPaths.Normalize(filename);
    public const int LockTimeoutMs = 1500;

    public static string DefaultFilename(string localApplicationData) =>
        LocalPaths.Normalize(Path.Combine(LocalPaths.Normalize(localApplicationData), "AgentRecorderIntegrations", "binding.json"));

    public void ValidateLocation(string packageRoot, string dataDir)
    {
        var directory = Path.GetDirectoryName(Filename)!;
        if (LocalPaths.Contains(packageRoot, directory) || LocalPaths.Contains(dataDir, directory))
            throw new ContractException("binding_location_conflict");
    }

    public Binding? Read()
    {
        LocalPaths.Normalize(Filename);
        if (!File.Exists(Filename)) return null;
        if (new FileInfo(Filename).Length > 16384) throw new ContractException("binding_corrupt");
        try
        {
            using var document = Json.Read(File.ReadAllText(Filename, new UTF8Encoding(false, true)));
            if (!document.RootElement.TryGetProperty("schema_version", out var schema) || schema.GetInt32() != 1)
                throw new ContractException("binding_schema_unsupported");
            var binding = document.RootElement.Deserialize<Binding>(Json.Options)!;
            if (binding.InitializationPolicy is not ("existing" or "initialize_new") ||
                binding.InitializationState is not ("initialized" or "pending" or "unknown") ||
                binding.HostEnsureState is not ("idle" or "unknown") ||
                string.IsNullOrEmpty(binding.DataDirectoryIdentity) ||
                binding.InitializationState == "initialized" && string.IsNullOrEmpty(binding.DatabaseIdentity) ||
                binding.ProductVersion != PackageValidator.SupportedVersion ||
                binding.InitializationPolicy == "existing" && binding.InitializationState == "pending" ||
                new[] { binding.PackageRoot, binding.AppPath, binding.CliPath, binding.DataDir }.Any(p =>
                    !string.Equals(p, LocalPaths.Normalize(p), StringComparison.Ordinal)))
                throw new ContractException("binding_corrupt");
            return binding;
        }
        catch (JsonException) { throw new ContractException("binding_corrupt"); }
        catch (DecoderFallbackException) { throw new ContractException("binding_corrupt"); }
        catch (InvalidOperationException) { throw new ContractException("binding_corrupt"); }
    }

    public static void Validate(Binding binding)
    {
        var package = PackageValidator.Validate(LocalPaths.Normalize(binding.PackageRoot));
        if (!LocalPaths.Equal(package.App, binding.AppPath) || !LocalPaths.Equal(package.Cli, binding.CliPath))
            throw new ContractException("binding_path_mismatch");
        LocalPaths.Normalize(binding.DataDir);
        if (binding.InitializationState == "unknown") throw new ContractException("initialization_unknown");
        if (binding.HostEnsureState == "unknown") throw new ContractException("host_start_unknown");
        if (!Directory.Exists(binding.DataDir)) throw new ContractException("data_directory_missing");
        if (FileIdentity.Read(binding.DataDir) != binding.DataDirectoryIdentity)
            throw new ContractException("data_directory_replaced");
        if (binding.InitializationState == "initialized")
        {
            if (RequireInitializedData(binding.DataDir) != binding.DatabaseIdentity)
                throw new ContractException("initialized_data_replaced");
        }
        else if (Directory.EnumerateFileSystemEntries(binding.DataDir).Any())
            throw new ContractException("pending_data_changed");
    }

    // Only observe files. The authoritative App/CLI creates/migrates/validates the business DB.
    public static string RequireInitializedData(string directory)
    {
        if (!Directory.Exists(directory)) throw new ContractException("data_directory_missing");
        var db = LocalPaths.Normalize(Path.Combine(directory, "state", "agent-recorder.db"));
        var key = LocalPaths.Normalize(Path.Combine(directory, "config", "api-key.txt"));
        if (!File.Exists(db) || new FileInfo(db).Length == 0 || !File.Exists(key) || new FileInfo(key).Length == 0)
            throw new ContractException("initialized_data_missing");
        using var file = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[16];
        if (file.Read(header) != 16 || !header.SequenceEqual("SQLite format 3\0"u8))
            throw new ContractException("initialized_data_invalid");
        return FileIdentity.Read(db);
    }

    public async Task<FileStream> AcquireAsync(CancellationToken cancellationToken)
    {
        LocalPaths.Normalize(Filename);
        Directory.CreateDirectory(Path.GetDirectoryName(Filename)!);
        var lockPath = LocalPaths.Normalize(Filename + ".lock");
        var timer = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (timer.ElapsedMilliseconds < LockTimeoutMs)
            { await Task.Delay(40, cancellationToken); }
            catch (IOException) { throw new ContractException("binding_busy"); }
        }
    }

    public void Write(Binding binding)
    {
        LocalPaths.Normalize(Filename);
        var temp = LocalPaths.Normalize(Filename + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, binding, Json.Options);
                file.Flush(true);
            }
            if (File.Exists(Filename)) File.Replace(temp, Filename, null);
            else File.Move(temp, Filename);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
