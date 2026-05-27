# Copilot instructions for `multirepo-mcp`

A read-only MCP (Model Context Protocol) server that exposes GitHub repository contents and code search across multiple GitHub App installations. ASP.NET Core on `net10.0`, deployed to Azure (App Service / Container Apps) with a Key-Vault-resident signing key.

## Build, test, lint

There is no CI workflow yet; validate locally:

```pwsh
dotnet build src/MultiRepoMcp/MultiRepoMcp.csproj --nologo
dotnet test tests/MultiRepoMcp.UnitTests/MultiRepoMcp.UnitTests.csproj --nologo
dotnet test tests/MultiRepoMcp.IntegrationTests/MultiRepoMcp.IntegrationTests.csproj --nologo
```

- A single test: `dotnet test <csproj> --nologo --filter "FullyQualifiedName~Stale_installation_id_invalidates"`. Test methods use underscore-separated names (xUnit convention; `CA1707` is suppressed in `tests/Directory.Build.props`).
- "Lint" is part of `dotnet build`: `Directory.Build.props` sets `TreatWarningsAsErrors=true`, `AnalysisMode=AllEnabledByDefault`, and `EnforceCodeStyleInBuild=true`. Suppress new analyzer noise in `Directory.Build.props` (production) or `tests/Directory.Build.props` (tests) — both list rationale comments for each rule already suppressed. Do not add per-file pragmas unless the suppression is genuinely local.
- All projects target **`net10.0` only**. Do not multi-target.

## Packages

Central package management is on (`Directory.Packages.props` with `ManagePackageVersionsCentrally=true`). New dependencies:

1. Add `<PackageVersion Include="X" Version="…" />` to `Directory.Packages.props`.
2. Add `<PackageReference Include="X" />` (no `Version`) to the consuming csproj.

**Do not `PackageReference` anything in `Microsoft.Extensions.*` or `Microsoft.AspNetCore.*` that already ships with the `Microsoft.NET.Sdk.Web` framework** (health checks, options.DataAnnotations, caching.memory, etc.) — NU1510 will fail the build. The comment in `Directory.Packages.props` documents this.

## Architecture (request flow you must understand to change auth/tooling code)

Every MCP tool call goes through this DI pipeline (`Program.cs` wires it; the interfaces in `src/MultiRepoMcp/GitHub/` are the public seams):

```
POST /mcp
 ├─ StaticBearerAuthenticationHandler            (Authentication/) – constant-time bearer compare
 ├─ AllowlistedCallerAuthorizationHandler        (Authentication/) – validates X-Caller-Repository against the optional allowlist
 ├─ MCP tool dispatch                            (Mcp/Tools/)
 │   ├─ GitHubInputValidation                    (Mcp/) – owner/repo/path/ref shape checks
 │   ├─ IInstallationResolver                    (GitHub/) – owner/repo → installationId; positive + negative cache;
 │   │                                             dedicated bounded MemoryCache (size-limited) tracks last-seen
 │   │                                             installation id so a reinstall (id change) invalidates the IAT.
 │   ├─ IInstallationTokenCache                  (GitHub/) – per-(installationId, repoLower) IAT;
 │   │                                             single-flight refresh under a Lazy<Task>; tokens minted with
 │   │                                             `repositories: [repo]` so each cached IAT is GitHub-scoped to ONE repo.
 │   ├─ IGitHubClientFactory                     (GitHub/) – Octokit client over a shared HttpMessageHandler
 │   └─ IJwtSigner: KeyVaultJwtSigner            (GitHub/) – production: signs the JWT digest inside Key Vault
 │              or LocalPemJwtSigner             (GitHub/) – dev-only fallback gated by GitHubApp:LocalPrivateKeyPath
 └─ McpToolErrorMapper                           (Mcp/) – classifies exceptions into typed tool errors
```

**Tool error pattern.** Tool methods do **not** throw on expected failure modes — they catch the exception and `return McpToolErrorMapper.BuildToolError(ex);`. The underlying MCP `AIFunction` wrapper swallows raw exception messages, so throwing yields a generic "An error occurred invoking …" to the caller. The full set of error kinds emitted by `BuildToolError` is: `AppNotInstalled`, `NotFound`, `ValidationError`, `PrimaryRateLimit`, `SecondaryRateLimit`, `InvalidSearchQuery`, `AuthFailure`, `GitHubApiError`, `InternalError`. Add new categories only by extending `ClassifyForTool` in `McpToolErrorMapper.cs`.

Health endpoints (`HealthChecks/HealthCheckEndpointExtensions.cs`):

- `/health/live` — dependency-free liveness.
- `/health/ready` — real `Key Vault sign` of a probe digest **and** GitHub App `/app` round-trip, both behind single-flight semaphores with per-check 3s deadlines, response-cached to bound load.
- `/health/startup` — same dependency set and same cached results as readiness (no separate startup TTL today).

The MCP server is registered with `Stateless = true` (`Program.cs`); tools must remain pure request/response — no server-initiated sampling, elicitation, or per-connection state. This is required for horizontal scaling.

## Security invariants (do NOT relax these without explicit user signoff)

