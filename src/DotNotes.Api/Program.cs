using DotNotes.Api.Endpoints;
using DotNotes.Api.Mcp;
using DotNotes.Core.Config;
using DotNotes.Core.Links;
using DotNotes.Core.Media;
using DotNotes.Core.Notes;
using DotNotes.Core.Reorganization;
using DotNotes.Core.Search;
using DotNotes.Core.Sharing;
using DotNotes.Core.Vault;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Phase 5: multipart file uploads (docs/04-API-SPEC.md's Media section)
// need a larger request-body ceiling than Kestrel's ~28.6 MB default,
// since MediaEndpoints.MaxUploadSizeBytes is 50 MB. A little slack is
// added on top for multipart boundary/header overhead; MediaEndpoints
// itself still enforces the real 50 MB file-size limit and returns a
// clean {error, detail} response for an oversized file rather than
// relying on this raw ceiling alone (see PostUploadAsync).
const long UploadSizeLimitOverheadBytes = 1024 * 1024;
builder.WebHost.ConfigureKestrel(kestrelOptions =>
{
    kestrelOptions.Limits.MaxRequestBodySize = MediaEndpoints.MaxUploadSizeBytes + UploadSizeLimitOverheadBytes;
});
builder.Services.Configure<FormOptions>(formOptions =>
{
    formOptions.MultipartBodyLengthLimit = MediaEndpoints.MaxUploadSizeBytes + UploadSizeLimitOverheadBytes;
});

// Structured logging: Serilog replaces the default logging providers
// with console + rolling daily file output. Configuration (minimum
// level, overrides) is read from the "Serilog" section of
// appsettings.json, so it follows the same environment-variable
// override convention as everything else.
builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(context.HostingEnvironment.ContentRootPath, "logs", "dotnotes-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14);
});

// Strongly-typed configuration, bound from appsettings.json and
// overridable via environment variables using the standard ASP.NET
// Core double-underscore convention (e.g. Vault__RootPath).
builder.Services
    .AddOptions<VaultOptions>()
    .Bind(builder.Configuration.GetSection(VaultOptions.SectionName));
builder.Services
    .AddOptions<ServerOptions>()
    .Bind(builder.Configuration.GetSection(ServerOptions.SectionName));
builder.Services
    .AddOptions<SharingOptions>()
    .Bind(builder.Configuration.GetSection(SharingOptions.SectionName));
builder.Services
    .AddOptions<McpOptions>()
    .Bind(builder.Configuration.GetSection(McpOptions.SectionName));

// Resolve and validate the configured vault root path, creating it if it
// doesn't exist yet, *before* the host is built. This is the Phase 0
// "vault path is read from config and validated" exit criterion. The
// actual path-resolution and directory-creation logic lives in
// DotNotes.Core (framework-free, unit testable); only the "relative
// paths are resolved against the app's content root" concern is handled
// here, since content root is an ASP.NET Core hosting concept that
// DotNotes.Core deliberately knows nothing about.
var configuredVaultRootPath = builder.Configuration.GetSection(VaultOptions.SectionName)[nameof(VaultOptions.RootPath)];
if (string.IsNullOrWhiteSpace(configuredVaultRootPath))
{
    throw new InvalidOperationException(
        "Vault:RootPath is not configured. Set it in appsettings.json or via the Vault__RootPath environment variable.");
}

var contentRootRelativeVaultPath = Path.IsPathRooted(configuredVaultRootPath)
    ? configuredVaultRootPath
    : Path.Combine(builder.Environment.ContentRootPath, configuredVaultRootPath);
var vaultRootFullPath = VaultPathValidator.EnsureVaultRootExists(contentRootRelativeVaultPath);

// Overwrite the bound VaultOptions.RootPath with the fully-resolved,
// already-existing absolute path. Every consumer of IOptions<VaultOptions>
// (e.g. FileSystemNoteRepository in DotNotes.Core) then sees an absolute
// path and never needs to know about content-root-relative resolution.
builder.Services.Configure<VaultOptions>(options => options.RootPath = vaultRootFullPath);

// DotNotes.Core services. FileSystemNoteRepository has no mutable state
// beyond the resolved vault root path, so a singleton is safe.
builder.Services.AddSingleton<INoteRepository, FileSystemNoteRepository>();

// Phase 3: wikilinks/backlinks/graph (docs/06-DATA-MODEL.md). ILinkIndex
// is a singleton derived cache shared between the REST endpoints (reads)
// and VaultWatcherService (writes, as file-watcher events settle).
builder.Services.AddSingleton<ILinkIndex, InMemoryLinkIndex>();

// Phase 4: full-text search (docs/06-DATA-MODEL.md's "Search index"
// section). Same singleton derived-cache shape as ILinkIndex above.
builder.Services.AddSingleton<ISearchIndex, InMemorySearchIndex>();

// Both indexes above are also registered as IVaultChangeListener -
// resolving to the *same* singleton instances (via GetRequiredService,
// not a second instantiation) - so VaultWatcherService can fan a single
// shared watcher pass out to every index via IEnumerable<IVaultChangeListener>
// instead of depending on each concrete index type directly. See
// VaultWatcherService's remarks for why this replaced Phase 3's
// ILinkIndex-only wiring, and for how the initial full-vault scan avoids
// reading every file twice across the two indexes.
builder.Services.AddSingleton<IVaultChangeListener>(sp => sp.GetRequiredService<ILinkIndex>());
builder.Services.AddSingleton<IVaultChangeListener>(sp => sp.GetRequiredService<ISearchIndex>());

