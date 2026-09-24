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
        Assert.Equal("To Do", task.Status);
        Assert.Equal("tasks/TASK-1 - My First Task.md", task.Path);
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
    public async Task CreateAsync_IdIncrementsPastGapsAndArchivedTasks()
    {
        await _service.CreateAsync(new TaskCreateRequest { Title = "One" }); // TASK-1
        var second = await _service.CreateAsync(new TaskCreateRequest { Title = "Two" }); // TASK-2
        await _service.ArchiveAsync(second.Id);

        var third = await _service.CreateAsync(new TaskCreateRequest { Title = "Three" });
        Assert.Equal("TASK-3", third.Id); // archived TASK-2 still counts toward the max, never reused.
    }

    [Fact]
    public async Task CreateAsync_IdPrefixMatchIsCaseInsensitive_AndIgnoresDottedSubtaskIds()
    {
        await _repository.SaveAsync(
            "tasks/task-5 - Existing.md",
            "---\nid: task-5\nstatus: To Do\n---\n");
        await _repository.SaveAsync(
            "tasks/TASK-5.1 - Subtask.md",
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

        Assert.Equal("tasks/TASK-1 - New Title.md", updated.Path);
        Assert.False(await _repository.ExistsAsync("tasks/TASK-1 - Old Title.md"));

        var referrer = await _repository.GetAsync("notes/referrer.md");
        Assert.Contains("TASK-1 - New Title", referrer!.Content);
    }

    [Fact]
    public async Task UpdateAsync_TitleChange_DoesNotRename_WhenFileWasManuallyRenamed()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "Old Title" });
        await _repository.MoveAsync(task.Path, "tasks/Custom Name.md");
        await _taskIndex.RebuildAsync();

        var updated = await _service.UpdateAsync(task.Id, new TaskUpdate { Title = "New Title" });

        Assert.Equal("tasks/Custom Name.md", updated.Path);
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
        var c = await _service.CreateAsync(new TaskCreateRequest { Title = "C" }); // To Do

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

    [Fact]
    public async Task ArchiveAsync_MovesUnderArchiveFolder_AndSetsArchivedFlag()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "X" });
        var archived = await _service.ArchiveAsync(task.Id);

        Assert.Equal("tasks/archive/TASK-1 - X.md", archived.Path);
        Assert.True(archived.Archived);

        var listed = _service.List(TaskFilter.Empty);
        Assert.DoesNotContain(listed, t => t.Id == task.Id);

        var listedWithArchived = _service.List(new TaskFilter { IncludeArchived = true });
        Assert.Contains(listedWithArchived, t => t.Id == task.Id);
    }

    [Fact]
    public async Task ArchiveAsync_RewritesIncomingWikilinks_WhenBareTitleWouldOtherwiseBecomeAmbiguous()
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "X" }); // tasks/TASK-1 - X.md

        // Archiving only changes the note's *folder*, not its file name, so
        // a link normally still resolves to the same note afterward (even
        // by bare-title fallback) and is deliberately left untouched
        // (docs/06-DATA-MODEL.md's "Minimal" rewrite rule). A second,
        // unrelated note sharing the exact same bare title is added here so
        // that once the task moves under archive/, resolving by bare title
        // alone becomes genuinely ambiguous and picks the *other* note -
        // forcing this path-style link to actually need rewriting.
        await _repository.SaveAsync("elsewhere/TASK-1 - X.md", "unrelated");
        await _repository.SaveAsync("notes/referrer.md", "[[tasks/TASK-1 - X]]");

        await _service.ArchiveAsync(task.Id);

        var referrer = await _repository.GetAsync("notes/referrer.md");
        Assert.Contains("[[tasks/archive/TASK-1 - X]]", referrer!.Content);
    }

    [Fact]
    public async Task ConvertNoteAsync_AddsFrontmatterAndRenamesKeepingBody()
    {
        await _repository.SaveAsync("ideas/My Idea.md", "Some existing free-form text.");

        var converted = await _service.ConvertNoteAsync("ideas/My Idea.md", null);

        Assert.Equal("TASK-1", converted.Id);
        Assert.Equal("My Idea", converted.Title);
        Assert.Equal("To Do", converted.Status);
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
        _taskIndex.NoteChanged("tasks/TASK-1 - Fix login bug.md", "---\nid: TASK-1\nstatus: To Do\n---\nSomething about bug tracking in the body.\n");
        _taskIndex.NoteChanged("tasks/TASK-2 - Unrelated.md", "---\nid: TASK-2\nstatus: To Do\n---\nThis mentions a bug deep in the description.\n");

        var results = _service.Search("bug", 10);

        Assert.Equal(2, results.Count);
        Assert.Equal("TASK-1", results[0].Id); // title hit outranks description-only hit.
    }

    [Fact]
    public void GetBoard_AddsTrailingColumnForUnknownStatus()
    {
        _taskIndex.NoteChanged("tasks/TASK-1 - X.md", "---\nid: TASK-1\nstatus: Blocked\n---\n");

        var board = _service.GetBoard(TaskFilter.Empty);

        Assert.Equal(new[] { "To Do", "In Progress", "Done", "Blocked" }, board.Columns.Select(c => c.Status).ToArray());
    }
}
