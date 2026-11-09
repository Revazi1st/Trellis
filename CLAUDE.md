@AGENTS.md

## Claude Code

- Path-scoped layer rules live in `.claude/rules/` — they load automatically when you touch matching files.
- MCP servers in `.mcp.json`: `microsoft-learn` (.NET, EF Core and Aspire APIs), `context7` (third-party packages), `playwright` (the running app in a browser) and `aspire` (the running AppHost's logs, traces and resources, under `aspire run`); `docs/INDEX.md` says when to reach for each.
- Hooks in `.claude/hooks/` run from `.claude/settings.json` around your tool calls; `docs/INDEX.md` (*Hooks*) lists each with its event and what it does.
- Docs index for deeper context: `docs/INDEX.md`.
