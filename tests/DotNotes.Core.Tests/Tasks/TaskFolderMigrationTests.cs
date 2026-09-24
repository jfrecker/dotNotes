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
/// Exercises <see cref="TaskFolderMigration"/> against a real temp vault
/// directory on disk, same convention as <c>TaskServiceTests</c>.
/// </summary>
public sealed class TaskFolderMigrationTests : IDisposable
{
    private readonly DirectoryInfo _vaultDirectory;
    private readonly FileSystemNoteRepository _repository;
    private readonly VaultReorganizationService _reorganizationService;

    public TaskFolderMigrationTests()
    {
        _vaultDirectory = Directory.CreateTempSubdirectory("dotnotes-task-migration-tests-");
        var vaultOptions = Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName });
        _repository = new FileSystemNoteRepository(vaultOptions);
        var taskIndex = new InMemoryTaskIndex(_repository, Options.Create(new TasksOptions()), vaultOptions);
        var linkIndex = new InMemoryLinkIndex(_repository);
        _reorganizationService = new VaultReorganizationService(
            _repository,
            new IVaultChangeListener[] { taskIndex, linkIndex },
            NullLogger<VaultReorganizationService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_vaultDirectory.FullName))
        {
            _vaultDirectory.Delete(recursive: true);
        }
    }

    private string DiskPath(string relative) => Path.Combine(_vaultDirectory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));

    private TaskFolderMigration CreateMigration(string folder = "Task") =>
        new(
            _reorganizationService,
            _repository,
            Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName }),
            Options.Create(new TasksOptions { Folder = folder }),
            NullLogger<TaskFolderMigration>.Instance);

    [Fact]
    public async Task RunAsync_LowercaseTasksFolder_IsRenamedToTask()
    {
        await _repository.SaveAsync("tasks/TASK-1 - X.md", "---\nid: TASK-1\nstatus: To Do\n---\n");

        await CreateMigration().RunAsync();

        Assert.True(Directory.Exists(DiskPath("Task")));
        Assert.True(await _repository.ExistsAsync("Task/TASK-1 - X.md"));
        Assert.False(Directory.Exists(DiskPath("tasks")));
    }

    [Fact]
    public async Task RunAsync_SingularTaskLowercase_IsRenamedToTask()
    {
        await _repository.SaveAsync("task/TASK-1 - X.md", "---\nid: TASK-1\nstatus: To Do\n---\n");

        await CreateMigration().RunAsync();

        Assert.True(await _repository.ExistsAsync("Task/TASK-1 - X.md"));
    }

    [Fact]
    public async Task RunAsync_BothLegacyFoldersExist_MergedIntoTask_CollisionLeftInPlaceAndLogged()
    {
        await _repository.SaveAsync("task/TASK-1 - X.md", "---\nid: TASK-1\nstatus: To Do\n---\n");
        await _repository.SaveAsync("tasks/TASK-2 - Y.md", "---\nid: TASK-2\nstatus: To Do\n---\n");
        // Colliding name: both legacy folders have a file with the same name.
        await _repository.SaveAsync("tasks/TASK-1 - X.md", "colliding content");

        await CreateMigration().RunAsync();

        Assert.True(await _repository.ExistsAsync("Task/TASK-1 - X.md"));
        Assert.True(await _repository.ExistsAsync("Task/TASK-2 - Y.md"));

        // The colliding file is left behind in whichever legacy folder
        // wasn't renamed to become "Task" - it must not have been silently
        // dropped, and the original "Task/TASK-1 - X.md" must be untouched.
        var original = await _repository.GetAsync("Task/TASK-1 - X.md");
        Assert.NotEqual("colliding content", original!.Content);
        Assert.True(await _repository.ExistsAsync("tasks/TASK-1 - X.md"));
    }

    [Fact]
    public async Task RunAsync_ArchiveMergedIntoCompleted()
    {
        await _repository.SaveAsync("Task/archive/TASK-1 - X.md", "---\nid: TASK-1\nstatus: Done\n---\n");

        await CreateMigration().RunAsync();

        Assert.True(await _repository.ExistsAsync("Task/Completed/TASK-1 - X.md"));
        Assert.False(Directory.Exists(DiskPath("Task/archive")));
    }

    [Fact]
    public async Task RunAsync_ArchiveAndCompletedBothExist_MergedWithoutClobbering()
    {
        await _repository.SaveAsync("Task/archive/TASK-1 - X.md", "---\nid: TASK-1\nstatus: Done\n---\n");
        await _repository.SaveAsync("Task/Completed/TASK-2 - Y.md", "---\nid: TASK-2\nstatus: Done\n---\n");

        await CreateMigration().RunAsync();

        Assert.True(await _repository.ExistsAsync("Task/Completed/TASK-1 - X.md"));
        Assert.True(await _repository.ExistsAsync("Task/Completed/TASK-2 - Y.md"));
    }

    [Fact]
    public async Task RunAsync_IsIdempotent_SecondRunIsNoOp()
    {
        await _repository.SaveAsync("tasks/TASK-1 - X.md", "---\nid: TASK-1\nstatus: To Do\n---\n");
        var migration = CreateMigration();

        await migration.RunAsync();
        await migration.RunAsync();

        Assert.True(await _repository.ExistsAsync("Task/TASK-1 - X.md"));
        Assert.False(Directory.Exists(DiskPath("tasks")));
    }

    [Fact]
    public async Task RunAsync_ConfiguredFolderIsNotTask_IsNoOp()
    {
        await _repository.SaveAsync("tasks/TASK-1 - X.md", "---\nid: TASK-1\nstatus: To Do\n---\n");

        await CreateMigration(folder: "MyTasks").RunAsync();

        Assert.True(Directory.Exists(DiskPath("tasks")));
        Assert.False(Directory.Exists(DiskPath("Task")));
    }

    [Fact]
    public async Task RunAsync_NoLegacyFolders_DoesNothing_AndDoesNotThrow()
    {
        await _repository.SaveAsync("Task/TASK-1 - X.md", "---\nid: TASK-1\nstatus: To Do\n---\n");

        await CreateMigration().RunAsync();

        Assert.True(await _repository.ExistsAsync("Task/TASK-1 - X.md"));
    }

    [Fact]
    public async Task RunAsync_VaultRootMissing_DoesNotThrow()
    {
        var missingRoot = Path.Combine(_vaultDirectory.FullName, "does-not-exist");
        var migration = new TaskFolderMigration(
            _reorganizationService,
            _repository,
            Options.Create(new VaultOptions { RootPath = missingRoot }),
            Options.Create(new TasksOptions { Folder = "Task" }),
            NullLogger<TaskFolderMigration>.Instance);

        await migration.RunAsync();
    }

    [Fact]
    public async Task RunAsync_RewritesIncomingWikilinks_WhenBareTitleWouldOtherwiseBecomeAmbiguous()
    {
        await _repository.SaveAsync("tasks/TASK-1 - X.md", "---\nid: TASK-1\nstatus: To Do\n---\n");
        // A second, unrelated note sharing the exact same bare title makes
        // the path-qualified link genuinely need rewriting once the folder
        // is renamed - otherwise (docs/06-DATA-MODEL.md's "Minimal" rule)
        // it would be left untouched since it still resolves correctly by
        // bare-title fallback alone.
        await _repository.SaveAsync("elsewhere/TASK-1 - X.md", "unrelated");
        await _repository.SaveAsync("notes/referrer.md", "[[tasks/TASK-1 - X]]");

        await CreateMigration().RunAsync();

        var referrer = await _repository.GetAsync("notes/referrer.md");
        Assert.Contains("[[Task/TASK-1 - X]]", referrer!.Content);
    }

    /// <summary>
    /// Regression test for finding #1: the singular legacy "task" folder is
    /// a case-only rename of the target "Task" ("task" vs "Task" differ
    /// only by case), so the migration must always take the explicit
    /// temp-name hop for it (<see cref="TaskFolderMigration"/>'s remarks) -
    /// on a case-insensitive filesystem a single-step
    /// <c>IVaultReorganizationService.MoveFolderAsync("task", "Task")</c>
    /// throws (either DestinationAlreadyExistsException or, per finding #1,
    /// InvalidNotePathException from FileSystemNoteRepository's
    /// own-descendant check, which compares names case-insensitively on
    /// Windows) rather than actually renaming anything. This test exercises
    /// the hop's content-preservation guarantees directly: nested folders, a
    /// non-.md attachment, and an incoming wikilink that must still resolve
    /// correctly afterward (a link differing only by case from its target's
    /// new path resolves fine without needing a textual rewrite, since path
    /// resolution throughout the vault is case-insensitive - see
    /// VaultReorganizationService's "Minimal" rewrite rule) - the case-only
    /// detection itself is a plain string comparison, so it takes the same
    /// code path on this case-sensitive CI filesystem as it would on
    /// Windows/macOS.
    /// </summary>
    [Fact]
    public async Task RunAsync_CaseOnlyRename_UsesTempHop_PreservesNestedFoldersFilesAndLinks()
    {
        await _repository.SaveAsync("task/TASK-1 - X.md", "---\nid: TASK-1\nstatus: To Do\n---\n");
        await _repository.SaveAsync("task/ProjectX/TASK-2 - Y.md", "---\nid: TASK-2\nstatus: To Do\n---\n");
        await _repository.SaveAsync("notes/referrer.md", "[[task/TASK-1 - X]]");
        await File.WriteAllTextAsync(DiskPath("task/logo.png"), "not-a-real-image");

        await CreateMigration().RunAsync();

        Assert.False(Directory.Exists(DiskPath("task")));
        Assert.True(Directory.Exists(DiskPath("Task")));
        Assert.True(await _repository.ExistsAsync("Task/TASK-1 - X.md"));
        Assert.True(await _repository.ExistsAsync("Task/ProjectX/TASK-2 - Y.md"));
        Assert.True(File.Exists(DiskPath("Task/logo.png")));

        // The link still resolves to the renamed note (path resolution is
        // case-insensitive) - untouched is the correct, "Minimal" outcome
        // here, not a rewrite, since its target's identity didn't change.
        var referrer = await _repository.GetAsync("notes/referrer.md");
        Assert.Contains("[[task/TASK-1 - X]]", referrer!.Content);
    }

    /// <summary>
    /// Finding #2: only the exact (ordinal) legacy names "task"/"tasks" are
    /// matched - a user's own "Tasks" (capital, plural) folder is their own
    /// data and must be left completely untouched.
    /// </summary>
    [Fact]
    public async Task RunAsync_UserFolderNamedTasksCapitalPlural_IsNotMigrated()
    {
        await _repository.SaveAsync("Tasks/notes.md", "just a note, not a task");

        await CreateMigration().RunAsync();

        Assert.True(Directory.Exists(DiskPath("Tasks")));
        Assert.False(Directory.Exists(DiskPath("Task")));
        Assert.True(await _repository.ExistsAsync("Tasks/notes.md"));
    }

    /// <summary>
    /// Finding #12: a non-.md attachment inside a legacy folder that has to
    /// be merged (not renamed, since "Task" already exists) survives the
    /// merge via the raw-filesystem file-move path.
    /// </summary>
    [Fact]
    public async Task RunAsync_MergeIntoExistingTaskFolder_PreservesNonMarkdownFiles()
    {
        await _repository.SaveAsync("Task/TASK-1 - X.md", "---\nid: TASK-1\nstatus: To Do\n---\n");
        await _repository.SaveAsync("tasks/TASK-2 - Y.md", "---\nid: TASK-2\nstatus: To Do\n---\n");
        await File.WriteAllTextAsync(DiskPath("tasks/attachment.png"), "binary-ish content");

        await CreateMigration().RunAsync();

        Assert.True(await _repository.ExistsAsync("Task/TASK-2 - Y.md"));
        Assert.True(File.Exists(DiskPath("Task/attachment.png")));
        Assert.Equal("binary-ish content", await File.ReadAllTextAsync(DiskPath("Task/attachment.png")));
        Assert.False(Directory.Exists(DiskPath("tasks")));
    }

    /// <summary>
    /// Finding #12: a nested-folder name collision during a merge (both the
    /// legacy folder and "Task" already have a same-named subfolder) is
    /// left in place under the legacy folder and logged - never silently
    /// dropped or overwritten - so the legacy folder survives (non-empty)
    /// and both copies of the colliding subfolder's content remain
    /// findable.
    /// </summary>
    [Fact]
    public async Task RunAsync_NestedFolderCollisionDuringMerge_LeftInPlace_NoDataLoss()
    {
        await _repository.SaveAsync("Task/ProjectX/TASK-1 - X.md", "---\nid: TASK-1\nstatus: To Do\n---\n");
        await _repository.SaveAsync("tasks/ProjectX/TASK-2 - Y.md", "---\nid: TASK-2\nstatus: To Do\n---\n");

        await CreateMigration().RunAsync();

        // Original "Task/ProjectX" content is untouched.
        Assert.True(await _repository.ExistsAsync("Task/ProjectX/TASK-1 - X.md"));

        // The colliding legacy subfolder is left exactly where it was,
        // still holding its own content - not merged, not dropped.
        Assert.True(Directory.Exists(DiskPath("tasks/ProjectX")));
        Assert.True(await _repository.ExistsAsync("tasks/ProjectX/TASK-2 - Y.md"));

        // The legacy "tasks" folder itself survives (non-empty).
        Assert.True(Directory.Exists(DiskPath("tasks")));
    }

    /// <summary>
    /// Finding #3: the archive-&gt;Completed merge runs for whatever
    /// Tasks:Folder is configured, independent of the "Task" folder-rename
    /// step being gated off for a non-default folder name.
    /// </summary>
    [Fact]
    public async Task RunAsync_ConfiguredFolderIsNotTask_ArchiveStillMergedIntoCompleted()
    {
        await _repository.SaveAsync("MyTasks/archive/TASK-1 - X.md", "---\nid: TASK-1\nstatus: Done\n---\n");

        await CreateMigration(folder: "MyTasks").RunAsync();

        Assert.True(await _repository.ExistsAsync("MyTasks/Completed/TASK-1 - X.md"));
        Assert.False(Directory.Exists(DiskPath("MyTasks/archive")));
    }

    /// <summary>
    /// Finding #2/#3: only the exact (ordinal) "archive" name is matched -
    /// a user's own "Archive" folder next to their tasks is left alone.
    /// </summary>
    [Fact]
    public async Task RunAsync_UserFolderNamedArchiveCapitalized_IsNotMigrated()
    {
        await _repository.SaveAsync("Task/Archive/notes.md", "just a note");

        await CreateMigration().RunAsync();

        Assert.True(Directory.Exists(DiskPath("Task/Archive")));
        Assert.False(Directory.Exists(DiskPath("Task/Completed")));
    }
}
