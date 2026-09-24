using DotNotes.Core.Config;
using DotNotes.Core.Notes;
using DotNotes.Core.Reorganization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNotes.Core.Tasks;

/// <summary>
/// One-time, idempotent startup migration for the v0.2.1 Tasks folder
/// rename (<c>task</c>/<c>tasks</c> -&gt; <c>Task</c>) and the retirement of
/// the <c>&lt;Folder&gt;/archive</c> convention in favor of
/// <see cref="TaskFolders.Completed"/>. Called once from <c>Program.cs</c>
/// after <c>builder.Build()</c> and before <c>app.Run()</c> (before the
/// vault watcher's initial scan); every step is best-effort and logged - a
/// failure here must never prevent the app from starting.
/// </summary>
/// <remarks>
/// <para>
/// Directory names are read via real filesystem enumeration
/// (<see cref="Directory.EnumerateDirectories(string)"/>), not assumed,
/// since a case-insensitive filesystem (Windows/macOS) reports whatever
/// casing is actually stored on disk regardless of what a caller asks
/// <see cref="Directory.Exists(string?)"/> about.
/// </para>
/// <para>
/// <b>Step 1</b> (only runs when the configured <see cref="TasksOptions.Folder"/>
/// is exactly, ordinally, <c>"Task"</c> - the new default; a vault
/// deliberately configured to keep using a different folder name is left
/// untouched): every root-level folder whose actual on-disk name is
/// exactly (ordinal) <c>task</c> or <c>tasks</c> - a user's own
/// <c>Tasks</c>, <c>TASKS</c>, etc is deliberately <i>not</i> matched, see
/// docs/KNOWN-ISSUES.md - is folded into <c>Task</c>. If no folder is
/// already named <c>Task</c>, this is a straight rename via
/// <see cref="IVaultReorganizationService.MoveFolderAsync"/> (so incoming
/// wikilinks are rewritten) - except when the legacy name and <c>Task</c>
/// differ only by case (i.e. <c>task</c> -&gt; <c>Task</c>), where a direct
/// rename would spuriously collide with itself on a case-insensitive
/// filesystem (or simply be an inconsequential no-op on a case-sensitive
/// one that still leaves the on-disk casing wrong); that case always hops
/// through a throwaway temp name instead - via two
/// <see cref="IVaultReorganizationService.MoveFolderAsync"/> calls so
/// wikilinks are still rewritten each hop, falling back to a raw
/// <see cref="Directory.Move(string, string)"/> hop only if that fails - so
/// the OS actually updates the stored casing regardless of platform. If a
/// folder already exists at <c>Task</c>, the legacy folder's children are
/// merged into it one at a time (never clobbering an existing item - a
/// collision is logged and the colliding item is left in the legacy
/// folder), which is also how a vault that somehow has <i>both</i>
/// <c>task</c> and <c>tasks</c> alongside <c>Task</c> converges: the first
/// legacy folder processed becomes (or is merged into) <c>Task</c>, and the
/// second is then merged into it too. The legacy folder is deleted only
/// once it ends up empty.
/// </para>
/// <para>
/// <b>Step 2</b>: the same rename-or-merge treatment for
/// <c>&lt;Tasks:Folder&gt;/archive</c> (exact, ordinal name only) into
/// <c>&lt;Tasks:Folder&gt;/Completed</c> - run for whatever
/// <see cref="TasksOptions.Folder"/> is actually configured, independent of
/// step 1's <c>"Task"</c> gate, so a vault still deliberately configured
/// with the v0.2.0 default (<c>Tasks:Folder=tasks</c>) doesn't end up with
/// every previously-archived task silently un-completed.
/// </para>
/// <para>
/// Both steps are naturally idempotent: a second run finds nothing left
/// named exactly <c>task</c>/<c>tasks</c>/<c>archive</c> and does nothing.
/// This migration does not write any marker file to record that it has
/// run - see docs/KNOWN-ISSUES.md for what that means for a folder created
/// later that happens to match one of these exact legacy names.
/// </para>
/// </remarks>
public sealed class TaskFolderMigration
{
    private readonly IVaultReorganizationService _reorganizationService;
    private readonly IOptions<VaultOptions> _vaultOptions;
    private readonly IOptions<TasksOptions> _tasksOptions;
    private readonly ILogger<TaskFolderMigration> _logger;

