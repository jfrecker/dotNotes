using System.ComponentModel;
using DotNotes.Core.Config;
using DotNotes.Core.Links;
using DotNotes.Core.Notes;
using DotNotes.Core.Reorganization;
using DotNotes.Core.Search;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DotNotes.Api.Mcp;

/// <summary>
/// Implements every MCP tool listed in docs/05-MCP-SPEC.md's "Tools to
/// expose" table. Hosted in-process at <c>/mcp</c> (see Program.cs) - every
/// tool here calls straight into the same <c>DotNotes.Core</c> services the
/// REST endpoints use (<see cref="Endpoints.NotesEndpoints"/>,
/// <see cref="Endpoints.SearchEndpoints"/>, <see cref="Endpoints.ConfigEndpoints"/>),
/// so there is only ever one code path that reads or writes a note, and
/// every tool goes through the exact same vault-root path validation an
/// HTTP request would (see <see cref="InvalidNotePathException"/>).
/// </summary>
/// <remarks>
/// Tool failures are surfaced as structured MCP tool errors, never raw
/// .NET exceptions: every caller-supplied path is validated by routing it
/// through an <see cref="INoteRepository"/> method (which throws
/// <see cref="InvalidNotePathException"/> for anything unsafe), and that
/// exception - along with "note not found" / "note already exists" cases -
/// is translated into a <see cref="McpException"/>. Per the installed SDK's
/// own documented behavior (see <c>McpServerTool</c>'s remarks), throwing
/// <see cref="McpException"/> from a tool method propagates its
/// <see cref="Exception.Message"/> to the client as a structured tool
/// error (<c>CallToolResult.IsError = true</c>); any other exception type
/// would instead be replaced with a generic message, which is why every
/// path here is deliberately funneled through <see cref="McpException"/>.
/// </remarks>
[McpServerToolType]
public sealed class DotNotesMcpTools(
    INoteRepository noteRepository,
    IVaultReorganizationService reorganizationService,
    ISearchIndex searchIndex,
    ILinkIndex linkIndex,
    IOptions<SharingOptions> sharingOptions,
    IOptions<McpOptions> mcpOptions)
{
    private const int DefaultSearchLimit = 10;
    private const int DefaultRecentNotesLimit = 10;

    [McpServerTool(Name = "search_notes")]
    [Description("Search notes by keyword across the whole vault. Returns ranked hits with a short snippet, same ranking as the app's search box.")]
    public IReadOnlyList<SearchHitDto> SearchNotes(
        [Description("Search text.")] string query,
        [Description("Maximum number of results to return (default 10).")] int? limit = null)
    {
        var effectiveLimit = limit is > 0 ? limit.Value : DefaultSearchLimit;

        return searchIndex.Search(query, effectiveLimit)
            .Select(r => new SearchHitDto(r.Path, r.Title, r.Snippet))
            .ToArray();
    }

    [McpServerTool(Name = "get_note")]
    [Description("Read a note's full raw markdown content by its vault-relative path. Optionally include the notes that link to it.")]
    public async Task<NoteDto> GetNote(
        [Description("Vault-relative note path, e.g. 'projects/idea.md'.")] string path,
        [Description("If true, also return the notes that link to this one.")] bool includeBacklinks = false,
        CancellationToken cancellationToken = default)
    {
        var note = await GetNoteOrThrowAsync(path, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<BacklinkDto>? backlinks = null;
        if (includeBacklinks)
        {
            backlinks = linkIndex.GetBacklinks(note.Path)
                .Select(sourcePath => new BacklinkDto(sourcePath, WikiLinkResolver.GetBareTitle(sourcePath)))
                .ToArray();
        }

        return new NoteDto(note.Path, note.Content, backlinks);
    }

    [McpServerTool(Name = "create_note")]
    [Description("Create a brand-new note at the given vault-relative path. Fails if a note already exists there - call update_note instead to modify an existing one.")]
    public async Task<CreateNoteResultDto> CreateNote(
        [Description("Vault-relative path for the new note, e.g. 'projects/idea.md'.")] string path,
        [Description("Markdown content for the new note.")] string content,
        CancellationToken cancellationToken = default)
    {
        bool exists;
        try
        {
            exists = await noteRepository.ExistsAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidNotePathException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (VaultUnavailableException ex)
        {
            throw new McpException(ex.Message);
        }

        if (exists)
        {
            throw new McpException($"A note already exists at '{path}'. Use update_note to modify it.");
        }

        try
        {
            var result = await noteRepository.SaveAsync(path, content, cancellationToken).ConfigureAwait(false);
            return new CreateNoteResultDto(result.Path);
        }
        catch (InvalidNotePathException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (VaultUnavailableException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "update_note")]
    [Description("Create or overwrite a note's content at the given vault-relative path. Creates the note (and any missing parent folders) if it doesn't already exist.")]
    public async Task<UpdateNoteResultDto> UpdateNote(
        [Description("Vault-relative note path, e.g. 'projects/idea.md'.")] string path,
        [Description("New markdown content for the note.")] string content,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await noteRepository.SaveAsync(path, content, cancellationToken).ConfigureAwait(false);
            return new UpdateNoteResultDto(result.Path, result.UpdatedAt);
        }
        catch (InvalidNotePathException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (VaultUnavailableException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "create_folder")]
    [Description("Create a folder at the given vault-relative path, creating any missing parent folders too (mkdir -p semantics). A no-op success if the folder already exists, unless failIfExists is true. Fails if a note (file) already exists at that exact path.")]
    public async Task<CreateFolderResultDto> CreateFolder(
        [Description("Vault-relative folder path, e.g. 'projects/archive'.")] string path,
        [Description("When true, fail instead of succeeding if a folder already exists at path. Defaults to false.")] bool failIfExists = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var createdPath = await noteRepository.CreateFolderAsync(path, failIfExists, cancellationToken).ConfigureAwait(false);
            return new CreateFolderResultDto(createdPath);
        }
        catch (DestinationAlreadyExistsException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (InvalidNotePathException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (VaultUnavailableException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "move_note")]
    [Description("Move or rename a single note from one vault-relative path to another, auto-creating any missing destination parent folders. Never overwrites an existing destination - fails instead, leaving both the source and any existing destination untouched. Rewrites any [[wikilinks]] elsewhere in the vault that would otherwise break.")]
    public async Task<MoveNoteResultDto> MoveNote(
        [Description("Vault-relative path of the note to move, e.g. 'projects/idea.md'.")] string path,
        [Description("New vault-relative path for the note, e.g. 'projects/archive/idea.md'.")] string destinationPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await reorganizationService.MoveNoteAsync(path, destinationPath, cancellationToken).ConfigureAwait(false);
            return new MoveNoteResultDto(result.Path, result.UpdatedAt, result.RewrittenNotes);
        }
        catch (SourceNotFoundException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (DestinationAlreadyExistsException ex)
        {
            throw new McpException($"Cannot move '{path}' to '{destinationPath}': {ex.Message} Nothing was moved.");
        }
        catch (InvalidNotePathException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (VaultUnavailableException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "move_folder")]
    [Description("Move or rename a folder and everything inside it, auto-creating any missing destination parent folders. Never overwrites an existing destination, and fails if the destination is the folder itself or one of its own descendants. Rewrites any [[wikilinks]] elsewhere in the vault that would otherwise break.")]
    public async Task<MoveFolderResultDto> MoveFolder(
        [Description("Vault-relative path of the folder to move, e.g. 'projects'.")] string path,
        [Description("New vault-relative path for the folder, e.g. 'archive/projects'.")] string destinationPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await reorganizationService.MoveFolderAsync(path, destinationPath, cancellationToken).ConfigureAwait(false);
            return new MoveFolderResultDto(result.Path, result.RewrittenNotes);
        }
        catch (SourceNotFoundException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (DestinationAlreadyExistsException ex)
        {
            throw new McpException($"Cannot move '{path}' to '{destinationPath}': {ex.Message} Nothing was moved.");
        }
        catch (InvalidNotePathException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (VaultUnavailableException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "get_backlinks")]
    [Description("List the notes that link to the given vault-relative note path.")]
    public async Task<IReadOnlyList<BacklinkDto>> GetBacklinks(
        [Description("Vault-relative path of the target note, e.g. 'projects/idea.md'.")] string path,
        CancellationToken cancellationToken = default)
    {
        // GetBacklinks itself is a pure in-memory lookup with no filesystem
        // access, but every tool must still go through the same vault-root
        // path validation an HTTP request would - ExistsAsync routes
        // through FileSystemNoteRepository's path validation (throwing
        // InvalidNotePathException for anything unsafe) without requiring
        // the target note to actually exist on disk, since a still-unresolved
        // wikilink target is a valid thing to ask for backlinks about.
        try
        {
            await noteRepository.ExistsAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidNotePathException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (VaultUnavailableException ex)
        {
            throw new McpException(ex.Message);
        }

        var normalizedPath = WikiLinkResolver.NormalizeToNotePath(path);
        return linkIndex.GetBacklinks(normalizedPath)
            .Select(sourcePath => new BacklinkDto(sourcePath, WikiLinkResolver.GetBareTitle(sourcePath)))
            .ToArray();
    }

    [McpServerTool(Name = "get_recent_notes")]
    [Description("List the most recently modified notes in the vault, newest first.")]
    public async Task<IReadOnlyList<RecentNoteDto>> GetRecentNotes(
        [Description("Maximum number of notes to return (default 10).")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveLimit = limit is > 0 ? limit.Value : DefaultRecentNotesLimit;

        try
        {
            var tree = await noteRepository.GetTreeAsync(cancellationToken).ConfigureAwait(false);
            var filePaths = new List<string>();
            CollectFilePaths(tree, filePaths);

            // INoteRepository has no "list with timestamps" method - only
            // GetAsync(path) (a full file read) currently carries UpdatedAt.
            // For a single-user personal vault (CLAUDE.md - not a high-scale
            // app) walking the tree and reading each file's metadata is an
            // acceptable trade-off rather than adding a new repository method
            // just for this one tool. See this phase's report for the same
            // judgment call spelled out.
            var notes = new List<RecentNoteDto>(filePaths.Count);
            foreach (var filePath in filePaths)
            {
                var note = await noteRepository.GetAsync(filePath, cancellationToken).ConfigureAwait(false);
                if (note is not null)
                {
                    notes.Add(new RecentNoteDto(note.Path, WikiLinkResolver.GetBareTitle(note.Path), note.UpdatedAt));
                }
            }

            return notes
                .OrderByDescending(n => n.UpdatedAt)
                .Take(effectiveLimit)
                .ToArray();
        }
        catch (VaultUnavailableException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "get_config")]
    [Description("Report the app's name, version, enabled feature flags, and autosave delay - lets a calling assistant discover capabilities before guessing.")]
    public AppConfigResponse GetConfig() => AppInfo.GetConfig(sharingOptions, mcpOptions);

    private async Task<NoteContent> GetNoteOrThrowAsync(string path, CancellationToken cancellationToken)
    {
        NoteContent? note;
        try
        {
            note = await noteRepository.GetAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidNotePathException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (VaultUnavailableException ex)
        {
            throw new McpException(ex.Message);
        }

        return note ?? throw new McpException($"No note exists at '{path}'.");
    }

    private static void CollectFilePaths(IReadOnlyList<NoteTreeEntry> entries, List<string> filePaths)
    {
        foreach (var entry in entries)
        {
            if (entry.Type == NoteEntryType.File)
            {
                filePaths.Add(entry.Path);
            }
            else if (entry.Children is not null)
            {
                CollectFilePaths(entry.Children, filePaths);
            }
        }
    }
}

/// <summary>Backs the <c>search_notes</c> tool's <c>{ path, title, snippet }</c> per-hit shape.</summary>
public sealed record SearchHitDto(string Path, string Title, string Snippet);

/// <summary>Backs the <c>get_note</c> tool's <c>{ path, content, backlinks? }</c> response.</summary>
public sealed record NoteDto(string Path, string Content, IReadOnlyList<BacklinkDto>? Backlinks);

/// <summary>Backs the <c>get_backlinks</c> tool's list entries and <c>get_note</c>'s optional <c>backlinks</c> field: <c>{ path, title }</c>.</summary>
public sealed record BacklinkDto(string Path, string Title);

/// <summary>Backs the <c>create_note</c> tool's <c>{ path }</c> response.</summary>
public sealed record CreateNoteResultDto(string Path);

/// <summary>Backs the <c>update_note</c> tool's <c>{ path, updatedAt }</c> response.</summary>
public sealed record UpdateNoteResultDto(string Path, DateTimeOffset UpdatedAt);

/// <summary>Backs the <c>get_recent_notes</c> tool's <c>{ path, title, updatedAt }</c> per-entry shape.</summary>
public sealed record RecentNoteDto(string Path, string Title, DateTimeOffset UpdatedAt);

/// <summary>Backs the <c>create_folder</c> tool's <c>{ path }</c> response.</summary>
public sealed record CreateFolderResultDto(string Path);

/// <summary>Backs the <c>move_note</c> tool's <c>{ path, updatedAt, rewrittenNotes }</c> response.</summary>
public sealed record MoveNoteResultDto(string Path, DateTimeOffset UpdatedAt, IReadOnlyList<string> RewrittenNotes);

/// <summary>Backs the <c>move_folder</c> tool's <c>{ path, rewrittenNotes }</c> response.</summary>
public sealed record MoveFolderResultDto(string Path, IReadOnlyList<string> RewrittenNotes);
