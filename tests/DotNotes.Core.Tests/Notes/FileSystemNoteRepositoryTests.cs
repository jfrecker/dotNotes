using DotNotes.Core.Config;
using DotNotes.Core.Notes;
using Microsoft.Extensions.Options;

namespace DotNotes.Core.Tests.Notes;

/// <summary>
/// Each test gets its own throwaway vault directory under the OS temp
/// folder, created fresh in the constructor and removed in
/// <see cref="Dispose"/>, so tests never collide with each other or with
/// a real vault.
/// </summary>
public sealed class FileSystemNoteRepositoryTests : IDisposable
{
    private readonly DirectoryInfo _vaultDirectory;
    private readonly FileSystemNoteRepository _repository;

    public FileSystemNoteRepositoryTests()
    {
        _vaultDirectory = Directory.CreateTempSubdirectory("dotnotes-tests-");
        _repository = new FileSystemNoteRepository(
            Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName }));
    }

    public void Dispose()
    {
        // A handful of tests below (the "vault root has vanished" guard
        // tests) delete this directory themselves as part of the scenario
        // they're exercising, so it may already be gone by the time
        // Dispose runs - that's expected, not a cleanup failure.
        if (Directory.Exists(_vaultDirectory.FullName))
        {
            _vaultDirectory.Delete(recursive: true);
        }
    }

    // ---- Happy path: create / read / update / delete / list ----

    [Fact]
    public async Task SaveAsync_ThenGetAsync_RoundTripsContent()
    {
        var result = await _repository.SaveAsync("idea.md", "# Idea\n\nSome text.");

        Assert.Equal("idea.md", result.Path);

        var note = await _repository.GetAsync("idea.md");

        Assert.NotNull(note);
        Assert.Equal("idea.md", note!.Path);
        Assert.Equal("# Idea\n\nSome text.", note.Content);
    }

    [Fact]
    public async Task SaveAsync_CreatesMissingParentFolders()
    {
        await _repository.SaveAsync("projects/nested/idea.md", "content");

        var fullPath = Path.Combine(_vaultDirectory.FullName, "projects", "nested", "idea.md");
        Assert.True(File.Exists(fullPath));
    }

    [Fact]
    public async Task SaveAsync_AcceptsBackslashSeparators_AndNormalizesToForwardSlashInResult()
    {
        var result = await _repository.SaveAsync("projects\\idea.md", "content");

        Assert.Equal("projects/idea.md", result.Path);
        Assert.True(File.Exists(Path.Combine(_vaultDirectory.FullName, "projects", "idea.md")));
    }

    [Fact]
    public async Task SaveAsync_OnExistingNote_OverwritesContent_AndReturnsSamePath()
    {
        await _repository.SaveAsync("note.md", "first version");
        var updateResult = await _repository.SaveAsync("note.md", "second version");

        Assert.Equal("note.md", updateResult.Path);

        var note = await _repository.GetAsync("note.md");
        Assert.Equal("second version", note!.Content);
    }

    [Fact]
    public async Task SaveAsync_LeavesNoLeftoverTempFiles()
    {
        await _repository.SaveAsync("note.md", "content");

        var filesInVault = Directory.GetFiles(_vaultDirectory.FullName);
        Assert.Single(filesInVault);
        Assert.EndsWith("note.md", filesInVault[0]);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNoteDoesNotExist()
    {
        var note = await _repository.GetAsync("missing.md");

        Assert.Null(note);
    }

    [Fact]
    public async Task ExistsAsync_ReflectsWhetherNoteWasCreated()
    {
        Assert.False(await _repository.ExistsAsync("note.md"));

        await _repository.SaveAsync("note.md", "content");

        Assert.True(await _repository.ExistsAsync("note.md"));
    }

    [Fact]
    public async Task DeleteAsync_RemovesNote_AndReturnsTrue()
    {
        await _repository.SaveAsync("note.md", "content");

        var deleted = await _repository.DeleteAsync("note.md");

        Assert.True(deleted);
        Assert.False(await _repository.ExistsAsync("note.md"));
    }

    [Fact]
    public async Task DeleteAsync_ReturnsFalse_WhenNoteDoesNotExist()
    {
        var deleted = await _repository.DeleteAsync("missing.md");

        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteAsync_DoesNotRemoveNowEmptyParentFolder()
    {
        await _repository.SaveAsync("projects/idea.md", "content");

        await _repository.DeleteAsync("projects/idea.md");

        var projectsDirectory = Path.Combine(_vaultDirectory.FullName, "projects");
        Assert.True(Directory.Exists(projectsDirectory));
        Assert.Empty(Directory.GetFileSystemEntries(projectsDirectory));
    }

    // ---- Tree listing ----

    [Fact]
    public async Task GetTreeAsync_ListsNestedFoldersAndFiles()
    {
        await _repository.SaveAsync("root.md", "content");
        await _repository.SaveAsync("projects/idea.md", "content");
        await _repository.SaveAsync("projects/nested/deep.md", "content");

        var tree = await _repository.GetTreeAsync();

        var rootFile = Assert.Single(tree, e => e.Type == NoteEntryType.File);
        Assert.Equal("root.md", rootFile.Path);

        var projectsFolder = Assert.Single(tree, e => e.Type == NoteEntryType.Folder);
        Assert.Equal("projects", projectsFolder.Path);
        Assert.NotNull(projectsFolder.Children);

        var ideaFile = Assert.Single(projectsFolder.Children!, e => e.Type == NoteEntryType.File);
        Assert.Equal("projects/idea.md", ideaFile.Path);

        var nestedFolder = Assert.Single(projectsFolder.Children!, e => e.Type == NoteEntryType.Folder);
        Assert.Equal("projects/nested", nestedFolder.Path);
        var deepFile = Assert.Single(nestedFolder.Children!);
        Assert.Equal("projects/nested/deep.md", deepFile.Path);
    }

    [Fact]
    public async Task GetTreeAsync_PreservesEmptyFolders()
    {
        Directory.CreateDirectory(Path.Combine(_vaultDirectory.FullName, "empty-folder"));

        var tree = await _repository.GetTreeAsync();

        var emptyFolder = Assert.Single(tree);
        Assert.Equal(NoteEntryType.Folder, emptyFolder.Type);
        Assert.NotNull(emptyFolder.Children);
        Assert.Empty(emptyFolder.Children!);
    }

    [Fact]
    public async Task GetTreeAsync_ExcludesMediaFolderAndDotfiles()
    {
        Directory.CreateDirectory(Path.Combine(_vaultDirectory.FullName, "_media"));
        await File.WriteAllTextAsync(Path.Combine(_vaultDirectory.FullName, "_media", "photo.png"), "binary-ish");
        await File.WriteAllTextAsync(Path.Combine(_vaultDirectory.FullName, ".nd-shares.json"), "{}");
        await _repository.SaveAsync("note.md", "content");

        var tree = await _repository.GetTreeAsync();

        Assert.Single(tree);
        Assert.Equal("note.md", tree[0].Path);
    }

    [Fact]
    public async Task GetTreeAsync_ExcludesNonMarkdownFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_vaultDirectory.FullName, "notes.txt"), "not a note");
        await _repository.SaveAsync("note.md", "content");

        var tree = await _repository.GetTreeAsync();

        Assert.Single(tree);
        Assert.Equal("note.md", tree[0].Path);
    }

    // ---- Path traversal rejection ----

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("projects/../../escape.md")]
    [InlineData("./note.md")]
    public async Task SaveAsync_Throws_ForDotOrDotDotSegments(string maliciousPath)
    {
        await Assert.ThrowsAsync<InvalidNotePathException>(
            () => _repository.SaveAsync(maliciousPath, "content"));
    }

    // Note: a bare leading backslash with no drive letter (e.g. "\foo.md")
    // is only "rooted" on Windows - on Linux a backslash is just an
    // ordinary filename character, so it is not included here to keep
    // this test's expectations true on every platform CLAUDE.md requires
    // this app to run on (Windows, WSL2, plain Linux). The leading-'/'
    // and drive-letter cases below are rejected on every platform.
    [Theory]
    [InlineData("/etc/passwd.md")]
    [InlineData("C:\\Windows\\evil.md")]
    [InlineData("C:evil.md")]
    public async Task SaveAsync_Throws_ForAbsoluteOrDriveRelativePaths(string maliciousPath)
    {
        await Assert.ThrowsAsync<InvalidNotePathException>(
            () => _repository.SaveAsync(maliciousPath, "content"));
    }

    [Fact]
    public async Task SaveAsync_Throws_ForNullCharacter()
    {
        await Assert.ThrowsAsync<InvalidNotePathException>(
            () => _repository.SaveAsync("note\0.md", "content"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SaveAsync_Throws_ForEmptyOrWhitespacePath(string emptyPath)
    {
        await Assert.ThrowsAsync<InvalidNotePathException>(
            () => _repository.SaveAsync(emptyPath, "content"));
    }

    [Fact]
    public async Task SaveAsync_Throws_WhenPathDoesNotEndInMd()
    {
        await Assert.ThrowsAsync<InvalidNotePathException>(
            () => _repository.SaveAsync("note.txt", "content"));
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("/etc/passwd.md")]
    public async Task GetAsync_Throws_ForUnsafePaths(string maliciousPath)
    {
        await Assert.ThrowsAsync<InvalidNotePathException>(() => _repository.GetAsync(maliciousPath));
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("/etc/passwd.md")]
    public async Task DeleteAsync_Throws_ForUnsafePaths(string maliciousPath)
    {
        await Assert.ThrowsAsync<InvalidNotePathException>(() => _repository.DeleteAsync(maliciousPath));
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("/etc/passwd.md")]
    public async Task ExistsAsync_Throws_ForUnsafePaths(string maliciousPath)
    {
        await Assert.ThrowsAsync<InvalidNotePathException>(() => _repository.ExistsAsync(maliciousPath));
    }

    // ---- Phase 8 QA pass: unicode/special-character paths, concurrency ----

    // Reproduces the exact case called out in Phase 8's edge-case list
    // (docs/01-PROJECT-PLAN.md): a folder/file name mixing non-ASCII
    // unicode, spaces, and accented characters, plus filenames containing
    // characters that are significant in URLs (#, ?, &) when this same
    // path travels through the REST API's route encoding. The repository
    // itself only deals in vault-relative strings (no URL involved), but
    // this pins that the filesystem layer round-trips all of them cleanly
    // with no mangling, confirmed manually end-to-end over HTTP during
    // this phase's QA pass as well.
    [Theory]
    [InlineData("日本語/café résumé.md")]
    [InlineData("weird#hash.md")]
    [InlineData("question?mark.md")]
    [InlineData("amp&ersand.md")]
    public async Task SaveAsync_UnicodeSpaceAndSpecialCharacterPaths_RoundTripCorrectly(string path)
    {
        const string content = "unicode/special-character path round-trip content: café 日本語 🎉";

        var result = await _repository.SaveAsync(path, content);
        Assert.Equal(path, result.Path);

        var note = await _repository.GetAsync(path);
        Assert.NotNull(note);
        Assert.Equal(path, note!.Path);
        Assert.Equal(content, note.Content);

        var tree = await _repository.GetTreeAsync();
        var flattened = Flatten(tree);
        Assert.Contains(path, flattened);
    }

    // Phase 8 QA pass: dotNotes has no operational-transform/CRDT layer, so
    // concurrent saves to the same note are expected to be last-write-wins
    // (see docs/01-PROJECT-PLAN.md Phase 1's "Concurrency" note) - but the
    // one thing that must never happen, per CLAUDE.md's data-loss hard
    // rule, is a *corrupted* file made of bytes from more than one writer
    // interleaved together. This fires many overlapping SaveAsync calls at
    // the same path (mirroring two browser tabs racing autosave requests)
    // and asserts the file that lands on disk is always exactly one
    // writer's full, untruncated payload - proving SaveAsync's
    // write-to-temp-file-then-move strategy is actually atomic under real
    // concurrent access, not just in the single-writer happy path the rest
    // of this file exercises.
    [Fact]
    public async Task SaveAsync_ConcurrentWritesToSameNote_NeverProducesCorruptedContent()
    {
        const int writerCount = 25;
        const int paddingLength = 5000;

        var payloads = Enumerable.Range(0, writerCount)
            .Select(i => new string('X', paddingLength) + $"-writer-{i}")
            .ToArray();

        await Task.WhenAll(payloads.Select(payload => _repository.SaveAsync("race.md", payload)));

        var note = await _repository.GetAsync("race.md");
        Assert.NotNull(note);

        // The final content must be exactly one writer's complete payload -
        // never a truncated prefix, never bytes from two different writers
        // spliced together.
        Assert.Contains(note!.Content, payloads);
    }

    // ---- Vault-root-vanishes-mid-operation guard (Phase 8 QA follow-up) ----

    // Reproduces the exact bug QA found live: mv-ing (here, Directory.Delete-ing)
    // the vault root away entirely while the app keeps running, then saving a
    // brand-new *root-level* note - the one case where SaveAsync's
    // parentDirectory IS the vault root itself, so an unguarded
    // Directory.CreateDirectory would silently fabricate a fresh, empty vault
    // root and write the note into it instead of failing. Asserts both that
    // the call throws VaultUnavailableException, and that no directory was
    // recreated at the old vault root path as a side effect of the failed call.
    [Fact]
    public async Task SaveAsync_Throws_WhenVaultRootHasVanished_AndDoesNotRecreateIt()
    {
        var vaultRootPath = _vaultDirectory.FullName;
        Directory.Delete(vaultRootPath, recursive: true);

        await Assert.ThrowsAsync<VaultUnavailableException>(
            () => _repository.SaveAsync("new-note.md", "content"));

        Assert.False(Directory.Exists(vaultRootPath));
    }

    [Fact]
    public async Task SaveAsync_Throws_WhenVaultRootHasVanished_ForNestedNote_AndDoesNotRecreateIt()
    {
        var vaultRootPath = _vaultDirectory.FullName;
        Directory.Delete(vaultRootPath, recursive: true);

        await Assert.ThrowsAsync<VaultUnavailableException>(
            () => _repository.SaveAsync("projects/new-note.md", "content"));

        Assert.False(Directory.Exists(vaultRootPath));
    }

    [Fact]
    public async Task GetTreeAsync_Throws_WhenVaultRootHasVanished()
    {
        Directory.Delete(_vaultDirectory.FullName, recursive: true);

        await Assert.ThrowsAsync<VaultUnavailableException>(() => _repository.GetTreeAsync());
    }

    [Fact]
    public async Task GetAsync_Throws_WhenVaultRootHasVanished_RatherThanReturningNull()
    {
        await _repository.SaveAsync("note.md", "content");
        Directory.Delete(_vaultDirectory.FullName, recursive: true);

        // Distinguishable from "note not found" (a null return) - the
        // vault itself is unavailable, not just this one note.
        await Assert.ThrowsAsync<VaultUnavailableException>(() => _repository.GetAsync("note.md"));
    }

    [Fact]
    public async Task ExistsAsync_Throws_WhenVaultRootHasVanished_RatherThanReturningFalse()
    {
        await _repository.SaveAsync("note.md", "content");
        Directory.Delete(_vaultDirectory.FullName, recursive: true);

        await Assert.ThrowsAsync<VaultUnavailableException>(() => _repository.ExistsAsync("note.md"));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenVaultRootHasVanished_RatherThanReturningFalse()
    {
        await _repository.SaveAsync("note.md", "content");
        Directory.Delete(_vaultDirectory.FullName, recursive: true);

        await Assert.ThrowsAsync<VaultUnavailableException>(() => _repository.DeleteAsync("note.md"));
    }

    [Fact]
    public async Task SaveAsync_WhenVaultRootReappears_SucceedsAgainWithNoRestart()
    {
        var vaultRootPath = _vaultDirectory.FullName;
        await _repository.SaveAsync("pre-existing.md", "old content");

        Directory.Delete(vaultRootPath, recursive: true);
        await Assert.ThrowsAsync<VaultUnavailableException>(
            () => _repository.SaveAsync("new-note.md", "content"));

        // Simulates the real directory being restored/remounted - no
        // FileSystemNoteRepository restart/recreation needed.
        Directory.CreateDirectory(vaultRootPath);
        await File.WriteAllTextAsync(Path.Combine(vaultRootPath, "pre-existing.md"), "old content");

        var result = await _repository.SaveAsync("new-note.md", "new content");
        Assert.Equal("new-note.md", result.Path);

        var preExisting = await _repository.GetAsync("pre-existing.md");
        Assert.NotNull(preExisting);
        Assert.Equal("old content", preExisting!.Content);
    }

    // ---- CreateFolderAsync ----

    [Fact]
    public async Task CreateFolderAsync_CreatesFolder_IncludingMissingIntermediateFolders()
    {
        var result = await _repository.CreateFolderAsync("projects/2026/archive");

        Assert.Equal("projects/2026/archive", result);
        Assert.True(Directory.Exists(Path.Combine(_vaultDirectory.FullName, "projects", "2026", "archive")));
    }

    [Fact]
    public async Task CreateFolderAsync_OnAlreadyExistingFolder_IsIdempotentSuccess()
    {
        await _repository.CreateFolderAsync("projects");

        var result = await _repository.CreateFolderAsync("projects");

        Assert.Equal("projects", result);
        Assert.True(Directory.Exists(Path.Combine(_vaultDirectory.FullName, "projects")));
    }

    [Fact]
    public async Task CreateFolderAsync_Throws_WhenNoteAlreadyExistsAtThatExactPath()
    {
        await _repository.SaveAsync("projects.md", "content");

        var ex = await Assert.ThrowsAsync<DestinationAlreadyExistsException>(
            () => _repository.CreateFolderAsync("projects.md"));

        Assert.Equal("projects.md", ex.Path);
        Assert.True(File.Exists(Path.Combine(_vaultDirectory.FullName, "projects.md")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("./folder")]
    public async Task CreateFolderAsync_Throws_ForUnsafePaths(string maliciousPath)
    {
        await Assert.ThrowsAsync<InvalidNotePathException>(() => _repository.CreateFolderAsync(maliciousPath));
    }

    [Fact]
    public async Task CreateFolderAsync_Throws_WhenVaultRootHasVanished()
    {
        Directory.Delete(_vaultDirectory.FullName, recursive: true);

        await Assert.ThrowsAsync<VaultUnavailableException>(() => _repository.CreateFolderAsync("projects"));
    }

    // ---- MoveAsync (note move/rename) ----

    [Fact]
    public async Task MoveAsync_MovesNote_ContentPreserved_OldPathGone()
    {
        await _repository.SaveAsync("old.md", "some content");

        var result = await _repository.MoveAsync("old.md", "new.md");

        Assert.Equal("new.md", result.Path);
        Assert.False(await _repository.ExistsAsync("old.md"));

        var moved = await _repository.GetAsync("new.md");
        Assert.NotNull(moved);
        Assert.Equal("some content", moved!.Content);
    }

    [Fact]
    public async Task MoveAsync_AutoCreatesMissingDestinationParentFolders()
    {
        await _repository.SaveAsync("note.md", "content");

        var result = await _repository.MoveAsync("note.md", "projects/nested/note.md");

        Assert.Equal("projects/nested/note.md", result.Path);
        Assert.True(File.Exists(Path.Combine(_vaultDirectory.FullName, "projects", "nested", "note.md")));
    }

    [Fact]
    public async Task MoveAsync_Throws_WhenSourceDoesNotExist()
    {
        await Assert.ThrowsAsync<SourceNotFoundException>(
            () => _repository.MoveAsync("missing.md", "destination.md"));
    }

    [Fact]
    public async Task MoveAsync_RefusesToOverwriteExistingDestination_AndLeavesBothFilesIntact()
    {
        await _repository.SaveAsync("source.md", "source content");
        await _repository.SaveAsync("destination.md", "destination content");

        await Assert.ThrowsAsync<DestinationAlreadyExistsException>(
            () => _repository.MoveAsync("source.md", "destination.md"));

        var source = await _repository.GetAsync("source.md");
        var destination = await _repository.GetAsync("destination.md");
        Assert.Equal("source content", source!.Content);
        Assert.Equal("destination content", destination!.Content);
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("/etc/passwd.md")]
    public async Task MoveAsync_Throws_ForUnsafeSourcePath(string maliciousPath)
    {
        await Assert.ThrowsAsync<InvalidNotePathException>(
            () => _repository.MoveAsync(maliciousPath, "destination.md"));
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("/etc/passwd.md")]
    public async Task MoveAsync_Throws_ForUnsafeDestinationPath(string maliciousPath)
    {
        await _repository.SaveAsync("source.md", "content");

        await Assert.ThrowsAsync<InvalidNotePathException>(
            () => _repository.MoveAsync("source.md", maliciousPath));
    }

    [Fact]
    public async Task MoveAsync_Throws_WhenVaultRootHasVanished()
    {
        await _repository.SaveAsync("source.md", "content");
        Directory.Delete(_vaultDirectory.FullName, recursive: true);

        await Assert.ThrowsAsync<VaultUnavailableException>(
            () => _repository.MoveAsync("source.md", "destination.md"));
    }

    // ---- MoveFolderAsync ----

    [Fact]
    public async Task MoveFolderAsync_MovesFolderContents_NestedSubfoldersIncluded_OldFolderGone()
    {
        await _repository.SaveAsync("projects/idea.md", "idea content");
        await _repository.SaveAsync("projects/nested/deep.md", "deep content");

        var result = await _repository.MoveFolderAsync("projects", "work");

        Assert.Equal("work", result);
        Assert.False(Directory.Exists(Path.Combine(_vaultDirectory.FullName, "projects")));

        var idea = await _repository.GetAsync("work/idea.md");
        var deep = await _repository.GetAsync("work/nested/deep.md");
        Assert.NotNull(idea);
        Assert.Equal("idea content", idea!.Content);
        Assert.NotNull(deep);
        Assert.Equal("deep content", deep!.Content);
    }

    [Fact]
    public async Task MoveFolderAsync_AutoCreatesMissingDestinationParentFolders()
    {
        await _repository.SaveAsync("projects/idea.md", "content");

        var result = await _repository.MoveFolderAsync("projects", "archive/2026/projects");

        Assert.Equal("archive/2026/projects", result);
        Assert.True(File.Exists(Path.Combine(_vaultDirectory.FullName, "archive", "2026", "projects", "idea.md")));
    }

    [Fact]
    public async Task MoveFolderAsync_Throws_WhenSourceDoesNotExist()
    {
        await Assert.ThrowsAsync<SourceNotFoundException>(
            () => _repository.MoveFolderAsync("missing-folder", "destination-folder"));
    }

    [Fact]
    public async Task MoveFolderAsync_RefusesToOverwriteExistingDestination()
    {
        await _repository.SaveAsync("source/idea.md", "source content");
        await _repository.SaveAsync("destination/idea.md", "destination content");

        await Assert.ThrowsAsync<DestinationAlreadyExistsException>(
            () => _repository.MoveFolderAsync("source", "destination"));

        Assert.True(Directory.Exists(Path.Combine(_vaultDirectory.FullName, "source")));
        var destinationIdea = await _repository.GetAsync("destination/idea.md");
        Assert.Equal("destination content", destinationIdea!.Content);
    }

    [Fact]
    public async Task MoveFolderAsync_Throws_WhenDestinationIsSourceItself()
    {
        await _repository.SaveAsync("projects/idea.md", "content");

        await Assert.ThrowsAsync<InvalidNotePathException>(
            () => _repository.MoveFolderAsync("projects", "projects"));
    }

    [Fact]
    public async Task MoveFolderAsync_Throws_WhenDestinationIsOwnDescendant()
    {
        await _repository.SaveAsync("projects/idea.md", "content");

        await Assert.ThrowsAsync<InvalidNotePathException>(
            () => _repository.MoveFolderAsync("projects", "projects/archive/projects"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    public async Task MoveFolderAsync_Throws_ForUnsafeSourcePath(string maliciousPath)
    {
        await Assert.ThrowsAsync<InvalidNotePathException>(
            () => _repository.MoveFolderAsync(maliciousPath, "destination"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    public async Task MoveFolderAsync_Throws_ForUnsafeDestinationPath(string maliciousPath)
    {
        await _repository.SaveAsync("source/idea.md", "content");

        await Assert.ThrowsAsync<InvalidNotePathException>(
            () => _repository.MoveFolderAsync("source", maliciousPath));
    }

    [Fact]
    public async Task MoveFolderAsync_Throws_WhenVaultRootHasVanished()
    {
        await _repository.SaveAsync("projects/idea.md", "content");
        Directory.Delete(_vaultDirectory.FullName, recursive: true);

        await Assert.ThrowsAsync<VaultUnavailableException>(
            () => _repository.MoveFolderAsync("projects", "work"));
    }

    // ---- Phase 10: name rules for *new* names (docs/06-DATA-MODEL.md's "Names") ----

    [Theory]
    [InlineData(" leadingspace.md")]
    [InlineData("trailing /note.md")] // folder segment "trailing " has trailing whitespace
    [InlineData("control.md")] // BEL control character
    [InlineData("brack[et.md")]
    [InlineData("brack]et.md")]
    [InlineData("pi|pe.md")]
    public async Task SaveAsync_CreatingNewNote_RejectsDisallowedNewName(string path)
    {
        await Assert.ThrowsAsync<InvalidNotePathException>(() => _repository.SaveAsync(path, "content"));
        Assert.False(await _repository.ExistsAsync(path));
    }

    [Fact]
    public async Task SaveAsync_CreatingNewNote_RejectsDisallowedNewFolderSegment()
    {
        await Assert.ThrowsAsync<InvalidNotePathException>(
            () => _repository.SaveAsync("pro[jects/idea.md", "content"));
    }

    [Fact]
    public async Task SaveAsync_UpdatingExistingNoteWithDisallowedName_StillSucceeds()
    {
        // The disallowed name predates these rules (or was created by
        // another tool) - autosave on an already-open note must not start
        // failing just because the rule was added later.
        var fullPath = Path.Combine(_vaultDirectory.FullName, "brack[et.md");
        await File.WriteAllTextAsync(fullPath, "original");

        var result = await _repository.SaveAsync("brack[et.md", "updated");

        Assert.Equal("updated", (await _repository.GetAsync("brack[et.md"))!.Content);
        Assert.Equal("brack[et.md", result.Path);
    }

    [Fact]
    public async Task DeleteAsync_ExistingNoteWithDisallowedName_StillSucceeds()
    {
        var fullPath = Path.Combine(_vaultDirectory.FullName, "pi|pe.md");
        await File.WriteAllTextAsync(fullPath, "content");

        Assert.True(await _repository.DeleteAsync("pi|pe.md"));
    }

    [Fact]
    public async Task MoveAsync_ExistingNoteWithDisallowedName_CanStillBeMovedAwayToAValidName()
    {
        var fullPath = Path.Combine(_vaultDirectory.FullName, "brack[et.md");
        await File.WriteAllTextAsync(fullPath, "content");

        var result = await _repository.MoveAsync("brack[et.md", "valid-name.md");

        Assert.Equal("valid-name.md", result.Path);
        Assert.False(await _repository.ExistsAsync("brack[et.md"));
        Assert.True(await _repository.ExistsAsync("valid-name.md"));
    }

    [Theory]
    [InlineData(" leading.md")]
    [InlineData("trailing /note.md")] // folder segment "trailing " has trailing whitespace
    [InlineData("brack[et.md")]
    [InlineData("brack]et.md")]
    [InlineData("pi|pe.md")]
    public async Task MoveAsync_RejectsDisallowedNewDestinationName(string destination)
    {
        await _repository.SaveAsync("source.md", "content");

        await Assert.ThrowsAsync<InvalidNotePathException>(
            () => _repository.MoveAsync("source.md", destination));

        // Nothing should have moved.
        Assert.True(await _repository.ExistsAsync("source.md"));
    }

    [Fact]
    public async Task MoveAsync_DoesNotRejectExistingParentFolderSegmentsInDestination()
    {
        // "projects" already exists on disk - only the new leaf segment
        // needs to pass the name-rule checks.
        await _repository.CreateFolderAsync("projects");
        await _repository.SaveAsync("source.md", "content");

        var result = await _repository.MoveAsync("source.md", "projects/idea.md");

        Assert.Equal("projects/idea.md", result.Path);
    }

    [Theory]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("brack[et")]
    [InlineData("brack]et")]
    [InlineData("pi|pe")]
    public async Task CreateFolderAsync_RejectsDisallowedNewFolderName(string folderName)
    {
        await Assert.ThrowsAsync<InvalidNotePathException>(() => _repository.CreateFolderAsync(folderName));
    }

    [Fact]
    public async Task CreateFolderAsync_OnlyChecksNewlyCreatedIntermediateSegments()
    {
        await _repository.CreateFolderAsync("existing");

        // "existing" already exists (never checked); "new[folder" is being
        // newly created and must be rejected.
        await Assert.ThrowsAsync<InvalidNotePathException>(
            () => _repository.CreateFolderAsync("existing/new[folder"));
    }

    [Theory]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("brack[et")]
    [InlineData("pi|pe")]
    public async Task MoveFolderAsync_RejectsDisallowedNewDestinationName(string destination)
    {
        await _repository.CreateFolderAsync("source");

        await Assert.ThrowsAsync<InvalidNotePathException>(
            () => _repository.MoveFolderAsync("source", destination));

        Assert.True(Directory.Exists(Path.Combine(_vaultDirectory.FullName, "source")));
    }

    // Phase 8's regression pin (SaveAsync_UnicodeSpaceAndSpecialCharacterPaths_RoundTripCorrectly
    // above) already proves '#', '?', '&' and unicode names round-trip;
    // this confirms the same holds for CreateFolderAsync/MoveAsync/MoveFolderAsync
    // destinations now that name-rule validation runs on those paths too.
    [Theory]
    [InlineData("日本語 café résumé")]
    [InlineData("weird#hash")]
    [InlineData("question?mark")]
    [InlineData("amp&ersand")]
    public async Task CreateFolderAsync_AcceptsHostValidSpecialCharacters(string folderName)
    {
        var result = await _repository.CreateFolderAsync(folderName);
        Assert.Equal(folderName, result);
    }

    private static List<string> Flatten(IReadOnlyList<NoteTreeEntry> entries)
    {
        var paths = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Type == NoteEntryType.File)
            {
                paths.Add(entry.Path);
            }
            else if (entry.Children is not null)
            {
                paths.AddRange(Flatten(entry.Children));
            }
        }

        return paths;
    }
}
