# Requirements

- multirepo-mcp is an MCP server implemented in C# targeting .NET 10.
- The project should use the `ModelContextProtocol` and `ModelContextProtocol.AspNetCore` packages to implement a streamable HTTP MCP server that listens for incoming MCP requests.
- The server should implement the necessary MCP endpoints to handle requests for querying and retrieving content from GitHub repositories. Key tools that must be implemented include:
  - `get_file_contents`: Retrieve the contents of a file in a repository.
  - `search_code`: Search for code within a repository.
- The server should be able to search across multiple repositories, so tools should accept a repository identifier (e.g., owner/repo) as part of the request.
- The server should implement authentication using a static bearer token for incoming requests. This token should be configurable and validated against incoming requests to ensure only authorized clients can access the MCP server.
  - The server should also support an optional allowlist of caller repositories. If this allowlist is configured, the server should validate that the caller repository (identified in the request) is in the allowlist before processing the request.
- The server should interact with GitHub using the GitHub REST API to perform the necessary operations for the implemented tools.
- GitHub authentication should be handled with GitHub App installation access tokens as described in the "Authentication Flow" section of the README.
  - You *MUST* read the "Authentication Flow" section of the README carefully to understand how GitHub authentication should work for this project.
- The server MUST support a single GitHub App that is installed on multiple installations (e.g., different organizations or user accounts). For every request, the server MUST resolve the correct installation for the target repository, mint or reuse an installation access token (IAT) for that installation, and use it for the GitHub API call. See the "Multi-Installation Handling" section below.
- The solution should include error handling to manage cases such as invalid requests, authentication failures, and GitHub API errors gracefully, returning appropriate HTTP status codes and error messages.
- This solution should include thorough unit tests and integration tests to validate the functionality of the MCP server. GitHub API interactions should be mocked.
- The project should be well-documented, including clear instructions for setup, configuration, and usage in the README file.
- The project should include health endpoints that validate the server and all necessary dependencies are operational. This includes validating connectivity to GitHub and Azure Key Vault.

## Multi-Installation Handling
 
A single deployment of multirepo-mcp supports a GitHub App that may be installed on many installations (one per organization or personal account). The server is responsible for resolving the correct installation per request and managing tokens per installation.
 
- **Installation ID discovery.** For each tool invocation that targets `owner/repo`, the server must resolve the installation ID via `GET /repos/{owner}/{repo}/installation` (authenticated with the App JWT). The resolved `(owner/repo → installation_id)` mapping should be cached in memory with a reasonable TTL (e.g., 1 hour) to avoid repeated lookups.
- **IAT cache keyed by installation ID.** The in-memory IAT cache must be keyed by `installation_id`, not global. Each installation gets its own cached IAT and its own refresh lifecycle.
- **Single-flight refresh.** When an IAT for a given installation is expired or near-expiry, only one outstanding token-exchange call should be in flight per installation; concurrent requests for the same installation must wait for that single refresh rather than each issuing their own.
- **Proactive refresh.** IATs should be refreshed when the remaining lifetime drops below a configurable threshold (default: 5 minutes) rather than waiting for an outright expiry.
- **App-not-installed error path.** If the App is not installed on the requested `owner/repo`, the discovery call will return 404. The server must translate this into a clear MCP tool error such as `"The multirepo-mcp GitHub App is not installed on {owner}/{repo}. Ask a repository administrator to install it."` — not a generic 500.
- **Cross-org access is supported by design.** Because each org/user is a separate installation with its own IAT, no special configuration beyond installing the App in each org is required. The server must not assume all accessible repos share an installation.

## Technologies to Use
- C# with .NET 10
- ASP.NET Core for the HTTP server
- GitHub REST API for interacting with GitHub repositories
- Azure Key Vault for securely storing the GitHub App private key
- Managed Identity for authenticating with Azure Key Vault
- xUnit and Moq for unit and integration testing
- Serilog for logging
- Configuration should be handled using the built-in .NET configuration system, allowing for configuration via appsettings.json, environment variables, and Azure Key Vault.

## Non-goals

- This project does not need to implement every possible MCP tool; it only needs to implement a representative set (e.g., `get_file_contents` and `search_code`) to demonstrate the authentication flow and multi-repo access.
- This project does not need to support write operations to GitHub (e.g., creating issues, pull requests, etc.). Read-only access is sufficient for the purposes of this proof-of-concept.
- This project does not need to attribute actions to specific users since it authenticates as a GitHub App installation rather than individual users. All actions will be performed with the permissions of the GitHub App installation.
