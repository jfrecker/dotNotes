using System.Text;
using DotNotes.Core.Config;
using DotNotes.Core.Media;
using Microsoft.Extensions.Options;

namespace DotNotes.Core.Tests.Media;

/// <summary>
/// Each test gets its own throwaway vault directory under the OS temp
/// folder, created fresh in the constructor and removed in
/// <see cref="Dispose"/> - same shape as <c>FileSystemNoteRepositoryTests</c>.
/// </summary>
public sealed class FileSystemMediaStoreTests : IDisposable
{
    private readonly DirectoryInfo _vaultDirectory;
    private readonly FileSystemMediaStore _store;

    public FileSystemMediaStoreTests()
    {
        _vaultDirectory = Directory.CreateTempSubdirectory("dotnotes-media-tests-");
        _store = new FileSystemMediaStore(
            Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName }));
    }

    public void Dispose()
    {
        _vaultDirectory.Delete(recursive: true);
    }

    private static Stream StreamFor(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    // ---- Save + safe name generation ----

    [Fact]
    public async Task SaveAsync_CreatesFileUnderMediaDirectory_AndReturnsVaultRelativePath()
    {
        var relativePath = await _store.SaveAsync(StreamFor("fake-png-bytes"), ".png");

        Assert.StartsWith("_media/", relativePath);
        Assert.EndsWith(".png", relativePath);

        var fullPath = Path.Combine(_vaultDirectory.FullName, "_media", Path.GetFileName(relativePath));
        Assert.True(File.Exists(fullPath));
        Assert.Equal("fake-png-bytes", await File.ReadAllTextAsync(fullPath));
    }

    [Fact]
    public async Task SaveAsync_NeverUsesACallerSuppliedFileName_GeneratesAGuidBasedNameInstead()
    {
        // IMediaStore.SaveAsync doesn't even accept a caller filename - only
        // an extension - which is itself the point: there is no code path
        // by which a caller-controlled name reaches the filesystem. This
        // test pins down the *shape* of the generated name (GUID hex + ext).
        var relativePath = await _store.SaveAsync(StreamFor("data"), "png");

        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        Assert.True(Guid.TryParse(fileName, out _), $"Expected a GUID-based file name, got '{fileName}'.");
    }

    [Fact]
    public async Task SaveAsync_AcceptsExtensionWithOrWithoutLeadingDot()
    {
        var withDot = await _store.SaveAsync(StreamFor("a"), ".jpg");
        var withoutDot = await _store.SaveAsync(StreamFor("b"), "jpg");

        Assert.EndsWith(".jpg", withDot);
        Assert.EndsWith(".jpg", withoutDot);
    }

    [Fact]
    public async Task SaveAsync_TwoUploads_GetDifferentGeneratedNames()
    {
        var first = await _store.SaveAsync(StreamFor("a"), ".png");
        var second = await _store.SaveAsync(StreamFor("b"), ".png");

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("png; DROP TABLE notes")]
    [InlineData("png/../../etc")]
    [InlineData(".exe.sh.verylongextensionthatshouldbereject")]
    public async Task SaveAsync_UnsafeOrEmptyExtension_ThrowsArgumentException(string unsafeExtension)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveAsync(StreamFor("data"), unsafeExtension));
    }

    [Fact]
    public async Task SaveAsync_LeavesNoLeftoverTempFiles()
    {
        await _store.SaveAsync(StreamFor("data"), ".png");

        var mediaDirectory = Path.Combine(_vaultDirectory.FullName, "_media");
        var leftoverTempFiles = Directory.GetFiles(mediaDirectory, "*.tmp");
        Assert.Empty(leftoverTempFiles);
    }

    // ---- Resolve for serving + path-traversal rejection ----

    [Fact]
    public async Task ResolveExistingMediaFullPath_ExistingFile_ReturnsItsFullPath()
    {
        var relativePath = await _store.SaveAsync(StreamFor("data"), ".png");
        var fileName = relativePath["_media/".Length..];

        var resolved = _store.ResolveExistingMediaFullPath(fileName);

        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void ResolveExistingMediaFullPath_MissingFile_ReturnsNull()
    {
        var resolved = _store.ResolveExistingMediaFullPath("does-not-exist.png");

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("..\\outside.png")]
    [InlineData("nested/../../outside.png")]
    public void ResolveExistingMediaFullPath_PathTraversalAttempt_ThrowsInvalidMediaPathException(string traversalPath)
    {
        Assert.Throws<InvalidMediaPathException>(() => _store.ResolveExistingMediaFullPath(traversalPath));
    }

    [Fact]
    public void ResolveExistingMediaFullPath_AbsolutePathAttempt_ThrowsInvalidMediaPathException()
    {
        var absoluteAttempt = OperatingSystem.IsWindows() ? "C:\\Windows\\win.ini" : "/etc/passwd";

        Assert.Throws<InvalidMediaPathException>(() => _store.ResolveExistingMediaFullPath(absoluteAttempt));
    }

    [Fact]
    public void ResolveExistingMediaFullPath_DriveRelativePathAttempt_ThrowsInvalidMediaPathException()
    {
        Assert.Throws<InvalidMediaPathException>(() => _store.ResolveExistingMediaFullPath("C:foo.png"));
    }

    [Fact]
    public void ResolveExistingMediaFullPath_NullCharacter_ThrowsInvalidMediaPathException()
    {
        Assert.Throws<InvalidMediaPathException>(() => _store.ResolveExistingMediaFullPath("foo\0.png"));
    }

    [Fact]
    public void ResolveExistingMediaFullPath_EmptyPath_ThrowsInvalidMediaPathException()
    {
        Assert.Throws<InvalidMediaPathException>(() => _store.ResolveExistingMediaFullPath(""));
    }
}
