using DotNotes.Core.Config;
using DotNotes.Core.Notes;
using Microsoft.Extensions.Options;

namespace DotNotes.Core.Tasks;

/// <summary>
/// Default <see cref="ITaskIndex"/> implementation: an in-memory
/// path -&gt; <see cref="TaskItem"/> cache guarded by a single lock, the
/// same style as <see cref="Search.InMemorySearchIndex"/>. A pure derived
/// cache (docs/features/tasks-kanban/PLAN.md §2) - safe to discard/rebuild
/// at any time via <see cref="RebuildAsync"/>.
/// </summary>
/// <remarks>
/// <see cref="IVaultChangeListener.NoteChanged"/> doesn't carry a
/// timestamp (and <c>VaultWatcherService</c>'s startup scan populates this
/// index through it, not <see cref="RebuildAsync"/>), so the file's real
/// last-write time is read from disk when the vault root is known, falling
/// back to <see cref="DateTimeOffset.UtcNow"/> only if that fails.
/// <see cref="ITaskService"/> skips the extra stat for its own writes via
/// <see cref="NoteSaved"/>, which carries the timestamp
/// <see cref="Notes.INoteRepository.SaveAsync"/> already returned.
/// </remarks>
public sealed class InMemoryTaskIndex : ITaskIndex
{
    private readonly INoteRepository _noteRepository;
    private readonly IOptions<TasksOptions> _options;
    private readonly string? _vaultRootPath;
    private readonly object _lock = new();

    private readonly Dictionary<string, TaskItem> _tasksByPath = new(StringComparer.OrdinalIgnoreCase);
    // id -> every path currently holding a task with that id. Normally one;
    // more than one only if a file was copied (duplicate id). GetById
    // resolves deterministically to the lowest path (ordinal, ignore case),
    // and removing a path falls back to whichever other path still holds the
    // id instead of orphaning it.
    private readonly Dictionary<string, SortedSet<string>> _pathsById = new(StringComparer.OrdinalIgnoreCase);

    private long _revision;

    public InMemoryTaskIndex(
        INoteRepository noteRepository,
        IOptions<TasksOptions> options,
        IOptions<VaultOptions>? vaultOptions = null)
    {
        _noteRepository = noteRepository;
        _options = options;
        _vaultRootPath = vaultOptions?.Value.RootPath;
    }

    public long Revision
    {
        get
        {
            lock (_lock)
            {
                return _revision;
            }
        }
    }

    public IReadOnlyList<TaskItem> GetAll()
    {
        lock (_lock)
        {
            return _tasksByPath.Values.ToArray();
        }
    }

    public TaskItem? GetById(string id)
    {
        lock (_lock)
        {
            return _pathsById.TryGetValue(id, out var paths) && paths.Count > 0 && _tasksByPath.TryGetValue(paths.Min!, out var task)
                ? task
                : null;
        }
    }

    public TaskItem? GetByPath(string path)
    {
        lock (_lock)
        {
            return _tasksByPath.TryGetValue(path, out var task) ? task : null;
        }
    }

    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        var tree = await _noteRepository.GetTreeAsync(cancellationToken).ConfigureAwait(false);
        var filePaths = new List<string>();
        CollectFilePaths(tree, filePaths);

        var newTasksByPath = new Dictionary<string, TaskItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Reading the whole file (rather than a partial "peek") is
            // simplest and correct; INoteRepository has no partial-read
            // API, and a second file-open per note to peek wouldn't save
            // meaningfully at a personal vault's scale. The cheap
            // QuickLooksLikeFrontmatter rejection still avoids running the
            // full YAML parse on every non-task note.
            var note = await _noteRepository.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (note is null || !TaskMarkdown.QuickLooksLikeFrontmatter(note.Content))
            {
                continue;
            }

            if (!TaskMarkdown.TryParse(note.Content, out var document) || document is null)
            {
                continue;
            }

