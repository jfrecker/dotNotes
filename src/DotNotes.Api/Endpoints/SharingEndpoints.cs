using System.Text.Json;
using DotNotes.Api.Sharing;
using DotNotes.Core.Config;
using DotNotes.Core.Notes;
using DotNotes.Core.Sharing;
using Microsoft.Extensions.Options;

namespace DotNotes.Api.Endpoints;

/// <summary>
/// Wires up docs/04-API-SPEC.md's Sharing section:
/// <c>POST /api/share/{**path}</c>, <c>DELETE /api/share/{token}</c>,
/// <c>GET /shared/{token}</c> - plus one endpoint not in that doc's table
/// yet, <c>GET /api/share/{token}/content</c>, a narrow token-scoped,
/// read-only content endpoint that <c>wwwroot/js/shared.js</c> calls
/// instead of the main (unauthenticated) <c>/api/notes/*</c> surface. See
/// this phase's report for why that endpoint exists.
/// </summary>
/// <remarks>
/// All four endpoints here are gated on <see cref="SharingOptions.Enabled"/>:
/// when sharing is disabled, every one of them returns the exact same
/// generic 404 shape as an unresolvable token (<see cref="ShareNotFound"/>),
/// so a caller can't distinguish "sharing is off" from "this token never
/// existed" - see docs/06-DATA-MODEL.md's "these cases are deliberately
/// indistinguishable" note on <see cref="IShareTokenStore.ResolveAsync"/>.
/// </remarks>
public static class SharingEndpoints
{
    public static WebApplication MapSharingEndpoints(this WebApplication app)
    {
        app.MapPost("/api/share/{**path}", PostShareAsync);
        app.MapDelete("/api/share/{token}", DeleteShareAsync);
        app.MapGet("/api/share/{token}/content", GetShareContentAsync);
        app.MapGet("/shared/{token}", GetSharedPageAsync);

        return app;
    }

