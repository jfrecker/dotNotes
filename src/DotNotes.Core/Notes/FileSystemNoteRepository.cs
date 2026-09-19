using System.Text.RegularExpressions;
using DotNotes.Core.Config;
using Microsoft.Extensions.Options;

namespace DotNotes.Core.Notes;

/// <summary>
/// Disk-backed <see cref="INoteRepository"/>. Notes are plain UTF-8
/// markdown files under the configured vault root; folders are just
/// directories. This type has no external state beyond the resolved
/// vault root path, so a singleton lifetime is safe.
/// </summary>
public sealed class FileSystemNoteRepository : INoteRepository
{
    // Catches drive-relative Windows paths like "C:foo.md" (no separator
    // after the colon), which Path.IsPathRooted treats as NOT rooted but
    // which still resolve relative to a specific drive's current
    // directory - i.e. potentially outside the vault.
    private static readonly Regex DriveLetterPattern = new(@"^[a-zA-Z]:", RegexOptions.Compiled);

    // Host-specific invalid file-name characters (docs/06-DATA-MODEL.md's
    // "Names" rule). On Linux this is just '\0' and '/' - neither of which
    // can actually appear in a single already-split path segment - so the
    // explicit control-character and '['/']'/'|' checks in
    // ValidateSegmentName below do the real work on that platform; this
    // set matters most on Windows (reserved characters like '<', '>', ':',
    // '"', '\\', '?', '*').
    private static readonly HashSet<char> InvalidFileNameChars = new(Path.GetInvalidFileNameChars());

    private readonly string _vaultRootFullPath;
    private readonly string _vaultRootWithTrailingSeparator;
    private readonly StringComparison _pathComparison;

    public FileSystemNoteRepository(IOptions<VaultOptions> vaultOptions)
    {
        // Resolves (and creates, if missing) the vault root using the same
        // framework-free logic Program.cs uses at startup. Calling this
        // again here is intentional and idempotent: it lets this type be
        // constructed directly in unit tests (via Options.Create) without
        // any ASP.NET Core hosting, and keeps DotNotes.Core the single
        // owner of "what does the vault root path resolve to".
        _vaultRootFullPath = VaultPathValidator.EnsureVaultRootExists(vaultOptions.Value.RootPath);
        _vaultRootWithTrailingSeparator = _vaultRootFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? _vaultRootFullPath
            : _vaultRootFullPath + Path.DirectorySeparatorChar;

        // The filesystem itself is case-insensitive on Windows and
        // case-sensitive on Linux (WSL2/Docker); match that when checking
        // whether a resolved path still lives under the vault root.
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    public Task<IReadOnlyList<NoteTreeEntry>> GetTreeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureVaultRootExists();
        IReadOnlyList<NoteTreeEntry> tree = BuildTree(_vaultRootFullPath, string.Empty, cancellationToken);
        return Task.FromResult(tree);
    }

    public async Task<NoteContent?> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveNotePath(path);
        EnsureVaultRootExists();
        if (!File.Exists(resolved.FullPath))
        {
            return null;
        }

        var content = await File.ReadAllTextAsync(resolved.FullPath, cancellationToken).ConfigureAwait(false);
        var updatedAt = GetLastWriteTimeUtc(resolved.FullPath);