    public TaskFolderMigration(
        IVaultReorganizationService reorganizationService,
        INoteRepository noteRepository,
        IOptions<VaultOptions> vaultOptions,
        IOptions<TasksOptions> tasksOptions,
        ILogger<TaskFolderMigration> logger)
    {
        // `noteRepository` is accepted for shape/DI-registration
        // consistency with every other DotNotes.Core orchestration type
        // (and in case a future revision needs it), but every actual file
        // operation here goes through either IVaultReorganizationService
        // (for wikilink-rewriting moves) or the raw filesystem directly
        // (for the parts of this migration - detecting exact on-disk
        // casing, moving non-.md content, the temp-name case-rename hop -
        // that INoteRepository has no API surface for at all).
        _ = noteRepository;
        _reorganizationService = reorganizationService;
        _vaultOptions = vaultOptions;
        _tasksOptions = tasksOptions;
        _logger = logger;
    }

    /// <summary>
    /// Runs the migration. Never throws - every failure is caught and
    /// logged so a broken/locked legacy folder can't block app startup.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var vaultRoot = _vaultOptions.Value.RootPath;
            if (string.IsNullOrEmpty(vaultRoot) || !Directory.Exists(vaultRoot))
            {
                _logger.LogDebug("Tasks folder migration: skipped - vault root does not exist yet.");
                return;
            }

            var configuredFolder = _tasksOptions.Value.Folder;

