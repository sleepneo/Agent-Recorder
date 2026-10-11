using AgentRecorder.Infrastructure;

namespace AgentRecorder.Capture;

// Immutable, private handoff. No public constructor or serialization surface.
internal sealed record PublicationReceipt
{
    internal PublicationIdentityAnchor Anchor { get; }
    private PublicationReceipt(PublicationIdentityAnchor anchor) => Anchor = anchor;
    internal static PublicationReceipt? Sign(object authority, PublicationIdentityAnchor anchor) =>
        StagingToFinalPublisher.IsSigningAuthority(authority) && anchor.Valid ? new(anchor) : null;
    // Explicit trusted synthetic fixture point; never used by production readers.
    internal static PublicationReceipt IssueForTests(string path, long length, ObservedFileIdentity identity) =>
        new(new PublicationIdentityAnchor(path, length, identity));
    public override string ToString() => "PublicationReceipt(private)";
}