        return new NoteContent
        {
            Path = resolved.NormalizedRelativePath,
            Content = content,
            UpdatedAt = updatedAt
        };
    }

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveNotePath(path);
        EnsureVaultRootExists();
        return Task.FromResult(File.Exists(resolved.FullPath));
    }

    public async Task<NoteWriteResult> SaveAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var resolved = ResolveNotePath(path);

        // Guard against a vanished vault root *before* the
        // Directory.CreateDirectory call below, which would otherwise
        // silently fabricate a brand-new, empty directory at the vault
        // root path (for a root-level note, parentDirectory IS the vault
        // root itself) and write into it - see VaultUnavailableException's
        // remarks and the Phase 8 QA finding that motivated this guard.
        EnsureVaultRootExists();

        // Name rules (docs/06-DATA-MODEL.md's "Names" section) apply only
        // when this save is *creating* a brand-new note - never to an
        // update of an existing one (autosave must keep working on a
        // pre-existing note whose name predates these rules, or was
        // created by another tool).
        if (!File.Exists(resolved.FullPath))
        {
            ValidateNewSegmentNames(path, resolved.NormalizedRelativePath.Split('/'), leafIsDirectory: false);
        }

        var parentDirectory = Path.GetDirectoryName(resolved.FullPath);
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        // Atomic write: write the full content to a temp file in the same
        // directory (so the subsequent move is same-volume, and therefore
        // atomic), then move it onto the final path with overwrite. A
        // crash or process kill mid-write can then never leave a
        // half-written note behind - the old content (or nothing, for a
        // brand-new note) survives until the move succeeds.
        var tempFilePath = Path.Combine(
            parentDirectory ?? _vaultRootFullPath,
            $".{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(tempFilePath, content, cancellationToken).ConfigureAwait(false);
            File.Move(tempFilePath, resolved.FullPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }

            throw;
        }

        var updatedAt = GetLastWriteTimeUtc(resolved.FullPath);
        return new NoteWriteResult
        {
            Path = resolved.NormalizedRelativePath,
            UpdatedAt = updatedAt
        };
    }

    public Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveNotePath(path);
        EnsureVaultRootExists();
        if (!File.Exists(resolved.FullPath))
        {
            return Task.FromResult(false);
        }

        File.Delete(resolved.FullPath);

        // Deliberately do NOT walk up and remove now-empty parent
        // directories: an empty folder is valid and preserved per
        // docs/06-DATA-MODEL.md - deleting the last note in a folder
        // must not silently remove the folder structure.
        return Task.FromResult(true);
    }

    public Task<string> CreateFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveFolderPath(path);

        // Same "don't fabricate the vault root" guard SaveAsync uses,
        // applied before the Directory.CreateDirectory call below.
        EnsureVaultRootExists();

        if (File.Exists(resolved.FullPath))
        {
            throw new DestinationAlreadyExistsException(
                resolved.NormalizedRelativePath,
                $"A note already exists at '{resolved.NormalizedRelativePath}'; a file and a folder cannot share the same path.");
        }

        // Name rules apply only to segments this call would actually
        // create - mkdir -p semantics mean some prefix of `path` may
        // already exist and is never checked (docs/06-DATA-MODEL.md).
        ValidateNewSegmentNames(path, resolved.NormalizedRelativePath.Split('/'), leafIsDirectory: true);

        // mkdir -p semantics: creates any missing intermediate folders,
        // and is a no-op (not an error) if the folder already exists.
        Directory.CreateDirectory(resolved.FullPath);

        return Task.FromResult(resolved.NormalizedRelativePath);
    }

    public Task<bool> DeleteFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        // ResolveFolderPath is the same choke point every other folder
        // operation uses: it rejects absolute/drive-relative paths, '.'
        // and '..' segments, and anything resolving outside the vault
        // root. Because IsWithinVaultRoot compares against the root *with*
        // a trailing separator, the vault root itself can never resolve
        // here - and an empty path is rejected before that - so there is
        // no path through this method that deletes the vault.
        var resolved = ResolveFolderPath(path);

        EnsureVaultRootExists();

        if (!Directory.Exists(resolved.FullPath))
        {
            // Also the "a note, not a folder, lives here" case: deleting
            // a note is DELETE /api/notes/{**path}'s job, never this one.
            return Task.FromResult(false);
        }

        // recursive: true - a folder is deleted with everything inside it
        // (nested notes, subfolders, media). Directory.Delete without it
        // throws IOException for any non-empty folder, which is the whole
        // point of this operation.
        Directory.Delete(resolved.FullPath, recursive: true);

        // Deliberately does NOT walk up and remove now-empty parent
        // folders, for the same reason DeleteAsync doesn't - see its
        // comment and docs/06-DATA-MODEL.md.
        return Task.FromResult(true);
    }

    public Task<NoteWriteResult> MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        var source = ResolveNotePath(sourcePath);
        var destination = ResolveNotePath(destinationPath);

        EnsureVaultRootExists();

        if (!File.Exists(source.FullPath))
        {
            throw new SourceNotFoundException(
                source.NormalizedRelativePath,
                $"No note exists at '{source.NormalizedRelativePath}'.");
        }

        // Name rules apply only to the destination's newly-created
        // segments - the source is never checked, so a pre-existing note
        // with an otherwise-disallowed name can still be moved *away* to a
        // valid name (docs/06-DATA-MODEL.md's "Names" section).
        ValidateNewSegmentNames(destinationPath, destination.NormalizedRelativePath.Split('/'), leafIsDirectory: false);

        var destinationParent = Path.GetDirectoryName(destination.FullPath);
        if (!string.IsNullOrEmpty(destinationParent))
        {
            Directory.CreateDirectory(destinationParent);
        }

        try
        {
            // overwrite: false makes File.Move itself refuse to clobber an
            // existing destination, throwing IOException - caught below and
            // translated into a clear, typed exception rather than letting
            // a raw IOException leak out of this repository.
            File.Move(source.FullPath, destination.FullPath, overwrite: false);
        }
        catch (IOException ex) when (File.Exists(destination.FullPath) || Directory.Exists(destination.FullPath))
        {
            throw new DestinationAlreadyExistsException(
                destination.NormalizedRelativePath,
                $"A note or folder already exists at '{destination.NormalizedRelativePath}'; move refused to avoid overwriting it.",
                ex);
        }

        var updatedAt = GetLastWriteTimeUtc(destination.FullPath);
        return Task.FromResult(new NoteWriteResult
        {
            Path = destination.NormalizedRelativePath,
            UpdatedAt = updatedAt
        });
    }

    public Task<string> MoveFolderAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        var source = ResolveFolderPath(sourcePath);
        var destination = ResolveFolderPath(destinationPath);

        EnsureVaultRootExists();

        if (!Directory.Exists(source.FullPath))
        {
            throw new SourceNotFoundException(
                source.NormalizedRelativePath,
                $"No folder exists at '{source.NormalizedRelativePath}'.");
        }

        // Reject moving a folder into itself or one of its own descendants
        // explicitly, up front - Directory.Move's own behavior for this
        // case is inconsistent/platform-dependent, so this must not rely
        // on it. Compared on the normalized, '/'-separated relative paths
        // rather than full disk paths so it's independent of platform path
        // separators.
        if (IsSourceOrDescendant(source.NormalizedRelativePath, destination.NormalizedRelativePath))
        {
            throw new InvalidNotePathException(
                destinationPath,
                $"Cannot move folder '{source.NormalizedRelativePath}' into itself or one of its own descendants ('{destination.NormalizedRelativePath}').");
        }

        // Same "only newly-created destination segments are checked" rule
        // as MoveAsync above.
        ValidateNewSegmentNames(destinationPath, destination.NormalizedRelativePath.Split('/'), leafIsDirectory: true);

        var destinationParent = Path.GetDirectoryName(destination.FullPath);
        if (!string.IsNullOrEmpty(destinationParent))
        {
            Directory.CreateDirectory(destinationParent);
        }

        try
        {
            // Directory.Move naturally refuses to overwrite an existing
            // destination directory (throws IOException); caught below and
            // translated into a clear, typed exception rather than letting
            // a raw IOException leak out of this repository.
            Directory.Move(source.FullPath, destination.FullPath);
        }
        catch (IOException ex) when (File.Exists(destination.FullPath) || Directory.Exists(destination.FullPath))
        {
            throw new DestinationAlreadyExistsException(
                destination.NormalizedRelativePath,
                $"A note or folder already exists at '{destination.NormalizedRelativePath}'; move refused to avoid overwriting it.",
                ex);
        }

        return Task.FromResult(destination.NormalizedRelativePath);
    }

    /// <summary>
    /// True if <paramref name="candidateRelativePath"/> is exactly
    /// <paramref name="sourceRelativePath"/> or is nested underneath it
    /// (e.g. source <c>projects</c>, candidate <c>projects/archive</c>).
    /// Both paths must already be normalized, <c>/</c>-separated,
    /// vault-relative paths (see <see cref="ResolveFolderPath"/>).
    /// </summary>
    private bool IsSourceOrDescendant(string sourceRelativePath, string candidateRelativePath)
    {
        if (string.Equals(sourceRelativePath, candidateRelativePath, _pathComparison))
        {
            return true;
        }

        var sourceWithTrailingSlash = sourceRelativePath + "/";
        return candidateRelativePath.StartsWith(sourceWithTrailingSlash, _pathComparison);
    }

    /// <summary>
    /// Validates every path segment that would be *newly created* on disk
    /// by the call site that invoked this - i.e. everything from the first
    /// segment whose ancestor directory does not already exist, down to
    /// the leaf itself - per docs/06-DATA-MODEL.md's "Names" section. A
    /// segment whose ancestor directory already exists on disk is never
    /// checked, so an existing note or folder with an otherwise-disallowed
    /// name can still be read, saved (autosaved), deleted, or moved *away*
    /// to a valid name.
    /// </summary>
    /// <param name="originalPath">
    /// The original, caller-supplied path (for a move, the destination) -
    /// used only to populate a thrown exception's <c>AttemptedPath</c>.
    /// </param>
    /// <param name="segments">
    /// The already-split, normalized, non-empty <c>/</c>-separated
    /// segments of the vault-relative path being created or moved to.
    /// </param>
    /// <param name="leafIsDirectory">
    /// <see langword="true"/> for a folder path (every segment, including
    /// the last, is a directory); <see langword="false"/> for a note path
    /// (every segment except the last is a directory, and the last is a
    /// file).
    /// </param>
    /// <exception cref="InvalidNotePathException">
    /// A newly-created segment is empty or all-whitespace, has leading/
    /// trailing whitespace, contains a control character or a character
    /// invalid for a file name on the host OS, or contains <c>[</c>,
    /// <c>]</c> or <c>|</c>.
    /// </exception>
    private void ValidateNewSegmentNames(string originalPath, IReadOnlyList<string> segments, bool leafIsDirectory)
    {
        var currentDirectory = _vaultRootFullPath;
        var encounteredNewSegment = false;

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var isLeaf = i == segments.Count - 1;
            var candidatePath = Path.Combine(currentDirectory, segment);

            var alreadyExists = !encounteredNewSegment && (isLeaf && !leafIsDirectory
                ? File.Exists(candidatePath) || Directory.Exists(candidatePath)
                : Directory.Exists(candidatePath));

            if (!alreadyExists)
            {
                encounteredNewSegment = true;
                ValidateSegmentName(originalPath, segment);
            }

            currentDirectory = candidatePath;
        }
    }

    /// <summary>
    /// The actual character-level name-rule checks from
    /// docs/06-DATA-MODEL.md's "Names" section, applied to one path
    /// segment that <see cref="ValidateNewSegmentNames"/> has determined
    /// is being newly created.
    /// </summary>
    private static void ValidateSegmentName(string originalPath, string segment)
    {
        if (segment.Trim().Length == 0)
        {
            throw new InvalidNotePathException(originalPath, $"Name '{segment}' must not be empty or all whitespace.");
        }

        if (segment != segment.Trim())
        {
            throw new InvalidNotePathException(originalPath, $"Name '{segment}' must not have leading or trailing whitespace.");
        }

        foreach (var c in segment)
        {
            if (char.IsControl(c))
            {
                throw new InvalidNotePathException(originalPath, $"Name '{segment}' must not contain control characters.");
            }

            if (c is '[' or ']' or '|')
            {
                throw new InvalidNotePathException(
                    originalPath,
                    $"Name '{segment}' must not contain '[', ']' or '|' - it could not be written as a wikilink target.");
            }

            if (InvalidFileNameChars.Contains(c))
            {
                throw new InvalidNotePathException(
                    originalPath,
                    $"Name '{segment}' contains a character not allowed in a file name on this host ('{c}').");
            }
        }
    }

    private static DateTimeOffset GetLastWriteTimeUtc(string fullPath) =>
        new(File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero);

    /// <summary>
    /// Guards every public method against a vault root that has vanished
    /// out from under this repository since construction (see
    /// <see cref="VaultUnavailableException"/>'s remarks). Called after
    /// <see cref="ResolveNotePath"/>/<see cref="ResolveFolderPath"/> (so a
    /// caller-supplied path is still validated first) but before any
    /// filesystem operation that could
    /// either fabricate the vault root (<c>Directory.CreateDirectory</c>
    /// in <see cref="SaveAsync"/>) or report a misleading "not found" that
    /// is indistinguishable from an actually-deleted note
    /// (<c>File.Exists</c> in <see cref="GetAsync"/>/
    /// <see cref="ExistsAsync"/>/<see cref="DeleteAsync"/>).
    /// </summary>
    /// <exception cref="VaultUnavailableException">
    /// The vault root directory does not currently exist on disk.
    /// </exception>
    private void EnsureVaultRootExists()
    {
        if (!Directory.Exists(_vaultRootFullPath))
        {
            throw new VaultUnavailableException(_vaultRootFullPath);
        }
    }

    /// <summary>
    /// Resolves and validates a caller-supplied vault-relative note path.
    /// This is the single choke point every note-touching public method
    /// routes through before touching the filesystem - see
    /// docs/02-ARCHITECTURE.md / CLAUDE.md's "never let a path escape the
    /// vault root" hard rule. Shares all escaping/traversal validation
    /// with <see cref="ResolveFolderPath"/> via
    /// <see cref="ValidatePathShapeAndResolve"/>; the <c>.md</c>-extension
    /// check below is the only note-specific rule.
    /// </summary>
    /// <exception cref="InvalidNotePathException">
    /// The path is null/empty, absolute, drive-relative, contains a
    /// <c>..</c> segment, contains a null character, does not end in
    /// <c>.md</c>, or resolves outside the vault root.
    /// </exception>
    private (string FullPath, string NormalizedRelativePath) ResolveNotePath(string path)
    {
        var resolved = ValidatePathShapeAndResolve(path, "Note");

        if (!resolved.NormalizedRelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidNotePathException(path, "Note path must end with a '.md' extension.");
        }

        return resolved;
    }

    /// <summary>
    /// Resolves and validates a caller-supplied vault-relative folder
    /// path. This is the single choke point every folder-touching public
    /// method (<see cref="CreateFolderAsync"/>, <see cref="MoveFolderAsync"/>)
    /// routes through before touching the filesystem. Identical to
    /// <see cref="ResolveNotePath"/> minus the <c>.md</c>-extension
    /// requirement, per docs/06-DATA-MODEL.md's "Folder & note move/rename"
    /// section - both share <see cref="ValidatePathShapeAndResolve"/> so
    /// the escaping/traversal checks can never drift apart between the two.
    /// </summary>
    /// <exception cref="InvalidNotePathException">
    /// The path is null/empty, absolute, drive-relative, contains a
    /// <c>..</c> segment, contains a null character, or resolves outside
    /// the vault root.
    /// </exception>
    private (string FullPath, string NormalizedRelativePath) ResolveFolderPath(string path) =>
        ValidatePathShapeAndResolve(path, "Folder");

    /// <summary>
    /// Shared escaping/traversal validation for both note paths and folder
    /// paths - the single choke point <see cref="ResolveNotePath"/> and
    /// <see cref="ResolveFolderPath"/> both route through, so path-safety
    /// checks can only be written (and audited) once. <paramref name="entryKind"/>
    /// ("Note" or "Folder") is used only to make thrown messages read
    /// naturally for whichever caller invoked this.
    /// </summary>
    /// <exception cref="InvalidNotePathException">
    /// The path is null/empty, absolute, drive-relative, contains a
    /// <c>..</c> segment, contains a null character, or resolves outside
    /// the vault root.
    /// </exception>
    private (string FullPath, string NormalizedRelativePath) ValidatePathShapeAndResolve(string path, string entryKind)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidNotePathException(path, $"{entryKind} path must not be null or empty.");
        }

        if (path.Contains('\0'))
        {
            throw new InvalidNotePathException(path, $"{entryKind} path must not contain null characters.");
        }

        // Reject absolute paths up front: Path.IsPathRooted catches
        // "/foo", "\foo", and "C:\foo" on Windows (and "/foo" on Linux);
        // the drive-letter regex additionally catches "C:foo"
        // (drive-relative, no separator), which IsPathRooted does NOT
        // consider rooted but which Path.GetFullPath would still resolve
        // against a specific drive's current directory - i.e. potentially
        // outside the vault.
        if (Path.IsPathRooted(path) || DriveLetterPattern.IsMatch(path))
        {
            throw new InvalidNotePathException(path, $"{entryKind} path must be relative to the vault root, not absolute.");
        }

        // Accept '/' or '\' from callers; work in '/'-separated segments
        // so the traversal check and the round-tripped display path are
        // both platform-independent.
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new InvalidNotePathException(path, $"{entryKind} path must not be empty.");
        }

        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidNotePathException(path, $"{entryKind} path must not contain '.' or '..' segments.");
        }

        var normalizedRelativePath = string.Join('/', segments);
        var platformRelativePath = string.Join(Path.DirectorySeparatorChar, segments);
        var fullPath = Path.GetFullPath(Path.Combine(_vaultRootFullPath, platformRelativePath));

        if (!IsWithinVaultRoot(fullPath))
        {
            throw new InvalidNotePathException(path, $"{entryKind} path resolves outside the vault root.");
        }

        return (fullPath, normalizedRelativePath);
    }

    private bool IsWithinVaultRoot(string fullPath) =>
        fullPath.StartsWith(_vaultRootWithTrailingSeparator, _pathComparison);

    /// <summary>
    /// Recursively builds the note tree for one directory. Folders are
    /// always included (even when empty, so empty folders are preserved
    /// in the listing per docs/06-DATA-MODEL.md); only <c>.md</c> files
    /// are surfaced as file nodes. Dotfiles/dot-directories (e.g. the
    /// generated <c>.nd-shares.json</c>, or a <c>.git</c> a user might
    /// init inside the vault) and the reserved <c>_media/</c> binary-asset
    /// folder are excluded, since neither is a note.
    /// </summary>
    private static List<NoteTreeEntry> BuildTree(string directoryFullPath, string relativePrefix, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = new List<NoteTreeEntry>();
        var directoryInfo = new DirectoryInfo(directoryFullPath);

        // Folders first, then files, each alphabetical - a conventional
        // file-tree ordering for a sidebar UI. Neither doc mandates an
        // order, so this is a judgment call.
        foreach (var subdirectory in directoryInfo.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (IsExcludedDirectoryName(subdirectory.Name))
            {
                continue;
            }

            var childRelativePath = CombineRelative(relativePrefix, subdirectory.Name);
            var children = BuildTree(subdirectory.FullName, childRelativePath, cancellationToken);
            entries.Add(new NoteTreeEntry
            {
                Path = childRelativePath,
                Name = subdirectory.Name,
                Type = NoteEntryType.Folder,
                Children = children
            });
        }

        foreach (var file in directoryInfo.EnumerateFiles()
            .Where(f => !IsExcludedFileName(f.Name) && f.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var childRelativePath = CombineRelative(relativePrefix, file.Name);
            entries.Add(new NoteTreeEntry
            {
                Path = childRelativePath,
                Name = file.Name,
                Type = NoteEntryType.File,
                Children = null
            });
        }

        return entries;
    }

    private static string CombineRelative(string prefix, string name) =>
        string.IsNullOrEmpty(prefix) ? name : $"{prefix}/{name}";

    private static bool IsExcludedDirectoryName(string name) =>
        name.StartsWith('.') || name.Equals("_media", StringComparison.Ordinal);

    private static bool IsExcludedFileName(string name) => name.StartsWith('.');
}
