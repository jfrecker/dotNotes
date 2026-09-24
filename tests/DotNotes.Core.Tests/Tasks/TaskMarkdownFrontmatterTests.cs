using DotNotes.Core.Tasks;

namespace DotNotes.Core.Tests.Tasks;

public sealed class TaskMarkdownFrontmatterTests
{
    // Deliberately written in exactly the layout TaskMarkdown's own
    // emitter produces (field order, 2-space list indent, quoting) so the
    // full-file round trip below can assert byte-for-byte equality, not
    // just semantic equivalence.
    private const string CanonicalSample =
        "---\n" +
        "id: BACK-355.04\n" +
        "title: Fix login redirect\n" +
        "status: In Progress\n" +
        "assignee:\n" +
        "  - '@jonathan'\n" +
        "reporter: '@alice'\n" +
        "created_date: '2026-01-02 10:30'\n" +
        "updated_date: '2026-01-03 09:15'\n" +
        "labels:\n" +
        "  - auth\n" +
        "  - bug\n" +
        "milestone: v0.3\n" +
        "dependencies: []\n" +
        "priority: high\n" +
        "ordinal: 2000\n" +
        "modified_files:\n" +
        "  - src/a.cs\n" +
        "  - src/b.cs\n" +
        "custom_flag: yes\n" +
        "---\n" +
        "\n" +
        "## Description\n" +
        "\n" +
        "<!-- SECTION:DESCRIPTION:BEGIN -->\n" +
        "Users land on / instead of the page they asked for.\n" +
        "<!-- SECTION:DESCRIPTION:END -->\n" +
        "\n" +
        "## Acceptance Criteria\n" +
        "<!-- AC:BEGIN -->\n" +
        "- [x] #1 Redirect preserves the original path\n" +
        "- [ ] #2 Covered by an integration test\n" +
        "<!-- AC:END -->\n" +
        "\n" +
        "## Definition of Done\n" +
        "- Code reviewed\n" +
        "- Tests pass\n" +
        "\n" +
        "## Implementation Plan\n" +
        "\n" +
        "<!-- SECTION:PLAN:BEGIN -->\n" +
        "Investigate router config.\n" +
        "<!-- SECTION:PLAN:END -->\n" +
        "\n" +
        "## Implementation Notes\n" +
        "\n" +
        "<!-- SECTION:NOTES:BEGIN -->\n" +
        "Found the bug in router.ts.\n" +
        "<!-- SECTION:NOTES:END -->\n" +
        "\n" +
        "## Comments\n" +
        "- alice: looks good\n" +
        "\n" +
        "## Final Summary\n" +
        "\n" +
        "<!-- SECTION:FINAL_SUMMARY:BEGIN -->\n" +
        "Fixed by updating the redirect target.\n" +
        "<!-- SECTION:FINAL_SUMMARY:END -->\n";

    [Fact]
    public void TryParse_BacklogStyleSample_Succeeds()
    {
        Assert.True(TaskMarkdown.TryParse(CanonicalSample, out var document));
        Assert.NotNull(document);

        var fm = document!.Frontmatter;
        Assert.Equal("BACK-355.04", fm.Id);
        Assert.Equal("Fix login redirect", fm.Title);
        Assert.Equal("In Progress", fm.Status);
        Assert.Equal(new[] { "@jonathan" }, fm.Assignee);
        Assert.Equal("@alice", fm.Reporter);
        Assert.Equal(new[] { "auth", "bug" }, fm.Labels);
        Assert.Equal("v0.3", fm.Milestone);
        Assert.Empty(fm.Dependencies);
        Assert.Equal("high", fm.Priority);
        Assert.Equal(2000d, fm.Ordinal);
        Assert.Equal(2, fm.UnknownFields.Count);
        Assert.Equal("modified_files", fm.UnknownFields[0].Key);
        Assert.Equal("custom_flag", fm.UnknownFields[1].Key);
    }

