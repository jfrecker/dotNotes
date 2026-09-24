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
/// Regression tests for the confirmed review findings on the Tasks backend
/// (frontmatter round-trip fidelity, YAML-hostile values, known keys with
/// unexpected shapes, duplicate ids, move semantics, unknown-status
/// columns). Kept in its own file so it doesn't clash with the other task
/// test files.
/// </summary>
public sealed class TaskReviewRegressionTests : IDisposable
{
    private const string Header = "id: TASK-1\ntitle: T\nstatus: To Do\n";

    private readonly DirectoryInfo _vaultDirectory;
    private readonly FileSystemNoteRepository _repository;
    private readonly InMemoryTaskIndex _taskIndex;
    private readonly TaskService _service;

    public TaskReviewRegressionTests()
    {
        _vaultDirectory = Directory.CreateTempSubdirectory("dotnotes-task-review-tests-");
        var vaultOptions = Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName });
        _repository = new FileSystemNoteRepository(vaultOptions);
        var optionsWrapper = Options.Create(new TasksOptions());
        _taskIndex = new InMemoryTaskIndex(_repository, optionsWrapper);
        var linkIndex = new InMemoryLinkIndex(_repository);
        var reorganization = new VaultReorganizationService(
            _repository,
            new IVaultChangeListener[] { _taskIndex, linkIndex },
            NullLogger<VaultReorganizationService>.Instance);
        _service = new TaskService(_repository, _taskIndex, reorganization, optionsWrapper);
    }

    public void Dispose()
    {
        if (Directory.Exists(_vaultDirectory.FullName))
        {
            _vaultDirectory.Delete(recursive: true);
        }
    }

    private static string Frontmatter(string yaml, string newline = "\n") =>
        ("---\n" + Header + yaml + "\n---\n\nBody text.\n").Replace("\n", newline);

    private static string RoundTrip(string content)
    {
        Assert.True(TaskMarkdown.TryParse(content, out var document), "input should parse as a task");
        var output = TaskMarkdown.Serialize(document!);

        // Still a task, and a second pass is byte-identical (idempotent).
        Assert.True(TaskMarkdown.TryParse(output, out var again), "output should still parse as a task");
        Assert.Equal(output, TaskMarkdown.Serialize(again!));
        return output;
    }

    private async Task<string> WriteTaskFileAsync(string path, string content)
    {
        await _repository.SaveAsync(path, content);
        _taskIndex.NoteChanged(path, content);
        return path;
    }

    private void SetOldMtime(string path)
    {
        var full = Path.Combine(_vaultDirectory.FullName, path.Replace('/', Path.DirectorySeparatorChar));
        File.SetLastWriteTimeUtc(full, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    private DateTime Mtime(string path) =>
        File.GetLastWriteTimeUtc(Path.Combine(_vaultDirectory.FullName, path.Replace('/', Path.DirectorySeparatorChar)));

    // ================= A. unknown-key raw capture =================

    public static TheoryData<string> UnknownKeyShapes => new()
    {
        "meta:\n  k: v\n  j: w",
        "flow: {a: 1, b: 2}",
        "flowlist: [1, 2, [3, 4]]",
        "items:\n  - a: 1\n    b: 2\n  - c: 3",
        "grid:\n  - - 1\n    - 2\n  - - 3",
        "deep:\n  x:\n    y:\n      - 1\n      - z: 2",
        "note: |\n  line one\n  line two\n\n  after blank",
        "folded: >\n  folded text\n  continues here",
        "q: \"line one\n  line two\"",
        "sq: 'it''s\n  multi'",
        "a: 1\n# a comment between keys\nb: 2",
        "plainseq:\n  - one\n  - two",
    };

    [Theory]
    [MemberData(nameof(UnknownKeyShapes))]
    public void UnknownKey_RoundTrips_InTheMiddleAndAtTheEnd(string raw)
    {
        // Unknown key followed by a known key and another unknown key, so
        // both "next entry" and "end of block" boundaries are exercised.
        var content = Frontmatter($"{raw}\npriority: high\nzz: last");
        var output = RoundTrip(content);

        Assert.Contains(raw + "\n", output.Replace("priority: high\n", string.Empty).Replace("\npriority: high", string.Empty) + "\n");
        Assert.Contains("\nzz: last\n---", output);
        Assert.Contains("priority: high", output);

        // Also as the very last entry of the block.
        var atEnd = RoundTrip(Frontmatter(raw));
        Assert.Contains(raw + "\n---\n", atEnd);
    }

    [Theory]
    [MemberData(nameof(UnknownKeyShapes))]
    public void UnknownKey_Crlf_IsNormalisedToLfWithNoMixedLineEndings(string raw)
    {
        var content = Frontmatter($"{raw}\nzz: last", "\r\n");
        var output = RoundTrip(content);

        var frontmatterBlock = output[..(output.IndexOf("\n---\n", 4, StringComparison.Ordinal) + 1)];
        Assert.DoesNotContain('\r', frontmatterBlock);
        Assert.Contains(raw, output);
        Assert.Contains("\nzz: last\n", output);
    }

    [Fact]
    public void UnknownKey_FlowStyleRootMapping_StillCapturesEachEntry()
    {
        var content = "---\n{id: TASK-1, status: To Do, meta: {a: 1, b: 2}, tag: x}\n---\nBody\n";
        var output = RoundTrip(content);

        Assert.Contains("meta: {a: 1, b: 2}", output);
        Assert.Contains("tag: x", output);
    }

    [Fact]
    public void UnknownKey_IndentedRootMapping_IsDedented()
    {
        var content = "---\n  id: TASK-1\n  status: To Do\n  meta:\n    k: v\n    j: w\n---\nBody\n";
        var output = RoundTrip(content);

        Assert.Contains("\nmeta:\n  k: v\n  j: w\n---", output);
    }

    [Fact]
    public async Task ConvertNoteAsync_NoteWithNestedUnknownMetadata_KeepsIt()
    {
        var path = "Nested.md";
        await _repository.SaveAsync(path, "---\nmeta:\n  k: v\n  j: w\nitems:\n  - a: 1\n    b: 2\nflow: {a: 1, b: 2}\n---\nBody\n");

        var task = await _service.ConvertNoteAsync(path, null);

        var note = await _repository.GetAsync(task.Path);
        Assert.NotNull(note);
        Assert.Contains("meta:\n  k: v\n  j: w\n", note!.Content);
        Assert.Contains("items:\n  - a: 1\n    b: 2\n", note.Content);
        Assert.Contains("flow: {a: 1, b: 2}", note.Content);
        Assert.True(TaskMarkdown.IsTask(note.Content));
    }

    [Fact]
    public async Task UpdateAsync_PreservesNestedUnknownMetadata()
    {
        var created = await _service.CreateAsync(new TaskCreateRequest { Title = "Keep meta" });
        var note = await _repository.GetAsync(created.Path);
        var edited = note!.Content.Replace("\n---\n", "\nmeta:\n  k: v\n  j: w\nflow: {a: 1, b: 2}\n---\n");
        await WriteTaskFileAsync(created.Path, edited);

        await _service.UpdateAsync(created.Id, new TaskUpdate { Priority = "high" });
        await _service.MoveAsync(created.Id, "Done", null);

        var after = (await _repository.GetAsync(created.Path))!.Content;
        Assert.Contains("meta:\n  k: v\n  j: w\n", after);
        Assert.Contains("flow: {a: 1, b: 2}", after);
        Assert.Contains("priority: high", after);
    }

    // ================= B. YAML-hostile values =================

    public static TheoryData<string> HostileValues => new()
    {
        "a\nb",
        "a\rb",
        "a\r\nb",
        "a\tb",
        "a\u0085b",
        "a\u2028b",
        "a\u2029b",
        "a\u0001b",
        "a\u007Fb",
        "a\n---\nb",
        "---",
        "a:\tb",
        "a: b",
        "a:\nb",
        "trailing\n",
        "\"quoted\" and \\ backslash\n",
    };

    [Theory]
    [MemberData(nameof(HostileValues))]
    public void Emitter_HostileValue_RoundTripsExactly(string value)
    {
        var document = new TaskDocument
        {
            Frontmatter = new TaskFrontmatterData
            {
                Id = "TASK-1",
                Title = value,
                Status = "To Do",
                Assignee = new[] { value },
                Labels = new[] { value, "ok" },
                Milestone = value,
                Dependencies = new[] { value },
            },
            Body = "\nBody\n",
        };

        var content = TaskMarkdown.Serialize(document);
        Assert.True(TaskMarkdown.TryParse(content, out var parsed), "hostile value must not break the frontmatter");

        var fm = parsed!.Frontmatter;
        Assert.Equal("TASK-1", fm.Id);
        Assert.Equal("To Do", fm.Status);
        Assert.Equal(value, fm.Title);
        Assert.Equal(new[] { value }, fm.Assignee);
        Assert.Equal(new[] { value, "ok" }, fm.Labels);
        Assert.Equal(value, fm.Milestone);
        Assert.Equal(new[] { value }, fm.Dependencies);
        Assert.Equal("\nBody\n", parsed.Body);
    }

    [Theory]
    [MemberData(nameof(HostileValues))]
    public async Task Service_HostileTitleAndLists_StayParseableAndAreNormalisedToSingleLine(string value)
    {
        var created = await _service.CreateAsync(new TaskCreateRequest
        {
            Title = "T " + value + " end",
            Labels = new[] { value, "  ", "keep" },
            Assignee = new[] { "@" + value },
            Milestone = value,
            Dependencies = new[] { value },
        });

        await _taskIndex.RebuildAsync();
        var reloaded = _service.GetById(created.Id);
        Assert.NotNull(reloaded);
        Assert.False(string.IsNullOrWhiteSpace(reloaded!.Title));
        Assert.All(new[] { reloaded.Title }.Concat(reloaded.Labels).Concat(reloaded.Assignee).Concat(reloaded.Dependencies).Append(reloaded.Milestone ?? string.Empty),
            v => Assert.DoesNotContain(v, c => char.IsControl(c) || c is '\u2028' or '\u2029'));
        Assert.Contains("keep", reloaded.Labels);
        Assert.DoesNotContain(reloaded.Labels, l => l.Trim().Length == 0);

        // And a PATCH with the same value.
        var updated = await _service.UpdateAsync(created.Id, new TaskUpdate { Title = value + " X", Labels = new[] { value, "keep" }, Milestone = value });
        await _taskIndex.RebuildAsync();
        Assert.NotNull(_service.GetById(updated.Id));
    }

    [Fact]
    public async Task Service_TitleWithNewlineFenceNewline_CollapsesToOneLine()
    {
        var created = await _service.CreateAsync(new TaskCreateRequest { Title = "a\n---\nb" });

        Assert.Equal("a --- b", created.Title);
        await _taskIndex.RebuildAsync();
        Assert.Equal("a --- b", _service.GetById(created.Id)!.Title);
    }

    [Theory]
    [InlineData("a\nb", "a b")]
    [InlineData("a \r\n \t b", "a b")]
    [InlineData("  padded  ", "padded")]
    [InlineData("a\u0085b\u2028c", "a b c")]
    public async Task Service_TitleNormalisation(string input, string expected)
    {
        var created = await _service.CreateAsync(new TaskCreateRequest { Title = input });
        Assert.Equal(expected, created.Title);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData(" \t\r\n ")]
    public async Task Service_TitleThatNormalisesToEmpty_IsRejected(string title)
    {
        await Assert.ThrowsAsync<TaskValidationException>(() => _service.CreateAsync(new TaskCreateRequest { Title = title }));

        var created = await _service.CreateAsync(new TaskCreateRequest { Title = "ok" });
        await Assert.ThrowsAsync<TaskValidationException>(() => _service.UpdateAsync(created.Id, new TaskUpdate { Title = title }));
    }

    [Fact]
    public async Task Service_LoneSurrogate_IsReplacedNotFatal()
    {
        var created = await _service.CreateAsync(new TaskCreateRequest { Title = "ok", Labels = new[] { "bad\uD800label" } });

        await _taskIndex.RebuildAsync();
        Assert.Equal("bad\uFFFDlabel", _service.GetById(created.Id)!.Labels.Single());
    }

    [Fact]
    public async Task Service_ValueThatCannotRoundTrip_ThrowsBeforeAnythingIsWritten()
    {
        var created = await _service.CreateAsync(new TaskCreateRequest { Title = "Guarded" });
        var before = (await _repository.GetAsync(created.Path))!.Content;

        // NaN is written as "ordinal: NaN", which the reader (correctly)
        // refuses as a numeric ordinal - the write must be rejected up front.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UpdateAsync(created.Id, new TaskUpdate { Ordinal = double.NaN }));

        var after = (await _repository.GetAsync(created.Path))!.Content;
        Assert.Equal(before, after);
        Assert.NotNull(_service.GetById(created.Id));
    }

    // ================= C. known keys with unexpected shapes =================

    [Fact]
    public void KnownList_ScalarValue_IsOneItemList_NotCommaSplit()
    {
        var output = RoundTrip(Frontmatter("labels: bug\nassignee: '@me'\ndependencies: TASK-2, TASK-3"));

        Assert.True(TaskMarkdown.TryParse(output, out var parsed));
        Assert.Equal(new[] { "bug" }, parsed!.Frontmatter.Labels);
        Assert.Equal(new[] { "@me" }, parsed.Frontmatter.Assignee);
        Assert.Equal(new[] { "TASK-2, TASK-3" }, parsed.Frontmatter.Dependencies);
        Assert.Contains("labels:\n  - bug\n", output);
        Assert.Contains("assignee:\n  - '@me'\n", output);
    }

    [Theory]
    [InlineData("labels:")]
    [InlineData("labels: ~")]
    [InlineData("labels: null")]
    [InlineData("labels: []")]
    public void KnownList_NullOrEmpty_IsEmptyList(string yaml)
    {
        var output = RoundTrip(Frontmatter(yaml));

        Assert.True(TaskMarkdown.TryParse(output, out var parsed));
        Assert.Empty(parsed!.Frontmatter.Labels);
        Assert.Contains("labels: []", output);
    }

    [Theory]
    [InlineData("created_date: yesterday")]
    [InlineData("updated_date: 'not a date'")]
    [InlineData("ordinal: high")]
    [InlineData("ordinal: NaN")]
    [InlineData("labels:\n  - a: 1")]
    [InlineData("labels:\n  k: v")]
    [InlineData("labels:\n  - - x")]
    [InlineData("title: [a, b]")]
    public void KnownKey_UnparseableOrOddShape_IsPreservedVerbatim_AndNotDuplicated(string yaml)
    {
        var key = yaml[..yaml.IndexOf(':')];
        var content = Frontmatter(yaml).Replace("title: T\n", key == "title" ? string.Empty : "title: T\n");

        var output = RoundTrip(content);

        Assert.Contains(yaml, output);
        Assert.Equal(1, CountLines(output, key + ":"));
    }

    [Fact]
    public void KnownKey_PreservedRaw_IsReplacedByATypedEdit_NotEmittedTwice()
    {
        var content = Frontmatter("created_date: yesterday\nlabels:\n  k: v");
        Assert.True(TaskMarkdown.TryParse(content, out var parsed));
        var fm = parsed!.Frontmatter;

        var edited = new TaskDocument
        {
            Body = parsed.Body,
            Frontmatter = new TaskFrontmatterData
            {
                Id = fm.Id,
                Title = fm.Title,
                Status = fm.Status,
                Labels = new[] { "fresh" },
                CreatedDate = new DateTimeOffset(2026, 1, 2, 3, 4, 0, TimeSpan.Zero),
                UnknownFields = fm.UnknownFields,
            },
        };

        var output = TaskMarkdown.Serialize(edited);
        Assert.True(TaskMarkdown.TryParse(output, out var reparsed));
        Assert.Equal(new[] { "fresh" }, reparsed!.Frontmatter.Labels);
        Assert.Equal(1, CountLines(output, "labels:"));
        Assert.Equal(1, CountLines(output, "created_date:"));
        Assert.DoesNotContain("yesterday", output);
    }

    [Fact]
    public async Task Service_UpdateOnFileWithOddKnownKeys_DoesNotDropThem()
    {
        var path = await WriteTaskFileAsync(
            "Task/TASK-1 - Odd.md",
            "---\nid: TASK-1\ntitle: Odd\nstatus: To Do\nlabels: bug\nassignee: '@me'\ncreated_date: yesterday\nordinal: soon\n---\n\nBody\n");

        await _service.UpdateAsync("TASK-1", new TaskUpdate { Priority = "high" });

        var after = (await _repository.GetAsync(path))!.Content;
        Assert.Contains("labels:\n  - bug\n", after);
        Assert.Contains("assignee:\n  - '@me'\n", after);
        Assert.Contains("created_date: yesterday", after);
        Assert.Contains("ordinal: soon", after);
        Assert.Equal(new[] { "bug" }, _service.GetById("TASK-1")!.Labels);
    }

    private static int CountLines(string text, string prefix) =>
        text.Split('\n').Count(l => l.StartsWith(prefix, StringComparison.Ordinal));

    // ================= D. duplicate ids in the index =================

    [Fact]
    public async Task Index_DuplicateIds_DeletingOneFallsBackToTheOther()
    {
        const string content = "---\nid: TASK-9\ntitle: Dup\nstatus: To Do\n---\nBody\n";
        _taskIndex.NoteChanged("b/TASK-9 - Dup.md", content);
        _taskIndex.NoteChanged("a/TASK-9 - Dup.md", content);

        // Deterministic: lowest path wins while both exist.
        Assert.Equal("a/TASK-9 - Dup.md", _taskIndex.GetById("TASK-9")!.Path);

        _taskIndex.NoteDeleted("a/TASK-9 - Dup.md");
        Assert.Equal("b/TASK-9 - Dup.md", _taskIndex.GetById("TASK-9")!.Path);

        _taskIndex.NoteDeleted("b/TASK-9 - Dup.md");
        Assert.Null(_taskIndex.GetById("TASK-9"));
        await Task.CompletedTask;
    }

    [Fact]
    public void Index_DuplicateIds_DeletingTheOtherOneKeepsTheFirstResolvable()
    {
        const string content = "---\nid: TASK-9\ntitle: Dup\nstatus: To Do\n---\nBody\n";
        _taskIndex.NoteChanged("a.md", content);
        _taskIndex.NoteChanged("b.md", content);

        _taskIndex.NoteDeleted("b.md");

        Assert.Equal("a.md", _taskIndex.GetById("TASK-9")!.Path);
    }

    [Fact]
    public void Index_DuplicateIds_OneFileChangedAwayFromTheId_FallsBackToTheOther()
    {
        const string content = "---\nid: TASK-9\ntitle: Dup\nstatus: To Do\n---\nBody\n";
        _taskIndex.NoteChanged("a.md", content);
        _taskIndex.NoteChanged("b.md", content);

        // a.md gets a new id, then b.md stops being a task altogether.
        _taskIndex.NoteChanged("a.md", content.Replace("TASK-9", "TASK-10"));
        Assert.Equal("b.md", _taskIndex.GetById("TASK-9")!.Path);
        Assert.Equal("a.md", _taskIndex.GetById("TASK-10")!.Path);

        _taskIndex.NoteChanged("b.md", "# plain now");
        Assert.Null(_taskIndex.GetById("TASK-9"));
    }

    [Fact]
    public async Task Index_Rebuild_ResolvesDuplicateIdsDeterministically()
    {
        const string content = "---\nid: TASK-9\ntitle: Dup\nstatus: To Do\n---\nBody\n";
        await _repository.SaveAsync("z/dup.md", content);
        await _repository.SaveAsync("a/dup.md", content);

        await _taskIndex.RebuildAsync();

        Assert.Equal("a/dup.md", _taskIndex.GetById("TASK-9")!.Path);
        _taskIndex.NoteDeleted("a/dup.md");
        Assert.Equal("z/dup.md", _taskIndex.GetById("TASK-9")!.Path);
    }

    // ================= E. move semantics =================

    private async Task<TaskItem[]> CreateColumnAsync(int count, string status = "To Do")
    {
        var tasks = new List<TaskItem>();
        for (var i = 0; i < count; i++)
        {
            tasks.Add(await _service.CreateAsync(new TaskCreateRequest { Title = $"Card {i + 1}", Status = status }));
        }

        return tasks.ToArray();
    }

    private string[] Column(string status) =>
        _service.List(new TaskFilter { Status = status }).Select(t => t.Id).ToArray();

    [Fact]
    public async Task Move_BeforeId_InsertsImmediatelyBeforeThatTask()
    {
        var c = await CreateColumnAsync(3);

        await _service.MoveAsync(c[2].Id, "To Do", null, c[0].Id);

        Assert.Equal(new[] { c[2].Id, c[0].Id, c[1].Id }, Column("To Do"));
    }

    [Fact]
    public async Task Move_BeforeId_FromAnotherColumn()
    {
        var todo = await CreateColumnAsync(2);
        var done = await CreateColumnAsync(1, "Done");

        await _service.MoveAsync(done[0].Id, "To Do", null, todo[1].Id);

        Assert.Equal(new[] { todo[0].Id, done[0].Id, todo[1].Id }, Column("To Do"));
        Assert.Empty(Column("Done"));
    }

    [Fact]
    public async Task Move_BeforeId_TakesPrecedenceOverIndex()
    {
        var c = await CreateColumnAsync(4);

        await _service.MoveAsync(c[3].Id, "To Do", 0, beforeTaskId: c[2].Id);

        Assert.Equal(new[] { c[0].Id, c[1].Id, c[3].Id, c[2].Id }, Column("To Do"));
    }

    [Fact]
    public async Task Move_BeforeId_NotInTargetColumn_IsValidationError()
    {
        var todo = await CreateColumnAsync(1);
        var done = await CreateColumnAsync(1, "Done");
        var moving = await _service.CreateAsync(new TaskCreateRequest { Title = "Mover", Status = "In Progress" });

        await Assert.ThrowsAsync<TaskValidationException>(() => _service.MoveAsync(moving.Id, "To Do", null, done[0].Id));
        await Assert.ThrowsAsync<TaskValidationException>(() => _service.MoveAsync(moving.Id, "To Do", null, "TASK-999"));
        await Assert.ThrowsAsync<TaskValidationException>(() => _service.MoveAsync(todo[0].Id, "To Do", null, todo[0].Id));
    }

    [Fact]
    public async Task Move_BeforeId_CompletedTaskIsNotInTheColumn()
    {
        var c = await CreateColumnAsync(2);
        await _service.CompleteAsync(c[0].Id);
        var extra = await _service.CreateAsync(new TaskCreateRequest { Title = "Extra" });

        await Assert.ThrowsAsync<TaskValidationException>(() => _service.MoveAsync(extra.Id, "To Do", null, c[0].Id));
    }

    [Fact]
    public async Task Move_BeforeId_NullAndBlank_MeanEnd()
    {
        var c = await CreateColumnAsync(3);

        await _service.MoveAsync(c[0].Id, "To Do", null, beforeTaskId: null);
        Assert.Equal(new[] { c[1].Id, c[2].Id, c[0].Id }, Column("To Do"));

        await _service.MoveAsync(c[1].Id, "To Do", null, "  ");
        Assert.Equal(new[] { c[2].Id, c[0].Id, c[1].Id }, Column("To Do"));
    }

    [Fact]
    public async Task Move_ToCurrentStatusAndPosition_IsANoOp_NoWriteNoUpdatedDateBump()
    {
        var c = await CreateColumnAsync(3);
        foreach (var t in c)
        {
            SetOldMtime(t.Path);
        }

        var before = c.Select(t => _repository.GetAsync(t.Path).Result!.Content).ToArray();

        await _service.MoveAsync(c[1].Id, "To Do", 1);            // same index
        await _service.MoveAsync(c[1].Id, "To Do", null, c[2].Id); // already directly before c[2]
        await _service.MoveAsync(c[2].Id, "To Do", null);          // already last
        await _service.MoveAsync(c[0].Id, "To Do", 0);             // already first

        for (var i = 0; i < c.Length; i++)
        {
            Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), Mtime(c[i].Path));
            Assert.Equal(before[i], (await _repository.GetAsync(c[i].Path))!.Content);
        }
    }

    [Fact]
    public async Task Move_ToADifferentPosition_StillWrites()
    {
        var c = await CreateColumnAsync(3);
        SetOldMtime(c[0].Path);

        await _service.MoveAsync(c[0].Id, "To Do", null);

        Assert.NotEqual(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), Mtime(c[0].Path));
        Assert.Equal(new[] { c[1].Id, c[2].Id, c[0].Id }, Column("To Do"));
    }

    [Fact]
    public async Task Move_Rebalance_OnlyRewritesFilesWhoseOrdinalChanged()
    {
        var c = await CreateColumnAsync(3);
        await _service.UpdateAsync(c[0].Id, new TaskUpdate { Ordinal = 1000 });
        await _service.UpdateAsync(c[1].Id, new TaskUpdate { Ordinal = 1000.0000005 });
        await _service.UpdateAsync(c[2].Id, new TaskUpdate { Ordinal = 3000 });
        var mover = (await CreateColumnAsync(1, "Done"))[0];

        foreach (var t in c)
        {
            SetOldMtime(t.Path);
        }

        // Between the two near-equal ordinals: gap < minimum, so the column
        // is renumbered 1000, 2000, 3000, 4000 - c[0] already has 1000.
        await _service.MoveAsync(mover.Id, "To Do", 1);

        Assert.Equal(new[] { c[0].Id, mover.Id, c[1].Id, c[2].Id }, Column("To Do"));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), Mtime(c[0].Path));
        Assert.NotEqual(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), Mtime(c[1].Path));
        Assert.NotEqual(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), Mtime(c[2].Path));
        Assert.Equal(new double?[] { 1000, 2000, 3000, 4000 }, Column("To Do").Select(id => _service.GetById(id)!.Ordinal).ToArray());
    }

    // ================= F. Backlog column absorbs unknown/empty statuses =================

    private async Task<string[]> SeedBacklogColumnAsync()
    {
        var ids = new[] { "TASK-11", "TASK-12", "TASK-13" };
        var rawStatuses = new[] { "Blocked", "", "Backlog" };
        for (var i = 0; i < ids.Length; i++)
        {
            await WriteTaskFileAsync(
                $"Task/{ids[i]} - Backlog {i}.md",
                $"---\nid: {ids[i]}\ntitle: Backlog {i}\nstatus: {rawStatuses[i]}\nordinal: {(i + 1) * 1000}\n---\n\nBody\n");
        }

        return ids;
    }

    private string[] BacklogColumn() =>
        _service.GetBoard(TaskFilter.Empty).Columns.Single(c => c.IsBacklog).Tasks.Select(t => t.Id).ToArray();

    [Fact]
    public async Task GetBoard_MixedRawStatuses_AllLandInTheSameBacklogColumn()
    {
        var ids = await SeedBacklogColumnAsync();

        Assert.Equal(ids, BacklogColumn());
    }

    [Fact]
    public async Task Move_WithinBacklog_KeepsRawStatus_OnlyChangesOrdinal()
    {
        var ids = await SeedBacklogColumnAsync();

        var moved = await _service.MoveAsync(ids[2], "Backlog", 0);

        Assert.Equal("Backlog", moved.Status); // ids[2]'s raw status was already "Backlog" - unchanged.
        Assert.Equal(new[] { ids[2], ids[0], ids[1] }, BacklogColumn());

        var reordered = await _service.MoveAsync(ids[0], "backlog", null, ids[2]); // case-insensitive column name, beforeId
        Assert.Equal("Blocked", reordered.Status); // raw "Blocked" status is preserved, not overwritten to "Backlog".
        Assert.Equal(new[] { ids[0], ids[2], ids[1] }, BacklogColumn());
    }

    [Fact]
    public async Task Move_FromBacklogToToDo_AndBack_Persists()
    {
        var ids = await SeedBacklogColumnAsync();

        var movedOut = await _service.MoveAsync(ids[0], "To Do", null);
        Assert.Equal("To Do", movedOut.Status);
        Assert.DoesNotContain(ids[0], BacklogColumn());

        var movedBack = await _service.MoveAsync(ids[0], "Backlog", null);
        Assert.Equal("Backlog", movedBack.Status); // came from a non-Backlog column, so it gets the effective Backlog status.
        Assert.Contains(ids[0], BacklogColumn());
    }

    [Fact]
    public async Task Update_ReSupplyingTheCurrentUnknownStatus_IsAllowed_ButSwitchingToAnotherUnknownIsNot()
    {
        var ids = await SeedBacklogColumnAsync();

        var updated = await _service.UpdateAsync(ids[0], new TaskUpdate { Status = "Blocked", Priority = "high" });
        Assert.Equal("Blocked", updated.Status);

        await Assert.ThrowsAsync<TaskValidationException>(() => _service.UpdateAsync(ids[0], new TaskUpdate { Status = "Nowhere" }));
    }

    [Fact]
    public async Task Create_WithUnknownStatus_StaysStrict()
    {
        await SeedBacklogColumnAsync();

        await Assert.ThrowsAsync<TaskValidationException>(() =>
            _service.CreateAsync(new TaskCreateRequest { Title = "New", Status = "Blocked" }));
    }

    [Fact]
    public async Task Create_WithNoStatus_GoesToBacklog()
    {
        var created = await _service.CreateAsync(new TaskCreateRequest { Title = "New" });

        Assert.Equal("Backlog", created.Status);
        Assert.Contains(created.Id, BacklogColumn());
    }

    [Fact]
    public async Task Move_ToABrandNewUnknownStatus_IsRejected()
    {
        var todo = await _service.CreateAsync(new TaskCreateRequest { Title = "Regular" });

        await Assert.ThrowsAsync<TaskValidationException>(() => _service.MoveAsync(todo.Id, "Nowhere", null));
    }
}
