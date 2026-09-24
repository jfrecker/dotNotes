using System.Globalization;
using System.Text.RegularExpressions;
using DotNotes.Core.Config;
using DotNotes.Core.Notes;
using DotNotes.Core.Reorganization;
using Microsoft.Extensions.Options;

namespace DotNotes.Core.Tasks;

/// <summary>
/// Default <see cref="ITaskService"/> implementation: read-modify-write
/// over <see cref="INoteRepository"/>/<see cref="IVaultReorganizationService"/>,
/// per docs/features/tasks-kanban/PLAN.md §3. All writes are serialized
/// through one <see cref="SemaphoreSlim"/> and always re-read the fresh
/// on-disk file before applying a patch, so a board edit merges with
/// whatever the editor last saved. After every write, the fresh content is
/// pushed into <see cref="ITaskIndex"/> synchronously (via
/// <see cref="ITaskIndex.NoteSaved"/>) so the method's own return value -
/// and the very next caller's read - are immediately consistent, without
/// waiting for the file-watcher.
/// </summary>
public sealed class TaskService : ITaskService
{
    private readonly INoteRepository _noteRepository;
    private readonly ITaskIndex _taskIndex;
    private readonly IVaultReorganizationService _reorganizationService;
    private readonly IOptions<TasksOptions> _options;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public TaskService(
        INoteRepository noteRepository,
        ITaskIndex taskIndex,
        IVaultReorganizationService reorganizationService,
        IOptions<TasksOptions> options)
    {
        _noteRepository = noteRepository;
        _taskIndex = taskIndex;
        _reorganizationService = reorganizationService;
        _options = options;
    }

    public IReadOnlyList<TaskItem> List(TaskFilter filter)
    {
        return _taskIndex.GetAll()
            .Where(t => Matches(t, filter))
            .OrderBy(ConfiguredStatusIndex)
            .ThenBy(t => t, TaskOrdering.Comparer)
            .ToArray();
    }

    public TaskBoard GetBoard(TaskFilter filter)
    {
        var matches = List(filter);
        var effectiveStatuses = TaskStatuses.GetEffectiveStatuses(_options.Value);

        var byColumn = new Dictionary<string, List<TaskItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var status in effectiveStatuses)
        {
            byColumn[status] = new List<TaskItem>();
        }

        // `matches` is already ordered column-then-Comparer (see List), so
        // appending in iteration order keeps each column's own list sorted.
        foreach (var task in matches)
        {
            var column = TaskStatuses.ClassifyColumn(_options.Value, task.Status);
            byColumn[column].Add(task);
        }

        var columns = effectiveStatuses
            .Select(status => new TaskColumn(status, byColumn[status], TaskStatuses.IsBacklogColumnName(_options.Value, status)))
            .ToArray();

        return new TaskBoard(columns, _taskIndex.Revision);
    }

    public IReadOnlyList<TaskItem> Search(string query, int limit) => Search(query, limit, includeCompleted: false);

    public IReadOnlyList<TaskItem> Search(string query, int limit, bool includeCompleted)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return Array.Empty<TaskItem>();
        }

        return _taskIndex.GetAll()
            .Where(t => includeCompleted || !t.Completed)
            .Select(t => (Task: t, Score: ComputeSearchScore(t, query)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Task.Id, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => x.Task)
            .ToArray();
    }

    public TaskItem? GetById(string id) => _taskIndex.GetById(id);

    public async Task<TaskItem> CreateAsync(TaskCreateRequest request, CancellationToken cancellationToken = default)
    {
        var title = NormalizeInline(request.Title);
        if (title.Length == 0)
        {
            throw new TaskValidationException("Title must not be empty.");
        }

        var status = CanonicalizeStatus(NormalizeInline(request.Status));
        var priority = CanonicalizePriority(NormalizeInline(request.Priority));

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var allTasks = _taskIndex.GetAll();
            var id = GenerateNextId(allTasks);

            var folder = NormalizeFolder(string.IsNullOrWhiteSpace(request.Folder) ? _options.Value.Folder : request.Folder);

            if (TaskFolders.IsInCompletedFolder(folder))
            {
                throw new TaskValidationException($"Cannot create a task inside a '{TaskFolders.Completed}' folder ('{folder}').");
            }

            var fileName = TaskFileNames.BuildFileName(id, title);
            var path = CombinePath(folder, fileName);

            if (await _noteRepository.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
            {
                throw new TaskValidationException($"A note already exists at '{path}'; cannot create task '{id}' there.");
            }

            var now = DateTimeOffset.UtcNow;
            var columnTasks = allTasks.Where(t => !t.Completed &&
                string.Equals(TaskStatuses.ClassifyColumn(_options.Value, t.Status), TaskStatuses.ClassifyColumn(_options.Value, status), StringComparison.OrdinalIgnoreCase));
            var ordinal = TaskOrdering.NextOrdinalForNewTask(columnTasks);

            var frontmatter = new TaskFrontmatterData
            {
                Id = id,
                Title = title,
                Status = status,
                Assignee = NormalizeList(request.Assignee) ?? Array.Empty<string>(),
                Labels = NormalizeList(request.Labels) ?? Array.Empty<string>(),
                Priority = priority,
                Milestone = NullIfEmpty(NormalizeInline(request.Milestone)),
                Dependencies = NormalizeList(request.Dependencies) ?? Array.Empty<string>(),
                CreatedDate = now,
                UpdatedDate = now,
                Ordinal = ordinal,
            };

            var body = "\n";
            if (!string.IsNullOrWhiteSpace(request.Description))
            {
                body = TaskMarkdown.SetDescription(body, request.Description);
            }

            if (request.AcceptanceCriteria is { Count: > 0 })
            {
                body = TaskMarkdown.SetAcceptanceCriteria(body, request.AcceptanceCriteria.Select(text => (text, false)).ToArray());
            }

            var content = SerializeChecked(new TaskDocument { Frontmatter = frontmatter, Body = body });

            var writeResult = await _noteRepository.SaveAsync(path, content, cancellationToken).ConfigureAwait(false);
            _taskIndex.NoteSaved(writeResult.Path, content, writeResult.UpdatedAt);

            return _taskIndex.GetByPath(writeResult.Path)
                ?? throw new InvalidOperationException($"Task index did not pick up newly created task '{id}' at '{writeResult.Path}'.");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<TaskItem> UpdateAsync(string id, TaskUpdate update, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = _taskIndex.GetById(id) ?? throw new TaskNotFoundException(id);
            var path = existing.Path;

            var (fm, body, hasBom) = await ReadFreshDocumentOrThrowAsync(id, path, cancellationToken).ConfigureAwait(false);

            string? newTitle = fm.Title;
            if (update.Title is not null)
            {
                var trimmed = NormalizeInline(update.Title);
                if (trimmed.Length == 0)
                {
                    throw new TaskValidationException("Title must not be empty.");
                }

                newTitle = trimmed;
            }

            // Re-supplying the task's *current* status is always allowed, even
            // if it's not a configured one (a task with a hand-written
            // unknown status can still be edited); switching to an unknown
            // status stays a validation error.
            var newStatus = update.Status is null
                ? fm.Status
                : CanonicalizeStatus(NormalizeInline(update.Status), fm.Status);
            var newPriority = update.Priority is null
                ? fm.Priority
                : NullIfEmpty(NormalizeInline(update.Priority)) is { } requestedPriority ? CanonicalizePriority(requestedPriority) : null;
            var newAssignee = NormalizeList(update.Assignee) ?? fm.Assignee;
            var newLabels = NormalizeList(update.Labels) ?? fm.Labels;
            var newMilestone = update.Milestone is null ? fm.Milestone : NullIfEmpty(NormalizeInline(update.Milestone));
            var newDependencies = NormalizeList(update.Dependencies) ?? fm.Dependencies;
            var newOrdinal = update.Ordinal ?? fm.Ordinal;

            if (update.Description is not null)
            {
                body = TaskMarkdown.SetDescription(body, update.Description);
            }

            body = ApplyAcceptanceCriteriaUpdate(body, update);
            body = ApplyOptionalSectionUpdate(body, update.ImplementationPlan, update.PlanAppend, TaskMarkdown.GetImplementationPlan, TaskMarkdown.SetImplementationPlan);
            body = ApplyOptionalSectionUpdate(body, update.ImplementationNotes, update.NotesAppend, TaskMarkdown.GetImplementationNotes, TaskMarkdown.SetImplementationNotes);

            if (update.FinalSummary is not null)
            {
                body = TaskMarkdown.SetFinalSummary(body, update.FinalSummary);
            }

            var updatedFrontmatter = new TaskFrontmatterData
            {
                Id = fm.Id,
                Title = newTitle,
                Status = newStatus,
                Assignee = newAssignee,
                Reporter = fm.Reporter,
                CreatedDate = fm.CreatedDate,
                UpdatedDate = DateTimeOffset.UtcNow,
                Labels = newLabels,
                Milestone = newMilestone,
                Dependencies = newDependencies,
                Priority = newPriority,
                Ordinal = newOrdinal,
                UnknownFields = fm.UnknownFields,
            };

            var newContent = SerializeChecked(new TaskDocument { Frontmatter = updatedFrontmatter, Body = body, HasBom = hasBom });

            var titleChanged = update.Title is not null && !string.Equals(fm.Title ?? string.Empty, newTitle, StringComparison.Ordinal);
            var finalPath = path;

            if (titleChanged && TaskFileNames.IsIdDerivedFileName(path, fm.Id))
            {
                // Save the patched content at the OLD path first, so the
                // move carries the caller's other field changes along with
                // the rename - a single logical PATCH, not two separate
                // writes a concurrent reader could observe mid-way.
                await _noteRepository.SaveAsync(path, newContent, cancellationToken).ConfigureAwait(false);

                var directory = GetDirectory(path);
                var newFileName = TaskFileNames.BuildFileName(fm.Id, newTitle);
                var destination = CombinePath(directory, newFileName);

                if (!string.Equals(destination, path, StringComparison.Ordinal))
                {
                    var moveResult = await _reorganizationService.MoveNoteAsync(path, destination, cancellationToken).ConfigureAwait(false);
                    finalPath = moveResult.Path;
                }

                var finalNote = await _noteRepository.GetAsync(finalPath, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Task '{id}' vanished immediately after being renamed to '{finalPath}'.");
                _taskIndex.NoteSaved(finalPath, finalNote.Content, finalNote.UpdatedAt);
            }
            else
            {
                var writeResult = await _noteRepository.SaveAsync(path, newContent, cancellationToken).ConfigureAwait(false);
                _taskIndex.NoteSaved(writeResult.Path, newContent, writeResult.UpdatedAt);
            }

            return _taskIndex.GetByPath(finalPath)
                ?? throw new InvalidOperationException($"Task index did not pick up the update to task '{id}' at '{finalPath}'.");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public Task<TaskItem> MoveAsync(string id, string status, int? index, CancellationToken cancellationToken = default) =>
        MoveAsync(id, status, index, beforeTaskId: null, cancellationToken);

    public async Task<TaskItem> MoveAsync(string id, string status, int? index, string? beforeTaskId, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = _taskIndex.GetById(id) ?? throw new TaskNotFoundException(id);
            var allTasks = _taskIndex.GetAll();

            // `targetStatus` is the raw frontmatter value that would be
            // written if the moved task's status changes at all: one of the
            // effective statuses, or the task's own current (possibly
            // unrecognised, hand-written) raw status re-supplied as-is.
            // `targetColumn` is the board column that value actually
            // belongs to (TaskStatuses.ClassifyColumn) - these two are NOT
            // the same thing for an unrecognised raw status (e.g. moving a
            // task to its own current "Blocked" status): the column is
            // Backlog (every empty/unrecognised/Backlog raw status lands
            // there), even though the raw status written is "Blocked", not
            // "Backlog". Using the raw status itself as if it were a column
            // name here was the bug - it could never match any task's
            // ClassifyColumn result, so the "destination column" below came
            // up empty for every such move.
            var targetStatus = CanonicalizeStatus(NormalizeInline(status), existing.Status);
            var targetColumn = TaskStatuses.ClassifyColumn(_options.Value, targetStatus);
            var currentColumn = TaskStatuses.ClassifyColumn(_options.Value, existing.Status);
            var movingIntoBacklog = TaskStatuses.IsBacklogColumnName(_options.Value, targetColumn);
            var currentlyInBacklog = TaskStatuses.IsBacklogColumnName(_options.Value, currentColumn);

            // Column *membership* (TaskStatuses.ClassifyColumn), not raw
            // status equality - the Backlog column mixes tasks holding many
            // different raw statuses (empty, "Backlog", or anything
            // unrecognised), and ordinal math must treat them as one column.
            var destinationColumn = allTasks
                .Where(t => !t.Completed &&
                            !string.Equals(t.Id, existing.Id, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(TaskStatuses.ClassifyColumn(_options.Value, t.Status), targetColumn, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t, TaskOrdering.Comparer)
                .ToArray();

            // beforeTaskId wins over index: it names a card the caller can
            // see (unlike a positional index, which is ambiguous when the
            // caller's view is filtered).
            if (!string.IsNullOrWhiteSpace(beforeTaskId))
            {
                var beforeIndex = Array.FindIndex(
                    destinationColumn,
                    t => string.Equals(t.Id, beforeTaskId.Trim(), StringComparison.OrdinalIgnoreCase));
                if (beforeIndex < 0)
                {
                    throw new TaskValidationException(
                        $"beforeId '{beforeTaskId}' is not another active task in the '{targetColumn}' column.");
                }

                index = beforeIndex;
            }

            // No-op: already in this column at the requested position.
            // Skipping the write avoids bumping updated_date and racing an
            // open editor's expectedUpdatedAt for nothing.
            if (!existing.Completed && string.Equals(currentColumn, targetColumn, StringComparison.OrdinalIgnoreCase))
            {
                var currentIndex = destinationColumn.Count(t => TaskOrdering.Comparer.Compare(t, existing) < 0);
                var requestedIndex = index is null or < 0 || index > destinationColumn.Length ? destinationColumn.Length : index.Value;
                if (currentIndex == requestedIndex)
                {
                    return existing;
                }
            }

            var changedOrdinals = TaskOrdering.ComputeMove(destinationColumn, index, existing.Id);

            // A reorder within the Backlog column (task already belongs
            // there, still moving within it - including a move to its own
            // unrecognised raw status) keeps the task's own raw status
            // untouched - only its ordinal changes. Moving into Backlog
            // from elsewhere, or into any other column, sets the status
            // field to the target's raw status value.
            var newStatusForMovedTask = movingIntoBacklog && currentlyInBacklog ? null : targetStatus;

            foreach (var (taskId, ordinal) in changedOrdinals)
            {
                var isMovedTask = string.Equals(taskId, existing.Id, StringComparison.OrdinalIgnoreCase);
                if (isMovedTask)
                {
                    var unchanged = (newStatusForMovedTask is null || string.Equals(existing.Status, newStatusForMovedTask, StringComparison.Ordinal)) &&
                                    existing.Ordinal is { } currentOrdinal && currentOrdinal == ordinal;
                    if (unchanged)
                    {
                        continue;
                    }
                }

                await ApplyOrdinalAndStatusAsync(taskId, ordinal, isMovedTask ? newStatusForMovedTask : null, cancellationToken).ConfigureAwait(false);
            }

            return _taskIndex.GetById(existing.Id) ?? throw new TaskNotFoundException(id);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Design note: the note is moved into its Completed subfolder
    /// <i>first</i>, and the status/updated_date frontmatter is written to
    /// it at its new path second. If the process crashes between those two
    /// steps, the task ends up filed under Completed/ but still shows its
    /// pre-completion status - a visibly-inconsistent but easily fixable
    /// state (re-running CompleteAsync finishes the job, since a task
    /// already in a Completed folder just gets its status re-applied). The
    /// alternative order (write status first, then move) would instead risk
    /// leaving a task showing status "Done" while still sitting in its
    /// original column/folder if the crash happened between those two steps
    /// - which is worse, since nothing about the board would suggest the
    /// move never finished.
    /// </summary>
    public async Task<TaskItem> CompleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = _taskIndex.GetById(id) ?? throw new TaskNotFoundException(id);
            var completedStatus = TaskStatuses.GetEffectiveCompletedStatus(_options.Value);

            if (TaskFolders.IsInCompletedFolder(existing.Path))
            {
                // Idempotent: already filed under a Completed folder -
                // never nest Completed/Completed. Just make sure the status
                // reflects completion too.
                if (string.Equals(existing.Status, completedStatus, StringComparison.Ordinal))
                {
                    return existing;
                }

                await ApplyOrdinalAndStatusAsync(existing.Id, null, completedStatus, cancellationToken).ConfigureAwait(false);
                return _taskIndex.GetById(id) ?? throw new InvalidOperationException($"Task index did not pick up the completion of task '{id}'.");
            }

            var directory = GetDirectory(existing.Path);
            var completedFolder = string.IsNullOrEmpty(directory) ? TaskFolders.Completed : $"{directory}/{TaskFolders.Completed}";
            var fileName = System.IO.Path.GetFileName(existing.Path.Replace('\\', '/'));
            var destination = await ResolveCollisionFreeDestinationAsync(completedFolder, fileName, cancellationToken).ConfigureAwait(false);

            await _reorganizationService.MoveNoteAsync(existing.Path, destination, cancellationToken).ConfigureAwait(false);
            await ApplyOrdinalAndStatusAsync(existing.Id, null, completedStatus, cancellationToken).ConfigureAwait(false);

            return _taskIndex.GetById(id) ?? throw new InvalidOperationException($"Task index did not pick up the completion of task '{id}'.");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>Vault-relative <paramref name="folder"/>/<paramref name="fileName"/>, or, if that already exists, the same name with " (2)", " (3)", ... inserted before the extension until a free path is found. Never overwrites.</summary>
    private async Task<string> ResolveCollisionFreeDestinationAsync(string folder, string fileName, CancellationToken cancellationToken)
    {
        var candidate = CombinePath(folder, fileName);
        if (!await _noteRepository.ExistsAsync(candidate, cancellationToken).ConfigureAwait(false))
        {
            return candidate;
        }

        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var extension = System.IO.Path.GetExtension(fileName);
        for (var suffix = 2; ; suffix++)
        {
            var attemptPath = CombinePath(folder, $"{stem} ({suffix}){extension}");
            if (!await _noteRepository.ExistsAsync(attemptPath, cancellationToken).ConfigureAwait(false))
            {
                return attemptPath;
            }
        }
    }

    public async Task<TaskItem> ConvertNoteAsync(string path, string? status, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var note = await _noteRepository.GetAsync(path, cancellationToken).ConfigureAwait(false)
                ?? throw new TaskNotFoundException(path, $"No note exists at '{path}'.");

            if (TaskMarkdown.IsTask(note.Content))
            {
                throw new TaskValidationException($"The note at '{path}' is already a task.");
            }

            var canonicalStatus = CanonicalizeStatus(NormalizeInline(status));
            var allTasks = _taskIndex.GetAll();
            var id = GenerateNextId(allTasks);

            // A note that already has (non-task) frontmatter, e.g. `tags:`,
            // keeps those keys; its body is everything after that block.
            TaskMarkdown.TryParseAnyFrontmatter(note.Content, out var existingDocument);
            var existing = existingDocument?.Frontmatter;
            var body = existingDocument?.Body ?? note.Content;

            var fileNameStem = System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
            var title = TaskFileNames.Sanitize(NormalizeInline(string.IsNullOrWhiteSpace(existing?.Title) ? fileNameStem : existing.Title));

            var now = DateTimeOffset.UtcNow;
            var columnTasks = allTasks.Where(t => !t.Completed &&
                string.Equals(TaskStatuses.ClassifyColumn(_options.Value, t.Status), TaskStatuses.ClassifyColumn(_options.Value, canonicalStatus), StringComparison.OrdinalIgnoreCase));
            var ordinal = TaskOrdering.NextOrdinalForNewTask(columnTasks);

            var frontmatter = new TaskFrontmatterData
            {
                Id = id,
                Title = title,
                Status = canonicalStatus,
                Assignee = NormalizeList(existing?.Assignee) ?? Array.Empty<string>(),
                Reporter = existing?.Reporter,
                CreatedDate = existing?.CreatedDate ?? now,
                UpdatedDate = now,
                Labels = NormalizeList(existing?.Labels) ?? Array.Empty<string>(),
                Milestone = NullIfEmpty(NormalizeInline(existing?.Milestone)),
                Dependencies = NormalizeList(existing?.Dependencies) ?? Array.Empty<string>(),
                Priority = existing?.Priority,
                Ordinal = ordinal,
                UnknownFields = existing?.UnknownFields ?? Array.Empty<TaskFrontmatterField>(),
            };

            var newContent = SerializeChecked(new TaskDocument
            {
                Frontmatter = frontmatter,
                Body = body,
                HasBom = existingDocument?.HasBom ?? false,
            });
            await _noteRepository.SaveAsync(path, newContent, cancellationToken).ConfigureAwait(false);

            var directory = GetDirectory(path);
            var newFileName = TaskFileNames.BuildFileName(id, title);
            var destination = CombinePath(directory, newFileName);

            var finalPath = string.Equals(destination, path, StringComparison.Ordinal)
                ? path
                : (await _reorganizationService.MoveNoteAsync(path, destination, cancellationToken).ConfigureAwait(false)).Path;

            var finalNote = await _noteRepository.GetAsync(finalPath, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Converted task '{id}' vanished immediately after being renamed to '{finalPath}'.");
            _taskIndex.NoteSaved(finalPath, finalNote.Content, finalNote.UpdatedAt);

            return _taskIndex.GetById(id) ?? throw new InvalidOperationException($"Task index did not pick up converted task '{id}'.");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task ApplyOrdinalAndStatusAsync(string taskId, double? ordinal, string? status, CancellationToken cancellationToken)
    {
        var current = _taskIndex.GetById(taskId);
        if (current is null)
        {
            // Best-effort: the id came from the index moments ago under
            // the same semaphore, so this should not happen in practice.
            return;
        }

        var (fm, body, hasBom) = await ReadFreshDocumentOrThrowAsync(taskId, current.Path, cancellationToken).ConfigureAwait(false);

        var updated = new TaskFrontmatterData
        {
            Id = fm.Id,
            Title = fm.Title,
            Status = status ?? fm.Status,
            Assignee = fm.Assignee,
            Reporter = fm.Reporter,
            CreatedDate = fm.CreatedDate,
            UpdatedDate = DateTimeOffset.UtcNow,
            Labels = fm.Labels,
            Milestone = fm.Milestone,
            Dependencies = fm.Dependencies,
            Priority = fm.Priority,
            Ordinal = ordinal ?? fm.Ordinal,
            UnknownFields = fm.UnknownFields,
        };

        var newContent = SerializeChecked(new TaskDocument { Frontmatter = updated, Body = body, HasBom = hasBom });
        var writeResult = await _noteRepository.SaveAsync(current.Path, newContent, cancellationToken).ConfigureAwait(false);
        _taskIndex.NoteSaved(writeResult.Path, newContent, writeResult.UpdatedAt);
    }

    private async Task<(TaskFrontmatterData Frontmatter, string Body, bool HasBom)> ReadFreshDocumentOrThrowAsync(
        string id, string path, CancellationToken cancellationToken)
    {
        var note = await _noteRepository.GetAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw new TaskNotFoundException(id, $"Task '{id}''s note no longer exists at '{path}'.");

        if (!TaskMarkdown.TryParse(note.Content, out var document) || document is null)
        {
            throw new TaskNotFoundException(id, $"The note at '{path}' is no longer a task.");
        }

        return (document.Frontmatter, document.Body, document.HasBom);
    }

    private static string ApplyAcceptanceCriteriaUpdate(string body, TaskUpdate update)
    {
        var anyAcUpdate = update.AcceptanceCriteria is not null
            || update.AcceptanceCriteriaAdd is { Count: > 0 }
            || update.AcceptanceCriteriaRemove is { Count: > 0 }
            || update.AcceptanceCriteriaCheck is { Count: > 0 }
            || update.AcceptanceCriteriaUncheck is { Count: > 0 };

        if (!anyAcUpdate)
        {
            return body;
        }

        var current = update.AcceptanceCriteria is not null
            ? update.AcceptanceCriteria.ToList()
            : TaskMarkdown.GetAcceptanceCriteria(body).Select(c => (c.Text, c.Checked)).ToList();

        if (update.AcceptanceCriteriaAdd is { Count: > 0 })
        {
            foreach (var text in update.AcceptanceCriteriaAdd)
            {
                current.Add((text, false));
            }
        }

        if (update.AcceptanceCriteriaRemove is { Count: > 0 })
        {
            foreach (var index in update.AcceptanceCriteriaRemove.OrderByDescending(i => i))
            {
                if (index >= 1 && index <= current.Count)
                {
                    current.RemoveAt(index - 1);
                }
            }
        }

        if (update.AcceptanceCriteriaCheck is { Count: > 0 })
        {
            foreach (var index in update.AcceptanceCriteriaCheck)
            {
                if (index >= 1 && index <= current.Count)
                {
                    current[index - 1] = (current[index - 1].Item1, true);
                }
            }
        }

        if (update.AcceptanceCriteriaUncheck is { Count: > 0 })
        {
            foreach (var index in update.AcceptanceCriteriaUncheck)
            {
                if (index >= 1 && index <= current.Count)
                {
                    current[index - 1] = (current[index - 1].Item1, false);
                }
            }
        }

        return TaskMarkdown.SetAcceptanceCriteria(body, current);
    }

    private static string ApplyOptionalSectionUpdate(
        string body,
        string? replace,
        string? append,
        Func<string, string?> get,
        Func<string, string, string> set)
    {
        if (replace is not null)
        {
            return set(body, replace);
        }

        if (!string.IsNullOrEmpty(append))
        {
            var current = get(body) ?? string.Empty;
            var combined = current.Length == 0 ? append : current + "\n\n" + append;
            return set(body, combined);
        }

        return body;
    }

    private bool Matches(TaskItem task, TaskFilter filter)
    {
        if (!filter.IncludeCompleted && task.Completed)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Status) && !string.Equals(task.Status, filter.Status, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Label) && !task.Labels.Any(l => string.Equals(l, filter.Label, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Assignee) && !task.Assignee.Any(a => string.Equals(a, filter.Assignee, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Priority) && !string.Equals(task.Priority, filter.Priority, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Milestone) && !string.Equals(task.Milestone, filter.Milestone, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Query) && ComputeSearchScore(task, filter.Query) <= 0)
        {
            return false;
        }

        return true;
    }

    private int ConfiguredStatusIndex(TaskItem task)
    {
        var effectiveStatuses = TaskStatuses.GetEffectiveStatuses(_options.Value);
        var column = TaskStatuses.ClassifyColumn(_options.Value, task.Status);
        for (var i = 0; i < effectiveStatuses.Count; i++)
        {
            if (string.Equals(effectiveStatuses[i], column, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static int ComputeSearchScore(TaskItem task, string query)
    {
        var q = query.Trim();
        if (q.Length == 0)
        {
            return 0;
        }

        var score = 0;
        if (task.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (task.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (task.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        if (task.Labels.Any(l => l.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            score += 5;
        }

        if (task.Assignee.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            score += 5;
        }

        return score;
    }

    /// <summary>
    /// Resolves <paramref name="requested"/> to an effective status/column
    /// name (see <see cref="TaskStatuses.GetEffectiveStatuses"/>), case
    /// insensitively. An empty/whitespace request resolves to
    /// <see cref="TasksOptions.DefaultStatus"/> if configured, else the
    /// first effective status (Backlog). <paramref name="alsoAllowed"/> - a
    /// task's own current raw status - is accepted as-is even when it isn't
    /// an effective status, since re-supplying a hand-written/unrecognised
    /// status unchanged must always be allowed. Anything else not among the
    /// effective statuses throws <see cref="TaskValidationException"/>.
    /// </summary>
    private string CanonicalizeStatus(string? requested, string? alsoAllowed = null)
    {
        var effectiveStatuses = TaskStatuses.GetEffectiveStatuses(_options.Value);

        if (string.IsNullOrWhiteSpace(requested))
        {
            if (!string.IsNullOrWhiteSpace(_options.Value.DefaultStatus))
            {
                var matchedDefault = effectiveStatuses.FirstOrDefault(s => string.Equals(s, _options.Value.DefaultStatus, StringComparison.OrdinalIgnoreCase));
                return matchedDefault ?? _options.Value.DefaultStatus!;
            }

            return effectiveStatuses[0];
        }

        var match = effectiveStatuses.FirstOrDefault(s => string.Equals(s, requested, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        if (alsoAllowed is not null && string.Equals(alsoAllowed, requested, StringComparison.OrdinalIgnoreCase))
        {
            return alsoAllowed;
        }

        throw new TaskValidationException($"Status '{requested}' is not one of the configured statuses ({string.Join(", ", effectiveStatuses)}).");
    }

    private string? CanonicalizePriority(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }

        var priorities = _options.Value.Priorities;
        var match = priorities.FirstOrDefault(p => string.Equals(p, requested, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        throw new TaskValidationException($"Priority '{requested}' is not one of the configured priorities ({string.Join(", ", priorities)}).");
    }

    private string GenerateNextId(IReadOnlyList<TaskItem> allTasks)
    {
        var prefix = string.IsNullOrWhiteSpace(_options.Value.IdPrefix) ? "TASK" : _options.Value.IdPrefix;
        var prefixDash = prefix + "-";
        long max = 0;

        foreach (var task in allTasks)
        {
            if (!task.Id.StartsWith(prefixDash, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = task.Id[prefixDash.Length..];

            // Dotted subtask ids (e.g. "12.1") are ignored for max - only a
            // pure top-level numeric id counts, per
            // docs/features/tasks-kanban/PLAN.md's "ID generation" rule.
            if (long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > max)
            {
                max = n;
            }
        }

        return $"{prefix}-{max + 1}";
    }

    private static readonly Regex ControlRun = new(@"\s*[\p{Cc}\u2028\u2029]\s*", RegexOptions.Compiled);

    /// <summary>
    /// Frontmatter scalars are single-line values: collapses any run
    /// containing a line break, tab or other control character to one space
    /// and trims. <see langword="null"/> becomes the empty string.
    /// </summary>
    internal static string NormalizeInline(string? value) =>
        value is null ? string.Empty : ControlRun.Replace(LoneSurrogate.Replace(value, "\uFFFD"), " ").Trim();

    private static readonly Regex LoneSurrogate = new(
        @"[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]",
        RegexOptions.Compiled);

    /// <summary>Normalises each item (see <see cref="NormalizeInline"/>) and silently drops the ones that end up empty. <see langword="null"/> stays <see langword="null"/> ("unchanged").</summary>
    private static IReadOnlyList<string>? NormalizeList(IReadOnlyList<string>? items)
    {
        if (items is null)
        {
            return null;
        }

        var result = new List<string>(items.Count);
        foreach (var item in items)
        {
            var normalized = NormalizeInline(item);
            if (normalized.Length > 0)
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>
    /// Serializes <paramref name="document"/> and proves the result parses
    /// back as the same task <i>before</i> anything touches disk, so a value
    /// the YAML emitter can't represent can never leave an unparseable file
    /// (which would silently make the task vanish from the index).
    /// </summary>
    private static string SerializeChecked(TaskDocument document)
    {
        var content = TaskMarkdown.Serialize(document);
        var expected = document.Frontmatter;

        if (!TaskMarkdown.TryParse(content, out var parsed) || parsed is null)
        {
            throw new InvalidOperationException(
                $"Refusing to write task '{expected.Id}': the serialized frontmatter does not parse back as a task (a field value cannot be represented in YAML).");
        }

        var actual = parsed.Frontmatter;
        var same = string.Equals(actual.Id, expected.Id, StringComparison.Ordinal) &&
                   string.Equals(actual.Status, expected.Status, StringComparison.Ordinal) &&
                   string.Equals(actual.Title ?? string.Empty, expected.Title ?? string.Empty, StringComparison.Ordinal) &&
                   string.Equals(actual.Milestone ?? string.Empty, expected.Milestone ?? string.Empty, StringComparison.Ordinal) &&
                   string.Equals(actual.Priority ?? string.Empty, expected.Priority ?? string.Empty, StringComparison.Ordinal) &&
                   actual.Assignee.SequenceEqual(expected.Assignee, StringComparer.Ordinal) &&
                   actual.Labels.SequenceEqual(expected.Labels, StringComparer.Ordinal) &&
                   actual.Dependencies.SequenceEqual(expected.Dependencies, StringComparer.Ordinal) &&
                   Nullable.Equals(actual.Ordinal, expected.Ordinal);

        if (!same)
        {
            throw new InvalidOperationException(
                $"Refusing to write task '{expected.Id}': the serialized frontmatter does not round-trip to the same field values (a field value cannot be represented in YAML).");
        }

        return content;
    }

    private static string NormalizeFolder(string? folder) => (folder ?? string.Empty).Trim('/', '\\');

    private static string CombinePath(string folder, string fileName) =>
        string.IsNullOrEmpty(folder) ? fileName : $"{folder}/{fileName}";

    private static string GetDirectory(string path)
    {
        var normalized = path.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : normalized[..lastSlash];
    }
}
