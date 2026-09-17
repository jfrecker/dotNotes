using DotNotes.Core.Media;
using Microsoft.AspNetCore.Http;

namespace DotNotes.Api.Endpoints;

/// <summary>
/// Wires up docs/04-API-SPEC.md's Media section (<c>POST /api/upload</c>)
/// plus <c>GET /media/{**path}</c> - a top-level (not <c>/api</c>-prefixed)
/// endpoint that streams a file already saved under <c>_media/</c> back to
/// the browser, since <c>_media/</c> lives in the vault root, outside
/// <c>wwwroot/</c>, and therefore isn't reachable through the existing
/// static-file middleware. See this phase's report for the "not
/// token-gated" trade-off this implies.
/// </summary>
public static class MediaEndpoints
{
    /// <summary>
    /// Maximum accepted upload size: 50 MB. Kept as a shared public const
    /// so <c>Program.cs</c> can size Kestrel's <c>MaxRequestBodySize</c> /
    /// <c>FormOptions.MultipartBodyLengthLimit</c> around it without
    /// duplicating the number in two places.
    /// </summary>
    public const long MaxUploadSizeBytes = 50L * 1024 * 1024;

    public static WebApplication MapMediaEndpoints(this WebApplication app)
    {
        app.MapPost("/api/upload", PostUploadAsync);
        app.MapGet("/media/{**path}", GetMediaAsync);

        return app;
    }

    private static async Task<IResult> PostUploadAsync(
        HttpRequest request,
        IMediaStore mediaStore,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return BadRequest("invalid_request", "Request must be multipart/form-data with a single file.");
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex)
        {
            // Thrown when the body exceeds Kestrel's configured
            // MaxRequestBodySize (see Program.cs) while being buffered as
            // a multipart form, or when the multipart body is otherwise
            // malformed - either way, a clean {error, detail} response
            // with the framework's own status code, rather than an
            // unhandled exception.
            return Results.Json(
                new ErrorResponse("upload_failed", ex.Message),
                statusCode: ex.StatusCode is >= 400 and < 600 ? ex.StatusCode : StatusCodes.Status400BadRequest);
        }
        catch (InvalidDataException ex)
        {
            // Thrown by the multipart form reader itself when the body
            // exceeds FormOptions.MultipartBodyLengthLimit (see
            // Program.cs) - a server-host-agnostic limit (applies under
            // both Kestrel and the in-memory TestServer used by
            // MediaEndpointsTests), distinct from BadHttpRequestException
            // above, which is Kestrel's own raw-body-size cutoff.
            return Results.Json(
                new ErrorResponse("file_too_large", ex.Message),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            return BadRequest("invalid_request", "Request must include a non-empty file (form field name 'file').");
        }

        if (file.Length > MaxUploadSizeBytes)
        {
            return Results.Json(
                new ErrorResponse("file_too_large", $"File exceeds the maximum allowed size of {MaxUploadSizeBytes / (1024 * 1024)} MB."),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var declaredContentType = file.ContentType;
        if (!MediaContentTypes.IsAcceptedContentType(declaredContentType))
        {
            return Results.Json(
                new ErrorResponse("unsupported_content_type", $"Content type '{declaredContentType}' is not accepted. Allowed: images, audio, video, and PDF."),
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        // Defense in depth (not a full magic-byte sniff - see this phase's
        // report): cross-check the declared Content-Type against the
        // original file extension, when one was supplied.
        var originalExtension = Path.GetExtension(file.FileName);
        if (!string.IsNullOrEmpty(originalExtension) &&
            !MediaContentTypes.ExtensionMatchesContentType(originalExtension, declaredContentType))
        {
            return BadRequest(
                "content_type_mismatch",
                $"File extension '{originalExtension}' does not match declared content type '{declaredContentType}'.");
        }

        var canonicalExtension = MediaContentTypes.GetCanonicalExtension(declaredContentType)!;

        await using var stream = file.OpenReadStream();
        var relativePath = await mediaStore.SaveAsync(stream, canonicalExtension, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new UploadResponse(relativePath));
    }

    private static IResult GetMediaAsync(string path, IMediaStore mediaStore)
    {
        string? fullPath;
        try
        {
            fullPath = mediaStore.ResolveExistingMediaFullPath(path);
        }
        catch (InvalidMediaPathException ex)
        {
            return InvalidPath(ex);
        }

        if (fullPath is null)
        {
            return NotFound(path);
        }

        var extension = Path.GetExtension(fullPath);
        MediaContentTypes.TryGetContentTypeForExtension(extension, out var contentType);

        // Range processing enabled for reasonable audio/video seek support
        // in the shared view / editor preview - not required by
        // docs/04-API-SPEC.md, just a low-cost usability improvement.
        return Results.File(fullPath, contentType, enableRangeProcessing: true);
    }

    private static IResult NotFound(string path) =>
        Results.Json(
            new ErrorResponse("not_found", $"No media file exists at '{path}'."),
            statusCode: StatusCodes.Status404NotFound);

    private static IResult InvalidPath(InvalidMediaPathException ex) =>
        Results.Json(
            new ErrorResponse("invalid_path", ex.Message),
            statusCode: StatusCodes.Status400BadRequest);

    private static IResult BadRequest(string error, string? detail) =>
        Results.Json(
            new ErrorResponse(error, detail),
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Standard error shape per docs/04-API-SPEC.md's Conventions section.</summary>
    private sealed record ErrorResponse(string Error, string? Detail = null);

    /// <summary>Backs <c>POST /api/upload</c>'s <c>{ relativePath }</c> response.</summary>
    private sealed record UploadResponse(string RelativePath);
}
