# MultiRepo-MCP

MultiRepo-MCP is an MCP server that enables access to GitHub repositories. It enables AI agents to query across multiple repositories, providing a unified interface for repository access. This MCP server differs from the standard GitHub MCP server in how it handles authentication. Rather than authenticating with a PAT, MultiRepo-MCP needs to run as a GitHub App and [authenticate as an app installation](https://docs.github.com/apps/creating-github-apps/authenticating-with-a-github-app/about-authentication-with-a-github-app#authentication-as-an-app-installation). This allows it to access repositories without needing to authenticate with a PAT or authenticate as any specific user.

> Note that this is a tool is a proof-of-concept demonstrating how GitHub Copilot can access GitHub repositories without needing to authenticate with a PAT. It is not a supported product.

## Setup

### 1. Register a GitHub App

1. Follow GitHub's docs to [register a GitHub App](https://docs.github.com/apps/creating-github-apps/registering-a-github-app/registering-a-github-app) for the server.
   - **Permissions:** `Repository → Contents: Read-only`. No other permissions are needed for the current toolset.
   - **No webhooks**, no callback URL, no device flow, no user-authorization flow.
   - **Where can this GitHub App be installed?** — typically `Any account` so the App can be installed across organizations.
2. On the App's settings page, click **Generate a private key**. Download the resulting `.pem` file.
3. Note the App's numeric **App ID** (shown at the top of the App settings page).
4. [Install the App](https://docs.github.com/apps/using-github-apps/installing-your-own-github-app) on every repository the server should be allowed to read. The App can have multiple installations — the server resolves the correct one per request.

### 2. Import the private key into Azure Key Vault

The server signs GitHub App JWTs **inside Key Vault** — the private key never leaves the vault, even at runtime. To make this work the PEM must be imported as a **Key Vault key** (not a secret).

1. Create (or pick) an Azure Key Vault that the server's hosting environment can reach over Managed Identity.
2. Import the PEM as an RSA key:

   ```pwsh
   az keyvault key import `
     --vault-name my-vault `
     --name multirepo-mcp-private-key `
     --pem-file ./multirepo-mcp.private-key.pem
   ```

   The default key name expected by the server is `multirepo-mcp-private-key`; you can override this via `GitHubApp:PrivateKeyName`.
3. Grant the hosting compute's Managed Identity the **Sign** permission on the key — RBAC role `Key Vault Crypto User` is sufficient (or `Sign` via an access policy if your vault still uses the legacy permission model).

> Local development can skip Key Vault entirely by setting `GitHubApp:LocalPrivateKeyPath` to a filesystem path containing the PEM (see the dev section below). In that mode signing happens locally with the loaded RSA key.

### 3. Pick a static bearer token

The server authenticates **inbound callers** via a single static bearer token.

- Generate a cryptographically random value of at least 20 characters (e.g., `openssl rand -base64 48`).
- Store it in a secure secret store (Key Vault, App Service config, container env var, etc.) and surface it to the server as `Authentication:BearerToken` or env var `Authentication__BearerToken`.
- Startup validation will reject obvious placeholders (`changeme`, `todo`, `...`, all-same-char strings, etc.), so do not ship a half-finished config.

### 4. (Optional) Define a caller-repository allowlist

If you want to restrict which inbound callers are accepted, set `Authentication:CallerRepositoryAllowlist` to a list of `owner/repo` values. Callers must then send an `X-Caller-Repository: owner/repo` header that matches the allowlist. Leave the allowlist null or empty to disable the check.

### 5. Configure the server

`appsettings.json` (or environment variables using the `__` delimiter):

```jsonc
{
  "AllowedHosts": "multirepo-mcp.example.com",
  "Authentication": {
    "BearerToken": "<random ≥ 20 chars>",
    "CallerRepositoryAllowlist": [ "octo/cat", "contoso/widget" ]
  },
  "GitHubApp": {
    "AppId": 123456,
    "KeyVaultUri": "https://my-vault.vault.azure.net/",
    "PrivateKeyName": "multirepo-mcp-private-key",
    "InstallationTokenRefreshThreshold": "00:05:00",
    "InstallationTokenRefreshJitter": "00:00:30"
  },
  "Cache": {
    "InstallationDiscoveryTtl": "01:00:00",
    "InstallationNotFoundTtl": "00:01:00",
    "HealthCheckResultTtl": "00:00:30",
    "HealthCheckDependencyTimeout": "00:00:03"
  }
}
```

#### Configuration reference

| Key | Env var | Default | Description |
| --- | --- | --- | --- |
| `AllowedHosts` | `AllowedHosts` | `*` | Semicolon-separated list of permitted `Host` headers (DNS-rebinding defense). Set to your public hostname in production. |
| `Authentication:BearerToken` | `Authentication__BearerToken` | _(required)_ | Static token callers present as `Authorization: Bearer …`. Min 20 chars; startup rejects placeholders. |
| `Authentication:CallerRepositoryAllowlist` | `Authentication__CallerRepositoryAllowlist__0`, `…__1`, … | `null` | Optional list of permitted `owner/repo` values for `X-Caller-Repository`. |
| `GitHubApp:AppId` | `GitHubApp__AppId` | _(required)_ | Numeric App ID. |
| `GitHubApp:KeyVaultUri` | `GitHubApp__KeyVaultUri` | _(required)_ | Key Vault URI containing the App's signing key. |
| `GitHubApp:PrivateKeyName` | `GitHubApp__PrivateKeyName` | `multirepo-mcp-private-key` | Key Vault **key** name. Signing happens inside the vault; the private key never leaves it. |
| `GitHubApp:InstallationTokenRefreshThreshold` | `GitHubApp__InstallationTokenRefreshThreshold` | `00:05:00` | Proactively refresh IATs when remaining lifetime falls below this. |
| `GitHubApp:InstallationTokenRefreshJitter` | `GitHubApp__InstallationTokenRefreshJitter` | `00:00:30` | Max per-key jitter added to the refresh threshold to spread herds. |
| `GitHubApp:ApiBaseAddress` | `GitHubApp__ApiBaseAddress` | _(unset → api.github.com)_ | Override the GitHub REST base URL (used by tests). |
| `GitHubApp:LocalPrivateKeyPath` | `GitHubApp__LocalPrivateKeyPath` | _(unset)_ | **Dev only.** Load the PEM from a local file instead of Key Vault. |
| `Cache:InstallationDiscoveryTtl` | `Cache__InstallationDiscoveryTtl` | `01:00:00` | Positive cache TTL for `owner/repo → installation_id`. |
| `Cache:InstallationNotFoundTtl` | `Cache__InstallationNotFoundTtl` | `00:01:00` | Negative cache TTL for "App not installed" responses. |
| `Cache:HealthCheckResultTtl` | `Cache__HealthCheckResultTtl` | `00:00:30` | How long each readiness check result is cached. |
| `Cache:HealthCheckDependencyTimeout` | `Cache__HealthCheckDependencyTimeout` | `00:00:03` | Per-dependency timeout inside the readiness probe. |

### 6. Run locally

```pwsh
# 1. Put the PEM somewhere safe (NOT inside the repo).
$env:GitHubApp__LocalPrivateKeyPath = "C:\secrets\multirepo-mcp.pem"
$env:GitHubApp__AppId               = "123456"
$env:GitHubApp__KeyVaultUri         = "https://placeholder.vault.azure.net/"   # required for option binding; unused when LocalPrivateKeyPath is set
$env:Authentication__BearerToken    = "<random ≥ 20 chars>"

dotnet run --project src/MultiRepoMcp
```

With the checked-in `Properties/launchSettings.json`, `dotnet run` binds Kestrel to `http://localhost:5070` (the `http` profile; the `https` profile additionally exposes `https://localhost:7294`). When the app is started without a launch profile (for example in a published container), Kestrel falls back to its built-in defaults of `http://localhost:5000` / `https://localhost:5001`. The MCP endpoint is `POST /mcp` and health probes live under `/health/{live,ready,startup}` on whichever URL the host is bound to.

### 7. Run as a container

```pwsh
docker build -t multirepo-mcp:dev .
docker run --rm -p 8080:8080 `
  -e Authentication__BearerToken="<random ≥ 20 chars>" `
  -e GitHubApp__AppId=123456 `
  -e GitHubApp__KeyVaultUri="https://my-vault.vault.azure.net/" `
  multirepo-mcp:dev
```

The container expects to run behind a **TLS-terminating reverse proxy** (Azure Container Apps, App Service, Kubernetes ingress, etc.). HTTPS redirection is enabled outside Development.

## Usage

The server exposes a [streamable-HTTP](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports) MCP endpoint at `POST /mcp`.

The transport-level headers required by the streamable-HTTP spec — `Content-Type: application/json` and `Accept: application/json, text/event-stream` — are sent automatically by every conforming MCP client (the official TypeScript, Python, and C# SDKs all do this in their HTTP transport). You should **not** need to configure these yourself.

The headers you do need to configure on the MCP client are:

| Header | Required | Value |
| --- | --- | --- |
| `Authorization` | yes | `Bearer <configured bearer token>` — the static token from `Authentication:BearerToken` |
| `X-Caller-Repository` | when allowlist is enabled | `owner/repo` of the calling repository; the server rejects the request if this is missing or not in `Authentication:CallerRepositoryAllowlist` |
| `X-Correlation-Id` | optional | caller-supplied correlation ID; echoed back in responses and surfaced in server logs for cross-system tracing |

Most MCP clients expose a way to set additional HTTP headers on the transport — for example, the C# SDK accepts them via `SseClientTransportOptions.AdditionalHeaders`, and the TypeScript SDK takes a `requestInit.headers` option on `StreamableHTTPClientTransport`.

### Tool reference

#### `get_file_contents`

Read a single file from a target repository.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `owner` | string | yes | Repository owner (user or org). |
| `repo` | string | yes | Repository name. |
| `path` | string | yes | Repo-relative file path. Must not contain `\\`, leading `/`, control chars, or exceed 1024 chars. |
| `ref` | string | no | Branch, tag, or commit SHA. Defaults to the repo's default branch. |

Returns one of:

- **Text file:** `{ path, sha, size, encoding: "utf-8", content }`
- **Binary file:** `{ path, sha, size, encoding: "base64", content }`
- **Directory:** error `PathIsDirectory` (directory listing is intentionally not implemented).
- **Submodule:** error `PathIsSubmodule` with `submodule_git_url` and `sha`.
- **Symlink:** error `PathIsSymlink` with the symlink's `target` (not auto-followed).
- **File too large (> 1 MiB):** error `FileTooLarge` (size is checked before any base64 decode).

Example:

```jsonc
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "get_file_contents",
    "arguments": { "owner": "octo", "repo": "hello", "path": "README.md", "ref": "main" }
  }
}
```

#### `search_code`

Search code inside a single target repository using GitHub's code-search REST API.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `owner` | string | yes | Repository owner. |
| `repo` | string | yes | Repository name. |
| `query` | string | yes | Free-text search expression. **May not** contain qualifier prefixes (`repo:`, `path:`, `language:`, …), boolean operators (`OR`, `NOT`), or leading-dash exclusions — the tool enforces single-repo scoping. Use double quotes for literal terms containing `:` or `-`. |
| `max_results` | integer | no | Cap on returned results. Defaults to 30; absolute max 100. |

Returns `{ total_count, incomplete_results, returned_count, results: [{ path, name, sha, html_url, repo }], notes? }`.

**Limitations of GitHub's code-search REST API** (inherent, documented here so callers don't expect filesystem-wide grep semantics):

- Only the repository's **default branch** is indexed.
- Files larger than ~384 KB are not indexed.
- The target repository must be indexed by GitHub code-search. Org-owned repos are generally indexed automatically; personal/user repos may need opt-in. When the API returns 0 hits the response includes a `notes` hint about indexing.
- The Search API has a small, separate rate-limit budget — propagated to callers as a `PrimaryRateLimit` error with the `Retry-After` window.

### Error shape

Validation errors and GitHub API failures are returned as in-band MCP tool results (not HTTP errors) with shape `{ "error": "<kind>", "message": "<human readable>" }`. Stable kinds:

| Kind | Cause |
| --- | --- |
| `AppNotInstalled` | The App isn't installed on the target repo (404 on installation discovery, or 422 on IAT mint). |
| `NotFound` | Repo, file, or ref not found. |
| `ValidationError` | Argument failed validation before any GitHub call. |
| `InvalidSearchQuery` | GitHub returned 422 for the search query. |
| `PrimaryRateLimit` | GitHub primary rate limit hit. |
| `SecondaryRateLimit` | GitHub secondary / abuse-detection rate limit hit. |
| `AuthFailure` | GitHub rejected the App-JWT or installation token. |
| `GitHubApiError` | Other 4xx/5xx from GitHub. |
| `InternalError` | Unexpected server error. |
| `PathIsDirectory` / `PathIsSubmodule` / `PathIsSymlink` / `FileTooLarge` | `get_file_contents`-only non-error responses for non-file paths and oversized files. |

Transport-level errors (bearer rejected, allowlist deny, host filtering, etc.) are returned as conventional HTTP status codes (`400`/`401`/`403`).

### Health endpoints

| Endpoint | Purpose |
| --- | --- |
| `GET /health/live` | Liveness. Dependency-free; returns `200 {"status":"Healthy"}` whenever the process is up. **Use this for `livenessProbe`** — never wire dependency checks into liveness or transient GitHub blips will trigger container restarts. |
| `GET /health/ready` | Readiness. Verifies Key Vault signing is reachable (or the local dev key is loadable) by signing a fixed probe digest, and that GitHub responds to `GET /app` via the App JWT. Results are cached for `Cache:HealthCheckResultTtl` (default 30 s) so a flood of probes can't exhaust the GitHub rate-limit budget. |
| `GET /health/startup` | Same checks as `/health/ready`, intended for orchestrator startup probes. |

Health responses intentionally omit individual check names, descriptions, and exception data — only `{"status":"Healthy|Unhealthy"}` is returned to avoid leaking topology. Diagnostic detail is in the server log.

## Threat model

This is an intentionally small POC. Key assumptions and known limits:

- **Inbound auth is a shared bearer token.** Any holder of a valid bearer can request any repo the App is installed on. The `X-Caller-Repository` header is **self-attested** — it gates inbound access via the allowlist but does NOT prove the request actually originates from that repo. If per-caller scoping becomes a requirement, the natural extensions are (a) a `BearerToken → AllowedTargetRepos` map enforced at the tool layer, or (b) per-caller bearers issued by an authenticating gateway.
- **GitHub-side scoping is the security boundary.** Each `tools/call` mints an installation access token with `repositories: [target_repo]` so the token can read only the named repo at GitHub's authoritative boundary. A malicious `search_code` query cannot reach other repos in the same installation because the token itself is scoped. Query sanitization (qualifier rejection, boolean-operator rejection, leading-dash rejection) is defense-in-depth.
- **Constant-time bearer comparison.** Bearer validation uses `CryptographicOperations.FixedTimeEquals` over UTF-8 byte arrays after a length check. Plain `==` would be a timing-side-channel vulnerability.
- **PEM lifetime.** Loaded once at startup. **Rotation requires a restart.** Out of scope for the POC.
- **TLS termination.** The server expects to run behind a TLS-terminating reverse proxy; CORS is intentionally disabled (MCP clients are not browsers).
- **DNS rebinding.** `AllowedHosts` should be tightened from `*` to the production hostname.
- **Secrets in logs.** Bearer tokens and IATs are never logged. Correlation IDs, caller repo, target repo, and installation ID are.

## Multi-Installation Handling

When the same GitHub App is installed in many places (multi-org / multi-account), the server needs to do two things correctly for each request: find the right **installation** for the target repo, and mint a token that can only read **that repo**.

### Installation discovery

For each `tools/call`, the installation resolver calls GitHub's `GET /repos/{owner}/{repo}/installation` using the App JWT, then caches `owner/repo → installation_id` in memory for `Cache:InstallationDiscoveryTtl` (default 1 hour). 404s are **negatively cached** for `Cache:InstallationNotFoundTtl` (default 60 s) so a loop-driven caller asking for an uninstalled repo cannot hammer GitHub. When the resolver later sees a *different* installation ID for the same `owner/repo` (the app-uninstalled-then-reinstalled case), it proactively invalidates the IAT cache entry tied to the stale installation ID.

### Per-`(installation, repo)` IAT cache

Tokens are minted with `repositories: [repo]` on `POST /app/installations/{id}/access_tokens`, so each cached token can read only the specific target repo. The cache key is `(installation_id, repo_name_lower)` — two repos under the same installation get **independent** tokens.

### Single-flight refresh

Concurrent requests for the same `(installation, repo)` collapse to a single in-flight refresh via `Lazy<Task<CachedToken>>`. A read/refresh loop avoids the obvious TOCTOU race: when two callers both see a near-expiry entry, one wins `TryUpdate` and the other's proposed replacement is discarded; both end up awaiting the winner's refresh task. Refresh tasks are launched with `CancellationToken.None` so a single cancelled caller cannot tear down a refresh that other callers are awaiting.

### Proactive refresh with jitter

Tokens are refreshed when their remaining lifetime falls under `GitHubApp:InstallationTokenRefreshThreshold` plus a per-key deterministic jitter of `[0, InstallationTokenRefreshJitter]`. Jitter is per-key deterministic (same key → same offset) so the cache decision is idempotent across concurrent callers, while different keys age out at slightly different times — defense against thundering-herd refreshes when many installations were minted in the same window.

### App-not-installed error

Both signals — `GET /repos/{o}/{r}/installation` returning 404 and `POST /app/installations/{id}/access_tokens` returning 422 with a "repository not accessible" body — surface to callers as the same `AppNotInstalled` tool error so the caller doesn't need to know which side detected it.

### Tuning

| Knob | When to change it |
| --- | --- |
| `Cache:InstallationDiscoveryTtl` | Shorter if you frequently install/uninstall the App and don't want to wait an hour for the resolver to notice. |
| `Cache:InstallationNotFoundTtl` | Shorter only if you expect to install the App immediately after a 404 and want to retry without waiting; longer if loop-driven callers are common. |
| `GitHubApp:InstallationTokenRefreshThreshold` | Raise if you see request-time latency spikes correlated with refresh; lower if you want to recover faster from clock skew. |
| `GitHubApp:InstallationTokenRefreshJitter` | Raise if many installations refresh in lockstep; set to `00:00:00` only in tests. |
| `Cache:HealthCheckResultTtl` | Raise if orchestrator probes are very frequent; lower if you want faster recovery signal after a GitHub outage. |

## Authentication Flow

MultiRepo-MCP authenticates with GitHub in the following way:

During Setup:
1. A GitHub app is [registered](https://docs.github.com/apps/creating-github-apps/registering-a-github-app/registering-a-github-app) for MultiRepo-MCP. 
   1. This GitHub app does not need a callback URL, device flow, user authorization during installation, or any webhooks. It does require contents read access.
   2. The private key (PEM) for this GitHub app is **imported as a Key Vault key** (not stored as a secret). Signing happens inside Key Vault via the `/sign` API, so the private key never leaves the vault.
2. The GitHub app is [installed](https://docs.github.com/apps/using-github-apps/installing-your-own-github-app) on the repositories that MultiRepo-MCP needs to access.

On app startup:
1. MultiRepo-MCP wires up a Key Vault `CryptographyClient` pointing at the configured signing key.
   1. Authentication with Azure Key Vault is done using Managed Identity (`DefaultAzureCredential`), and the Managed Identity is granted the `Sign` permission (`Key Vault Crypto User` RBAC role) on the key.
   2. Whenever a GitHub App JWT is needed, the server computes a SHA-256 digest of the JWT signing input locally and asks Key Vault to sign that digest with `RS256`. The resulting signature is appended to produce the final JWT.
2. MultiRepo-MCP loads a static bearer token from configuration. This bearer token will be used by callers to authenticate with MultiRepo-MCP.
3. MultiRepo-MCP loads an optional list of pre-authorized caller repositories from configuration.

During operation:
1. When a request is made to MultiRepo-MCP, the caller includes the static bearer token in the Authorization header.
   1. MultiRepo-MCP validates the bearer token against the configured value. If the token is invalid, the request is rejected.
   2. If a list of pre-authorized caller repositories is configured, MultiRepo-MCP also validates that the caller repository (identified in the request) is in the allowlist. If the caller repository is not in the allowlist, the request is rejected.
2. If the bearer token is valid, MultiRepo-MCP produces a GitHub App JWT (signed inside Key Vault — only the SHA-256 digest of the signing input is sent to Key Vault, and only the signature comes back) and exchanges it for an installation access token (with `contents: read` permissions) with GitHub.
    1. The installation access token is cached in memory and reused for subsequent requests until it expires. Once the token expires, a new JWT token is generated and exchanged for a new installation access token.
    2. Note that because the GitHub App may be installed on multiple repositories (and therefore multiple installations), the server must resolve the correct installation for the target repository of each request and manage tokens per installation. Installation access tokens are cached keyed by installation ID, not globally.
3. MultiRepo-MCP uses the installation access token to authenticate API requests to GitHub, allowing it to access the repositories that the GitHub App is installed on.

## Roadmap and Future Improvements

- Improved audit logging of requests and GitHub API interactions.
- Support for additional MCP tools beyond `get_file_contents` and `search_code`.

## Resources

- [GitHub App Authentication](https://docs.github.com/apps/creating-github-apps/authenticating-with-a-github-app)
- [Registering a GitHub App](https://docs.github.com/apps/creating-github-apps/registering-a-github-app/registering-a-github-app)
- [Authenticating as an app installation](https://docs.github.com/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation)