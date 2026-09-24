using DotNotes.Core.Config;
using DotNotes.Core.Links;
using DotNotes.Core.Notes;
using DotNotes.Core.Reorganization;
using DotNotes.Core.Tasks;
using DotNotes.Core.Vault;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotNotes.Core.Tests.Tasks;

/// <summary>
/// Exercises <see cref="TaskService"/> end to end over a real
/// <see cref="FileSystemNoteRepository"/>, <see cref="InMemoryTaskIndex"/>,
/// <see cref="InMemoryLinkIndex"/> and <see cref="VaultReorganizationService"/>
/// against a throwaway temp vault - same convention as
/// <c>VaultReorganizationServiceTests</c>.
/// </summary>
public sealed class TaskServiceTests : IDisposable
{
    private readonly DirectoryInfo _vaultDirectory;
    private readonly FileSystemNoteRepository _repository;
    private readonly InMemoryTaskIndex _taskIndex;
    private readonly InMemoryLinkIndex _linkIndex;
    private readonly VaultReorganizationService _reorganizationService;
    private readonly TasksOptions _options;
    private readonly TaskService _service;

    public TaskServiceTests()
    {
        _vaultDirectory = Directory.CreateTempSubdirectory("dotnotes-task-service-tests-");
        var vaultOptions = Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName });
        _repository = new FileSystemNoteRepository(vaultOptions);
        _options = new TasksOptions();
        var optionsWrapper = Options.Create(_options);
        _taskIndex = new InMemoryTaskIndex(_repository, optionsWrapper);
        _linkIndex = new InMemoryLinkIndex(_repository);
        _reorganizationService = new VaultReorganizationService(
            _repository,
            new IVaultChangeListener[] { _taskIndex, _linkIndex },
            NullLogger<VaultReorganizationService>.Instance);
        _service = new TaskService(_repository, _taskIndex, _reorganizationService, optionsWrapper);
    }

    public void Dispose()
    {
        if (Directory.Exists(_vaultDirectory.FullName))
        {
            _vaultDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_CreatesNoteWithExpectedFrontmatterAndFileName()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "My First Task" });

        Assert.Equal("TASK-1", task.Id);
        Assert.Equal("Backlog", task.Status);
        Assert.Equal("Task/TASK-1 - My First Task.md", task.Path);
        Assert.Equal(1000d, task.Ordinal);

        var note = await _repository.GetAsync(task.Path);
        Assert.NotNull(note);
        Assert.True(TaskMarkdown.IsTask(note!.Content));
    }

    [Fact]
    public async Task CreateAsync_EmptyTitle_ThrowsValidation()
    {
        await Assert.ThrowsAsync<TaskValidationException>(() => _service.CreateAsync(new TaskCreateRequest { Title = "   " }));
    }

    [Fact]
    public async Task CreateAsync_UnknownStatus_ThrowsValidation()
    {
        await Assert.ThrowsAsync<TaskValidationException>(() =>
            _service.CreateAsync(new TaskCreateRequest { Title = "X", Status = "Nonexistent" }));
    }

    [Fact]
    public async Task CreateAsync_StatusIsCaseInsensitive()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "X", Status = "in progress" });
        Assert.Equal("In Progress", task.Status);
    }

    [Fact]
    public async Task CreateAsync_UnknownPriority_ThrowsValidation()
    {
        await Assert.ThrowsAsync<TaskValidationException>(() =>
            _service.CreateAsync(new TaskCreateRequest { Title = "X", Priority = "urgent" }));
    }

    [Fact]
    public async Task CreateAsync_FolderInsideCompletedFolder_ThrowsValidation()
    {
        await Assert.ThrowsAsync<TaskValidationException>(() =>
            _service.CreateAsync(new TaskCreateRequest { Title = "X", Folder = "Task/Completed" }));

        await Assert.ThrowsAsync<TaskValidationException>(() =>
            _service.CreateAsync(new TaskCreateRequest { Title = "X", Folder = "Task/Completed/Sub" }));
    }

    [Fact]
    public async Task CreateAsync_WithFolder_CreatesUnderThatFolder()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "X", Folder = "Projects/Alpha" });

        Assert.Equal("Projects/Alpha/TASK-1 - X.md", task.Path);
    }

    [Fact]
    public async Task CreateAsync_IdIncrementsPastGapsAndCompletedTasks()
    {
        await _service.CreateAsync(new TaskCreateRequest { Title = "One" }); // TASK-1
        var second = await _service.CreateAsync(new TaskCreateRequest { Title = "Two" }); // TASK-2
        await _service.CompleteAsync(second.Id);

        var third = await _service.CreateAsync(new TaskCreateRequest { Title = "Three" });
        Assert.Equal("TASK-3", third.Id); // completed TASK-2 still counts toward the max, never reused.
    }

    [Fact]
    public async Task CreateAsync_IdPrefixMatchIsCaseInsensitive_AndIgnoresDottedSubtaskIds()
    {
        await _repository.SaveAsync(
            "Task/task-5 - Existing.md",
            "---\nid: task-5\nstatus: To Do\n---\n");
        await _repository.SaveAsync(
            "Task/TASK-5.1 - Subtask.md",
            "---\nid: TASK-5.1\nstatus: To Do\n---\n");
        await _taskIndex.RebuildAsync();

        var created = await _service.CreateAsync(new TaskCreateRequest { Title = "Next" });
        Assert.Equal("TASK-6", created.Id);
    }

    [Fact]
    public async Task CreateAsync_SecondColumnTaskGetsHigherOrdinal()
    {
        var first = await _service.CreateAsync(new TaskCreateRequest { Title = "First" });
        var second = await _service.CreateAsync(new TaskCreateRequest { Title = "Second" });

        Assert.True(second.Ordinal > first.Ordinal);
    }

    [Fact]
    public async Task UpdateAsync_PatchesOnlySuppliedFields()
    {
        var created = await _service.CreateAsync(new TaskCreateRequest { Title = "Original", Description = "Desc" });

        var updated = await _service.UpdateAsync(created.Id, new TaskUpdate { Priority = "high" });

        Assert.Equal("Original", updated.Title);
        Assert.Equal("Desc", updated.Description);
        Assert.Equal("high", updated.Priority);
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<TaskNotFoundException>(() => _service.UpdateAsync("TASK-999", new TaskUpdate()));
    }

    [Fact]
    public async Task UpdateAsync_EmptyStringClearsMilestone()
    {
        var created = await _service.CreateAsync(new TaskCreateRequest { Title = "X", Milestone = "v1" });
        Assert.Equal("v1", created.Milestone);

        var updated = await _service.UpdateAsync(created.Id, new TaskUpdate { Milestone = "" });
        Assert.Null(updated.Milestone);
    }

    [Fact]
    public async Task UpdateAsync_TitleChange_RenamesFile_AndRewritesIncomingWikilinks()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "Old Title" });
        await _repository.SaveAsync("notes/referrer.md", "See [[TASK-1 - Old Title]] for details.");
        await _linkIndex.RebuildAsync();

        var updated = await _service.UpdateAsync(task.Id, new TaskUpdate { Title = "New Title" });

        Assert.Equal("Task/TASK-1 - New Title.md", updated.Path);
        Assert.False(await _repository.ExistsAsync("Task/TASK-1 - Old Title.md"));

        var referrer = await _repository.GetAsync("notes/referrer.md");
        Assert.Contains("TASK-1 - New Title", referrer!.Content);
    }

    [Fact]
    public async Task UpdateAsync_TitleChange_DoesNotRename_WhenFileWasManuallyRenamed()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "Old Title" });
        await _repository.MoveAsync(task.Path, "Task/Custom Name.md");
        await _taskIndex.RebuildAsync();

        var updated = await _service.UpdateAsync(task.Id, new TaskUpdate { Title = "New Title" });

        Assert.Equal("Task/Custom Name.md", updated.Path);
        Assert.Equal("New Title", updated.Title);
    }

    [Fact]
    public async Task UpdateAsync_AcceptanceCriteria_AddCheckRemove()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "X" });

        var withAdds = await _service.UpdateAsync(task.Id, new TaskUpdate
        {
            AcceptanceCriteriaAdd = new[] { "First", "Second", "Third" }
        });
        Assert.Equal(3, withAdds.AcceptanceCriteria.Count);

        var withCheck = await _service.UpdateAsync(task.Id, new TaskUpdate
        {
            AcceptanceCriteriaCheck = new[] { 1 }
        });
        Assert.True(withCheck.AcceptanceCriteria[0].Checked);

        var withRemove = await _service.UpdateAsync(task.Id, new TaskUpdate
        {
            AcceptanceCriteriaRemove = new[] { 2 }
        });
        Assert.Equal(2, withRemove.AcceptanceCriteria.Count);
        Assert.Equal("First", withRemove.AcceptanceCriteria[0].Text);
        Assert.Equal("Third", withRemove.AcceptanceCriteria[1].Text);
        Assert.Equal(2, withRemove.AcceptanceCriteria[1].Index);
    }

    [Fact]
    public async Task UpdateAsync_NotesAppend_AppendsToExistingNotes()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "X" });
        await _service.UpdateAsync(task.Id, new TaskUpdate { ImplementationNotes = "First line." });
        var updated = await _service.UpdateAsync(task.Id, new TaskUpdate { NotesAppend = "Second line." });

        Assert.Equal("First line.\n\nSecond line.", updated.ImplementationNotes);
    }

    [Fact]
    public async Task MoveAsync_ChangesStatusAndAppendsToEndByDefault()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "X" });
        var moved = await _service.MoveAsync(task.Id, "Done", null);

        Assert.Equal("Done", moved.Status);
    }

    [Fact]
    public async Task MoveAsync_ToSpecificIndex_ReordersColumn()
    {
        var a = await _service.CreateAsync(new TaskCreateRequest { Title = "A", Status = "Done" });
        var b = await _service.CreateAsync(new TaskCreateRequest { Title = "B", Status = "Done" });
        var c = await _service.CreateAsync(new TaskCreateRequest { Title = "C" }); // Backlog

        var moved = await _service.MoveAsync(c.Id, "Done", 0);

        var board = _service.GetBoard(TaskFilter.Empty);
        var doneColumn = board.Columns.Single(col => col.Status == "Done");
        Assert.Equal(new[] { moved.Id, a.Id, b.Id }, doneColumn.Tasks.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task MoveAsync_UnknownStatus_ThrowsValidation()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "X" });
        await Assert.ThrowsAsync<TaskValidationException>(() => _service.MoveAsync(task.Id, "Nowhere", null));
    }

    /// <summary>
    /// Regression test for finding #5: moving a task to its own current
    /// unrecognised raw status (e.g. a hand-written "Blocked") must be
    /// treated as a Backlog-column reorder - honouring beforeId against
    /// other Backlog-column tasks - not silently fail to find any
    /// destination column at all (the bug: the raw status value itself was
    /// used as if it were a column name, which no task's classified column
    /// could ever match).
    /// </summary>
    [Fact]
    public async Task MoveAsync_ToOwnUnrecognisedRawStatus_IsBacklogColumnReorder_HonoursBeforeId()
    {
        var content = "---\nid: TASK-1\nstatus: Blocked\n---\n";
        var writeResult = await _repository.SaveAsync("Task/TASK-1 - X.md", content);
        _taskIndex.NoteSaved(writeResult.Path, content, writeResult.UpdatedAt);

        var other = await _service.CreateAsync(new TaskCreateRequest { Title = "Other" }); // lands in Backlog

        var moved = await _service.MoveAsync("TASK-1", "Blocked", null, other.Id);

        Assert.Equal("Blocked", moved.Status); // raw status preserved, not overwritten to "Backlog"
        var board = _service.GetBoard(TaskFilter.Empty);
        var backlog = board.Columns.Single(c => c.Status == "Backlog");
        Assert.True(backlog.IsBacklog);
        Assert.Equal(new[] { "TASK-1", other.Id }, backlog.Tasks.Select(t => t.Id).ToArray());
    }

    /// <summary>
    /// Regression test for finding #5: moving a lone task (nothing else in
    /// its column) to its own current unrecognised raw status, at its
    /// current position, is a true no-op - it must not bump updated_date or
    /// write anything.
    /// </summary>
    [Fact]
    public async Task MoveAsync_ToOwnUnrecognisedRawStatus_AlreadyAtPosition_IsNoOp()
    {
        var content = "---\nid: TASK-1\nstatus: Blocked\n---\n";
        var writeResult = await _repository.SaveAsync("Task/TASK-1 - X.md", content);
        _taskIndex.NoteSaved(writeResult.Path, content, writeResult.UpdatedAt);

        var moved = await _service.MoveAsync("TASK-1", "Blocked", null);

        Assert.Equal("Blocked", moved.Status);
        var afterMove = await _repository.GetAsync("Task/TASK-1 - X.md");
        Assert.Equal(writeResult.UpdatedAt, afterMove!.UpdatedAt); // untouched on disk
    }

    [Fact]
    public async Task CompleteAsync_MovesUnderCompletedFolder_AndSetsStatusAndFlag()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "X" });
        var completed = await _service.CompleteAsync(task.Id);

        Assert.Equal("Task/Completed/TASK-1 - X.md", completed.Path);
        Assert.True(completed.Completed);
        Assert.Equal("Done", completed.Status);

        var listed = _service.List(TaskFilter.Empty);
        Assert.DoesNotContain(listed, t => t.Id == task.Id);

        var listedWithCompleted = _service.List(new TaskFilter { IncludeCompleted = true });
        Assert.Contains(listedWithCompleted, t => t.Id == task.Id);
    }

    [Fact]
    public async Task CompleteAsync_RootLevelTask_MovesUnderRootCompletedFolder()
    {
        await _repository.SaveAsync("TASK-1 - Root.md", "---\nid: TASK-1\nstatus: To Do\n---\n");
        await _taskIndex.RebuildAsync();

        var completed = await _service.CompleteAsync("TASK-1");

        Assert.Equal("Completed/TASK-1 - Root.md", completed.Path);
    }

    [Fact]
    public async Task CompleteAsync_NestedProjectFolder_MovesIntoSiblingCompletedFolder()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "Nested", Folder = "Task/ProjectX" });

        var completed = await _service.CompleteAsync(task.Id);

        Assert.Equal("Task/ProjectX/Completed/TASK-1 - Nested.md", completed.Path);
    }

    [Fact]
    public async Task CompleteAsync_NameCollision_AppendsNumericSuffix_NeverOverwrites()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "X" });
        await _repository.SaveAsync("Task/Completed/TASK-1 - X.md", "pre-existing content");

        var completed = await _service.CompleteAsync(task.Id);

        Assert.Equal("Task/Completed/TASK-1 - X (2).md", completed.Path);
        var untouched = await _repository.GetAsync("Task/Completed/TASK-1 - X.md");
        Assert.Equal("pre-existing content", untouched!.Content);
    }

    [Fact]
    public async Task CompleteAsync_SecondCollision_UsesNextSuffix()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "X" });
        await _repository.SaveAsync("Task/Completed/TASK-1 - X.md", "one");
        await _repository.SaveAsync("Task/Completed/TASK-1 - X (2).md", "two");

        var completed = await _service.CompleteAsync(task.Id);

        Assert.Equal("Task/Completed/TASK-1 - X (3).md", completed.Path);
    }

    [Fact]
    public async Task CompleteAsync_Idempotent_WhenAlreadyCompleted_DoesNotNest()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "X" });
        var once = await _service.CompleteAsync(task.Id);

        var twice = await _service.CompleteAsync(task.Id);

        Assert.Equal(once.Path, twice.Path);
        Assert.DoesNotContain("Completed/Completed", twice.Path);
    }

    [Fact]
    public async Task CompleteAsync_RewritesIncomingWikilinks_WhenBareTitleWouldOtherwiseBecomeAmbiguous()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "X" }); // Task/TASK-1 - X.md

        // Completing only changes the note's *folder*, not its file name,
        // so a link normally still resolves to the same note afterward
        // (even by bare-title fallback) and is deliberately left untouched
        // (docs/06-DATA-MODEL.md's "Minimal" rewrite rule). A second,
        // unrelated note sharing the exact same bare title is added here so
        // that once the task moves under Completed/, resolving by bare
        // title alone becomes genuinely ambiguous and picks the *other*
        // note - forcing this path-style link to actually need rewriting.
        await _repository.SaveAsync("elsewhere/TASK-1 - X.md", "unrelated");
        await _repository.SaveAsync("notes/referrer.md", "[[Task/TASK-1 - X]]");

        await _service.CompleteAsync(task.Id);

        var referrer = await _repository.GetAsync("notes/referrer.md");
        Assert.Contains("[[Task/Completed/TASK-1 - X]]", referrer!.Content);
    }

    [Fact]
    public async Task ConvertNoteAsync_AddsFrontmatterAndRenamesKeepingBody()
    {
        await _repository.SaveAsync("ideas/My Idea.md", "Some existing free-form text.");

        var converted = await _service.ConvertNoteAsync("ideas/My Idea.md", null);

        Assert.Equal("TASK-1", converted.Id);
        Assert.Equal("My Idea", converted.Title);
        Assert.Equal("Backlog", converted.Status);
        Assert.Equal("ideas/TASK-1 - My Idea.md", converted.Path);
        Assert.Equal("Some existing free-form text.", converted.Description);
    }

    [Fact]
    public async Task ConvertNoteAsync_NoteWithExistingFrontmatter_MergesKeysInsteadOfEmbeddingThem()
    {
        await _repository.SaveAsync("ideas/Tagged.md", "---\ntags:\n  - reading\nlabels:\n  - books\n---\nBody text.\n");

        var converted = await _service.ConvertNoteAsync("ideas/Tagged.md", "in progress");

        Assert.Equal("In Progress", converted.Status);
        Assert.Equal(new[] { "books" }, converted.Labels);
        Assert.Equal("Body text.", converted.Description.Trim());
        var content = (await _repository.GetAsync(converted.Path))!.Content;
        Assert.Contains("tags:\n  - reading", content);
        Assert.Equal(2, content.Split("---\n").Length - 1); // exactly one frontmatter block
    }

    [Fact]
    public async Task UpdateAsync_TitleChangeThatSanitizesToSameFileName_DoesNotFail()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "Plan" });

        var updated = await _service.UpdateAsync(task.Id, new TaskUpdate { Title = "Plan?" });

        Assert.Equal("Plan?", updated.Title);
        Assert.Equal(task.Path, updated.Path);
    }

    [Fact]
    public async Task ConvertNoteAsync_AlreadyATask_ThrowsValidation()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "X" });
        await Assert.ThrowsAsync<TaskValidationException>(() => _service.ConvertNoteAsync(task.Path, null));
    }

    [Fact]
    public async Task ConvertNoteAsync_NoteDoesNotExist_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<TaskNotFoundException>(() => _service.ConvertNoteAsync("nope.md", null));
    }

    [Fact]
    public void Search_RanksIdAndTitleHitsFirst()
    {
        _taskIndex.NoteChanged("Task/TASK-1 - Fix login bug.md", "---\nid: TASK-1\nstatus: To Do\n---\nSomething about bug tracking in the body.\n");
        _taskIndex.NoteChanged("Task/TASK-2 - Unrelated.md", "---\nid: TASK-2\nstatus: To Do\n---\nThis mentions a bug deep in the description.\n");

        var results = _service.Search("bug", 10);

        Assert.Equal(2, results.Count);
        Assert.Equal("TASK-1", results[0].Id); // title hit outranks description-only hit.
    }

    /// <summary>
    /// Regression test for finding #4: the includeCompleted overload of
    /// Search excludes completed tasks by default (same shape as List/GetBoard)
    /// and includes them when explicitly asked.
    /// </summary>
    [Fact]
    public void Search_IncludeCompletedOverload_ExcludesCompletedUnlessRequested()
    {
        _taskIndex.NoteChanged("Task/TASK-1 - Fix login bug.md", "---\nid: TASK-1\nstatus: To Do\n---\n");
        _taskIndex.NoteChanged("Task/Completed/TASK-2 - Fix login redirect.md", "---\nid: TASK-2\nstatus: Done\n---\n");

        Assert.Single(_service.Search("login", 10));
        Assert.Single(_service.Search("login", 10, includeCompleted: false));
        Assert.Equal(2, _service.Search("login", 10, includeCompleted: true).Count);
    }

    [Fact]
    public void GetBoard_UnknownStatus_LandsInBacklogColumn_NoTrailingColumn()
    {
        _taskIndex.NoteChanged("Task/TASK-1 - X.md", "---\nid: TASK-1\nstatus: Blocked\n---\n");

        var board = _service.GetBoard(TaskFilter.Empty);

        Assert.Equal(new[] { "Backlog", "To Do", "In Progress", "Done" }, board.Columns.Select(c => c.Status).ToArray());
        var backlog = board.Columns.Single(c => c.Status == "Backlog");
        Assert.True(backlog.IsBacklog);
        Assert.Contains(backlog.Tasks, t => t.Id == "TASK-1");
        Assert.All(board.Columns.Where(c => c.Status != "Backlog"), c => Assert.False(c.IsBacklog));
    }

    [Fact]
    public void GetBoard_BacklogColumnIsFirst_AndContainsEmptyStatusTasks()
    {
        _taskIndex.NoteChanged("Task/TASK-1 - X.md", "---\nid: TASK-1\nstatus:\n---\n");

        var board = _service.GetBoard(TaskFilter.Empty);

        Assert.Equal("Backlog", board.Columns[0].Status);
        Assert.Contains(board.Columns[0].Tasks, t => t.Id == "TASK-1");
    }
}