            if (string.Equals(configuredFolder, "Task", StringComparison.Ordinal))
            {
                await MergeLegacyRootFoldersAsync(vaultRoot, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogDebug("Tasks folder migration: folder-rename step skipped - Tasks:Folder is not \"Task\".");
            }

            // Independent of the folder-rename step above (finding #3): a
            // vault still deliberately configured with a non-"Task" folder
            // (e.g. the v0.2.0 default Tasks:Folder=tasks) still had a
            // v0.2.0-style "archive" subfolder under *that* folder, and
            // every task in it must still be folded into "Completed" or it
            // silently stops counting as completed.
            if (!string.IsNullOrWhiteSpace(configuredFolder))
            {
                await MergeArchiveIntoCompletedAsync(vaultRoot, configuredFolder, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tasks folder migration failed; continuing startup without it.");
        }
    }

    private async Task MergeLegacyRootFoldersAsync(string vaultRoot, CancellationToken cancellationToken)
    {
        var actualNames = GetActualChildDirectoryNames(vaultRoot);

        // Exact (ordinal) match only, per finding #2: a user's own "Tasks"
        // (capital, plural), "TASKS", "Task/archive" (different casing), etc
        // are their own folders and must never be silently merged - only
        // the two literal legacy names v0.2.0 could have produced
        // ("task" and "tasks") are matched.
        //
        // Sorted for deterministic behaviour regardless of filesystem
        // enumeration order: "task" (the singular, closer to the target
        // name) is processed before "tasks" if both somehow exist, so the
        // first becomes "Task" via a plain rename and the second is merged
        // into it.
        var legacyNames = actualNames
            .Where(n => n is "task" or "tasks")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        foreach (var legacyName in legacyNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RenameOrMergeIntoAsync(vaultRoot, sourceRelative: legacyName, destinationRelative: "Task", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MergeArchiveIntoCompletedAsync(string vaultRoot, string tasksFolder, CancellationToken cancellationToken)
    {
        var tasksFolderFull = Path.Combine(vaultRoot, tasksFolder.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(tasksFolderFull))
        {
            return;
        }

        // Exact (ordinal) match only, per finding #2/#3: "Archive" or
        // "ARCHIVE" is the user's own folder, not v0.2.0's convention.
        var hasArchive = GetActualChildDirectoryNames(tasksFolderFull).Any(n => n == "archive");
        if (!hasArchive)
        {
            return;
        }

        await RenameOrMergeIntoAsync(
            vaultRoot,
            sourceRelative: $"{tasksFolder}/archive",
            destinationRelative: $"{tasksFolder}/{TaskFolders.Completed}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// If nothing already exists at <paramref name="destinationRelative"/>,
    /// renames <paramref name="sourceRelative"/> straight to it (via
    /// <see cref="IVaultReorganizationService.MoveFolderAsync"/> so incoming
    /// wikilinks are rewritten) - except when the two names differ only by
    /// case, which always goes through <see cref="CaseOnlyRenameViaTempHopAsync"/>
    /// instead (see its remarks for why). Otherwise, merges
    /// <paramref name="sourceRelative"/>'s children into the existing
    /// <paramref name="destinationRelative"/> one at a time.
    /// </summary>
    private async Task RenameOrMergeIntoAsync(string vaultRoot, string sourceRelative, string destinationRelative, CancellationToken cancellationToken)
    {
        var destinationFull = Path.Combine(vaultRoot, destinationRelative.Replace('/', Path.DirectorySeparatorChar));
        var destinationParent = Path.GetDirectoryName(destinationFull) ?? vaultRoot;
        var destinationName = Path.GetFileName(destinationRelative);
        var destinationExists = Directory.Exists(destinationParent) &&
            GetActualChildDirectoryNames(destinationParent).Any(n => string.Equals(n, destinationName, StringComparison.Ordinal));

        if (!destinationExists)
        {
            // Finding #1: on a case-insensitive filesystem (Windows/macOS),
            // 'sourceRelative' and 'destinationRelative' differing only by
            // case are the very same physical directory - a direct
            // MoveFolderAsync call can throw either
            // DestinationAlreadyExistsException (INoteRepository sees the
            // destination "already exist") or InvalidNotePathException
            // ("into itself or one of its own descendants" - the exact
            // failure mode FileSystemNoteRepository.MoveFolderAsync hits,
            // since its own-descendant check compares names
            // case-insensitively on Windows). Rather than special-casing
            // both of those exception types - fragile, and impossible to
            // exercise on a case-sensitive dev/CI filesystem, where neither
            // ever gets thrown for this pair of names - detect a case-only
            // rename purely from the two strings up front and always route
            // it through the explicit temp-name hop, which is correct (if
            // one extra hop) on every platform.
            var isCaseOnlyRename =
                !string.Equals(sourceRelative, destinationRelative, StringComparison.Ordinal) &&
                string.Equals(sourceRelative, destinationRelative, StringComparison.OrdinalIgnoreCase);

            if (isCaseOnlyRename)
            {
                await CaseOnlyRenameViaTempHopAsync(vaultRoot, sourceRelative, destinationRelative, cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                await _reorganizationService.MoveFolderAsync(sourceRelative, destinationRelative, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Tasks folder migration: renamed '{Source}' to '{Destination}'.", sourceRelative, destinationRelative);
                return;
            }
            catch (DestinationAlreadyExistsException)
            {
                // Defensive fallback: the up-front destinationExists check
                // above (an ordinal on-disk-name comparison) missed a
                // destination that turned out to already exist after all -
                // merge children instead of losing the source folder.
                _logger.LogWarning(
                    "Tasks folder migration: '{Destination}' already existed when renaming '{Source}'; merging children instead.", destinationRelative, sourceRelative);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tasks folder migration: failed to rename '{Source}' to '{Destination}'; leaving it in place.", sourceRelative, destinationRelative);
                return;
            }
        }

        await MergeChildrenAsync(vaultRoot, sourceRelative, destinationRelative, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renames <paramref name="sourceRelative"/> to <paramref name="destinationRelative"/>
    /// - names that differ only by case - by hopping through a throwaway
    /// temp name (<c>.task-migrating-&lt;guid&gt;</c>) instead of a single
    /// direct rename, so the OS is forced to actually update the stored
    /// casing even on a case-insensitive filesystem where a single-step
    /// rename would otherwise look like a no-op or a self-collision.
    /// Prefers two <see cref="IVaultReorganizationService.MoveFolderAsync"/>
    /// hops (source -&gt; temp, temp -&gt; destination) so nested files,
    /// non-<c>.md</c> content, and incoming wikilinks all survive exactly
    /// as a normal folder move would preserve them; falls back to a raw
    /// <see cref="Directory.Move(string, string)"/> hop only if the
    /// reorg-service hop itself fails (e.g. mid-hop I/O error - wikilinks
    /// pointing at the folder's notes are not rewritten in that fallback
    /// path, since nothing about the file system move itself failed).
    /// </summary>
    private async Task CaseOnlyRenameViaTempHopAsync(string vaultRoot, string sourceRelative, string destinationRelative, CancellationToken cancellationToken)
    {
        var tempRelative = $".task-migrating-{Guid.NewGuid():N}";

        try
        {
            await _reorganizationService.MoveFolderAsync(sourceRelative, tempRelative, cancellationToken).ConfigureAwait(false);
            await _reorganizationService.MoveFolderAsync(tempRelative, destinationRelative, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Tasks folder migration: case-renamed '{Source}' to '{Destination}' via a temp name.", sourceRelative, destinationRelative);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Tasks folder migration: reorg-service temp hop failed while case-renaming '{Source}' to '{Destination}'; falling back to a raw filesystem move.", sourceRelative, destinationRelative);
        }

        var sourceFull = Path.Combine(vaultRoot, sourceRelative.Replace('/', Path.DirectorySeparatorChar));
        var tempFull = Path.Combine(vaultRoot, tempRelative);
        var destinationFull = Path.Combine(vaultRoot, destinationRelative.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            if (Directory.Exists(tempFull))
            {
                // The reorg-service attempt above got as far as the first
                // hop before failing on the second - finish from there
                // rather than trying (and failing) to move a source that
                // no longer exists under its original name.
                Directory.Move(tempFull, destinationFull);
            }
            else if (Directory.Exists(sourceFull) && !Directory.Exists(destinationFull))
            {
                Directory.Move(sourceFull, tempFull);
                Directory.Move(tempFull, destinationFull);
            }

            _logger.LogInformation(
                "Tasks folder migration: case-renamed '{Source}' to '{Destination}' via a raw filesystem temp hop.", sourceRelative, destinationRelative);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tasks folder migration: failed to case-rename '{Source}' to '{Destination}'.", sourceRelative, destinationRelative);
        }
    }

    /// <summary>
    /// Moves every child (file or subfolder) of <paramref name="sourceRelative"/>
    /// into <paramref name="destinationRelative"/> individually - never
    /// overwriting an existing item at the destination (a collision is
    /// logged and the colliding item is left where it is) - then deletes
    /// <paramref name="sourceRelative"/> if it ends up empty.
    /// </summary>
    private async Task MergeChildrenAsync(string vaultRoot, string sourceRelative, string destinationRelative, CancellationToken cancellationToken)
    {
        var sourceFull = Path.Combine(vaultRoot, sourceRelative.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(sourceFull))
        {
            return;
        }

        foreach (var childDirFull in Directory.EnumerateDirectories(sourceFull).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(childDirFull);
            var childSourceRelative = $"{sourceRelative}/{name}";
            var childDestinationRelative = $"{destinationRelative}/{name}";
            var childDestinationFull = Path.Combine(vaultRoot, childDestinationRelative.Replace('/', Path.DirectorySeparatorChar));

            if (Directory.Exists(childDestinationFull) || File.Exists(childDestinationFull))
            {
                _logger.LogWarning(
                    "Tasks folder migration: leaving '{Source}' in place - '{Destination}' already exists.", childSourceRelative, childDestinationRelative);
                continue;
            }

            try
            {
                await _reorganizationService.MoveFolderAsync(childSourceRelative, childDestinationRelative, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Tasks folder migration: moved folder '{Source}' to '{Destination}'.", childSourceRelative, childDestinationRelative);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tasks folder migration: failed to move folder '{Source}' to '{Destination}'.", childSourceRelative, childDestinationRelative);
            }
        }

        foreach (var childFileFull in Directory.EnumerateFiles(sourceFull).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(childFileFull);
            var childSourceRelative = $"{sourceRelative}/{name}";
            var childDestinationRelative = $"{destinationRelative}/{name}";
            var childDestinationFull = Path.Combine(vaultRoot, childDestinationRelative.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(childDestinationFull) || Directory.Exists(childDestinationFull))
            {
                _logger.LogWarning(
                    "Tasks folder migration: leaving '{Source}' in place - '{Destination}' already exists.", childSourceRelative, childDestinationRelative);
                continue;
            }

            try
            {
                if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    // Goes through INoteRepository/IVaultReorganizationService
                    // so incoming wikilinks to this note are rewritten.
                    await _reorganizationService.MoveNoteAsync(childSourceRelative, childDestinationRelative, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Non-markdown content (images, attachments, ...)
                    // INoteRepository has no notion of - move it directly.
                    Directory.CreateDirectory(Path.GetDirectoryName(childDestinationFull)!);
                    File.Move(childFileFull, childDestinationFull);
                }

                _logger.LogInformation("Tasks folder migration: moved '{Source}' to '{Destination}'.", childSourceRelative, childDestinationRelative);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tasks folder migration: failed to move '{Source}' to '{Destination}'.", childSourceRelative, childDestinationRelative);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(sourceFull).Any())
        {
            try
            {
                Directory.Delete(sourceFull);
                _logger.LogInformation("Tasks folder migration: removed now-empty legacy folder '{Source}'.", sourceRelative);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tasks folder migration: failed to remove empty legacy folder '{Source}'.", sourceRelative);
            }
        }
        else
        {
            _logger.LogWarning(
                "Tasks folder migration: legacy folder '{Source}' left in place with unmerged item(s) due to name collisions.", sourceRelative);
        }
    }

    private static List<string> GetActualChildDirectoryNames(string parentFull) =>
        Directory.Exists(parentFull)
            ? Directory.EnumerateDirectories(parentFull).Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToList()
            : new List<string>();
}
