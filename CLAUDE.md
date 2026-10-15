# CLAUDE.md

.NET 10 Discord bot (`src/HomelabBot`), tests in `tests/HomelabBot.Tests`. Build and test with `dotnet build` and
`dotnet test`. Deploy: push to `master` builds the image and the self-hosted runner restarts the compose stack in
`/home/ubuntu/docker/homelab-bot`.

## Rules

- Explicit enum values (`= N`) are persisted in the database. Keep every `= N`: same name, same number, no removal,
  no reorder.
- A model change needs an EF migration: `dotnet ef migrations add <Name> --project src/HomelabBot`.
  `MigrationDriftTests` fails otherwise.
- A tool that changes something outside the bot gets `[ChangesState]` (see `Plugins/ChangesStateAttribute.cs`).
  `KernelToolSurfaceTests` lists every marked tool.

## Claude runner (`/claude`, `AskClaude`)

- `Services/ClaudeRunnerService.cs`: the one run slot (try, never wait), time limits, output parsing, redaction
  (`ClaudeOutputRedactor`), run storage (`ClaudeRuns`), session links (`ClaudeSessionLinks`: thread id or DM message
  id → session id), interrupted-run marking at startup.
- `Services/ClaudeProcessRunner.cs`: the `ssh` command. The host only allows the forced command `claude-run`.
- `Plugins/ClaudePlugin.cs`: `AskClaude`, kernel only. Never add it to `McpServerBuilderExtensions`: Claude on the
  host uses this bot's MCP server, so `ask_bot` → `AskClaude` would start Claude again. `[ChangesState(OwnerOnly = true)]`
  gives the button, keeps it off scheduled runs, and fails closed in an `ask_bot` turn.
- Routing: `DiscordBotService.OnMessageCreated` checks the session link of a thread before the bot's own LLM;
  `HandleDmMessageAsync` checks the replied message id.
- Host setup and configuration: `README.md`.
