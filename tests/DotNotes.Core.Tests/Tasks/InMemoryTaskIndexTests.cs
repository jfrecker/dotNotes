using DotNotes.Core.Config;
using DotNotes.Core.Notes;
using DotNotes.Core.Tasks;
using Microsoft.Extensions.Options;

namespace DotNotes.Core.Tests.Tasks;

public sealed class InMemoryTaskIndexTests : IDisposable
{
    private readonly DirectoryInfo _vaultDirectory;
    private readonly FileSystemNoteRepository _repository;
    private readonly InMemoryTaskIndex _index;

    public InMemoryTaskIndexTests()
    {
        _vaultDirectory = Directory.CreateTempSubdirectory("dotnotes-task-index-tests-");
        _repository = new FileSystemNoteRepository(Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName }));
        _index = new InMemoryTaskIndex(_repository, Options.Create(new TasksOptions()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_vaultDirectory.FullName))
        {
            _vaultDirectory.Delete(recursive: true);
        }
    }

    private const string TaskContent = "---\nid: TASK-1\nstatus: To Do\n---\nBody text.\n";
    private const string PlainNoteContent = "# Just a note\n\nNo frontmatter here.";

    [Fact]
    public async Task NoteChanged_WithVaultRoot_UsesFileLastWriteTimeNotNow()
    {
        // VaultWatcherService's startup scan feeds the index via NoteChanged,
        // so without this every task would report the app-start time.
        var vaultOptions = Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName });
        var index = new InMemoryTaskIndex(_repository, Options.Create(new TasksOptions()), vaultOptions);
        const string path = "tasks/TASK-1 - Hello.md";
        await _repository.SaveAsync(path, TaskContent);
        var fileTime = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(_vaultDirectory.FullName, "tasks", "TASK-1 - Hello.md"), fileTime);

        index.NoteChanged(path, TaskContent);

        Assert.Equal(new DateTimeOffset(fileTime), index.GetByPath(path)!.UpdatedAt);
    }

    [Fact]
    public async Task RebuildAsync_FindsTaskNotesOnly()
    {
        await _repository.SaveAsync("tasks/TASK-1 - Hello.md", TaskContent);
        await _repository.SaveAsync("notes/plain.md", PlainNoteContent);

        await _index.RebuildAsync();

        var all = _index.GetAll();
        Assert.Single(all);
        Assert.Equal("TASK-1", all[0].Id);
    }

    [Fact]
    public void NoteChanged_AddsTask_AndBumpsRevision()
    {
        var before = _index.Revision;
        _index.NoteChanged("tasks/TASK-1 - Hello.md", TaskContent);

        Assert.True(_index.Revision > before);
        Assert.NotNull(_index.GetById("TASK-1"));
        Assert.NotNull(_index.GetByPath("tasks/TASK-1 - Hello.md"));
    }

    [Fact]
    public void NoteChanged_ForPlainNote_DoesNotBumpRevisionOrIndex()
    {
        var before = _index.Revision;
        _index.NoteChanged("notes/plain.md", PlainNoteContent);

        Assert.Equal(before, _index.Revision);
        Assert.Empty(_index.GetAll());
    }

    [Fact]
    public void NoteChanged_TaskBecomesPlainNote_RemovesFromIndex()
    {
        _index.NoteChanged("tasks/TASK-1 - Hello.md", TaskContent);
        Assert.NotNull(_index.GetById("TASK-1"));

        _index.NoteChanged("tasks/TASK-1 - Hello.md", "No longer a task.");

        Assert.Null(_index.GetById("TASK-1"));
        Assert.Empty(_index.GetAll());
    }

    [Fact]
    public void NoteDeleted_RemovesTask_AndBumpsRevision()
    {
        _index.NoteChanged("tasks/TASK-1 - Hello.md", TaskContent);
        var before = _index.Revision;

        _index.NoteDeleted("tasks/TASK-1 - Hello.md");

        Assert.True(_index.Revision > before);
        Assert.Null(_index.GetById("TASK-1"));
    }

    [Fact]
    public void NoteDeleted_UnknownPath_IsNoOp()
    {
        var before = _index.Revision;
        _index.NoteDeleted("tasks/does-not-exist.md");
        Assert.Equal(before, _index.Revision);
    }

    [Fact]
    public void ArchivedFlag_SetForPathsUnderConfiguredArchiveFolder()
    {
        _index.NoteChanged("tasks/archive/TASK-1 - Hello.md", TaskContent);
        var task = _index.GetById("TASK-1");

        Assert.NotNull(task);
        Assert.True(task!.Archived);
    }

    [Fact]
    public void TitleFallback_DerivedFromFileNameWhenFrontmatterTitleMissing()
    {
        _index.NoteChanged("tasks/TASK-1 - My Great Title.md", TaskContent);
        var task = _index.GetById("TASK-1");

        Assert.Equal("My Great Title", task!.Title);
    }

    [Fact]
    public void GetById_IsCaseInsensitive()
    {
        _index.NoteChanged("tasks/TASK-1 - Hello.md", TaskContent);
        Assert.NotNull(_index.GetById("task-1"));
    }

    [Fact]
    public void NoteSaved_UsesSuppliedTimestamp()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _index.NoteSaved("tasks/TASK-1 - Hello.md", TaskContent, timestamp);

        Assert.Equal(timestamp, _index.GetById("TASK-1")!.UpdatedAt);
    }
}
