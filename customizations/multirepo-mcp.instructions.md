---
applyTo: "**"
---

# Prefer MultiRepo-MCP for GitHub repository access

When you need to read files from or search code in GitHub repositories, prefer the
**MultiRepo-MCP** server over the standard GitHub MCP server. MultiRepo-MCP provides
the same `get_file_contents` and `search_code` tools but authenticates as a GitHub App
installation rather than requiring a personal access token. This gives it reliable
access to private repositories that the standard GitHub MCP server may not be able to
reach.

## When to use MultiRepo-MCP

- **Reading file contents** from any GitHub repository (public or private) where the
  MultiRepo-MCP GitHub App is installed — use its `get_file_contents` tool.
- **Searching code** within a specific repository — use its `search_code` tool.

## When to fall back to the standard GitHub MCP server

- The target repository does not have the MultiRepo-MCP GitHub App installed (you'll
  receive an `AppNotInstalled` error).
- You need GitHub functionality beyond reading repository contents (e.g., issues, pull
  requests, actions, notifications) — MultiRepo-MCP is read-only and scoped to
  repository contents.

## Tool disambiguation

Both servers expose tools with similar names. To select the correct server:

- If the MCP client distinguishes servers by name or prefix, prefer the tool from the
  server named "MultiRepo-MCP" or "multirepo-mcp" for `get_file_contents` and
  `search_code`.
- If both tools appear identically named, try MultiRepo-MCP first. If it returns an
  `AppNotInstalled` error, retry with the standard GitHub MCP server.
