namespace AgentRecorder.Infrastructure;

// Stored evidence is data, never a receipt authorizing a new publication.
internal sealed record PublicationIdentityAnchor(string Path, long Length, ObservedFileIdentity Identity)
{
    internal const int Version = 1;
    internal const string Source = "staging_copy_handle/v1";
    internal bool Valid => WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(Path) && Length > 0 &&
        Identity is { VolumeSerialNumber: > 0 } && (Identity.IdLow != 0 || Identity.IdHigh != 0);
    public override string ToString() => "PublicationIdentityAnchor(private)";
}
