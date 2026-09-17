using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DotNotes.Api.Tests;

/// <summary>
/// Integration tests for docs/04-API-SPEC.md's Notes section, exercised
/// end-to-end through a real ASP.NET Core test host (see
/// <see cref="NotesApiFactory"/>). Each test gets its own fresh
/// <see cref="NotesApiFactory"/> backed by its own temp vault directory
/// (created in the constructor, deleted in <see cref="Dispose"/>) so
/// tests never interfere with each other or a real vault.
/// </summary>
public sealed class NotesEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _vaultRootPath;
    private readonly NotesApiFactory _factory;
    private readonly HttpClient _client;

    public NotesEndpointsTests()
    {
        _vaultRootPath = Directory.CreateTempSubdirectory("dotnotes-api-tests-").FullName;
        _factory = new NotesApiFactory(_vaultRootPath);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();

        // Matches the Vault__RootPath environment variable set in
        // NotesApiFactory's constructor - see its remarks.
        Environment.SetEnvironmentVariable("Vault__RootPath", null);

        if (Directory.Exists(_vaultRootPath))
        {
            Directory.Delete(_vaultRootPath, recursive: true);
        }
    }

    [Fact]
    public async Task GetTree_EmptyVault_ReturnsEmptyArray()
    {
        var response = await _client.GetAsync("/api/notes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tree = await response.Content.ReadFromJsonAsync<List<NoteTreeEntryDto>>(ResponseJsonOptions);
        Assert.NotNull(tree);
        Assert.Empty(tree!);
    }

    [Fact]
    public async Task GetTree_WithNotes_ReturnsFilesAndFoldersSorted()
    {
        WriteNoteToDisk("root.md", "# root");
        WriteNoteToDisk("projects/idea.md", "# idea");
        WriteNoteToDisk("daily/2026-09-16.md", "# daily");

        var response = await _client.GetAsync("/api/notes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tree = await response.Content.ReadFromJsonAsync<List<NoteTreeEntryDto>>(ResponseJsonOptions);
        Assert.NotNull(tree);

        // Folders first (alphabetical), then files (alphabetical) - per
        // FileSystemNoteRepository.BuildTree's documented ordering.
        Assert.Collection(
            tree!,
            daily =>
            {
                Assert.Equal("daily", daily.Path);
                Assert.Equal("daily", daily.Name);
                Assert.Equal("folder", daily.Type);
                Assert.NotNull(daily.Children);
                var dailyChild = Assert.Single(daily.Children!);
                Assert.Equal("daily/2026-09-16.md", dailyChild.Path);
                Assert.Equal("file", dailyChild.Type);
                Assert.Null(dailyChild.Children);
            },
            projects =>
            {
                Assert.Equal("projects", projects.Path);
                Assert.Equal("folder", projects.Type);
                var projectsChild = Assert.Single(projects.Children!);
                Assert.Equal("projects/idea.md", projectsChild.Path);
            },
            root =>
            {
                Assert.Equal("root.md", root.Path);
                Assert.Equal("root.md", root.Name);
                Assert.Equal("file", root.Type);
                Assert.Null(root.Children);
            });
    }

    [Fact]
    public async Task GetNote_ExistingNote_ReturnsContentAndUpdatedAt()
    {
        WriteNoteToDisk("projects/idea.md", "# idea\n\nsome content");

        var response = await _client.GetAsync("/api/notes/projects/idea.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var note = await response.Content.ReadFromJsonAsync<NoteContentDto>(ResponseJsonOptions);
        Assert.NotNull(note);
        Assert.Equal("projects/idea.md", note!.Path);
        Assert.Equal("# idea\n\nsome content", note.Content);
        Assert.True(note.UpdatedAt > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task GetNote_MissingNote_Returns404WithErrorShape()
    {
        var response = await _client.GetAsync("/api/notes/does/not/exist.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("not_found", error!.Error);
        Assert.False(string.IsNullOrWhiteSpace(error.Detail));
    }

    [Fact]
    public async Task GetNote_WithoutIncludeBacklinks_OmitsOrNullsBacklinksField()
    {
        WriteNoteToDisk("projects/idea.md", "# idea");

        var response = await _client.GetAsync("/api/notes/projects/idea.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var note = await response.Content.ReadFromJsonAsync<NoteContentDto>(ResponseJsonOptions);
        Assert.NotNull(note);
        Assert.Equal("projects/idea.md", note!.Path);
        Assert.Null(note.Backlinks);
    }

    [Fact]
    public async Task GetNote_IncludeBacklinksTrue_PopulatesBacklinksFromLinkingNotes()
    {
        // These files are written directly to disk *after* the host (and
        // its VaultWatcherService) already started in the constructor, so
        // this also exercises the live file-watcher path end-to-end
        // (write -> debounced watcher event -> index update), not just
        // the initial startup scan - hence the polling helper below
        // rather than a single immediate request.
        WriteNoteToDisk("projects/idea.md", "# idea");
        WriteNoteToDisk("daily/2026-09-16.md", "See [[projects/idea]] for details.");

        var note = await GetNoteUntilAsync(
            "/api/notes/projects/idea.md?includeBacklinks=true",
            n => n?.Backlinks is { Count: > 0 });

        Assert.NotNull(note);
        Assert.NotNull(note!.Backlinks);
        var backlink = Assert.Single(note.Backlinks!);
        Assert.Equal("daily/2026-09-16.md", backlink.Path);
        Assert.Equal("2026-09-16", backlink.Title);
    }

    /// <summary>
    /// Polls <paramref name="requestUri"/> until <paramref name="isReady"/>
    /// is satisfied or a generous timeout elapses - used for assertions
    /// that depend on the debounced file-watcher (a few hundred ms by
    /// design, see <c>VaultWatcherService</c>) having settled, rather than
    /// a single immediate request that would be flaky under CI load.
    /// </summary>
    private async Task<NoteContentDto?> GetNoteUntilAsync(string requestUri, Func<NoteContentDto?, bool> isReady)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        NoteContentDto? note = null;

        while (DateTime.UtcNow < deadline)
        {
            var response = await _client.GetAsync(requestUri);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                note = await response.Content.ReadFromJsonAsync<NoteContentDto>(ResponseJsonOptions);
                if (isReady(note))
                {
                    return note;
                }
            }

            await Task.Delay(100);
        }

        return note;
    }

    [Fact]
    public async Task PutNote_NewNote_CreatesFileAndParentFolders()
    {
        var response = await _client.PutAsJsonAsync("/api/notes/new/deep/note.md", new { content = "hello world" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<NoteWriteDto>(ResponseJsonOptions);
        Assert.NotNull(result);
        Assert.Equal("new/deep/note.md", result!.Path);

        var fullPath = Path.Combine(_vaultRootPath, "new", "deep", "note.md");
        Assert.True(File.Exists(fullPath));
        Assert.Equal("hello world", await File.ReadAllTextAsync(fullPath));
    }

    [Fact]
    public async Task PutNote_ExistingNote_OverwritesContent()
    {
        WriteNoteToDisk("projects/idea.md", "old content");

        var response = await _client.PutAsJsonAsync("/api/notes/projects/idea.md", new { content = "new content" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<NoteWriteDto>(ResponseJsonOptions);
        Assert.NotNull(result);
        Assert.Equal("projects/idea.md", result!.Path);

        var fullPath = Path.Combine(_vaultRootPath, "projects", "idea.md");
        Assert.Equal("new content", await File.ReadAllTextAsync(fullPath));
    }

    [Fact]
    public async Task PutNote_MissingContentField_Returns400WithErrorShape()
    {
        var response = await _client.PutAsJsonAsync("/api/notes/projects/idea.md", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_request", error!.Error);
    }

    [Fact]
    public async Task PutNote_MalformedJsonBody_Returns400WithErrorShape()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/notes/projects/idea.md")
        {
            Content = new StringContent("{ not valid json", System.Text.Encoding.UTF8, "application/json")
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_body", error!.Error);
    }

    [Fact]
    public async Task DeleteNote_ExistingNote_Returns204AndRemovesFile()
    {
        WriteNoteToDisk("projects/idea.md", "content");

        var response = await _client.DeleteAsync("/api/notes/projects/idea.md");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(_vaultRootPath, "projects", "idea.md")));
    }

    [Fact]
    public async Task DeleteNote_MissingNote_Returns204Idempotently()
    {
        // Judgment call (docs/04-API-SPEC.md does not distinguish): DELETE
        // is idempotent, so deleting a note that never existed still
        // returns 204 - see the comment in NotesEndpoints.DeleteNoteAsync.
        var response = await _client.DeleteAsync("/api/notes/does/not/exist.md");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // NOTE on path-traversal test technique: a literal (or %2e%2e-encoded)
    // "/api/notes/../outside.md" never reaches FileSystemNoteRepository's
    // own ".." segment check at all - ASP.NET Core's own request-path
    // normalization (independent of, and earlier than, routing) collapses
    // RFC 3986 dot-segments before routing sees the URL, so the request
    // ends up targeting a path with no matching route ("/api/outside.md"),
    // producing a plain framework 404 with an empty body - see
    // GetNote_DotSegmentInUrl_IsNormalizedAwayByAspNetCoreBeforeRouting
    // below, which pins down that (harmless) behaviour. To exercise
    // FileSystemNoteRepository's own "must not contain '.' or '..'
    // segments" rejection over HTTP, the ".." has to arrive inside a
    // single route segment that ASP.NET Core's normalizer doesn't touch -
    // a backslash-separated segment does this, since '\' isn't a URL path
    // separator, so "..%5coutside.md" survives routing intact as the
    // single segment "..\outside.md", which FileSystemNoteRepository then
    // splits on '/' *and* '\' and rejects.
    [Fact]
    public async Task GetNote_PathTraversalAttempt_Returns400WithErrorShape()
    {
        var response = await _client.GetAsync("/api/notes/..%5coutside.md");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_path", error!.Error);
    }

    [Fact]
    public async Task PutNote_PathTraversalAttempt_Returns400WithErrorShape()
    {
        var response = await _client.PutAsJsonAsync("/api/notes/..%5coutside.md", new { content = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_path", error!.Error);
    }

    [Fact]
    public async Task DeleteNote_PathTraversalAttempt_Returns400WithErrorShape()
    {
        var response = await _client.DeleteAsync("/api/notes/..%5coutside.md");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_path", error!.Error);
    }

    [Fact]
    public async Task GetNote_DriveRelativePathAttempt_Returns400WithErrorShape()
    {
        // Another InvalidNotePathException case, reachable over HTTP
        // without relying on backslash-segment behaviour: a drive-relative
        // Windows path ("C:foo.md") is not rooted per Path.IsPathRooted,
        // but FileSystemNoteRepository's DriveLetterPattern check still
        // rejects it (see FileSystemNoteRepository.ResolveNotePath).
        var response = await _client.GetAsync("/api/notes/C%3Afoo.md");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_path", error!.Error);
    }

    [Fact]
    public async Task GetNote_DotSegmentInUrl_IsNormalizedAwayByAspNetCoreBeforeRouting()
    {
        // Documents the framework-level behaviour described in the note
        // above: ASP.NET Core normalizes ".." out of the request path
        // before routing, so this never reaches our "/api/notes/{**path}"
        // route (let alone FileSystemNoteRepository) - it 404s as an
        // unmatched route, not as our own not_found/invalid_path JSON
        // shape. Still a safe outcome (no traversal occurs), just not the
        // code path this test file's name might suggest at first glance.
        var response = await _client.GetAsync("/api/notes/%2e%2e/outside.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);
    }

    // ---- Vault-root-vanishes-mid-request guard (Phase 8 QA follow-up) ----

    // Reproduces the exact scenario QA reproduced live end-to-end: the host
    // is already up and running against _vaultRootPath (started in this
    // test's constructor), then the vault root directory disappears out
    // from under it entirely (a WSL2/network-mount hiccup, or a Docker
    // bind-mount host directory vanishing) - here simulated the same way
    // QA did it, by removing the directory while the app keeps running.
    // Both PUT (the write path that could previously fabricate a brand-new
    // empty vault root) and GET /api/notes (the read path that previously
    // had no try/catch at all and leaked a raw framework 500) must now
    // return this app's own {error, detail} 503 shape.
    [Fact]
    public async Task PutNote_WhenVaultRootHasVanished_Returns503WithAppErrorShape_AndDoesNotRecreateVaultRoot()
    {
        Directory.Delete(_vaultRootPath, recursive: true);

        var response = await _client.PutAsJsonAsync("/api/notes/new-note.md", new { content = "hello" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("vault_unavailable", error!.Error);
        Assert.False(string.IsNullOrWhiteSpace(error.Detail));

        // The whole point of the fix: a failed write must never fabricate
        // a fresh, empty directory at the old vault root path.
        Assert.False(Directory.Exists(_vaultRootPath));
    }

    [Fact]
    public async Task GetTree_WhenVaultRootHasVanished_Returns503WithAppErrorShape_NotRawProblemDetails()
    {
        WriteNoteToDisk("pre-existing.md", "content");
        Directory.Delete(_vaultRootPath, recursive: true);

        var response = await _client.GetAsync("/api/notes");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        // This app's own { error, detail } shape - not ASP.NET Core's
        // default ProblemDetails shape (which would have "title"/"status"/
        // "traceId" fields instead of "error"/"detail").
        Assert.True(json.RootElement.TryGetProperty("error", out var errorProperty));
        Assert.Equal("vault_unavailable", errorProperty.GetString());
        Assert.True(json.RootElement.TryGetProperty("detail", out _));
        Assert.False(json.RootElement.TryGetProperty("title", out _));
        Assert.False(json.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task PutNote_AfterVaultRootReappears_SucceedsAgainWithNoRestart()
    {
        WriteNoteToDisk("pre-existing.md", "old content");
        Directory.Delete(_vaultRootPath, recursive: true);

        var outageResponse = await _client.PutAsJsonAsync("/api/notes/new-note.md", new { content = "hello" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, outageResponse.StatusCode);

        // Simulates the real mount/directory being restored - no app
        // restart, no recreation logic needed beyond the directory itself
        // existing again with its original content.
        WriteNoteToDisk("pre-existing.md", "old content");

        var recoveredResponse = await _client.PutAsJsonAsync("/api/notes/new-note.md", new { content = "hello" });
        Assert.Equal(HttpStatusCode.OK, recoveredResponse.StatusCode);

        var treeResponse = await _client.GetAsync("/api/notes");
        Assert.Equal(HttpStatusCode.OK, treeResponse.StatusCode);
        var tree = await treeResponse.Content.ReadFromJsonAsync<List<NoteTreeEntryDto>>(ResponseJsonOptions);
        Assert.NotNull(tree);
        Assert.Contains(tree!, e => e.Path == "pre-existing.md");
        Assert.Contains(tree!, e => e.Path == "new-note.md");
    }

    [Fact]
    public async Task MoveNote_ExistingNote_RenamesFileAndReturnsNewPath()
    {
        WriteNoteToDisk("projects/idea.md", "# idea");

        var response = await _client.PostAsJsonAsync("/api/notes/projects/idea.md/move", new { destinationPath = "archive/idea.md" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<NoteMoveDto>(ResponseJsonOptions);
        Assert.NotNull(result);
        Assert.Equal("archive/idea.md", result!.Path);

        // Nothing else in the vault links to this note, so nothing needed
        // rewriting - per docs/04-API-SPEC.md, rewrittenNotes is an empty
        // array (never omitted/null) in that case.
        Assert.NotNull(result.RewrittenNotes);
        Assert.Empty(result.RewrittenNotes);

        Assert.False(File.Exists(Path.Combine(_vaultRootPath, "projects", "idea.md")));
        Assert.True(File.Exists(Path.Combine(_vaultRootPath, "archive", "idea.md")));
        Assert.Equal("# idea", await File.ReadAllTextAsync(Path.Combine(_vaultRootPath, "archive", "idea.md")));
    }

    /// <summary>
    /// Exercises <c>IVaultReorganizationService.MoveNoteAsync</c> end-to-end
    /// through the REST layer per this task's brief: two notes, one linking
    /// to the other via a bare-title wikilink, moving (and renaming) the
    /// target so the old title no longer resolves at all afterward - this
    /// makes a rewrite of the linking note's content actually *necessary*
    /// (not just possible), per docs/06-DATA-MODEL.md's "minimal" rule:
    /// renaming <c>target.md</c> to <c>moved/renamed.md</c> means the bare
    /// title "target" can no longer resolve to anything post-move, so
    /// <c>[[target]]</c> must be rewritten to keep pointing at the same
    /// note (staying a bare title, since "renamed" is still unique -
    /// style-preserving). Confirms both the response's <c>rewrittenNotes</c>
    /// field and the on-disk content change land in the same request.
    /// </summary>
    [Fact]
    public async Task MoveNote_RenameBreaksIncomingBareTitleLink_RewritesLinkingNoteAndReportsIt()
    {
        // Written directly to disk *before* a fresh host is constructed
        // (see MoveFolder_RebuildsLinkAndSearchIndexesSynchronously_NoWaitRequired
        // in FoldersEndpointsTests for the same technique), so the initial
        // startup scan - not the debounced file watcher - is what the
        // pre-move index state reflects, and the immediate post-move
        // assertions below aren't racing a watcher window.
        WriteNoteToDisk("target.md", "# target");
        WriteNoteToDisk("linker.md", "See [[target]] for more.");

        using var factory = new NotesApiFactory(_vaultRootPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/notes/target.md/move", new { destinationPath = "moved/renamed.md" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<NoteMoveDto>(ResponseJsonOptions);
        Assert.NotNull(result);
        Assert.Equal("moved/renamed.md", result!.Path);
        Assert.NotNull(result.RewrittenNotes);
        Assert.Contains("linker.md", result.RewrittenNotes);

        var linkerContent = await File.ReadAllTextAsync(Path.Combine(_vaultRootPath, "linker.md"));
        Assert.Equal("See [[renamed]] for more.", linkerContent);

        // Immediate (no delay/poll) consistency check per docs/04-API-SPEC.md:
        // the very next request must already reflect the rewrite - both via
        // the graph and via the moved note's own backlinks.
        var graphResponse = await client.GetAsync("/api/graph");
        Assert.Equal(HttpStatusCode.OK, graphResponse.StatusCode);
        var graph = await graphResponse.Content.ReadFromJsonAsync<GraphDto>(ResponseJsonOptions);
        Assert.NotNull(graph);
        Assert.Contains(graph!.Edges, e => e.Source == "linker.md" && e.Target == "moved/renamed.md");

        var backlinksResponse = await client.GetAsync("/api/notes/moved/renamed.md?includeBacklinks=true");
        Assert.Equal(HttpStatusCode.OK, backlinksResponse.StatusCode);
        var noteWithBacklinks = await backlinksResponse.Content.ReadFromJsonAsync<NoteContentDto>(ResponseJsonOptions);
        Assert.NotNull(noteWithBacklinks);
        Assert.NotNull(noteWithBacklinks!.Backlinks);
        Assert.Contains(noteWithBacklinks.Backlinks!, b => b.Path == "linker.md");
    }

    [Fact]
    public async Task MoveNote_DestinationAlreadyExists_Returns409AndDoesNotOverwrite()
    {
        WriteNoteToDisk("projects/idea.md", "source content");
        WriteNoteToDisk("archive/idea.md", "existing destination content");

        var response = await _client.PostAsJsonAsync("/api/notes/projects/idea.md/move", new { destinationPath = "archive/idea.md" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.False(string.IsNullOrWhiteSpace(error!.Error));

        Assert.True(File.Exists(Path.Combine(_vaultRootPath, "projects", "idea.md")));
        Assert.Equal(
            "existing destination content",
            await File.ReadAllTextAsync(Path.Combine(_vaultRootPath, "archive", "idea.md")));
    }

    [Fact]
    public async Task MoveNote_MissingSourceNote_Returns404WithErrorShape()
    {
        var response = await _client.PostAsJsonAsync("/api/notes/does/not/exist.md/move", new { destinationPath = "elsewhere.md" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("not_found", error!.Error);
    }

    [Fact]
    public async Task MoveNote_InvalidDestinationPath_Returns400WithErrorShape()
    {
        WriteNoteToDisk("projects/idea.md", "# idea");

        var response = await _client.PostAsJsonAsync("/api/notes/projects/idea.md/move", new { destinationPath = "..\\outside.md" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_path", error!.Error);
    }

    [Fact]
    public async Task MoveNote_MissingDestinationPathField_Returns400WithErrorShape()
    {
        WriteNoteToDisk("projects/idea.md", "# idea");

        var response = await _client.PostAsJsonAsync("/api/notes/projects/idea.md/move", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_request", error!.Error);
    }

    [Fact]
    public async Task MoveNote_MalformedJsonBody_Returns400WithErrorShape()
    {
        WriteNoteToDisk("projects/idea.md", "# idea");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/notes/projects/idea.md/move")
        {
            Content = new StringContent("{ not valid json", System.Text.Encoding.UTF8, "application/json")
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_body", error!.Error);
    }

    // REST/MCP parity: DotNotesMcpToolsTests already covers
    // MoveNote_VaultRootMissing_ThrowsMcpExceptionNotRawVaultUnavailableException
    // for move_note; this is the REST-surface equivalent, following the
    // exact same reproduction as PutNote_WhenVaultRootHasVanished above.
    [Fact]
    public async Task MoveNote_WhenVaultRootHasVanished_Returns503WithAppErrorShape()
    {
        WriteNoteToDisk("projects/idea.md", "# idea");
        Directory.Delete(_vaultRootPath, recursive: true);

        var response = await _client.PostAsJsonAsync("/api/notes/projects/idea.md/move", new { destinationPath = "archive/idea.md" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("vault_unavailable", error!.Error);
        Assert.False(Directory.Exists(_vaultRootPath));
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

    private sealed record NoteTreeEntryDto(string Path, string Name, string Type, List<NoteTreeEntryDto>? Children);

    private sealed record NoteContentDto(
        string Path,
        string Content,
        DateTimeOffset UpdatedAt,
        List<BacklinkDto>? Backlinks);

    private sealed record BacklinkDto(string Path, string Title);

    private sealed record NoteWriteDto(string Path, DateTimeOffset UpdatedAt);

    /// <summary>Backs the <c>{ path, updatedAt, rewrittenNotes }</c> response of <c>POST /api/notes/{**path}/move</c>.</summary>
    private sealed record NoteMoveDto(string Path, DateTimeOffset UpdatedAt, List<string> RewrittenNotes);

    private sealed record ErrorDto(string Error, string? Detail);

    private sealed record GraphDto(List<GraphNodeDto> Nodes, List<GraphEdgeDto> Edges);

    private sealed record GraphNodeDto(string Id, string Label, bool Exists);

    private sealed record GraphEdgeDto(string Source, string Target);
}
