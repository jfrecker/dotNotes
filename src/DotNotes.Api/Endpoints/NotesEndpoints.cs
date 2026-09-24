using System.Text.Json;
using DotNotes.Core.Links;
using DotNotes.Core.Notes;
using DotNotes.Core.Reorganization;

namespace DotNotes.Api.Endpoints;

/// <summary>
/// Wires up the Notes section of docs/04-API-SPEC.md:
/// <c>GET /api/notes</c>, <c>GET/PUT/DELETE /api/notes/{**path}</c>, and
/// <c>POST /api/notes/{**path}/move</c> (see <see cref="PostNoteRouteAsync"/>'s
/// remarks for how that last one is actually routed).
/// Endpoints here are intentionally thin - all path validation and file
/// I/O lives in <see cref="INoteRepository"/> (DotNotes.Core); this file
/// only translates HTTP in/out to/from that contract.
/// </summary>
public static class NotesEndpoints
{
    public static WebApplication MapNotesEndpoints(this WebApplication app)
    {
        app.MapGet("/api/notes", GetTreeAsync);
        app.MapGet("/api/notes/{**path}", GetNoteAsync);
        app.MapPut("/api/notes/{**path}", PutNoteAsync);
        app.MapDelete("/api/notes/{**path}", DeleteNoteAsync);
        app.MapPost("/api/notes/{**path}", PostNoteRouteAsync);

        return app;
    }

    /// <summary>Trailing route segment recognized by <see cref="PostNoteRouteAsync"/> - see its remarks.</summary>
    private const string MoveRouteSuffix = "/move";

    /// <summary>
    /// Single dispatch point for every POST verb currently living under
    /// <c>/api/notes/{**path}</c> - per docs/04-API-SPEC.md, that's just
    /// <c>POST /api/notes/{**path}/move</c> today.
    /// </summary>
    /// <remarks>
    /// ASP.NET Core's route pattern parser throws a <c>RoutePatternException</c>
    /// at startup ("A catch-all parameter can only appear as the last
    /// segment of the route template") for any route template with a
    /// literal segment after a catch-all parameter - so
    /// <c>MapPost("/api/notes/{**path}/move", ...)</c> cannot be registered
    /// as written in docs/04-API-SPEC.md's table (verified experimentally).
    /// This preserves the exact same externally-observable URL/verb/body/
    /// response contract by mapping POST once to the same catch-all shape
    /// GET/PUT/DELETE already use above, then having the handler itself
    /// recognize the trailing "/move" segment before delegating to
    /// <see cref="MoveNoteAsync"/> - purely an internal dispatch technique,
    /// not a deviation from the documented contract. Any other POST under
    /// this prefix (nothing else is defined today) 404s the same way an
    /// unmapped route would.
    /// </remarks>
    private static Task<IResult> PostNoteRouteAsync(
        string path,
        HttpRequest request,
        IVaultReorganizationService reorganizationService,
        CancellationToken cancellationToken)
    {
        if (!path.EndsWith(MoveRouteSuffix, StringComparison.Ordinal))
        {
            return Task.FromResult(NotFound(path));
        }

        var sourcePath = path[..^MoveRouteSuffix.Length];
        return MoveNoteAsync(sourcePath, request, reorganizationService, cancellationToken);
    }

