namespace AgentRecorder.Infrastructure;

/// <summary>The same FileAttributes/ReparsePoint metadata mechanism used by the
/// existing output-path policies, with no writeability probe or content read.</summary>
public interface IReadOnlyPathMetadata
{
    FileAttributes GetAttributes(string path);
    long GetLength(string path);
}

public sealed class ReadOnlyPathMetadata : IReadOnlyPathMetadata
{
    public static ReadOnlyPathMetadata Instance { get; } = new();
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public long GetLength(string path) => new FileInfo(path).Length;
}
