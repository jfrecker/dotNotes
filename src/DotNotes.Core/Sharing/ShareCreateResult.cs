namespace DotNotes.Core.Sharing;

/// <summary>
/// The result of creating a new share token, as returned by
/// <see cref="IShareTokenStore.CreateAsync"/>. Backs
/// <c>POST /api/share/{**path}</c>'s <c>{ token, url, qrCodePngBase64 }</c>
/// response, minus the URL/QR code themselves - those are built by the
/// endpoint, which alone knows the current request's scheme/host (see
/// docs/04-API-SPEC.md's Sharing section).
/// </summary>
public sealed class ShareCreateResult
{
    /// <summary>Opaque, unguessable token (24 random bytes, URL-safe base64).</summary>
    public required string Token { get; init; }

    /// <summary>Vault-relative path of the shared note.</summary>
    public required string Path { get; init; }

    /// <summary>UTC expiry, or <see langword="null"/> if the share never expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}
