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
/// Phase 12 QA hardening: awkward-but-realistic inputs (hand-written CRLF/BOM
/// files, YAML-hostile titles, duplicate ids, unicode names, ordinal
/// exhaustion, concurrent writers) exercised end to end against a real
/// temp vault, asserting on what actually landed on disk.
/// </summary>
public sealed class TaskEdgeCaseTests : IDisposable
{
    private readonly DirectoryInfo _vaultDirectory;
    private readonly FileSystemNoteRepository _repository;
    private readonly InMemoryTaskIndex _taskIndex;
    private readonly TaskService _service;

    public TaskEdgeCaseTests()
    {
        _vaultDirectory = Directory.CreateTempSubdirectory("dotnotes-task-edge-tests-");
        var vaultOptions = Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName });
        _repository = new FileSystemNoteRepository(vaultOptions);
        var tasksOptions = Options.Create(new TasksOptions());
        _taskIndex = new InMemoryTaskIndex(_repository, tasksOptions, vaultOptions);
        var linkIndex = new InMemoryLinkIndex(_repository);
        var reorg = new VaultReorganizationService(
            _repository,
            new IVaultChangeListener[] { _taskIndex, linkIndex },
            NullLogger<VaultReorganizationService>.Instance);
        _service = new TaskService(_repository, _taskIndex, reorg, tasksOptions);
    }

    public void Dispose()
    {
        if (Directory.Exists(_vaultDirectory.FullName))
        {
            _vaultDirectory.Delete(recursive: true);
        }
    }

    private string DiskPath(string relative) => Path.Combine(_vaultDirectory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));

    // ---- hand-written files: CRLF / BOM ----

    [Fact]
    public async Task CrlfBomTaskFile_IsIndexed_AndUpdatedWithoutLosingBodyOrBom()
    {
        const string crlf =
            "﻿---\r\nid: TASK-7\r\ntitle: Legacy task\r\nstatus: To Do\r\nordinal: 1000\r\ncustom_key: keep me\r\n---\r\n" +
            "\r\n## Description\r\n\r\n<!-- SECTION:DESCRIPTION:BEGIN -->\r\nOld description\r\n<!-- SECTION:DESCRIPTION:END -->\r\n" +
            "\r\n## Acceptance Criteria\r\n<!-- AC:BEGIN -->\r\n- [ ] #1 first\r\n- [ ] #2 second\r\n<!-- AC:END -->\r\n" +
            "\r\n## Definition of Done\r\n- reviewed\r\n";

        // Write the raw bytes ourselves so nothing normalises the line endings.
        Directory.CreateDirectory(DiskPath("tasks"));
        await File.WriteAllTextAsync(DiskPath("tasks/TASK-7 - Legacy task.md"), crlf, new System.Text.UTF8Encoding(false));
        await _taskIndex.RebuildAsync();

        var task = _service.GetById("TASK-7");
        Assert.NotNull(task);
        Assert.Equal("Legacy task", task!.Title);
        Assert.Equal("Old description", task.Description.Trim());
        Assert.Equal(2, task.AcceptanceCriteria.Count);

        var updated = await _service.UpdateAsync("TASK-7", new TaskUpdate
        {
            AcceptanceCriteriaCheck = new[] { 2 },
            Status = "In Progress",
        });

        Assert.Equal("In Progress", updated.Status);
        Assert.Equal(new[] { false, true }, updated.AcceptanceCriteria.Select(c => c.Checked).ToArray());
        Assert.Equal("Old description", updated.Description.Trim());

        var onDisk = await File.ReadAllTextAsync(DiskPath("tasks/TASK-7 - Legacy task.md"));
        Assert.StartsWith("﻿---\n", onDisk);
        Assert.Contains("custom_key: keep me", onDisk);
        Assert.Contains("## Definition of Done", onDisk);
        Assert.Contains("- reviewed", onDisk);
        Assert.True(TaskMarkdown.IsTask(onDisk));
    }

    // ---- YAML-hostile scalars round-trip create -> get ----

    [Theory]
    [InlineData("Fix: the login bug")]
    [InlineData("Issue #42 is back")]
    [InlineData("@jonathan should look at this")]
    [InlineData("It's not working")]
    [InlineData("yes")]
    [InlineData("null")]
    [InlineData("12345")]
    [InlineData("1e3")]
    [InlineData("2026-01-02")]
    [InlineData("[wip] refactor")]
    [InlineData("- leading dash")]
    [InlineData("{braces} & *stars* !bang %pct")]
    [InlineData("quote \"double\" and 'single'")]
    [InlineData("back\\slash and trailing colon:")]
    [InlineData("Café ☕ 日本語 задача")]
    public async Task CreateThenGet_TitleWithYamlSpecialCharacters_RoundTripsExactly(string title)
    {
        var created = await _service.CreateAsync(new TaskCreateRequest { Title = title });
        Assert.Equal(title, created.Title);

        // Force a real re-parse from disk, not the in-memory cache.
        await _taskIndex.RebuildAsync();
        var reloaded = _service.GetById(created.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(title, reloaded!.Title);
        Assert.Equal("To Do", reloaded.Status);
    }

    [Fact]
    public async Task Create_TitleWithTab_TabIsCollapsedToSpace()
    {
        // Control whitespace in single-line frontmatter values is normalised
        // to one space at the service boundary (see TaskReviewRegressionTests).
        var created = await _service.CreateAsync(new TaskCreateRequest { Title = "Tabs\tinside" });
        Assert.Equal("Tabs inside", created.Title);
    }

    [Fact]
    public async Task CreateThenGet_LabelsAssigneesAndMilestoneWithCommasAndSpecials_RoundTrip()
    {
        var labels = new[] { "a,b", "c: d", "#hash", "it's", "yes", "42", "padded" };
        var assignees = new[] { "@jo,hn", "@x y" };

        var created = await _service.CreateAsync(new TaskCreateRequest
        {
            Title = "Special lists",
            Labels = labels,
            Assignee = assignees,
            Milestone = "v1, beta: 2",
        });

        await _taskIndex.RebuildAsync();
        var reloaded = _service.GetById(created.Id)!;

        Assert.Equal(labels, reloaded.Labels.ToArray());
        Assert.Equal(assignees, reloaded.Assignee.ToArray());
        Assert.Equal("v1, beta: 2", reloaded.Milestone);
    }

    [Fact]
    public async Task Create_TitleContainingNewlineAndFrontmatterFence_DoesNotCorruptFrontmatter()
    {
        // A title with an embedded newline + '---' must never terminate the
        // frontmatter block early (which would turn the note into a plain
        // note the board can't see).
        var created = await _service.CreateAsync(new TaskCreateRequest { Title = "Line one\n---\nline two" });

        var onDisk = await File.ReadAllTextAsync(DiskPath(created.Path));
        Assert.True(TaskMarkdown.IsTask(onDisk), "task file must still be detected as a task:\n" + onDisk);

        await _taskIndex.RebuildAsync();
        Assert.NotNull(_service.GetById(created.Id));
    }

    [Fact]
    public async Task AcceptanceCriterionWithLineBreak_IsKeptAsOneLine_NotSilentlyTruncated()
    {
        var created = await _service.CreateAsync(new TaskCreateRequest
        {
            Title = "Multi-line AC",
            AcceptanceCriteria = new[] { "first line\nsecond line", "plain" },
        });

        await _taskIndex.RebuildAsync();
        var reloaded = _service.GetById(created.Id)!;
        Assert.Equal(new[] { "first line second line", "plain" }, reloaded.AcceptanceCriteria.Select(c => c.Text).ToArray());

        // Editing another criterion must not drop any text.
        var checkedTask = await _service.UpdateAsync(created.Id, new TaskUpdate { AcceptanceCriteriaCheck = new[] { 2 } });
        Assert.Equal("first line second line", checkedTask.AcceptanceCriteria[0].Text);
    }

    // ---- duplicate ids ----

    [Fact]
    public async Task DuplicateIdsAcrossTwoFiles_BothListed_NewIdsStillUnique_AndUpdateTouchesExactlyOne()
    {
        Directory.CreateDirectory(DiskPath("tasks"));
        const string template = "---\nid: TASK-5\ntitle: {0}\nstatus: To Do\nordinal: {1}\n---\nbody {0}\n";
        await File.WriteAllTextAsync(DiskPath("tasks/TASK-5 - First copy.md"), string.Format(template, "First copy", 1000));
        await File.WriteAllTextAsync(DiskPath("tasks/TASK-5 - Second copy.md"), string.Format(template, "Second copy", 2000));
        await _taskIndex.RebuildAsync();

        // Documented behaviour: both files are real tasks and both show up
        // (nothing is hidden or deleted); id lookups resolve to exactly one.
        var all = _service.List(TaskFilter.Empty);
        Assert.Equal(2, all.Count);
        Assert.Equal(new[] { "First copy", "Second copy" }, all.Select(t => t.Title).ToArray());
        Assert.NotNull(_service.GetById("TASK-5"));

        // New ids never collide with the duplicate.
        var fresh = await _service.CreateAsync(new TaskCreateRequest { Title = "Fresh" });
        Assert.Equal("TASK-6", fresh.Id);

        // A patch through the id changes exactly one of the two files; the
        // other file's bytes must be untouched.
        var before1 = await File.ReadAllTextAsync(DiskPath("tasks/TASK-5 - First copy.md"));
        var before2 = await File.ReadAllTextAsync(DiskPath("tasks/TASK-5 - Second copy.md"));
        var target = _service.GetById("TASK-5")!;
        await _service.UpdateAsync("TASK-5", new TaskUpdate { Priority = "high" });
        var after1 = await File.ReadAllTextAsync(DiskPath("tasks/TASK-5 - First copy.md"));
        var after2 = await File.ReadAllTextAsync(DiskPath("tasks/TASK-5 - Second copy.md"));

        var changed = new[] { before1 != after1, before2 != after2 };
        Assert.Single(changed, c => c);
        Assert.Contains("priority: high", target.Path.Contains("First") ? after1 : after2);
    }

    // ---- unicode / long file names ----

    [Fact]
    public async Task UnicodeTitle_CreatesReadableFileName_AndRenamesToAnotherUnicodeTitle()
    {
        var created = await _service.CreateAsync(new TaskCreateRequest { Title = "Café ☕ 日本語 задача" });
        Assert.Equal("tasks/TASK-1 - Café ☕ 日本語 задача.md", created.Path);
        Assert.True(File.Exists(DiskPath(created.Path)));

        var renamed = await _service.UpdateAsync(created.Id, new TaskUpdate { Title = "Über 任務" });
        Assert.Equal("tasks/TASK-1 - Über 任務.md", renamed.Path);
        Assert.True(File.Exists(DiskPath(renamed.Path)));
        Assert.False(File.Exists(DiskPath(created.Path)));
    }

    [Fact]
    public async Task VeryLongEmojiTitle_TruncatedFileName_IsStillValidUtf16_AndTaskIsCreated()
    {
        // 79 ASCII chars then an astral emoji (surrogate pair) straddling the
        // 80-char cap: a naive [..80] slice would leave a lone high surrogate.
        var title = new string('a', 79) + "\U0001F600 tail";
        var created = await _service.CreateAsync(new TaskCreateRequest { Title = title });

        var fileName = Path.GetFileName(created.Path);
        for (var i = 0; i < fileName.Length; i++)
        {
            if (char.IsHighSurrogate(fileName[i]))
            {
                Assert.True(i + 1 < fileName.Length && char.IsLowSurrogate(fileName[i + 1]), "lone high surrogate in file name");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(fileName[i]), "lone low surrogate in file name");
            }
        }

        Assert.True(File.Exists(DiskPath(created.Path)));
        Assert.Equal(title, created.Title); // frontmatter keeps the full title
    }

    // ---- ordering ----

    [Fact]
    public async Task MoveToIndexZero_Repeatedly_KeepsOrderCorrect_ThroughGapHalvingAndRebalance()
    {
        var ids = new List<string>();
        foreach (var name in new[] { "A", "B", "C" })
        {
            ids.Add((await _service.CreateAsync(new TaskCreateRequest { Title = name })).Id);
        }

        // Rotate the last card to the top 45 times: each move halves the top
        // ordinal until the gap drops below 1e-6 and a rebalance renumbers
        // the column. Order must be right after every single move.
        var expected = new List<string>(ids);
        var sawRebalance = false;
        double previousTopOrdinal = double.MaxValue;
        for (var i = 0; i < 45; i++)
        {
            var last = expected[^1];
            expected.RemoveAt(expected.Count - 1);
            expected.Insert(0, last);

            await _service.MoveAsync(last, "To Do", 0);

            var column = _service.GetBoard(TaskFilter.Empty).Columns.Single(c => c.Status == "To Do").Tasks;
            Assert.Equal(expected, column.Select(t => t.Id).ToArray());

            var ordinals = column.Select(t => t.Ordinal!.Value).ToArray();
            Assert.True(ordinals.SequenceEqual(ordinals.OrderBy(o => o)), $"ordinals not ascending at iteration {i}");
            Assert.Equal(ordinals.Length, ordinals.Distinct().Count());
            Assert.All(ordinals, o => Assert.True(o > 0));

            if (ordinals[0] > previousTopOrdinal)
            {
                sawRebalance = true;
            }

            previousTopOrdinal = ordinals[0];
        }

        Assert.True(sawRebalance, "expected at least one full-column rebalance within 45 top-inserts");

        // What is on disk (not just the cache) must agree.
        await _taskIndex.RebuildAsync();
        var fromDisk = _service.GetBoard(TaskFilter.Empty).Columns.Single(c => c.Status == "To Do").Tasks.Select(t => t.Id).ToArray();
        Assert.Equal(expected, fromDisk);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(null)]
    public async Task MoveIntoEmptyColumn_AnyIndex_LandsAtStepOrdinal(int? index)
    {
        var task = await _service.CreateAsync(new TaskCreateRequest { Title = "Solo" });

        var moved = await _service.MoveAsync(task.Id, "In Progress", index);

        Assert.Equal("In Progress", moved.Status);
        Assert.Equal(1000d, moved.Ordinal);
        var column = _service.GetBoard(TaskFilter.Empty).Columns.Single(c => c.Status == "In Progress");
        Assert.Equal(new[] { task.Id }, column.Tasks.Select(t => t.Id).ToArray());
        Assert.Empty(_service.GetBoard(TaskFilter.Empty).Columns.Single(c => c.Status == "To Do").Tasks);
    }

    // ---- concurrency ----

    [Fact]
    public async Task ConcurrentUpdatesToDifferentFieldsOfSameTask_AllSurvive()
    {
        var created = await _service.CreateAsync(new TaskCreateRequest { Title = "Contended", Description = "orig" });

        var updates = new TaskUpdate[]
        {
            new() { Priority = "high" },
            new() { Milestone = "v9" },
            new() { Assignee = new[] { "@ann" } },
            new() { Labels = new[] { "l1", "l2" } },
            new() { Description = "new description" },
            new() { AcceptanceCriteriaAdd = new[] { "ac one" } },
            new() { NotesAppend = "a note" },
            new() { Status = "In Progress" },
        };

        await Task.WhenAll(updates.Select(u => Task.Run(() => _service.UpdateAsync(created.Id, u))));

        // Verify against the file on disk, not just the cache.
        await _taskIndex.RebuildAsync();
        var final = _service.GetById(created.Id)!;
        Assert.Equal("high", final.Priority);
        Assert.Equal("v9", final.Milestone);
        Assert.Equal(new[] { "@ann" }, final.Assignee.ToArray());
        Assert.Equal(new[] { "l1", "l2" }, final.Labels.ToArray());
        Assert.Equal("new description", final.Description.Trim());
        Assert.Equal("ac one", Assert.Single(final.AcceptanceCriteria).Text);
        Assert.Equal("a note", final.ImplementationNotes);
        Assert.Equal("In Progress", final.Status);
    }

    [Fact]
    public async Task ConcurrentCreates_ProduceUniqueIdsAndFiles()
    {
        var created = await Task.WhenAll(Enumerable.Range(0, 15)
            .Select(i => Task.Run(() => _service.CreateAsync(new TaskCreateRequest { Title = $"Parallel {i}" }))));

        Assert.Equal(15, created.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(15, Directory.GetFiles(DiskPath("tasks"), "*.md").Length);
    }
}
