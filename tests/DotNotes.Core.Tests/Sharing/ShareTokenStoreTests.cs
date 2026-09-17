using DotNotes.Core.Config;
using DotNotes.Core.Sharing;
using Microsoft.Extensions.Options;

namespace DotNotes.Core.Tests.Sharing;

/// <summary>
/// Each test gets its own throwaway vault directory under the OS temp
/// folder, created fresh in the constructor and removed in
/// <see cref="Dispose"/>, so tests never collide with each other or with
/// a real vault - same shape as <c>FileSystemNoteRepositoryTests</c>.
/// </summary>
public sealed class ShareTokenStoreTests : IDisposable
{
    private readonly DirectoryInfo _vaultDirectory;

    public ShareTokenStoreTests()
    {
        _vaultDirectory = Directory.CreateTempSubdirectory("dotnotes-share-tests-");
    }

    public void Dispose()
    {
        _vaultDirectory.Delete(recursive: true);
    }

    private ShareTokenStore CreateStore() =>
        new(Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName }));

    // ---- Create / resolve / revoke ----

    [Fact]
    public async Task CreateAsync_ThenResolveAsync_ReturnsTheSameNotePath()
    {
        var store = CreateStore();

        var result = await store.CreateAsync("projects/idea.md", expiresInDays: null);

        Assert.NotNull(result.Token);
        Assert.NotEmpty(result.Token);
        Assert.Equal("projects/idea.md", result.Path);
        Assert.Null(result.ExpiresAt);

        var resolved = await store.ResolveAsync(result.Token);
        Assert.Equal("projects/idea.md", resolved);
    }

    [Fact]
    public async Task CreateAsync_TokenIsUrlSafeBase64_AndNotDerivedFromThePath()
    {
        var store = CreateStore();

        var result = await store.CreateAsync("projects/idea.md", expiresInDays: null);

        Assert.DoesNotContain('+', result.Token);
        Assert.DoesNotContain('/', result.Token);
        Assert.DoesNotContain('=', result.Token);
        // 24 random bytes -> 32 base64 characters before the (stripped) padding.
        Assert.Equal(32, result.Token.Length);
        Assert.DoesNotContain("idea", result.Token, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_TwoCallsForTheSamePath_ProduceDifferentTokens()
    {
        var store = CreateStore();

        var first = await store.CreateAsync("projects/idea.md", expiresInDays: null);
        var second = await store.CreateAsync("projects/idea.md", expiresInDays: null);

        Assert.NotEqual(first.Token, second.Token);
    }

    [Fact]
    public async Task ResolveAsync_UnknownToken_ReturnsNull()
    {
        var store = CreateStore();

        var resolved = await store.ResolveAsync("not-a-real-token");

        Assert.Null(resolved);
    }

    [Fact]
    public async Task ResolveAsync_EmptyOrWhitespaceToken_ReturnsNull()
    {
        var store = CreateStore();

        Assert.Null(await store.ResolveAsync(""));
        Assert.Null(await store.ResolveAsync("   "));
    }

    [Fact]
    public async Task RevokeAsync_ExistingToken_RemovesItSoResolveThenReturnsNull()
    {
        var store = CreateStore();
        var result = await store.CreateAsync("projects/idea.md", expiresInDays: null);

        var revoked = await store.RevokeAsync(result.Token);

        Assert.True(revoked);
        Assert.Null(await store.ResolveAsync(result.Token));
    }

    [Fact]
    public async Task RevokeAsync_UnknownToken_ReturnsFalseIdempotently()
    {
        var store = CreateStore();

        var revoked = await store.RevokeAsync("never-issued-token");

        Assert.False(revoked);
    }

    // ---- Expiry ----

    [Fact]
    public async Task CreateAsync_WithExpiresInDays_SetsAFutureUtcExpiry()
    {
        var store = CreateStore();
        var before = DateTimeOffset.UtcNow;

        var result = await store.CreateAsync("projects/idea.md", expiresInDays: 7);

        Assert.NotNull(result.ExpiresAt);
        Assert.True(result.ExpiresAt > before.AddDays(6));
        Assert.True(result.ExpiresAt < before.AddDays(8));
    }

    [Fact]
    public async Task ResolveAsync_ExpiredToken_ReturnsNullIndistinguishableFromMissing()
    {
        var store = CreateStore();
        // expiresInDays: 0 is non-positive, so per the interface contract
        // it means "never expires" - use a helper that can inject an
        // already-past expiry to actually test expiry handling.
        var result = await store.CreateAsync("projects/idea.md", expiresInDays: 1);

        // Rewrite the persisted file with an already-past expiry for this
        // token, then reload a fresh store instance from disk - simulates
        // time having passed without a real Task.Delay in the test.
        var shareFilePath = Path.Combine(_vaultDirectory.FullName, ".nd-shares.json");
        var pastExpiryJson =
            $$"""
            { "{{result.Token}}": { "path": "projects/idea.md", "expiresAt": "2000-01-01T00:00:00Z" } }
            """;
        await File.WriteAllTextAsync(shareFilePath, pastExpiryJson);

        var reloadedStore = CreateStore();
        var resolved = await reloadedStore.ResolveAsync(result.Token);

        Assert.Null(resolved);
    }

    // ---- Persistence round-trip ----

    [Fact]
    public async Task CreateAsync_PersistsToNdSharesJson_ReadableByAFreshStoreInstance()
    {
        var store = CreateStore();
        var result = await store.CreateAsync("projects/idea.md", expiresInDays: 30);

        var shareFilePath = Path.Combine(_vaultDirectory.FullName, ".nd-shares.json");
        Assert.True(File.Exists(shareFilePath));

        var reloadedStore = CreateStore();
        var resolved = await reloadedStore.ResolveAsync(result.Token);

        Assert.Equal("projects/idea.md", resolved);
    }

    [Fact]
    public async Task CreateAsync_PersistedFileShape_MatchesDataModelDoc()
    {
        var store = CreateStore();
        await store.CreateAsync("projects/idea.md", expiresInDays: null);

        var shareFilePath = Path.Combine(_vaultDirectory.FullName, ".nd-shares.json");
        var json = await File.ReadAllTextAsync(shareFilePath);

        // docs/06-DATA-MODEL.md: `{ "<token>": { "path": "...", "expiresAt": "..."|null } }`
        Assert.Contains("\"path\"", json);
        Assert.Contains("\"expiresAt\"", json);
        Assert.Contains("projects/idea.md", json);
    }

    [Fact]
    public async Task RevokeAsync_PersistsRemoval_SoAFreshStoreInstanceAlsoNoLongerResolves()
    {
        var store = CreateStore();
        var result = await store.CreateAsync("projects/idea.md", expiresInDays: null);
        await store.RevokeAsync(result.Token);

        var reloadedStore = CreateStore();
        Assert.Null(await reloadedStore.ResolveAsync(result.Token));
    }

    [Fact]
    public async Task PersistAsync_LeavesNoLeftoverTempFiles()
    {
        var store = CreateStore();
        await store.CreateAsync("projects/idea.md", expiresInDays: null);

        var leftoverTempFiles = Directory.GetFiles(_vaultDirectory.FullName, "*.tmp");
        Assert.Empty(leftoverTempFiles);
    }

    // ---- Missing / corrupt file handling ----

    [Fact]
    public void Constructor_NoExistingShareFile_StartsWithNoShares()
    {
        // Constructing the store at all (with no .nd-shares.json present
        // yet) must not throw - "missing file" is just "no shares yet".
        var store = CreateStore();
        Assert.NotNull(store);
    }

    [Fact]
    public async Task Constructor_CorruptShareFile_TreatsItAsEmptyRatherThanThrowing()
    {
        var shareFilePath = Path.Combine(_vaultDirectory.FullName, ".nd-shares.json");
        await File.WriteAllTextAsync(shareFilePath, "{ not valid json at all");

        var store = CreateStore();

        // No shares recovered from the corrupt file, but the store itself
        // is fully usable - a corrupt cache is regenerated going forward.
        var result = await store.CreateAsync("projects/idea.md", expiresInDays: null);
        Assert.Equal("projects/idea.md", await store.ResolveAsync(result.Token));
    }

    [Fact]
    public async Task Constructor_CorruptShareFile_IsOverwrittenOnNextPersist()
    {
        var shareFilePath = Path.Combine(_vaultDirectory.FullName, ".nd-shares.json");
        await File.WriteAllTextAsync(shareFilePath, "not json");

        var store = CreateStore();
        await store.CreateAsync("projects/idea.md", expiresInDays: null);

        var json = await File.ReadAllTextAsync(shareFilePath);
        Assert.Contains("projects/idea.md", json);
    }
}
