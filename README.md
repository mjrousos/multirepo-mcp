# MultiRepo-MCP

MultiRepo-MCP is an MCP server that enables access to GitHub repositories. It enables AI agents to query across multiple repositories, providing a unified interface for repository access. This MCP server differs from the standard GitHub MCP server in how it handles authentication. Rather than authenticating with a PAT, MultiRepo-MCP needs to run as a GitHub App and [authenticate as an app installation](https://docs.github.com/apps/creating-github-apps/authenticating-with-a-github-app/about-authentication-with-a-github-app#authentication-as-an-app-installation). This allows it to access repositories without needing to authenticate with a PAT or authenticate as any specific user.

> Note that this is a tool is a proof-of-concept demonstrating how GitHub Copilot can access GitHub repositories without needing to authenticate with a PAT. It is not a supported product.

## Setup

// TODO

## Usage

// TODO

## Authentication Flow

MultiRepo-MCP authenticates with GitHub in the following way:

During Setup:
1. A GitHub app is [registered](https://docs.github.com/apps/creating-github-apps/registering-a-github-app/registering-a-github-app) for MultiRepo-MCP. 
   1. This GitHub app does not need a callback URL, device flow, user authorization during installation, or any webhooks. It does require contents read access.
   2. The private key (PEM) for this GitHub app is stored in Azure Key Vault.
2. The GitHub app is [installed](https://docs.github.com/apps/using-github-apps/installing-your-own-github-app) on the repositories that MultiRepo-MCP needs to access.

On app startup:
1. MultiRepo-MCP retrieves the private key file from Azure Key Vault. 
   1. Authentication with Azure Key Vault is done using Managed Identity.
   2. This private key file is used to sign a JWT token for GitHub App authentication.
2. MultiRepo-MCP loads a static bearer token from configuration. This bearer token will be used by callers to authenticate with MultiRepo-MCP.
3. MultiRepo-MCP loads an optional list of pre-authorized caller repositories from configuration.

During operation:
1. When a request is made to MultiRepo-MCP, the caller includes the static bearer token in the Authorization header.
   1. MultiRepo-MCP validates the bearer token against the configured value. If the token is invalid, the request is rejected.
   2. If a list of pre-authorized caller repositories is configured, MultiRepo-MCP also validates that the caller repository (identified in the request) is in the allowlist. If the caller repository is not in the allowlist, the request is rejected.
2. If the bearer token is valid, MultiRepo-MCP generates a JWT token signed with the GitHub App's private key and exchanges it for an installation access token (with `contents: read` permissions) with GitHub.
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