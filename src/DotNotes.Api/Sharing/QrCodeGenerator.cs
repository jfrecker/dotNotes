using QRCoder;

namespace DotNotes.Api.Sharing;

/// <summary>
/// Generates a QR code PNG (base64-encoded) for a share URL, backing
/// <c>POST /api/share/{**path}</c>'s <c>qrCodePngBase64</c> response
/// field (docs/04-API-SPEC.md's Sharing section).
/// </summary>
/// <remarks>
/// Lives in <c>DotNotes.Api</c>, not <c>DotNotes.Core</c>: the
/// <c>QRCoder</c> NuGet package itself has no ASP.NET Core dependency (so
/// either project would have been technically valid, per CLAUDE.md), but
/// "render this HTTP-facing share URL as a PNG for an HTTP JSON response"
/// is a presentation-layer concern tied to the endpoint, not vault/domain
/// logic that <c>DotNotes.Core</c> or a future MCP tool would ever need
/// independently of the REST API.
/// <para>
/// Uses <see cref="PngByteQRCode"/> specifically (not the
/// <c>System.Drawing</c>-based renderers QRCoder also ships) so this
/// stays free of a <c>libgdiplus</c> dependency at runtime - required per
/// CLAUDE.md's "runs in Docker on WSL2/Linux" hard constraint, even
/// though <c>QRCoder</c>'s own package happens to pull in
/// <c>System.Drawing.Common</c> transitively for its *other* renderers.
/// </para>
/// </remarks>
public static class QrCodeGenerator
{
    /// <summary>Pixels per QR module - a middle-ground size that stays crisp when displayed at a few hundred px wide.</summary>
    private const int PixelsPerModule = 10;

    public static string GeneratePngBase64(string content)
    {
        using var generator = new QRCodeGenerator();
        using var qrCodeData = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var pngQrCode = new PngByteQRCode(qrCodeData);
        var pngBytes = pngQrCode.GetGraphic(PixelsPerModule);
        return Convert.ToBase64String(pngBytes);
    }
}
