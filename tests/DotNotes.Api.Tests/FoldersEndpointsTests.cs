using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DotNotes.Api.Tests;

/// <summary>
/// Integration tests for docs/04-API-SPEC.md's folder-management rows:
/// <c>POST /api/folders/{**path}</c>, <c>POST /api/folders/{**path}/move</c>
/// and <c>DELETE /api/folders/{**path}</c>,
/// exercised end-to-end through a real ASP.NET Core test host (see
/// <see cref="NotesApiFactory"/>).
/// </summary>
public sealed class FoldersEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _vaultRootPath;
    private readonly NotesApiFactory _factory;
    private readonly HttpClient _client;

    public FoldersEndpointsTests()
    {
        _vaultRootPath = Directory.CreateTempSubdirectory("dotnotes-folders-api-tests-").FullName;
        _factory = new NotesApiFactory(_vaultRootPath);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("Vault__RootPath", null);

        if (Directory.Exists(_vaultRootPath))
        {
            Directory.Delete(_vaultRootPath, recursive: true);
        }
    }

    [Fact]
    public async Task CreateFolder_NewFolder_ReturnsPathAndCreatesDirectory()
    {
        var response = await _client.PostAsync("/api/folders/projects", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<FolderDto>(ResponseJsonOptions);
        Assert.NotNull(result);
        Assert.Equal("projects", result!.Path);
        Assert.True(Directory.Exists(Path.Combine(_vaultRootPath, "projects")));
    }

    [Fact]
    public async Task CreateFolder_NestedFolder_CreatesMissingParents()
    {
        var response = await _client.PostAsync("/api/folders/a/b/c", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<FolderDto>(ResponseJsonOptions);
        Assert.NotNull(result);
        Assert.Equal("a/b/c", result!.Path);
        Assert.True(Directory.Exists(Path.Combine(_vaultRootPath, "a", "b", "c")));
    }

    [Fact]
    public async Task CreateFolder_AlreadyExists_IsIdempotentAndReturns200()
    {
        await _client.PostAsync("/api/folders/projects", content: null);

        var response = await _client.PostAsync("/api/folders/projects", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<FolderDto>(ResponseJsonOptions);
        Assert.NotNull(result);
        Assert.Equal("projects", result!.Path);
    }

    [Fact]
    public async Task CreateFolder_NoteAlreadyExistsAtThatPath_Returns409()
    {
        WriteNoteToDisk("projects.md", "# projects");

        var response = await _client.PostAsync("/api/folders/projects.md", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.False(string.IsNullOrWhiteSpace(error!.Error));
    }

    [Fact]
    public async Task CreateFolder_PathTraversalAttempt_Returns400()
    {
        var response = await _client.PostAsync("/api/folders/..%5coutside", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_path", error!.Error);
    }

    [Fact]
    public async Task CreateFolder_FailIfExists_NestedSubfolder_Returns200AndCreatesDirectory()
    {
        await _client.PostAsync("/api/folders/projects", content: null);

        var response = await _client.PostAsync("/api/folders/projects/alpha?failIfExists=true", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<FolderDto>(ResponseJsonOptions);
        Assert.Equal("projects/alpha", result!.Path);
        Assert.True(Directory.Exists(Path.Combine(_vaultRootPath, "projects", "alpha")));
    }

    [Fact]
    public async Task CreateFolder_FailIfExists_ExistingFolder_Returns409AlreadyExists()
    {
        await _client.PostAsync("/api/folders/projects/alpha", content: null);

        var response = await _client.PostAsync("/api/folders/projects/alpha?failIfExists=true", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.Equal("already_exists", error!.Error);
    }

    [Theory]
    [InlineData("/api/folders/projects/..%5coutside?failIfExists=true")]
    [InlineData("/api/folders/projects/bad%7Cname?failIfExists=true")]
    public async Task CreateFolder_FailIfExists_TraversalOrInvalidName_Returns400(string url)
    {
        await _client.PostAsync("/api/folders/projects", content: null);

        var response = await _client.PostAsync(url, content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.Equal("invalid_path", error!.Error);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(_vaultRootPath)!, "outside")));
    }

    [Fact]
    public async Task CreateFolder_FailIfExists_EncodedSlashTraversal_StaysInsideTheVault()
    {
        // ASP.NET Core keeps %2F encoded in route values, so this is a
        // single (odd but harmless) segment name, never a traversal.
        await _client.PostAsync("/api/folders/projects", content: null);

        var response = await _client.PostAsync("/api/folders/projects/..%2F..%2Foutside?failIfExists=true", content: null);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest });
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(_vaultRootPath)!, "outside")));
        Assert.False(Directory.Exists(Path.Combine(_vaultRootPath, "outside")));
    }

    [Fact]
    public async Task MoveFolder_ExistingFolder_RenamesDirectoryAndReturnsNewPath()
    {
        WriteNoteToDisk("projects/idea.md", "# idea");

        var response = await _client.PostAsJsonAsync("/api/folders/projects/move", new { destinationPath = "work" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<FolderMoveDto>(ResponseJsonOptions);
        Assert.NotNull(result);
        Assert.Equal("work", result!.Path);

        // Nothing else in the vault links to this note, so nothing needed
        // rewriting - per docs/04-API-SPEC.md, rewrittenNotes is an empty
        // array (never omitted/null) in that case.
        Assert.NotNull(result.RewrittenNotes);
        Assert.Empty(result.RewrittenNotes);

        Assert.False(Directory.Exists(Path.Combine(_vaultRootPath, "projects")));
        Assert.True(File.Exists(Path.Combine(_vaultRootPath, "work", "idea.md")));
    }

    [Fact]
    public async Task MoveFolder_MissingSourceFolder_Returns404()
    {
        var response = await _client.PostAsJsonAsync("/api/folders/does-not-exist/move", new { destinationPath = "elsewhere" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("not_found", error!.Error);
    }

    [Fact]
    public async Task MoveFolder_DestinationAlreadyExists_Returns409()
    {
        WriteNoteToDisk("projects/idea.md", "# idea");
        await _client.PostAsync("/api/folders/work", content: null);

        var response = await _client.PostAsJsonAsync("/api/folders/projects/move", new { destinationPath = "work" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.False(string.IsNullOrWhiteSpace(error!.Error));
    }

    [Fact]
    public async Task MoveFolder_DestinationIsOwnDescendant_Returns400()
    {
        WriteNoteToDisk("projects/idea.md", "# idea");

        var response = await _client.PostAsJsonAsync("/api/folders/projects/move", new { destinationPath = "projects/archive" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_path", error!.Error);
    }

    [Fact]
    public async Task MoveFolder_MissingBody_Returns400()
    {
        WriteNoteToDisk("projects/idea.md", "# idea");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/folders/projects/move");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
    }

    /// <summary>
    /// The one subtle behaviour docs/06-DATA-MODEL.md calls out explicitly:
    /// a folder move must synchronously rebuild the link and search
    /// indexes before responding, since <c>VaultWatcherService</c>'s file
    /// watcher can't correctly reconcile a directory-level rename on its
    /// own. This writes two notes (one linking to the other) directly to
    /// disk *before* the host starts (so they're present for the initial
    /// startup scan, with no need to wait on the live file watcher), moves
    /// their containing folder, and immediately - no delay, no polling -
    /// asserts both <c>GET /api/graph</c> and <c>GET /api/search</c>
    /// reflect the new paths in the very same request that follows the
    /// move.
    /// </summary>
    [Fact]
    public async Task MoveFolder_RebuildsLinkAndSearchIndexesSynchronously_NoWaitRequired()
    {
        WriteNoteToDisk("projects/idea.md", "# idea\n\nSome unique-search-token content.");
        WriteNoteToDisk("projects/plan.md", "See [[projects/idea]] for details.");

        using var factory = new NotesApiFactory(_vaultRootPath);
        using var client = factory.CreateClient();

        var moveResponse = await client.PostAsJsonAsync("/api/folders/projects/move", new { destinationPath = "archive" });
        Assert.Equal(HttpStatusCode.OK, moveResponse.StatusCode);

        // Immediate (no delay/poll) graph check: old paths gone, new paths
        // present, and the wikilink edge now points at the new target path.
        var graphResponse = await client.GetAsync("/api/graph");
        Assert.Equal(HttpStatusCode.OK, graphResponse.StatusCode);
        var graph = await graphResponse.Content.ReadFromJsonAsync<GraphDto>(ResponseJsonOptions);
        Assert.NotNull(graph);

        Assert.DoesNotContain(graph!.Nodes, n => n.Id == "projects/idea.md");
        Assert.DoesNotContain(graph.Nodes, n => n.Id == "projects/plan.md");
        Assert.Contains(graph.Nodes, n => n.Id == "archive/idea.md" && n.Exists);
        Assert.Contains(graph.Nodes, n => n.Id == "archive/plan.md" && n.Exists);
        Assert.Contains(graph.Edges, e => e.Source == "archive/plan.md" && e.Target == "archive/idea.md");

        // Immediate (no delay/poll) search check: the moved note's content
        // is findable and reports its new path.
        var searchResponse = await client.GetAsync("/api/search?q=unique-search-token");
        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        var results = await searchResponse.Content.ReadFromJsonAsync<List<SearchResultDto>>(ResponseJsonOptions);
        Assert.NotNull(results);
        Assert.Contains(results!, r => r.Path == "archive/idea.md");
        Assert.DoesNotContain(results!, r => r.Path == "projects/idea.md");
    }

    /// <summary>
    /// Exercises <c>IVaultReorganizationService.MoveFolderAsync</c> end-to-end
    /// through the REST layer, covering a case the simpler test above
    /// doesn't: a rewrite that is actually *necessary*, not just possible.
    /// <c>WikiLinkResolver</c>'s unique-bare-title fallback (see its
    /// remarks) means a folder move alone usually "self-heals" a bare-title
    /// link with no rewrite needed at all - which is exactly what the test
    /// above demonstrates. Forcing a *real* rewrite therefore requires
    /// breaking that fallback: this fixture adds a second, stationary
    /// "decoy" note that happens to share the moved note's bare title
    /// ("target"), positioned (via folder name, alphabetically) so the
    /// bare-title tie-break would resolve <c>[[target]]</c> to the decoy,
    /// not the just-moved note, once the moved note's own folder name wins
    /// the tie-break pre-move but loses it post-move. That forces
    /// <c>[[target]]</c> to be rewritten to an explicit path
    /// (<c>[[zzz/target]]</c>) to keep pointing at the note that actually
    /// moved. See docs/06-DATA-MODEL.md's "Folder &amp; note move/rename"
    /// section for the underlying minimal/style-preserving rewrite rules.
    /// </summary>
    [Fact]
    public async Task MoveFolder_RenameCreatesLinkAmbiguity_RewritesLinkingNoteAndReportsIt()
    {
        // "aaa" sorts before "bbb" (source folder wins the pre-move
        // bare-title tie-break against the decoy), but the destination
        // "zzz" sorts after "bbb" (the decoy wins the tie-break post-move) -
        // see this test's remarks for why that's what forces a rewrite.
        WriteNoteToDisk("aaa/target.md", "# target");
        WriteNoteToDisk("bbb/target.md", "# decoy target");
        WriteNoteToDisk("linker.md", "See [[target]] for details.");

        using var factory = new NotesApiFactory(_vaultRootPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/folders/aaa/move", new { destinationPath = "zzz" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<FolderMoveDto>(ResponseJsonOptions);
        Assert.NotNull(result);
        Assert.Equal("zzz", result!.Path);
        Assert.NotNull(result.RewrittenNotes);
        Assert.Contains("linker.md", result.RewrittenNotes);

        var linkerContent = await File.ReadAllTextAsync(Path.Combine(_vaultRootPath, "linker.md"));
        Assert.Equal("See [[zzz/target]] for details.", linkerContent);

        // Immediate (no delay/poll) consistency check per docs/04-API-SPEC.md.
        var graphResponse = await client.GetAsync("/api/graph");
        Assert.Equal(HttpStatusCode.OK, graphResponse.StatusCode);
        var graph = await graphResponse.Content.ReadFromJsonAsync<GraphDto>(ResponseJsonOptions);
        Assert.NotNull(graph);
        Assert.Contains(graph!.Edges, e => e.Source == "linker.md" && e.Target == "zzz/target.md");
        Assert.DoesNotContain(graph.Edges, e => e.Source == "linker.md" && e.Target == "bbb/target.md");
    }

    // REST/MCP parity: DotNotesMcpToolsTests already covers
    // MoveFolder_VaultRootMissing_ThrowsMcpExceptionNotRawVaultUnavailableException
    // for move_folder; this is the REST-surface equivalent, following the
    // exact reproduction NotesEndpointsTests uses for the note-move case.
    [Fact]
    public async Task MoveFolder_WhenVaultRootHasVanished_Returns503WithAppErrorShape()
    {
        WriteNoteToDisk("projects/idea.md", "# idea");
        Directory.Delete(_vaultRootPath, recursive: true);

        var response = await _client.PostAsJsonAsync("/api/folders/projects/move", new { destinationPath = "archive" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("vault_unavailable", error!.Error);
        Assert.False(Directory.Exists(_vaultRootPath));
    }

    // --- DELETE /api/folders/{**path} -----------------------------------

    [Fact]
    public async Task DeleteFolder_EmptyFolder_RemovesItAndReturnsNoContent()
    {
        Directory.CreateDirectory(Path.Combine(_vaultRootPath, "projects"));

        var response = await _client.DeleteAsync("/api/folders/projects");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_vaultRootPath, "projects")));
    }

    [Fact]
    public async Task DeleteFolder_NonEmptyNestedFolder_RemovesTheWholeSubtree()
    {
        WriteNoteToDisk("projects/idea.md", "# idea");
        WriteNoteToDisk("projects/archive/old/ancient.md", "# ancient");

        var response = await _client.DeleteAsync("/api/folders/projects");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_vaultRootPath, "projects")));
    }

    [Fact]
    public async Task DeleteFolder_NestedPathWithSpaces_RemovesThatFolderOnly()
    {
        WriteNoteToDisk("my projects/deep folder/note.md", "# note");
        WriteNoteToDisk("my projects/keep.md", "# keep");

        var response = await _client.DeleteAsync("/api/folders/my%20projects/deep%20folder");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_vaultRootPath, "my projects", "deep folder")));
        Assert.True(File.Exists(Path.Combine(_vaultRootPath, "my projects", "keep.md")));
    }

    [Fact]
    public async Task DeleteFolder_MissingFolder_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync("/api/folders/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("not_found", error!.Error);
    }

    [Fact]
    public async Task DeleteFolder_PathIsANote_ReturnsNotFoundAndLeavesTheNoteAlone()
    {
        WriteNoteToDisk("idea.md", "# idea");

        var response = await _client.DeleteAsync("/api/folders/idea.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(_vaultRootPath, "idea.md")));
    }

    /// <summary>
    /// A <c>..</c> in the request URI never escapes the vault. Kestrel
    /// normalizes dot segments out of the request path before routing, so
    /// these requests arrive at the handler as a plain in-vault folder
    /// name and 404 rather than reaching outside the root; the repository's
    /// own <c>'.'</c>/<c>'..'</c>-segment rejection (covered directly in
    /// FileSystemNoteRepositoryTests) is the second line of defence for
    /// any caller that bypasses HTTP. Either way, what matters is asserted
    /// here: nothing outside the vault root is touched.
    /// </summary>
    [Theory]
    [InlineData("/api/folders/%2E%2E/escape")]
    [InlineData("/api/folders/projects/%2E%2E/%2E%2E/escape")]
    public async Task DeleteFolder_TraversalPath_DeletesNothingOutsideTheVault(string url)
    {
        var outsideDirectory = Path.Combine(Path.GetDirectoryName(_vaultRootPath)!, "escape");
        Directory.CreateDirectory(outsideDirectory);
        try
        {
            var response = await _client.DeleteAsync(url);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.True(Directory.Exists(outsideDirectory));
        }
        finally
        {
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolder_VaultRoot_IsRejectedAndLeavesTheVaultIntact()
    {
        WriteNoteToDisk("idea.md", "# idea");

        var response = await _client.DeleteAsync("/api/folders/");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_path", error!.Error);
        Assert.True(Directory.Exists(_vaultRootPath));
        Assert.True(File.Exists(Path.Combine(_vaultRootPath, "idea.md")));
    }

    [Fact]
    public async Task DeleteFolder_VaultRootMissing_ReturnsServiceUnavailable()
    {
        Directory.Delete(_vaultRootPath, recursive: true);

        var response = await _client.DeleteAsync("/api/folders/projects");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("vault_unavailable", error!.Error);
    }

    private void WriteNoteToDisk(string relativePath, string content)
    {
        var fullPath = Path.Combine(_vaultRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }

    private sealed record FolderDto(string Path);

    /// <summary>Backs the <c>{ path, rewrittenNotes }</c> response of <c>POST /api/folders/{**path}/move</c>.</summary>
    private sealed record FolderMoveDto(string Path, List<string> RewrittenNotes);

    private sealed record ErrorDto(string Error, string? Detail);

    private sealed record GraphDto(List<GraphNodeDto> Nodes, List<GraphEdgeDto> Edges);

    private sealed record GraphNodeDto(string Id, string Label, bool Exists);

    private sealed record GraphEdgeDto(string Source, string Target);

    private sealed record SearchResultDto(string Path, string Title, string Snippet, double Score);
}
