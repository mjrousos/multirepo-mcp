using Microsoft.Extensions.Options;
using MultiRepoMcp.Authentication;
using MultiRepoMcp.Configuration;
using MultiRepoMcp.GitHub;
using MultiRepoMcp.HealthChecks;
using MultiRepoMcp.Logging;
using MultiRepoMcp.Mcp.Tools;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.ConfigureSerilog();
builder.Services.AddMultiRepoMcpOptions(builder.Configuration);
builder.Services.AddMultiRepoMcpAuthentication();
builder.Services.AddMultiRepoMcpHealthChecks();

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<IJwtSigner>(sp =>
{
    // Choose the production (Key Vault) signer or the dev local-PEM signer
    // based on configuration: if a LocalPrivateKeyPath is set, prefer it
    // (developer override). Otherwise, sign via Azure Key Vault so the
    // App's private key never enters this process.
    var options = sp.GetRequiredService<IOptions<GitHubAppOptions>>();
    if (!string.IsNullOrEmpty(options.Value.LocalPrivateKeyPath))
    {
        return new LocalPemJwtSigner(
            options,
            sp.GetRequiredService<ILogger<LocalPemJwtSigner>>());
    }
    return new KeyVaultJwtSigner(
        options,
        sp.GetRequiredService<ILogger<KeyVaultJwtSigner>>());
});
builder.Services.AddSingleton<IGitHubAppJwtFactory, GitHubAppJwtFactory>();
builder.Services.AddSingleton<IGitHubClientFactory, GitHubClientFactory>();
builder.Services.AddSingleton<IInstallationTokenCache, InstallationTokenCache>();
builder.Services.AddSingleton<IInstallationResolver, InstallationResolver>();

builder.Services.AddMcpServer()
    .WithHttpTransport(options =>
    {
        // Stateless: tools are pure request/response — no server-initiated
        // sampling/elicitation needed. Required for horizontal scaling.
        options.Stateless = true;
    })
    .WithTools<GetFileContentsTool>()
    .WithTools<SearchCodeTool>();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapMultiRepoMcpHealthEndpoints();

app.MapMcp("/mcp")
    .RequireAuthorization(AllowlistedCallerRequirement.PolicyName);

app.Run();

namespace MultiRepoMcp
{
    /// <summary>Marker type used by <c>WebApplicationFactory&lt;Program&gt;</c> in tests.</summary>
    public partial class Program;
}