1. **Per-repo IAT scoping.** `InstallationTokenCache` always mints tokens with `repositories: [repo]`. This is the primary defense against the search-code "query-escape" risk. Cache keys include the repo for the same reason. Never widen this to all installation repos.
2. **Private key never leaves Key Vault in production.** `KeyVaultJwtSigner` sends only a 32-byte SHA-256 digest to `CryptographyClient.SignAsync(RS256, …)`. `LocalPemJwtSigner` only activates when `GitHubApp:LocalPrivateKeyPath` is set (developer override) and is the only path that holds PEM material in process memory.
3. **`search_code` query validation.** `SearchCodeQueryValidation` rejects **any** GitHub search qualifier outside double-quoted literals — the regex is `\b[a-z][a-z0-9_-]*:` (case-insensitive), so `repo:`, `org:`, `user:`, `language:`, `path:`, `filename:`, `extension:`, `size:`, `symbol:`, etc. are all blocked. It also rejects boolean `OR`/`NOT` and exclusion (`-`) terms. The server injects exactly one `repo:owner/name` qualifier. Any change here is a security-sensitive boundary; preserve the all-qualifier rejection and the all-whitespace split (`Split((char[]?)null, …)` so tab/newline-delimited operators can't bypass the check).
4. **Read-only App scopes.** Only `Contents: read` and `Metadata: read`. New tools must not require write/PR/issue/workflow scopes.
5. **HTTPS redirection** is enabled outside Development only (`Program.cs`). Preserve the `if (!app.Environment.IsDevelopment())` gate.
6. **Bearer comparison is constant-time.** `StaticBearerAuthenticationHandler.ConstantTimeEquals` uses `CryptographicOperations.FixedTimeEquals` with equal-length UTF-8 buffers. Don't replace with `==`.
7. **`X-Caller-Repository` is self-attested, not authenticated.** The header value is trimmed and either checked against the optional allowlist or accepted as-is when no allowlist is configured (`AllowlistedCallerAuthorizationHandler.cs`). Any holder of the bearer token can set any value. Never use `httpContext.Items["X-Caller-Repository"]` (or the header itself) as a trust boundary for per-caller authorization, rate limiting, or audit attribution that needs to be tamper-resistant — treat it strictly as an allowlist hint / log-correlation field.

## Test conventions

- xUnit + Moq + FluentAssertions. Integration tests use `Microsoft.AspNetCore.Mvc.Testing` via the in-repo `MultiRepoMcpFactory` (`tests/MultiRepoMcp.IntegrationTests/Support/`), which lets each test override configuration and DI registrations.
- `Program.cs` has a trailing `public partial class Program;` declaration solely so `WebApplicationFactory<Program>` can locate it. Do not remove.
- Both test projects have `InternalsVisibleTo` — tests can call internal types directly; you do not need to make types public for testability.
- GitHub HTTP traffic is stubbed with **WireMock.Net** through `tests/MultiRepoMcp.IntegrationTests/Support/GitHubStubs.cs`. Add new endpoint stubs as helper extension methods on `WireMockServer` in that file rather than spinning up additional mock servers.
- Time-sensitive logic takes `TimeProvider` (registered as `AddSingleton<TimeProvider>(TimeProvider.System)` — keep the explicit generic). Tests inject `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`.

## Code conventions

- Private instance fields are `_camelCase`; private `static readonly` fields are `PascalCase` with no underscore (e.g. `HeaderBytes`, `ProbeDigest`, `PlaceholderValues`). Both are warning-level rules in `.editorconfig`, which means build-breaking under `TreatWarningsAsErrors`.
- 4-space indent for `.cs`; 2-space for `.json`, `.md`, `.csproj`, `.props`, `.targets`, `.yml`.
- `Nullable` and `ImplicitUsings` are on repo-wide.
- Bounded in-memory caches use `new MemoryCache(new MemoryCacheOptions { SizeLimit = N })` with `Size = 1` on every entry and an `AbsoluteExpirationRelativeToNow`. The pattern lives in `InstallationResolver._lastSeenCache` — copy it instead of inventing a new bounded-cache abstraction.
- Classes owning a `MemoryCache` they constructed implement `IDisposable` and dispose it in `Dispose()` (see `InstallationResolver.Dispose`). These types are `sealed`, so no finalizer suppression is needed.

## Local-run gotchas

- `dotnet run` uses `Properties/launchSettings.json` — HTTP binds to **`http://localhost:5070`** (not 5000) and HTTPS to `https://localhost:7294`. The README's "Usage" section documents this.
- The MCP endpoint is `POST /mcp`. The streamable-HTTP `Content-Type` and `Accept` headers are added automatically by conforming MCP clients; users only configure `Authorization`, optional `X-Caller-Repository`, and optional `X-Correlation-Id`.
- `X-Correlation-Id` is sanitized in `Logging/CorrelationIdMiddleware.cs`: only `[A-Za-z0-9._-]` up to 128 chars is accepted; any other value (or absence) causes the middleware to generate a fresh GUID. The chosen value is pushed into Serilog via `LogContext.PushProperty("CorrelationId", …)` and echoed back on the response. Keep this character allowlist intact — log sinks downstream assume the value is safe to render unescaped.
- Node-based MCP clients (e.g., Copilot CLI) reject the ASP.NET Core dev HTTPS cert. For local testing, use the HTTP URL.
- `Authentication:BearerToken` is required, minimum 20 characters; startup rejects placeholders.

## MCP servers configured for this workspace

Two workspace-level config files register the **Microsoft Learn Docs** MCP server (`https://learn.microsoft.com/api/mcp`, streamable HTTP, no auth) so every contributor gets it without a per-machine setup:

- `.mcp.json` (repo root) — picked up by **Copilot CLI** (and Claude Code, which uses the same convention). Confirm with `copilot mcp list` — it appears under "Workspace servers".
- `.vscode/mcp.json` — picked up by **VS Code Copilot Chat** when the workspace is opened.

Use this MCP to look up authoritative documentation for any Microsoft-stack API touched by this repo — `Azure.Identity`, `Azure.Security.KeyVault.Keys`, `Microsoft.Extensions.Caching.Memory`, ASP.NET Core auth/authorization/health-checks, `TimeProvider`, and similar — instead of relying on training-data recall. Prefer it over a web search when the question is about a specific API surface, behavior, or version compatibility.
