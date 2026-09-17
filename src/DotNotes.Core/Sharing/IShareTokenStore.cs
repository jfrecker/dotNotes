namespace DotNotes.Core.Sharing;

/// <summary>
/// Persists token -&gt; (note path, optional expiry) mappings backing the
/// share-link feature (docs/04-API-SPEC.md's Sharing section,
/// docs/06-DATA-MODEL.md's "Share tokens" section). Not a database - a
/// small, regenerable JSON side-file (<c>.nd-shares.json</c> in the vault
/// root); a missing or corrupt file is treated as "no shares yet", never
/// as an error, per CLAUDE.md's "index/cache, never source of truth" rule.
/// </summary>
public interface IShareTokenStore
{
    /// <summary>
    /// Creates a new, opaque, unguessable share token for
    /// <paramref name="notePath"/> (a vault-relative note path - this
    /// store does not itself validate that a note exists at that path;
    /// callers are expected to have already checked, e.g. via
    /// <c>INoteRepository.ExistsAsync</c>, before calling this).
    /// <paramref name="expiresInDays"/>, when a positive number, sets an
    /// absolute UTC expiry that many days from now; <see langword="null"/>
    /// or a non-positive value means "never expires".
    /// </summary>
    Task<ShareCreateResult> CreateAsync(string notePath, int? expiresInDays, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves <paramref name="token"/> to its target note path, or
    /// <see langword="null"/> if the token is missing, was revoked, or has
    /// expired. These three cases are deliberately indistinguishable to
    /// callers (docs/06-DATA-MODEL.md), so a share link can't be probed to
    /// learn which case applies.
    /// </summary>
    Task<string?> ResolveAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes <paramref name="token"/>, if it exists. Idempotent: revoking
    /// an already-revoked or never-issued token is not an error. The
    /// returned bool is informational only - callers map every outcome to
    /// the same <c>204 No Content</c>, matching the existing
    /// <c>DELETE /api/notes/{path}</c> convention.
    /// </summary>
    Task<bool> RevokeAsync(string token, CancellationToken cancellationToken = default);
}
