using System.Security.Cryptography;
using System.Text.Json;
using DotNotes.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNotes.Core.Sharing;

/// <summary>
/// Default <see cref="IShareTokenStore"/> implementation: an in-memory
/// dictionary backed by <c>.nd-shares.json</c> in the vault root, loaded
/// once at construction and persisted (atomically - temp file + move,
/// same pattern as <see cref="Notes.FileSystemNoteRepository"/>) after
/// every create/revoke. A single <see cref="SemaphoreSlim"/> serializes
/// every read-modify-write cycle, which is more than sufficient for a
/// single-user local app.
/// </summary>
public sealed class ShareTokenStore : IShareTokenStore
{
    private const string StoreFileName = ".nd-shares.json";

    /// <summary>24 random bytes -&gt; 32 base64 characters before URL-safe encoding, per docs/06-DATA-MODEL.md.</summary>
    private const int TokenByteLength = 24;

    // camelCase to match docs/06-DATA-MODEL.md's documented on-disk shape
    // (`{ "path": "...", "expiresAt": "..."|null }`) exactly, so the file
    // reads naturally if a user ever opens it by hand.
    private static readonly JsonSerializerOptions PersistJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions LoadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _storeFilePath;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly ILogger<ShareTokenStore>? _logger;

    private readonly Dictionary<string, ShareEntry> _entries;

    public ShareTokenStore(IOptions<VaultOptions> vaultOptions, ILogger<ShareTokenStore>? logger = null)
    {
        var vaultRootFullPath = VaultPathValidator.EnsureVaultRootExists(vaultOptions.Value.RootPath);
        _storeFilePath = Path.Combine(vaultRootFullPath, StoreFileName);
        _logger = logger;
        _entries = LoadFromDisk(_storeFilePath, _logger);
    }

    public async Task<ShareCreateResult> CreateAsync(string notePath, int? expiresInDays, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notePath);

        DateTimeOffset? expiresAt = expiresInDays is > 0
            ? DateTimeOffset.UtcNow.AddDays(expiresInDays.Value)
            : null;

        // Tokens are generated outside the lock (pure CPU, no shared
        // state) and only collide with an astronomically small
        // probability at 24 random bytes - not worth a retry loop.
        var token = GenerateToken();

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _entries[token] = new ShareEntry { Path = notePath, ExpiresAt = expiresAt };
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        return new ShareCreateResult { Token = token, Path = notePath, ExpiresAt = expiresAt };
    }

    public async Task<string?> ResolveAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_entries.TryGetValue(token, out var entry))
            {
                return null;
            }

            if (entry.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
            {
                // Expired: treat identically to "never existed" (per the
                // interface contract) and clean it up while the lock is
                // already held, so it doesn't linger in the file forever.
                _entries.Remove(token);
                await PersistAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            return entry.Path;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<bool> RevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = _entries.Remove(token);
            if (removed)
            {
                await PersistAsync(cancellationToken).ConfigureAwait(false);
            }
            return removed;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// 24 random bytes, URL-safe base64 (per docs/06-DATA-MODEL.md) -
    /// never derived from the note path or anything predictable.
    /// </summary>
    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenByteLength);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// Loads <c>.nd-shares.json</c> if present and well-formed; a missing
    /// or corrupt file is treated as "no shares yet" (never an error),
    /// per CLAUDE.md's "cache, never source of truth" rule for generated
    /// vault-side state.
    /// </summary>
    private static Dictionary<string, ShareEntry> LoadFromDisk(string storeFilePath, ILogger? logger)
    {
        if (!File.Exists(storeFilePath))
        {
            return new Dictionary<string, ShareEntry>();
        }

        try
        {
            var json = File.ReadAllText(storeFilePath);
            var raw = JsonSerializer.Deserialize<Dictionary<string, ShareFileEntry>>(json, LoadJsonOptions);
            if (raw is null)
            {
                return new Dictionary<string, ShareEntry>();
            }

            var result = new Dictionary<string, ShareEntry>();
            foreach (var (token, fileEntry) in raw)
            {
                if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(fileEntry.Path))
                {
                    continue;
                }

                result[token] = new ShareEntry { Path = fileEntry.Path, ExpiresAt = fileEntry.ExpiresAt };
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Failed to load {StoreFileName}; treating as an empty share store.", StoreFileName);
            return new Dictionary<string, ShareEntry>();
        }
    }

    /// <summary>Atomic write: temp file in the same directory, then <see cref="File.Move(string, string, bool)"/> with overwrite.</summary>
    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_storeFilePath)!;
        var tempFilePath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");

        var onDiskShape = _entries.ToDictionary(
            kv => kv.Key,
            kv => new ShareFileEntry { Path = kv.Value.Path, ExpiresAt = kv.Value.ExpiresAt });

        try
        {
            var json = JsonSerializer.Serialize(onDiskShape, PersistJsonOptions);
            await File.WriteAllTextAsync(tempFilePath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempFilePath, _storeFilePath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
            throw;
        }
    }

    /// <summary>On-disk shape of one <c>.nd-shares.json</c> entry, per docs/06-DATA-MODEL.md: <c>{ "path": "...", "expiresAt": "..."|null }</c>.</summary>
    private sealed class ShareFileEntry
    {
        public string Path { get; set; } = string.Empty;

        public DateTimeOffset? ExpiresAt { get; set; }
    }
}
