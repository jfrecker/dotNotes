namespace DotNotes.Core.Notes;

/// <summary>
/// Thrown when the configured vault root directory does not currently
/// exist on disk at the moment a <see cref="FileSystemNoteRepository"/>
/// operation is attempted — e.g. a WSL2/network-mount hiccup, or a Docker
/// bind-mount host directory that has transiently vanished while the app
/// keeps running (see CLAUDE.md's "no database, the vault is the source
/// of truth" hard rule).
/// </summary>
/// <remarks>
/// This is deliberately a distinct exception from
/// <see cref="InvalidNotePathException"/> (a caller-supplied-path
/// problem) and from an ordinary "note not found" result (a
/// <see langword="null"/>/<see langword="false"/> return, not an
/// exception). Without this guard, a vanished vault root would let
/// <see cref="FileSystemNoteRepository.SaveAsync"/> silently fabricate a
/// brand-new, empty directory at the vault root path (via
/// <c>Directory.CreateDirectory</c>) and write the new note into it, and
/// would let <see cref="FileSystemNoteRepository.GetAsync"/> /
/// <see cref="FileSystemNoteRepository.ExistsAsync"/> report a misleading
/// "note not found" that is indistinguishable from an actually-deleted
/// note. Callers (REST endpoints, MCP tools) should catch exactly this
/// type and fail the request clearly (the REST layer maps it to 503
/// Service Unavailable) rather than proceeding or retrying with a
/// recreate.
/// </remarks>
public sealed class VaultUnavailableException : Exception
{
    /// <summary>The vault root's resolved full path that could not be found on disk.</summary>
    public string VaultRootPath { get; }

    public VaultUnavailableException(string vaultRootPath)
        : base($"The vault root '{vaultRootPath}' is not currently accessible on disk. It may have been unmounted, moved, or deleted outside of dotNotes. No changes were made; restore the directory and retry.")
    {
        VaultRootPath = vaultRootPath;
    }

    public VaultUnavailableException(string vaultRootPath, string message)
        : base(message)
    {
        VaultRootPath = vaultRootPath;
    }

    public VaultUnavailableException(string vaultRootPath, string message, Exception innerException)
        : base(message, innerException)
    {
        VaultRootPath = vaultRootPath;
    }
}
