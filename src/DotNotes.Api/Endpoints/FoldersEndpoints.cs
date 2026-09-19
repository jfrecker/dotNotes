using System.Text.Json;
using DotNotes.Core.Notes;
using DotNotes.Core.Reorganization;

namespace DotNotes.Api.Endpoints;

/// <summary>
/// Wires up the folder-management rows of docs/04-API-SPEC.md's Notes
/// section: <c>POST /api/folders/{**path}</c> (create, <c>mkdir -p</c>
/// semantics), <c>POST /api/folders/{**path}/move</c> (move/rename a
/// folder and everything inside it) and
/// <c>DELETE /api/folders/{**path}</c> (delete a folder and everything
/// inside it). Endpoints here are intentionally thin - all path
/// validation and file I/O lives in
/// <see cref="INoteRepository"/> (DotNotes.Core); this file only
/// translates HTTP in/out to/from that contract.
/// </summary>
public static class FoldersEndpoints
{
    public static WebApplication MapFoldersEndpoints(this WebApplication app)
    {
        app.MapPost("/api/folders/{**path}", PostFolderRouteAsync);
        app.MapDelete("/api/folders/{**path}", DeleteFolderAsync);

        return app;
    }

    /// <summary>Trailing route segment recognized by <see cref="PostFolderRouteAsync"/> - see its remarks.</summary>
    private const string MoveRouteSuffix = "/move";

    /// <summary>
    /// Single dispatch point for both POST verbs living under
    /// <c>/api/folders/{**path}</c>: plain create (no suffix) and
    /// <c>.../move</c>.
    /// </summary>
    /// <remarks>
    /// ASP.NET Core's route pattern parser throws a <c>RoutePatternException</c>
    /// at startup ("A catch-all parameter can only appear as the last
    /// segment of the route template") for any route template with a
    /// literal segment after a catch-all parameter - so
    /// <c>MapPost("/api/folders/{**path}/move", ...)</c> cannot be
    /// registered as its own route as written in docs/04-API-SPEC.md's
    /// table (verified experimentally; see
    /// <c>NotesEndpoints.PostNoteRouteAsync</c>'s matching remarks for the
    /// same technique applied to <c>POST /api/notes/{**path}/move</c>).
    /// This preserves the exact same externally-observable URL/verb/body/
    /// response contract by mapping POST once to the same catch-all shape
    /// and having the handler itself recognize the trailing "/move"
    /// segment - purely an internal dispatch technique, not a deviation
    /// from the documented contract.
    /// <para>
    /// One accepted edge case from this technique: a folder whose own
    /// name is literally "move" (e.g. creating <c>projects/move</c> via
    /// <c>POST /api/folders/projects/move</c>) is indistinguishable from a
    /// request to move folder <c>projects</c>, and is dispatched as a
    /// move attempt instead - it will 400 with <c>invalid_request</c>
    /// (no <c>destinationPath</c> in the body) rather than creating that
    /// folder. This is an inherent consequence of docs/04-API-SPEC.md
    /// defining both operations as catch-all-plus-suffix routes, which
    /// ASP.NET Core's routing does not support natively; a folder named
    /// "move" can still be created indirectly (e.g. via
    /// <c>PUT /api/notes/projects/move/note.md</c>, which auto-creates
    /// parent folders).
    /// </para>
    /// </remarks>
    private static Task<IResult> PostFolderRouteAsync(
        string path,
        HttpRequest request,
        INoteRepository noteRepository,
        IVaultReorganizationService reorganizationService,
        CancellationToken cancellationToken)
    {
        if (path.EndsWith(MoveRouteSuffix, StringComparison.Ordinal))
        {
            var sourcePath = path[..^MoveRouteSuffix.Length];
            return MoveFolderAsync(sourcePath, request, reorganizationService, cancellationToken);
        }

        return CreateFolderAsync(path, noteRepository, cancellationToken);
    }

    private static async Task<IResult> CreateFolderAsync(
        string path,
        INoteRepository noteRepository,
        CancellationToken cancellationToken)
    {
        try
        {
            var createdPath = await noteRepository.CreateFolderAsync(path, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new FolderResponse(createdPath));
        }
        catch (DestinationAlreadyExistsException ex)
        {
            return AlreadyExists(ex);
        }
        catch (InvalidNotePathException ex)
        {
            return InvalidPath(ex);
        }
        catch (VaultUnavailableException ex)
        {
            return VaultUnavailable(ex);
        }
    }

