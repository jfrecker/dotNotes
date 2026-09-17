namespace DotNotes.Core.Sharing;

/// <summary>
/// One entry of the on-disk <c>.nd-shares.json</c> store
/// (docs/06-DATA-MODEL.md's "Share tokens" section): a token's target
/// note path and optional expiry.
/// </summary>
public sealed class ShareEntry
{
    /// <summary>Vault-relative path of the shared note (e.g. <c>projects/idea.md</c>).</summary>
    public required string Path { get; init; }

    /// <summary>UTC expiry, or <see langword="null"/> if the share never expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}
