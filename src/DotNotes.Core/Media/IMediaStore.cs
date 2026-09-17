namespace DotNotes.Core.Media;

/// <summary>
/// Saves and resolves binary media assets (images/audio/video/PDF)
/// embedded in notes, stored under <c>_media/</c> in the vault root
/// (docs/06-DATA-MODEL.md's Vault layout section) - a separate concern
/// from <see cref="Notes.INoteRepository"/>, which is scoped to
/// <c>.md</c> note files only.
/// </summary>
public interface IMediaStore
{
    /// <summary>
    /// Saves <paramref name="content"/> under <c>_media/</c> using a
    /// freshly-generated, GUID-based file name - the caller's original
    /// file name is never trusted or used, so it can never introduce path
    /// traversal, collisions, or unexpected characters. Returns the
    /// vault-relative path to the saved file (e.g. <c>_media/&lt;guid&gt;.png</c>),
    /// suitable for both the <c>POST /api/upload</c> response and direct
    /// use in markdown image/link syntax.
    /// </summary>
    /// <param name="fileExtension">
    /// The file extension to save with, including or omitting the leading
    /// '.' (e.g. <c>".png"</c> or <c>"png"</c>). Validated to be a short
    /// alphanumeric extension; callers should pass an extension already
    /// checked against <see cref="MediaContentTypes"/>.
    /// </param>
    Task<string> SaveAsync(Stream content, string fileExtension, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves <paramref name="relativePath"/> (as it appears after the
    /// <c>_media/</c> prefix, e.g. <c>"photo.png"</c>) to a full filesystem
    /// path, validating it stays under <c>_media/</c> inside the vault
    /// root. Returns <see langword="null"/> if no file exists at that
    /// (safe) path.
    /// </summary>
    /// <exception cref="InvalidMediaPathException">
    /// <paramref name="relativePath"/> is unsafe (absolute, contains a
    /// <c>..</c> segment, a null character, or resolves outside
    /// <c>_media/</c>).
    /// </exception>
    string? ResolveExistingMediaFullPath(string relativePath);
}