    [Fact]
    public void Serialize_AfterParsingCanonicalSample_IsByteIdentical()
    {
        Assert.True(TaskMarkdown.TryParse(CanonicalSample, out var document));
        var result = TaskMarkdown.Serialize(document!);
        Assert.Equal(CanonicalSample, result);
    }

    [Fact]
    public void Serialize_IsIdempotent_AfterCreateThenReparse()
    {
        var frontmatter = new TaskFrontmatterData
        {
            Id = "TASK-1",
            Title = "Hello",
            Status = "To Do",
            CreatedDate = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            UpdatedDate = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            Ordinal = 1000,
        };

        var body = TaskMarkdown.SetDescription("\n", "Some text.");
        var document = new TaskDocument { Frontmatter = frontmatter, Body = body };
        var content = TaskMarkdown.Serialize(document);

        Assert.True(TaskMarkdown.TryParse(content, out var reparsed));
        var content2 = TaskMarkdown.Serialize(reparsed!);

        Assert.Equal(content, content2);
    }

    [Fact]
    public void TryParse_UnknownKeys_PreservedInOriginalOrder()
    {
        Assert.True(TaskMarkdown.TryParse(CanonicalSample, out var document));
        var keys = document!.Frontmatter.UnknownFields.Select(f => f.Key).ToArray();
        Assert.Equal(new[] { "modified_files", "custom_flag" }, keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Just a plain note with no frontmatter at all.")]
    [InlineData("---\ntitle: Not a task\n---\nbody")]
    [InlineData("---\nid: TASK-1\n---\nbody without status")]
    [InlineData("---\nid: ''\nstatus: 'To Do'\n---\nbody")]
    public void IsTask_ReturnsFalse_ForNonTaskContent(string content)
    {
        Assert.False(TaskMarkdown.IsTask(content));
    }

    [Fact]
    public void IsTask_ReturnsTrue_ForMinimalTaskFrontmatter()
    {
        Assert.True(TaskMarkdown.IsTask("---\nid: TASK-1\nstatus: To Do\n---\n"));
    }

    [Fact]
    public void TryParse_TolerantOfBomAndCrlf()
    {
        var content = "﻿---\r\nid: TASK-1\r\nstatus: To Do\r\n---\r\nHello\r\n";
        Assert.True(TaskMarkdown.TryParse(content, out var document));
        Assert.Equal("TASK-1", document!.Frontmatter.Id);
        Assert.True(document.HasBom);
    }

    [Fact]
    public void Serialize_QuotesTrickyScalars()
    {
        var frontmatter = new TaskFrontmatterData
        {
            Id = "TASK-2",
            Title = "Title",
            Status = "To Do",
            Assignee = new[] { "@bob" },
            Priority = "123", // numeric-looking must be quoted
            Milestone = "yes", // YAML-special word
        };

        var content = TaskMarkdown.Serialize(new TaskDocument { Frontmatter = frontmatter, Body = "\n" });

        Assert.Contains("assignee:\n  - '@bob'", content);
        Assert.Contains("priority: '123'", content);
        Assert.Contains("milestone: 'yes'", content);

        // And it must still parse back to the same values.
        Assert.True(TaskMarkdown.TryParse(content, out var reparsed));
        Assert.Equal("123", reparsed!.Frontmatter.Priority);
        Assert.Equal("yes", reparsed.Frontmatter.Milestone);
    }

    [Fact]
    public void Serialize_EscapesEmbeddedSingleQuotes()
    {
        var frontmatter = new TaskFrontmatterData
        {
            Id = "TASK-3",
            Title = "Title",
            Status = "To Do",
            Milestone = "it's here", // contains an apostrophe and requires quoting anyway due to leading-safe check... verify escaping
        };

        var content = TaskMarkdown.Serialize(new TaskDocument { Frontmatter = frontmatter, Body = "\n" });
        Assert.True(TaskMarkdown.TryParse(content, out var reparsed));
        Assert.Equal("it's here", reparsed!.Frontmatter.Milestone);
    }
}
