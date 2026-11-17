using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRecorder.Mcp.Tests;

// Equivalent restricted parsing for this bundled native server, not the Codex loader.
internal sealed record PluginStdioLaunch(string Command, string WorkingDirectory)
{
    public static PluginStdioLaunch Read(string pluginRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(pluginRoot, "mcp.json")));
        var servers = document.RootElement.GetProperty("mcpServers");
        if (servers.EnumerateObject().Count() != 1) throw new InvalidDataException("PLUGIN_COMMAND_INVALID");
        var entry = servers.GetProperty("agent-recorder-bootstrap");
        if (entry.GetProperty("type").GetString() != "stdio" || entry.GetProperty("args").GetArrayLength() != 0)
            throw new InvalidDataException("PLUGIN_COMMAND_INVALID");
        return Resolve(pluginRoot, entry.GetProperty("command").GetString()!,
            entry.TryGetProperty("cwd", out var cwd) ? cwd.GetString() : null);
    }

    public static PluginStdioLaunch Resolve(string root, string command, string? cwd)
    {
        if (!command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("PLUGIN_COMMAND_INVALID");
        var executable = Contained(root, command, directory: false);
        // Expansion belongs to cwd, never command. No PLUGIN_DATA or PATH use is needed here.
        if (cwd is null or "${PLUGIN_ROOT}") cwd = "./";
        else if (cwd.StartsWith("${PLUGIN_ROOT}/", StringComparison.Ordinal)) cwd = "./" + cwd["${PLUGIN_ROOT}/".Length..];
        return new(executable, Contained(root, cwd, directory: true));
    }

    private static string Contained(string root, string reference, bool directory)
    {
        var code = directory ? "PLUGIN_CWD_INVALID" : "PLUGIN_COMMAND_INVALID";
        if (!reference.StartsWith("./", StringComparison.Ordinal)) throw new InvalidDataException(code);
        var relative = reference[2..];
        var parts = directory && relative.Length == 0 ? [] : relative.Split('/');
        if (parts.Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
            part.Any(c => c < 32 || "\\:<>\"|?*$%{}".Contains(c)) ||
            Regex.IsMatch(part, "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\\.)", RegexOptions.IgnoreCase)))
            throw new InvalidDataException(code);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var resolved = Path.GetFullPath(parts.Aggregate(root, Path.Combine));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!string.Equals(root, resolved, StringComparison.OrdinalIgnoreCase) && !resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(code);
        for (string? cursor = resolved; cursor != null; cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(code);
        if (directory ? !Directory.Exists(resolved) : !File.Exists(resolved)) throw new InvalidDataException(code);
        return resolved;
    }
}
