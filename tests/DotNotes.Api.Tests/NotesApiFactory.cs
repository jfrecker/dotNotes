using Microsoft.AspNetCore.Mvc.Testing;

namespace DotNotes.Api.Tests;

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> pointed at a
/// caller-supplied, isolated temp directory as the vault root, so tests
/// never touch a real/shared vault. One instance is created per test
/// (see <c>NotesEndpointsTests</c>'s constructor/Dispose), never shared
/// across tests, so each test run gets a fully independent vault.
/// </summary>
/// <remarks>
/// Program.cs resolves and validates <c>Vault:RootPath</c> itself,
/// *before* calling <c>WebApplicationBuilder.Build()</c>. WebApplicationFactory
/// only gets a chance to mutate the builder (via ConfigureWebHost /
/// ConfigureAppConfiguration / UseSetting) at the moment <c>Build()</c>
/// is invoked - too late for that early read, which already ran with
/// whatever configuration existed right after
/// <c>WebApplication.CreateBuilder(args)</c> returned. The only
/// configuration source guaranteed to already be loaded at that point is
/// environment variables (added synchronously inside
/// <c>WebApplicationBuilder</c>'s own constructor), so this factory
/// overrides <c>Vault:RootPath</c> as the <c>Vault__RootPath</c>
/// environment variable rather than through the usual
/// ConfigureWebHost-based hooks. See <c>NotesEndpointsTests.Dispose</c>
/// for the matching cleanup, and this test project's
/// <c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c>
/// (in AssemblyInfo.cs), which keeps this process-wide environment
/// variable safe to use across tests.
/// </remarks>
public sealed class NotesApiFactory : WebApplicationFactory<Program>
{
    /// <param name="vaultRootPath">Isolated temp vault root for this test.</param>
    /// <param name="sharingEnabled">
    /// Overrides <c>Sharing:Enabled</c> via the <c>Sharing__Enabled</c>
    /// environment variable - same mechanism as <c>Vault__RootPath</c>
    /// above, just via the ordinary (post-<c>Build()</c>) configuration
    /// path rather than the early-resolution one Vault:RootPath needs
    /// (see this type's remarks). Defaults to <see langword="true"/>,
    /// matching appsettings.json, so existing callers are unaffected.
    /// </param>
    /// <param name="mcpEnabled">
    /// Overrides <c>Mcp:Enabled</c> via the <c>Mcp__Enabled</c>
    /// environment variable, same mechanism as <paramref name="sharingEnabled"/>
    /// above. Defaults to <see langword="true"/>, matching appsettings.json.
    /// </param>
    public NotesApiFactory(string vaultRootPath, bool sharingEnabled = true, bool mcpEnabled = true)
    {
        Environment.SetEnvironmentVariable("Vault__RootPath", vaultRootPath);
        Environment.SetEnvironmentVariable("Sharing__Enabled", sharingEnabled ? "true" : "false");
        Environment.SetEnvironmentVariable("Mcp__Enabled", mcpEnabled ? "true" : "false");
    }
}
