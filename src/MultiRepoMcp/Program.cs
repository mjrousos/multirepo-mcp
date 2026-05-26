using Microsoft.Extensions.Caching.Memory;
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

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<IGitHubPrivateKeyProvider, MultiRepoMcp.GitHub.KeyVaultPrivateKeyProvider>();
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

app.UseSerilogRequestLogging();
app.UseMiddleware<CorrelationIdMiddleware>();

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