    private static async Task<IResult> MoveFolderAsync(
        string sourcePath,
        HttpRequest request,
        IVaultReorganizationService reorganizationService,
        CancellationToken cancellationToken)
    {
        MoveFolderRequest? body;
        try
        {
            // Same manual-parse pattern as NotesEndpoints.PutNoteAsync/
            // MoveNoteAsync: a missing/malformed body maps to this app's
            // own {error, detail} shape rather than ASP.NET Core's
            // built-in 400 for a body-binding failure.
            body = await request.ReadFromJsonAsync<MoveFolderRequest>(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return BadRequest("invalid_body", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // Thrown by ReadFromJsonAsync when the request has no body,
            // or a Content-Type that isn't JSON.
            return BadRequest("invalid_body", ex.Message);
        }

        if (body?.DestinationPath is null)
        {
            return BadRequest("invalid_request", "Request body must include a 'destinationPath' string.");
        }

        FolderMoveResult result;
        try
        {
            result = await reorganizationService
                .MoveFolderAsync(sourcePath, body.DestinationPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SourceNotFoundException ex)
        {
            return FolderNotFound(ex.SourcePath);
        }
        catch (DestinationAlreadyExistsException ex)
        {
            return AlreadyExists(ex);
        }
        catch (InvalidNotePathException ex)
        {
            // Also covers "destination is the source itself or one of its
            // own descendants" per INoteRepository.MoveFolderAsync's
            // remarks - the repository already throws this same type for
            // that case, no special-casing needed here.
            return InvalidPath(ex);
        }
        catch (VaultUnavailableException ex)
        {
            return VaultUnavailable(ex);
        }

        // IVaultReorganizationService already left the link and search
        // indexes reflecting the move (and any wikilink rewrites) before
        // returning - see its remarks and docs/06-DATA-MODEL.md's "Folder &
        // note move/rename" section - so no rebuild is needed here.
        return Results.Ok(new FolderMoveResponse(result.Path, result.RewrittenNotes));
    }

    /// <summary>
    /// <c>DELETE /api/folders/{**path}</c> - removes the folder and
    /// everything inside it.
    /// </summary>
    /// <remarks>
    /// Unlike <c>DELETE /api/notes/{**path}</c> (which is deliberately
    /// idempotent and always 204s), a missing folder is reported as
    /// <c>404 not_found</c>: this is a destructive, recursive operation
    /// driven from a confirmation prompt in the UI, so "there was nothing
    /// there" is information the caller wants rather than noise to
    /// swallow. Path safety (no traversal, no escaping the vault, and the
    /// vault root itself never deletable) is enforced inside
    /// <see cref="INoteRepository.DeleteFolderAsync"/>, which surfaces a
    /// <see cref="InvalidNotePathException"/> mapped to <c>400</c> here.
    /// </remarks>
    private static async Task<IResult> DeleteFolderAsync(
        // Nullable so `DELETE /api/folders/` (an empty catch-all, i.e. the
        // vault root itself) reaches this handler and is answered with a
        // deliberate 400, rather than failing minimal-API parameter
        // binding for a required `string` before the handler ever runs.
        string? path,
        INoteRepository noteRepository,
        CancellationToken cancellationToken)
    {
        try
        {
            // A null/empty path names the vault root; DeleteFolderAsync
            // rejects it as an invalid path, same as any other unsafe one.
            var deleted = await noteRepository.DeleteFolderAsync(path ?? string.Empty, cancellationToken).ConfigureAwait(false);
            return deleted ? Results.NoContent() : FolderNotFound(path!);
        }
        catch (InvalidNotePathException ex)
        {
            return InvalidPath(ex);
        }
        catch (VaultUnavailableException ex)
        {
            return VaultUnavailable(ex);
        }
    }

    private static IResult FolderNotFound(string path) =>
        Results.Json(
            new ErrorResponse("not_found", $"No folder exists at '{path}'."),
            statusCode: StatusCodes.Status404NotFound);

    private static IResult InvalidPath(InvalidNotePathException ex) =>
        Results.Json(
            new ErrorResponse("invalid_path", ex.Message),
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// A move/create destination is already occupied - see
    /// <see cref="DestinationAlreadyExistsException"/>'s remarks. Callers
    /// map this to 409 Conflict per docs/04-API-SPEC.md.
    /// </summary>
    private static IResult AlreadyExists(DestinationAlreadyExistsException ex) =>
        Results.Json(
            new ErrorResponse("already_exists", ex.Message),
            statusCode: StatusCodes.Status409Conflict);

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

    /// <summary>Backs <c>POST /api/folders/{**path}</c>'s <c>{ path }</c> response.</summary>
    private sealed record FolderResponse(string Path);

    /// <summary>
    /// Backs the <c>{ path, rewrittenNotes }</c> response of
    /// <c>POST /api/folders/{**path}/move</c> per docs/04-API-SPEC.md.
    /// <c>RewrittenNotes</c> comes straight from
    /// <see cref="Core.Reorganization.FolderMoveResult.RewrittenNotes"/> -
    /// already vault-relative (post-move) paths, nothing to map.
    /// </summary>
    private sealed record FolderMoveResponse(string Path, IReadOnlyList<string> RewrittenNotes);

    /// <summary>Request body shape for <c>POST /api/folders/{**path}/move</c>: <c>{ destinationPath }</c>.</summary>
    private sealed record MoveFolderRequest(string? DestinationPath);
}
