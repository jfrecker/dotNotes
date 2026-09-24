using DotNotes.Api.Mcp;
using DotNotes.Core.Config;
using DotNotes.Core.Links;
using DotNotes.Core.Notes;
using DotNotes.Core.Reorganization;
using DotNotes.Core.Search;
using DotNotes.Core.Vault;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace DotNotes.Api.Tests.Mcp;

/// <summary>
/// Unit tests for <see cref="DotNotesMcpTools"/>, exercised directly
/// (no MCP wire transport involved - that's covered by this phase's
/// manual verification against a real MCP client) against a real
/// <see cref="FileSystemNoteRepository"/>/<see cref="InMemoryLinkIndex"/>/
/// <see cref="InMemorySearchIndex"/> over an isolated temp vault, mirroring
/// how <c>InMemorySearchIndexTests</c>/<c>InMemoryLinkIndexTests</c> test
/// their subjects directly with no ASP.NET Core host involved. Every tool
/// here calls into the exact same <c>DotNotes.Core</c> services the REST
/// endpoints use, so this is exercising real path validation and real
/// file I/O, not fakes.
/// </summary>
public sealed class DotNotesMcpToolsTests : IDisposable
{
    private readonly DirectoryInfo _vaultDirectory;
    private readonly FileSystemNoteRepository _noteRepository;
    private readonly InMemorySearchIndex _searchIndex;
    private readonly InMemoryLinkIndex _linkIndex;
    private readonly VaultReorganizationService _reorganizationService;