    private static async Task<IResult> MoveNoteAsync(
        string sourcePath,
        HttpRequest request,
        IVaultReorganizationService reorganizationService,
        CancellationToken cancellationToken)
    {
        MoveNoteRequest? body;
        try
        {
            // Same manual-parse pattern as PutNoteAsync (see its comment):
            // a missing/malformed body maps to this app's own
            // {error, detail} shape rather than ASP.NET Core's built-in
            // 400 for a body-binding failure.
            body = await request.ReadFromJsonAsync<MoveNoteRequest>(cancellationToken).ConfigureAwait(false);
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

        try
        {
            var result = await reorganizationService.MoveNoteAsync(sourcePath, body.DestinationPath, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new NoteMoveResponse(result.Path, result.UpdatedAt, result.RewrittenNotes));
        }
        catch (SourceNotFoundException ex)
        {
            return NotFound(ex.SourcePath);
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

    private static async Task<IResult> GetTreeAsync(
        INoteRepository noteRepository,
        CancellationToken cancellationToken)
    {
        try
        {
            var tree = await noteRepository.GetTreeAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(tree.Select(MapTreeEntry).ToArray());
        }
        catch (VaultUnavailableException ex)
        {
            return VaultUnavailable(ex);
        }
    }

    private static async Task<IResult> GetNoteAsync(
        string path,
        // `?includeBacklinks=true` populates the `backlinks` field per
        // docs/04-API-SPEC.md's Notes GET response (`backlinks?: [...]`).
        // Omitted (or any other value), `Backlinks` stays null and is
        // left out of the JSON response entirely.
        bool? includeBacklinks,
        INoteRepository noteRepository,
        ILinkIndex linkIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            var note = await noteRepository.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (note is null)
            {
                return NotFound(path);
            }

            IReadOnlyList<BacklinkResponse>? backlinks = null;
            if (includeBacklinks == true)
            {
                // Title = filename without the ".md" extension, matching
                // docs/06-DATA-MODEL.md's wikilink display-label rule
                // (a link's label is its filename without ".md" unless a
                // pipe alias overrides it) - there's no per-backlink
                // alias to prefer here, since a note can be linked to by
                // many different aliases across many different sources.
                backlinks = linkIndex.GetBacklinks(note.Path)
                    .Select(sourcePath => new BacklinkResponse(sourcePath, WikiLinkResolver.GetBareTitle(sourcePath)))
                    .ToArray();
            }

            return Results.Ok(new NoteContentResponse(note.Path, note.Content, note.UpdatedAt, backlinks));
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

    private static async Task<IResult> PutNoteAsync(
        string path,
        HttpRequest request,
        INoteRepository noteRepository,
        CancellationToken cancellationToken)
    {
        UpdateNoteRequest? body;
        try
        {
            // Read/parse the body manually (rather than via an implicit
            // `[FromBody]` parameter) so a missing/malformed body maps to
            // our own `{ error, detail }` shape instead of ASP.NET Core's
            // built-in 400 response for body-binding failures.
            body = await request.ReadFromJsonAsync<UpdateNoteRequest>(cancellationToken).ConfigureAwait(false);
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

        if (body?.Content is null)
        {
            return BadRequest("invalid_request", "Request body must include a 'content' string.");
        }

        try
        {
            // Optimistic concurrency (opt-in): docs/features/tasks-kanban/PLAN.md
            // §3 "Concurrent edits" - a caller (the editor) that supplies
            // `expectedUpdatedAt` is asking to fail loudly instead of
            // silently clobbering a newer on-disk version (e.g. one the
            // board/MCP just wrote). Omitting it keeps the pre-existing
            // last-write-wins behaviour for MCP's update_note and older
            // clients.
            if (body.ExpectedUpdatedAt is { } expected)
            {
                var current = await noteRepository.GetAsync(path, cancellationToken).ConfigureAwait(false);
                if (current is null)
                {
                    // Judgment call (docs/04-API-SPEC.md doesn't cover this
                    // case explicitly): the note the editor loaded no
                    // longer exists - deleted or renamed out from under
                    // it - which is exactly the "your copy is stale"
                    // situation `expectedUpdatedAt` exists to catch, so
                    // this is a 409 (with no `currentUpdatedAt` to report)
                    // rather than silently recreating the note or 404ing
                    // (which the editor would otherwise treat as "not
                    // found" rather than "reload or overwrite").
                    return Conflict("The note no longer exists at this path; it may have been deleted or moved.", currentUpdatedAt: null);
                }

                if (NoteConcurrency.HasConflict(expected, current.UpdatedAt))
                {
                    return Conflict("The note was modified since it was last loaded.", current.UpdatedAt);
                }
            }

            var result = await noteRepository.SaveAsync(path, body.Content, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new NoteWriteResponse(result.Path, result.UpdatedAt));
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

    private static async Task<IResult> DeleteNoteAsync(
        string path,
        INoteRepository noteRepository,
        CancellationToken cancellationToken)
    {
        try
        {
            // Judgment call (docs/04-API-SPEC.md doesn't distinguish):
            // DELETE is treated as idempotent. Whether or not a note
            // existed at `path`, the post-condition - "no note exists at
            // this path" - holds once this returns, so both cases return
            // 204 No Content. INoteRepository.DeleteAsync's bool return
            // value is intentionally not surfaced as a 404 here.
            await noteRepository.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
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

    private static NoteTreeEntryResponse MapTreeEntry(NoteTreeEntry entry) => new(
        entry.Path,
        entry.Name,
        entry.Type == NoteEntryType.File ? "file" : "folder",
        entry.Children?.Select(MapTreeEntry).ToArray());

    private static IResult NotFound(string path) =>
        Results.Json(
            new ErrorResponse("not_found", $"No note exists at '{path}'."),
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
    /// The vault root itself is currently missing on disk (e.g. an
    /// unmounted/transiently-vanished bind mount) - see
    /// <see cref="VaultUnavailableException"/>'s remarks. Deliberately a
    /// distinct 503, not 500/404, so a client (or the frontend) can tell
    /// "the whole vault is temporarily gone" apart from any other failure.
    /// </summary>
    private static IResult VaultUnavailable(VaultUnavailableException ex) =>
        Results.Json(
            new ErrorResponse("vault_unavailable", ex.Message),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult BadRequest(string error, string? detail) =>
        Results.Json(
            new ErrorResponse(error, detail),
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// <c>PUT /api/notes/{**path}</c>'s optimistic-concurrency conflict -
    /// see <see cref="NoteConcurrency"/> and this method's caller. Carries
    /// an extra <c>currentUpdatedAt</c> field (the note's actual current
    /// timestamp, so the editor can decide whether to reload or force an
    /// overwrite) beyond the standard <see cref="ErrorResponse"/> shape,
    /// hence its own dedicated record rather than reusing that one.
    /// </summary>
    private static IResult Conflict(string detail, DateTimeOffset? currentUpdatedAt) =>
        Results.Json(
            new ConflictErrorResponse("conflict", detail, currentUpdatedAt),
            statusCode: StatusCodes.Status409Conflict);

    /// <summary>Standard error shape per docs/04-API-SPEC.md's Conventions section.</summary>
    private sealed record ErrorResponse(string Error, string? Detail = null);

    /// <summary>
    /// Error shape for <c>PUT /api/notes/{**path}</c>'s 409 conflict
    /// response: the standard <c>{ error, detail }</c> plus
    /// <c>currentUpdatedAt</c> - see <see cref="Conflict"/>.
    /// </summary>
    private sealed record ConflictErrorResponse(string Error, string? Detail, DateTimeOffset? CurrentUpdatedAt);

    /// <summary>Backs <c>GET /api/notes</c>.</summary>
    private sealed record NoteTreeEntryResponse(
        string Path,
        string Name,
        string Type,
        IReadOnlyList<NoteTreeEntryResponse>? Children);

    /// <summary>Backs <c>GET /api/notes/{**path}</c>.</summary>
    private sealed record NoteContentResponse(
        string Path,
        string Content,
        DateTimeOffset UpdatedAt,
        IReadOnlyList<BacklinkResponse>? Backlinks);

    /// <summary>One entry of <c>NoteContentResponse.Backlinks</c>, populated when <c>?includeBacklinks=true</c>.</summary>
    private sealed record BacklinkResponse(string Path, string Title);

    /// <summary>Backs the <c>{ path, updatedAt }</c> response of <c>PUT /api/notes/{**path}</c>.</summary>
    private sealed record NoteWriteResponse(string Path, DateTimeOffset UpdatedAt);

    /// <summary>
    /// Request body shape for <c>PUT /api/notes/{**path}</c>:
    /// <c>{ content, expectedUpdatedAt? }</c>. <c>ExpectedUpdatedAt</c> is
    /// the opt-in optimistic-concurrency check per
    /// docs/features/tasks-kanban/PLAN.md §3 - see <see cref="PutNoteAsync"/>.
    /// </summary>
    private sealed record UpdateNoteRequest(string? Content, DateTimeOffset? ExpectedUpdatedAt);

    /// <summary>
    /// Backs the <c>{ path, updatedAt, rewrittenNotes }</c> response of
    /// <c>POST /api/notes/{**path}/move</c> per docs/04-API-SPEC.md.
    /// <c>RewrittenNotes</c> comes straight from
    /// <see cref="Core.Reorganization.NoteMoveResult.RewrittenNotes"/> -
    /// already vault-relative paths, nothing to map.
    /// </summary>
    private sealed record NoteMoveResponse(string Path, DateTimeOffset UpdatedAt, IReadOnlyList<string> RewrittenNotes);

    /// <summary>Request body shape for <c>POST /api/notes/{**path}/move</c>: <c>{ destinationPath }</c>.</summary>
    private sealed record MoveNoteRequest(string? DestinationPath);
}