            var task = BuildTaskItem(path, document, note.UpdatedAt);
            newTasksByPath[path] = task;
        }

        lock (_lock)
        {
            _tasksByPath.Clear();
            foreach (var (path, task) in newTasksByPath)
            {
                _tasksByPath[path] = task;
            }

            _pathsById.Clear();
            foreach (var (path, task) in newTasksByPath)
            {
                AddId(task.Id, path);
            }

            _revision++;
        }
    }

    public void NoteChanged(string path, string content) => ApplyChange(path, content, ReadLastWriteTime(path));

    public void NoteSaved(string path, string content, DateTimeOffset updatedAt) => ApplyChange(path, content, updatedAt);

    public void NoteDeleted(string path)
    {
        lock (_lock)
        {
            if (_tasksByPath.Remove(path, out var removed))
            {
                RemoveId(removed.Id, path);
                _revision++;
            }
        }
    }

    private void ApplyChange(string path, string content, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(content);

        TaskDocument? document = null;
        var isTask = TaskMarkdown.QuickLooksLikeFrontmatter(content) &&
                     TaskMarkdown.TryParse(content, out document) &&
                     document is not null;

        lock (_lock)
        {
            var wasTask = _tasksByPath.TryGetValue(path, out var previous);

            if (!isTask || document is null)
            {
                if (wasTask)
                {
                    _tasksByPath.Remove(path);
                    if (previous is not null)
                    {
                        RemoveId(previous.Id, path);
                    }

                    _revision++;
                }

                return;
            }

            var task = BuildTaskItem(path, document, updatedAt);

            // If the frontmatter id at this path changed, drop this path from
            // the old id's set (another path holding that id, if any, stays
            // resolvable).
            if (wasTask && previous is not null && !string.Equals(previous.Id, task.Id, StringComparison.OrdinalIgnoreCase))
            {
                RemoveId(previous.Id, path);
            }

            _tasksByPath[path] = task;
            AddId(task.Id, path);
            _revision++;
        }
    }

    private void AddId(string id, string path)
    {
        if (!_pathsById.TryGetValue(id, out var paths))
        {
            paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            _pathsById[id] = paths;
        }

        paths.Add(path);
    }

    private void RemoveId(string id, string path)
    {
        if (_pathsById.TryGetValue(id, out var paths))
        {
            paths.Remove(path);
            if (paths.Count == 0)
            {
                _pathsById.Remove(id);
            }
        }
    }

    private TaskItem BuildTaskItem(string path, TaskDocument document, DateTimeOffset updatedAt)
    {
        var fm = document.Frontmatter;
        var title = string.IsNullOrWhiteSpace(fm.Title) ? DeriveTitleFromFileName(path, fm.Id) : fm.Title!;

        return new TaskItem
        {
            Id = fm.Id,
            Title = title,
            Status = fm.Status,
            Assignee = fm.Assignee,
            Labels = fm.Labels,
            Reporter = fm.Reporter,
            Priority = fm.Priority,
            Milestone = fm.Milestone,
            Dependencies = fm.Dependencies,
            CreatedDate = fm.CreatedDate,
            UpdatedDate = fm.UpdatedDate,
            Ordinal = fm.Ordinal,
            Path = path,
            UpdatedAt = updatedAt,
            Archived = IsArchived(path),
            Description = TaskMarkdown.GetDescription(document.Body),
            AcceptanceCriteria = TaskMarkdown.GetAcceptanceCriteria(document.Body),
            ImplementationPlan = TaskMarkdown.GetImplementationPlan(document.Body),
            ImplementationNotes = TaskMarkdown.GetImplementationNotes(document.Body),
            FinalSummary = TaskMarkdown.GetFinalSummary(document.Body),
        };
    }

    private DateTimeOffset ReadLastWriteTime(string path)
    {
        if (!string.IsNullOrEmpty(_vaultRootPath))
        {
            try
            {
                var fullPath = System.IO.Path.Combine(_vaultRootPath, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (File.Exists(fullPath))
                {
                    return new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Fall through to "now" - the timestamp is display-only.
            }
        }

        return DateTimeOffset.UtcNow;
    }

    private bool IsArchived(string path)
    {
        var folder = (_options.Value.Folder ?? string.Empty).Trim('/', '\\');
        if (folder.Length == 0)
        {
            return false;
        }

        var archivePrefix = $"{folder}/archive/";
        var normalizedPath = path.Replace('\\', '/');
        return normalizedPath.StartsWith(archivePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string DeriveTitleFromFileName(string path, string id)
    {
        var fileName = System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
        var prefix = id + " - ";
        return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? fileName[prefix.Length..] : fileName;
    }

    private static void CollectFilePaths(IReadOnlyList<NoteTreeEntry> entries, List<string> filePaths)
    {
        foreach (var entry in entries)
        {
            if (entry.Type == NoteEntryType.File)
            {
                filePaths.Add(entry.Path);
            }
            else if (entry.Children is not null)
            {
                CollectFilePaths(entry.Children, filePaths);
            }
        }
    }
}