// VaultWatcherService itself performs the initial full-vault scan during
// its StartAsync, before the host reports "started", then keeps every
// registered IVaultChangeListener live with a single FileSystemWatcher
// for the app's lifetime - see its remarks.
builder.Services.AddHostedService<VaultWatcherService>();

// Phase 10: the single DotNotes.Core orchestration point for note/folder
// move + wikilink rewrite + index consistency (docs/06-DATA-MODEL.md's
// "Folder & note move/rename" section), used by both the REST move
// endpoints and the MCP move_note/move_folder tools so the two surfaces
// can't diverge. Depends on IEnumerable<IVaultChangeListener> - the same
// singleton index instances VaultWatcherService fans out to above - so it
// can update both indexes synchronously before returning. Reorganization
// operations are internally serialized (a SemaphoreSlim on the service
// itself), so a singleton lifetime is safe and avoids re-registering that
// serialization per request.
builder.Services.AddSingleton<IVaultReorganizationService, VaultReorganizationService>();

// Phase 5: share-token store (docs/06-DATA-MODEL.md's "Share tokens"
// section) and media store (uploaded binary assets under _media/).
// Both have no mutable state beyond the resolved vault root path (their
// own in-memory caches are populated/persisted internally), so a
// singleton lifetime is safe for each - same shape as INoteRepository.
builder.Services.AddSingleton<IShareTokenStore, ShareTokenStore>();
builder.Services.AddSingleton<IMediaStore, FileSystemMediaStore>();

// Phase 6: MCP server (docs/05-MCP-SPEC.md), hosted in-process at `/mcp`
// on the same Kestrel host as the REST API - tools call straight into the
// same DotNotes.Core services the REST endpoints use (DotNotesMcpTools is
// resolved per-call via DI, same lifetime rules as any other scoped MCP
// tool type). Registering the services here is cheap even when the `/mcp`
// route itself ends up not being mapped below (Mcp:Enabled=false).
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<DotNotesMcpTools>();

// Cross-cutting concerns.
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.Logger.LogInformation("Vault root resolved to {VaultRootPath}", vaultRootFullPath);

// Force every singleton whose constructor touches the vault root
// (IShareTokenStore, IMediaStore) to be constructed right now, in the
// same legitimate "first run creates the vault" startup window as
// VaultWatcherService's own eager construction of INoteRepository
// (VaultWatcherService is a hosted service, so DI constructs its
// INoteRepository dependency at StartAsync time regardless). Without
// this, both types are otherwise only constructed lazily on their first
// HTTP request - and their constructors call the same
// VaultPathValidator.EnsureVaultRootExists used at genuine first-run
// startup, which *creates* a missing directory. Leaving that lazy would
// reopen exactly the bug this phase's fix closes for INoteRepository: if
// the vault root vanishes after startup but before the very first
// /api/share or /api/upload/media request, that first request would
// silently fabricate a fresh, empty vault root - the FileSystemNoteRepository
// guard can't help here since the fabrication happens one layer below it,
// in a sibling singleton's constructor.
app.Services.GetRequiredService<IShareTokenStore>();
app.Services.GetRequiredService<IMediaStore>();

app.UseExceptionHandler();

// Serves the static frontend from wwwroot/ (populated starting Phase 2).
// UseDefaultFiles must run before UseStaticFiles so that a request for
// "/" resolves to wwwroot/index.html instead of 404ing.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHealthChecks("/healthz");

// Phase 2: Notes REST endpoints (docs/04-API-SPEC.md's Notes section).
// Config endpoints are a later phase.
app.MapNotesEndpoints();

// Folder management (docs/04-API-SPEC.md's Notes section):
// POST /api/folders/{**path} and POST /api/folders/{**path}/move.
app.MapFoldersEndpoints();

// Phase 3: Links & graph REST endpoints (docs/04-API-SPEC.md's "Links &
// graph" section).
app.MapGraphEndpoints();

// Phase 4: full-text search REST endpoint (docs/04-API-SPEC.md's Search
// section).
app.MapSearchEndpoints();

// Phase 5: sharing (docs/04-API-SPEC.md's Sharing section) and media
// upload/serving (docs/04-API-SPEC.md's Media section) endpoints.
app.MapSharingEndpoints();
app.MapMediaEndpoints();

// Phase 6: GET /api/config (docs/04-API-SPEC.md's Config section) - shares
// its field-sourcing logic with the MCP get_config tool via AppInfo, so
// the two can never drift apart.
app.MapConfigEndpoints();

// Phase 6: MCP server (docs/05-MCP-SPEC.md), gated on Mcp:Enabled - same
// "disabled means the route doesn't exist at all" shape as Sharing's own
// endpoints, rather than a 404 returned by a mapped-but-inert route.
if (app.Services.GetRequiredService<IOptions<McpOptions>>().Value.Enabled)
{
    app.MapMcp("/mcp");
}

app.Run();

// Exposes the top-level-statement-generated Program class publicly so
// integration tests can reference it via WebApplicationFactory<Program>
// (top-level statements normally generate an internal Program class,
// which is invisible outside this assembly).
public partial class Program
{
}
