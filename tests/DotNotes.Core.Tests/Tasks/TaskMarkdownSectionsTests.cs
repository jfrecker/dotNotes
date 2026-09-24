using DotNotes.Core.Tasks;

namespace DotNotes.Core.Tests.Tasks;

public sealed class TaskMarkdownSectionsTests
{
    [Fact]
    public void GetDescription_FromMarkerSection_ReturnsInnerText()
    {
        var body = "\n## Description\n\n<!-- SECTION:DESCRIPTION:BEGIN -->\nHello world.\n<!-- SECTION:DESCRIPTION:END -->\n";
        Assert.Equal("Hello world.", TaskMarkdown.GetDescription(body));
    }

    [Fact]
    public void GetDescription_NoMarkers_FallsBackToFreeText()
    {
        var body = "\nSome free-form text a converted note might have.\n\nMore text.\n";
        Assert.Equal("Some free-form text a converted note might have.\n\nMore text.", TaskMarkdown.GetDescription(body));
    }

    [Fact]
    public void GetDescription_NoMarkers_FallsBackToTextBeforeOtherSections()
    {
        var body = "\nExisting free text.\n\n## Implementation Plan\n\n<!-- SECTION:PLAN:BEGIN -->\nPlan text\n<!-- SECTION:PLAN:END -->\n";
        Assert.Equal("Existing free text.", TaskMarkdown.GetDescription(body));
    }

    [Fact]
    public void SetDescription_NoMarkersButFreeText_WrapsExistingText()
    {
        var body = "\nExisting free text.\n";
        var updated = TaskMarkdown.SetDescription(body, "New description.");

        Assert.Contains("## Description", updated);
        Assert.Contains("<!-- SECTION:DESCRIPTION:BEGIN -->\nNew description.\n<!-- SECTION:DESCRIPTION:END -->", updated);
        Assert.DoesNotContain("Existing free text.", updated);
        Assert.Equal("New description.", TaskMarkdown.GetDescription(updated));
    }

    [Fact]
    public void SetDescription_OnEmptyBody_InsertsFreshSection()
    {
        var updated = TaskMarkdown.SetDescription("\n", "Brand new.");
        Assert.Equal("Brand new.", TaskMarkdown.GetDescription(updated));
    }

    [Fact]
    public void SetDescription_ExistingMarkers_OnlyReplacesInnerSpan()
    {
        var body = "\n## Description\n\n<!-- SECTION:DESCRIPTION:BEGIN -->\nOld.\n<!-- SECTION:DESCRIPTION:END -->\n\n## Implementation Notes\n\n<!-- SECTION:NOTES:BEGIN -->\nUntouched.\n<!-- SECTION:NOTES:END -->\n";
        var updated = TaskMarkdown.SetDescription(body, "New.");

        Assert.Equal("New.", TaskMarkdown.GetDescription(updated));
        Assert.Equal("Untouched.", TaskMarkdown.GetImplementationNotes(updated));
    }

    [Fact]
    public void AcceptanceCriteria_ParseAndRenumber()
    {
        var body = "\n## Acceptance Criteria\n<!-- AC:BEGIN -->\n- [x] Missing hash number\n- [ ] #7 Has a stale number\n<!-- AC:END -->\n";
        var items = TaskMarkdown.GetAcceptanceCriteria(body);

        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0].Index);
        Assert.True(items[0].Checked);
        Assert.Equal("Missing hash number", items[0].Text);
        Assert.Equal(2, items[1].Index);
        Assert.False(items[1].Checked);
        Assert.Equal("Has a stale number", items[1].Text);
    }

    [Fact]
    public void SetAcceptanceCriteria_RenumbersSequentially()
    {
        var body = "\n";
        var updated = TaskMarkdown.SetAcceptanceCriteria(body, new[] { ("First", true), ("Second", false), ("Third", false) });

        var items = TaskMarkdown.GetAcceptanceCriteria(updated);
        Assert.Equal(3, items.Count);
        Assert.Equal(1, items[0].Index);
        Assert.Equal(2, items[1].Index);
        Assert.Equal(3, items[2].Index);
        Assert.True(items[0].Checked);
        Assert.False(items[1].Checked);
    }

    [Fact]
    public void SetAcceptanceCriteria_ExistingSection_OnlyReplacesInnerSpan()
    {
        var body = "\n## Description\n\n<!-- SECTION:DESCRIPTION:BEGIN -->\nDesc\n<!-- SECTION:DESCRIPTION:END -->\n\n## Acceptance Criteria\n<!-- AC:BEGIN -->\n- [ ] #1 Old\n<!-- AC:END -->\n";
        var updated = TaskMarkdown.SetAcceptanceCriteria(body, new[] { ("New", true) });

        Assert.Equal("Desc", TaskMarkdown.GetDescription(updated));
        var items = TaskMarkdown.GetAcceptanceCriteria(updated);
        Assert.Single(items);
        Assert.Equal("New", items[0].Text);
        Assert.True(items[0].Checked);
    }

    [Fact]
    public void ImplementationPlanAndNotesAndFinalSummary_RoundTrip()
    {
        var body = "\n";
        body = TaskMarkdown.SetImplementationPlan(body, "Plan text.");
        body = TaskMarkdown.SetImplementationNotes(body, "Notes text.");
        body = TaskMarkdown.SetFinalSummary(body, "Summary text.");

        Assert.Equal("Plan text.", TaskMarkdown.GetImplementationPlan(body));
        Assert.Equal("Notes text.", TaskMarkdown.GetImplementationNotes(body));
        Assert.Equal("Summary text.", TaskMarkdown.GetFinalSummary(body));
    }

    [Fact]
    public void GetOptionalSection_ReturnsNull_WhenNeverSet()
    {
        Assert.Null(TaskMarkdown.GetImplementationPlan("\nSome unrelated text.\n"));
    }

    [Fact]
    public void SectionInsertion_PreservesUnrelatedContent()
    {
        var body = "\n## Comments\n- someone said something\n";
        var updated = TaskMarkdown.SetFinalSummary(body, "Done.");

        Assert.Contains("## Comments\n- someone said something", updated);
        Assert.Equal("Done.", TaskMarkdown.GetFinalSummary(updated));
    }
}