    public DotNotesMcpToolsTests()
    {
        _vaultDirectory = Directory.CreateTempSubdirectory("dotnotes-mcp-tools-tests-");
        _noteRepository = new FileSystemNoteRepository(
            Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName }));
        _searchIndex = new InMemorySearchIndex(_noteRepository);
        _linkIndex = new InMemoryLinkIndex(_noteRepository);
        _reorganizationService = new VaultReorganizationService(
            _noteRepository,
            new IVaultChangeListener[] { _linkIndex, _searchIndex },
            NullLogger<VaultReorganizationService>.Instance);
    }

    public void Dispose()
    {
        // The vault-unavailable test deletes this directory itself as
        // part of the scenario it's exercising, so it may already be
        // gone by the time Dispose runs - that's expected, not a cleanup
        // failure. Directory.Exists (not DirectoryInfo.Exists, which
        // caches) reflects the current on-disk state.
        if (Directory.Exists(_vaultDirectory.FullName))
        {
            _vaultDirectory.Delete(recursive: true);
        }
    }

    private DotNotesMcpTools CreateTools(bool sharingEnabled = true, bool mcpEnabled = true) =>
        new(
            _noteRepository,
            _reorganizationService,
            _searchIndex,
            _linkIndex,
            Options.Create(new SharingOptions { Enabled = sharingEnabled }),
            Options.Create(new McpOptions { Enabled = mcpEnabled }));

    // ---- search_notes ----

    [Fact]
    public async Task SearchNotes_ReturnsSameRankingShapeAsSearchIndex()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "This note is about a great idea.");
        await _searchIndex.RebuildAsync();

        var tools = CreateTools();
        var results = tools.SearchNotes("idea", limit: 10);

        var hit = Assert.Single(results);
        Assert.Equal("projects/idea.md", hit.Path);
        Assert.Equal("idea", hit.Title);
        Assert.Contains("idea", hit.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchNotes_NoLimitSupplied_DefaultsToTen()
    {
        for (var i = 0; i < 15; i++)
        {
            await _noteRepository.SaveAsync($"note-{i}.md", "shared keyword content");
        }

        await _searchIndex.RebuildAsync();

        var tools = CreateTools();
        var results = tools.SearchNotes("keyword", limit: null);

        Assert.Equal(10, results.Count);
    }

    // ---- get_note ----

    [Fact]
    public async Task GetNote_ExistingNote_ReturnsPathAndContent()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "# idea\n\nbody text");

        var tools = CreateTools();
        var note = await tools.GetNote("projects/idea.md");

        Assert.Equal("projects/idea.md", note.Path);
        Assert.Equal("# idea\n\nbody text", note.Content);
        Assert.Null(note.Backlinks);
    }

    [Fact]
    public async Task GetNote_MissingNote_ThrowsMcpExceptionNotRawException()
    {
        var tools = CreateTools();

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.GetNote("does/not/exist.md"));
        Assert.Contains("does/not/exist.md", ex.Message);
    }

    [Fact]
    public async Task GetNote_PathEscapesVaultRoot_ThrowsMcpExceptionNotInvalidNotePathException()
    {
        var tools = CreateTools();

        await Assert.ThrowsAsync<McpException>(() => tools.GetNote("../../etc/passwd.md"));
    }

    [Fact]
    public async Task GetNote_VaultRootMissing_ThrowsMcpExceptionNotRawVaultUnavailableException()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "content");
        var tools = CreateTools();
        _vaultDirectory.Delete(recursive: true);

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.GetNote("projects/idea.md"));
        Assert.DoesNotContain("VaultUnavailableException", ex.Message);
    }

    [Fact]
    public async Task GetNote_IncludeBacklinks_ReturnsLinkingNotes()
    {
        await _noteRepository.SaveAsync("target.md", "the target note");
        await _noteRepository.SaveAsync("source.md", "links to [[target]]");
        _linkIndex.NoteChanged("target.md", "the target note");
        _linkIndex.NoteChanged("source.md", "links to [[target]]");

        var tools = CreateTools();
        var note = await tools.GetNote("target.md", includeBacklinks: true);

        Assert.NotNull(note.Backlinks);
        var backlink = Assert.Single(note.Backlinks!);
        Assert.Equal("source.md", backlink.Path);
        Assert.Equal("source", backlink.Title);
    }

    // ---- create_note ----

    [Fact]
    public async Task CreateNote_NewPath_CreatesFileOnDisk()
    {
        var tools = CreateTools();
        var result = await tools.CreateNote("projects/new-idea.md", "brand new content");

        Assert.Equal("projects/new-idea.md", result.Path);
        var onDisk = await _noteRepository.GetAsync("projects/new-idea.md");
        Assert.NotNull(onDisk);
        Assert.Equal("brand new content", onDisk!.Content);
    }

    [Fact]
    public async Task CreateNote_AlreadyExists_ThrowsMcpExceptionAndDoesNotOverwrite()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "original content");
        var tools = CreateTools();

        await Assert.ThrowsAsync<McpException>(() => tools.CreateNote("projects/idea.md", "clobbered content"));

        var onDisk = await _noteRepository.GetAsync("projects/idea.md");
        Assert.Equal("original content", onDisk!.Content);
    }

    // ---- update_note ----

    [Fact]
    public async Task UpdateNote_MissingNote_CreatesIt()
    {
        var tools = CreateTools();
        var result = await tools.UpdateNote("projects/idea.md", "content");

        Assert.Equal("projects/idea.md", result.Path);
        var onDisk = await _noteRepository.GetAsync("projects/idea.md");
        Assert.NotNull(onDisk);
    }

    [Fact]
    public async Task UpdateNote_ExistingNote_Overwrites()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "old content");
        var tools = CreateTools();

        var result = await tools.UpdateNote("projects/idea.md", "new content");

        Assert.Equal("projects/idea.md", result.Path);
        var onDisk = await _noteRepository.GetAsync("projects/idea.md");
        Assert.Equal("new content", onDisk!.Content);
    }

    [Fact]
    public async Task UpdateNote_PathEscapesVaultRoot_ThrowsMcpException()
    {
        var tools = CreateTools();

        await Assert.ThrowsAsync<McpException>(() => tools.UpdateNote("../escape.md", "x"));
    }

    // ---- get_backlinks ----

    [Fact]
    public async Task GetBacklinks_ReturnsSourcesLinkingToTarget()
    {
        await _noteRepository.SaveAsync("target.md", "the target note");
        await _noteRepository.SaveAsync("source.md", "links to [[target]]");
        _linkIndex.NoteChanged("target.md", "the target note");
        _linkIndex.NoteChanged("source.md", "links to [[target]]");

        var tools = CreateTools();
        var backlinks = await tools.GetBacklinks("target.md");

        var backlink = Assert.Single(backlinks);
        Assert.Equal("source.md", backlink.Path);
        Assert.Equal("source", backlink.Title);
    }

    [Fact]
    public async Task GetBacklinks_NothingLinksToIt_ReturnsEmptyList()
    {
        var tools = CreateTools();
        var backlinks = await tools.GetBacklinks("lonely.md");

        Assert.Empty(backlinks);
    }

    [Fact]
    public async Task GetBacklinks_PathEscapesVaultRoot_ThrowsMcpException()
    {
        var tools = CreateTools();

        await Assert.ThrowsAsync<McpException>(() => tools.GetBacklinks("../../escape.md"));
    }

    // ---- get_recent_notes ----

    [Fact]
    public async Task GetRecentNotes_SortsByMostRecentlyModifiedFirst()
    {
        await _noteRepository.SaveAsync("oldest.md", "content");
        await Task.Delay(20);
        await _noteRepository.SaveAsync("middle.md", "content");
        await Task.Delay(20);
        await _noteRepository.SaveAsync("newest.md", "content");

        var tools = CreateTools();
        var recent = await tools.GetRecentNotes(limit: 10);

        Assert.Equal(["newest.md", "middle.md", "oldest.md"], recent.Select(n => n.Path).ToArray());
    }

    [Fact]
    public async Task GetRecentNotes_RespectsLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            await _noteRepository.SaveAsync($"note-{i}.md", "content");
        }

        var tools = CreateTools();
        var recent = await tools.GetRecentNotes(limit: 2);

        Assert.Equal(2, recent.Count);
    }

    // ---- create_folder ----

    [Fact]
    public async Task CreateFolder_NewPath_CreatesFolderOnDisk()
    {
        var tools = CreateTools();
        var result = await tools.CreateFolder("projects/archive");

        Assert.Equal("projects/archive", result.Path);
        Assert.True(Directory.Exists(Path.Combine(_vaultDirectory.FullName, "projects", "archive")));
    }

    [Fact]
    public async Task CreateFolder_NoteAlreadyExistsAtPath_ThrowsMcpException()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "content");
        var tools = CreateTools();

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.CreateFolder("projects/idea.md"));
        Assert.Contains("projects/idea.md", ex.Message);
    }

    [Fact]
    public async Task CreateFolder_PathEscapesVaultRoot_ThrowsMcpException()
    {
        var tools = CreateTools();

        await Assert.ThrowsAsync<McpException>(() => tools.CreateFolder("../../escape"));
    }

    [Fact]
    public async Task CreateFolder_VaultRootMissing_ThrowsMcpExceptionNotRawVaultUnavailableException()
    {
        var tools = CreateTools();
        _vaultDirectory.Delete(recursive: true);

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.CreateFolder("projects/archive"));
        Assert.DoesNotContain("VaultUnavailableException", ex.Message);
    }

    // ---- move_note ----

    [Fact]
    public async Task MoveNote_ExistingNote_MovesToDestination()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "content");
        var tools = CreateTools();

        var result = await tools.MoveNote("projects/idea.md", "projects/archive/idea.md");

        Assert.Equal("projects/archive/idea.md", result.Path);
        Assert.Empty(result.RewrittenNotes);
        Assert.Null(await _noteRepository.GetAsync("projects/idea.md"));
        var moved = await _noteRepository.GetAsync("projects/archive/idea.md");
        Assert.NotNull(moved);
        Assert.Equal("content", moved!.Content);
    }

    [Fact]
    public async Task MoveNote_LinkedFromAnotherNote_RewritesTheLinkingNoteAndReportsIt()
    {
        // A decoy note ("other/idea.md") sharing the moved note's bare
        // title means the unique-title fallback that would otherwise
        // rescue a plain rename (docs/06-DATA-MODEL.md's "Minimal" rule -
        // see WikiLinkResolver's remarks) no longer resolves the link back
        // to the moved note post-move, so the link genuinely breaks and
        // must be rewritten.
        await _noteRepository.SaveAsync("projects/idea.md", "content");
        await _noteRepository.SaveAsync("other/idea.md", "an unrelated note with the same title");
        await _noteRepository.SaveAsync("index.md", "See [[projects/idea]] for details.");
        var tools = CreateTools();

        var result = await tools.MoveNote("projects/idea.md", "projects/archive/idea.md");

        Assert.Equal("projects/archive/idea.md", result.Path);
        Assert.Equal(["index.md"], result.RewrittenNotes);

        var linkingNote = await _noteRepository.GetAsync("index.md");
        Assert.NotNull(linkingNote);
        Assert.Contains("[[projects/archive/idea]]", linkingNote!.Content);
    }

    [Fact]
    public async Task MoveNote_DestinationAlreadyExists_ThrowsMcpExceptionAndTouchesNeitherFile()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "source content");
        await _noteRepository.SaveAsync("projects/archive/idea.md", "destination content");
        var tools = CreateTools();

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.MoveNote("projects/idea.md", "projects/archive/idea.md"));
        Assert.Contains("projects/idea.md", ex.Message);
        Assert.Contains("projects/archive/idea.md", ex.Message);

        var source = await _noteRepository.GetAsync("projects/idea.md");
        var destination = await _noteRepository.GetAsync("projects/archive/idea.md");
        Assert.NotNull(source);
        Assert.Equal("source content", source!.Content);
        Assert.NotNull(destination);
        Assert.Equal("destination content", destination!.Content);
    }

    [Fact]
    public async Task MoveNote_SourceDoesNotExist_ThrowsMcpException()
    {
        var tools = CreateTools();

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.MoveNote("does/not/exist.md", "somewhere/else.md"));
        Assert.Contains("does/not/exist.md", ex.Message);
    }

    [Fact]
    public async Task MoveNote_PathEscapesVaultRoot_ThrowsMcpException()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "content");
        var tools = CreateTools();

        await Assert.ThrowsAsync<McpException>(() => tools.MoveNote("projects/idea.md", "../../escape.md"));
    }

    [Fact]
    public async Task MoveNote_VaultRootMissing_ThrowsMcpExceptionNotRawVaultUnavailableException()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "content");
        var tools = CreateTools();
        _vaultDirectory.Delete(recursive: true);

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.MoveNote("projects/idea.md", "projects/archive/idea.md"));
        Assert.DoesNotContain("VaultUnavailableException", ex.Message);
    }

    // ---- move_folder ----

    [Fact]
    public async Task MoveFolder_FolderWithNestedNote_MovesEverythingAndRewritesLinks()
    {
        // The folder itself, plus a nested note, both move. A decoy note
        // sharing the nested note's bare title (same reasoning as
        // MoveNote_LinkedFromAnotherNote_RewritesTheLinkingNoteAndReportsIt
        // above) forces the path-style link elsewhere in the vault to
        // genuinely break on move rather than being rescued by the
        // unique-title fallback, so it must be rewritten.
        await _noteRepository.SaveAsync("projects/idea.md", "content");
        await _noteRepository.SaveAsync("other/idea.md", "an unrelated note with the same title");
        await _noteRepository.SaveAsync("index.md", "See [[projects/idea]] for details.");
        var tools = CreateTools();

        var result = await tools.MoveFolder("projects", "archive/projects");

        Assert.Equal("archive/projects", result.Path);
        Assert.Equal(["index.md"], result.RewrittenNotes);

        Assert.Null(await _noteRepository.GetAsync("projects/idea.md"));
        var moved = await _noteRepository.GetAsync("archive/projects/idea.md");
        Assert.NotNull(moved);
        Assert.Equal("content", moved!.Content);

        var linkingNote = await _noteRepository.GetAsync("index.md");
        Assert.NotNull(linkingNote);
        Assert.Contains("[[archive/projects/idea]]", linkingNote!.Content);
    }

    [Fact]
    public async Task MoveFolder_DestinationAlreadyExists_ThrowsMcpExceptionAndTouchesNeitherFolder()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "source content");
        await _noteRepository.SaveAsync("archive/idea.md", "destination content");
        var tools = CreateTools();

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.MoveFolder("projects", "archive"));
        Assert.Contains("projects", ex.Message);
        Assert.Contains("archive", ex.Message);

        var source = await _noteRepository.GetAsync("projects/idea.md");
        var destination = await _noteRepository.GetAsync("archive/idea.md");
        Assert.NotNull(source);
        Assert.Equal("source content", source!.Content);
        Assert.NotNull(destination);
        Assert.Equal("destination content", destination!.Content);
    }

    [Fact]
    public async Task MoveFolder_SourceDoesNotExist_ThrowsMcpException()
    {
        var tools = CreateTools();

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.MoveFolder("does/not/exist", "somewhere/else"));
        Assert.Contains("does/not/exist", ex.Message);
    }

    [Fact]
    public async Task MoveFolder_PathEscapesVaultRoot_ThrowsMcpException()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "content");
        var tools = CreateTools();

        await Assert.ThrowsAsync<McpException>(() => tools.MoveFolder("projects", "../../escape"));
    }

    [Fact]
    public async Task MoveFolder_DestinationIsOwnDescendant_ThrowsMcpException()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "content");
        var tools = CreateTools();

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.MoveFolder("projects", "projects/archive"));
        Assert.Contains("projects", ex.Message);
    }

    [Fact]
    public async Task MoveFolder_VaultRootMissing_ThrowsMcpExceptionNotRawVaultUnavailableException()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "content");
        var tools = CreateTools();
        _vaultDirectory.Delete(recursive: true);

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.MoveFolder("projects", "archive/projects"));
        Assert.DoesNotContain("VaultUnavailableException", ex.Message);
    }

    // ---- get_config ----

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void GetConfig_ReflectsSharingAndMcpFeatureFlags(bool sharingEnabled, bool mcpEnabled)
    {
        var tools = CreateTools(sharingEnabled, mcpEnabled);
        var config = tools.GetConfig();

        Assert.Equal("dotNotes", config.Name);
        Assert.False(string.IsNullOrWhiteSpace(config.Version));
        Assert.Equal(sharingEnabled, config.Features.Sharing);
        Assert.Equal(mcpEnabled, config.Features.Mcp);
        Assert.True(config.Features.Graph);
        Assert.True(config.Features.Tasks);
        Assert.Equal(1500, config.AutosaveDelayMs);
    }
}