    private static async Task<IResult> PostShareAsync(
        string path,
        HttpRequest request,
        INoteRepository noteRepository,
        IShareTokenStore shareTokenStore,
        IOptions<SharingOptions> sharingOptions,
        CancellationToken cancellationToken)
    {
        if (!sharingOptions.Value.Enabled)
        {
            return ShareNotFound();
        }

        CreateShareRequest? body = null;
        if (request.ContentLength is > 0)
        {
            try
            {
                // A body is optional here (`{ expiresInDays? }`) - only
                // attempt to parse one if the caller actually sent one, so
                // a bodyless POST (no expiry) isn't rejected as malformed JSON.
                body = await request.ReadFromJsonAsync<CreateShareRequest>(cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return BadRequest("invalid_body", ex.Message);
            }
        }

        bool noteExists;
        try
        {
            noteExists = await noteRepository.ExistsAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidNotePathException ex)
        {
            return InvalidPath(ex);
        }
        catch (VaultUnavailableException ex)
        {
            return VaultUnavailable(ex);
        }

        if (!noteExists)
        {
            return NoteNotFound(path);
        }

        // Normalize backslash separators the same way FileSystemNoteRepository
        // would, so the persisted share path is always forward-slash, even
        // though ExistsAsync above only returned a bool (not a normalized
        // path) - see this phase's report for why ExistsAsync, specifically,
        // is used for the existence check.
        var normalizedNotePath = path.Replace('\\', '/');

        var result = await shareTokenStore
            .CreateAsync(normalizedNotePath, body?.ExpiresInDays, cancellationToken)
            .ConfigureAwait(false);

        var shareUrl = $"{request.Scheme}://{request.Host}/shared/{result.Token}";
        var qrCodePngBase64 = QrCodeGenerator.GeneratePngBase64(shareUrl);

        return Results.Ok(new ShareCreatedResponse(result.Token, shareUrl, qrCodePngBase64));
    }

    private static async Task<IResult> DeleteShareAsync(
        string token,
        IShareTokenStore shareTokenStore,
        IOptions<SharingOptions> sharingOptions,
        CancellationToken cancellationToken)
    {
        if (!sharingOptions.Value.Enabled)
        {
            return ShareNotFound();
        }

        // Idempotent, matching the existing DELETE /api/notes/{path}
        // convention: whether or not the token existed, the
        // post-condition ("this token no longer resolves") holds once
        // this returns, so both cases return 204.
        await shareTokenStore.RevokeAsync(token, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> GetShareContentAsync(
        string token,
        IShareTokenStore shareTokenStore,
        INoteRepository noteRepository,
        IOptions<SharingOptions> sharingOptions,
        CancellationToken cancellationToken)
    {
        if (!sharingOptions.Value.Enabled)
        {
            return ShareNotFound();
        }

        var notePath = await shareTokenStore.ResolveAsync(token, cancellationToken).ConfigureAwait(false);
        if (notePath is null)
        {
            return ShareNotFound();
        }

        // The note may have been deleted/moved after the share was
        // created; treat that the same as an invalid token rather than
        // leaking a distinct error, and rather than throwing.
        NoteContent? note;
        try
        {
            note = await noteRepository.GetAsync(notePath, cancellationToken).ConfigureAwait(false);
        }
        catch (VaultUnavailableException ex)
        {
            return VaultUnavailable(ex);
        }

        if (note is null)
        {
            return ShareNotFound();
        }

        return Results.Ok(new SharedNoteContentResponse(note.Path, note.Content, note.UpdatedAt));
    }

    private static async Task<IResult> GetSharedPageAsync(
        string token,
        IShareTokenStore shareTokenStore,
        IOptions<SharingOptions> sharingOptions,
        IWebHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (!sharingOptions.Value.Enabled)
        {
            return ShareNotFound();
        }

        // Confirm the token itself resolves *before* serving the page
        // shell, so an invalid token still 404s here instead of serving
        // shared.html regardless (per this phase's brief).
        var notePath = await shareTokenStore.ResolveAsync(token, cancellationToken).ConfigureAwait(false);
        if (notePath is null)
        {
            return ShareNotFound();
        }

        var sharedHtmlPath = Path.Combine(environment.WebRootPath, "shared.html");
        return Results.File(sharedHtmlPath, "text/html");
    }

    private static IResult NoteNotFound(string path) =>
        Results.Json(
            new ErrorResponse("not_found", $"No note exists at '{path}'."),
            statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// The single 404 shape used for every "this token/feature isn't
    /// available" case across all four sharing endpoints - see this
    /// type's remarks for why it must not vary by cause.
    /// </summary>
    private static IResult ShareNotFound() =>
        Results.Json(
            new ErrorResponse("not_found", "Share not found."),
            statusCode: StatusCodes.Status404NotFound);

    private static IResult InvalidPath(InvalidNotePathException ex) =>
        Results.Json(
            new ErrorResponse("invalid_path", ex.Message),
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// The vault root itself is currently missing on disk - see
    /// <see cref="VaultUnavailableException"/>'s remarks and
    /// <c>NotesEndpoints.VaultUnavailable</c>'s matching helper.
    /// </summary>
    private static IResult VaultUnavailable(VaultUnavailableException ex) =>
        Results.Json(
            new ErrorResponse("vault_unavailable", ex.Message),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult BadRequest(string error, string? detail) =>
        Results.Json(
            new ErrorResponse(error, detail),
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Standard error shape per docs/04-API-SPEC.md's Conventions section.</summary>
    private sealed record ErrorResponse(string Error, string? Detail = null);

    /// <summary>Request body shape for <c>POST /api/share/{**path}</c>: <c>{ expiresInDays? }</c>.</summary>
    private sealed record CreateShareRequest(int? ExpiresInDays);

    /// <summary>Backs <c>POST /api/share/{**path}</c>'s <c>{ token, url, qrCodePngBase64 }</c> response.</summary>
    private sealed record ShareCreatedResponse(string Token, string Url, string QrCodePngBase64);

    /// <summary>Backs the token-scoped <c>GET /api/share/{token}/content</c>'s <c>{ path, content, updatedAt }</c> response.</summary>
    private sealed record SharedNoteContentResponse(string Path, string Content, DateTimeOffset UpdatedAt);
}
