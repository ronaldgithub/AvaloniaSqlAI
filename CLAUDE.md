# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

Run from the repo root:

```powershell
dotnet build BlitzIndexAI
dotnet run --project BlitzIndexAI
```

There are no tests. No linting step beyond the compiler.

## Architecture

This is a single-window Avalonia 12 / .NET 8 desktop app. The entry point is `Program.cs` → `App.axaml.cs` → `MainWindow` (which creates its own `MainWindowViewModel` in the constructor and wires up the confirmation dialog delegate).

**Automated workflow — end to end:**

1. On startup `MainWindowViewModel` reads the API key from `output\config.json`, falling back to the `ANTHROPIC_API_KEY3` environment variable.
2. It immediately queries SQL Server (localhost, Windows Authentication, `TrustServerCertificate=True`) for databases matching `StackOverflow%`.
3. Selecting a database loads its `dbo` tables via `INFORMATION_SCHEMA.TABLES`.
4. **Analyze** runs `EXEC dbo.sp_BlitzIndex @DatabaseName=…, @SchemaName='dbo', @TableName=…, @AI=2` (the proc lives in `master`). With `@AI=2` the proc returns a result set with a single `[AI Prompt]` column of XML type (`FOR XML PATH('ai_prompt'), TYPE`). The service reads that via `SqlDataReader.GetSqlXml(0)` → `XDocument` → `.Root.Value` and saves the plain text to `output\ai_prompt.txt`.
5. The prompt is split at the line `"I need help analyzing"` — everything before becomes the Claude `system` parameter, everything from that line onward is the `user` message. This is posted to `https://api.anthropic.com/v1/messages` (`claude-sonnet-4-6`, max 4096 tokens).
6. **Execute SQL Script** parses ` ```sql … ``` ` blocks from the Claude response with a regex and runs each batch (split on `GO`) against the target database.

## Key files

| File | Role |
| --- | --- |
| `Services/SqlServerService.cs` | All SQL Server I/O: list DBs, list tables, run sp_BlitzIndex, execute scripts |
| `Services/ClaudeApiService.cs` | Raw `HttpClient` POST to Claude API; handles prompt splitting and JSON parsing |
| `ViewModels/MainWindowViewModel.cs` | All state + commands; uses CommunityToolkit.Mvvm `[ObservableProperty]` / `[RelayCommand]` |
| `Views/MainWindow.axaml` | Single-window UI: left panel (DB/table select), right panel (response text), bottom bar (API key + status) |
| `Views/MainWindow.axaml.cs` | Wires `vm.ConfirmDialog` to a programmatic Avalonia `Window` dialog before Execute |

## CommunityToolkit.Mvvm conventions

- `[RelayCommand]` on method `FooAsync()` generates command property `FooCommand` (not `FooAsyncCommand`).
- `[NotifyCanExecuteChangedFor(nameof(FooCommand))]` on an `[ObservableProperty]` field automatically calls `FooCommand.NotifyCanExecuteChanged()` on change — use this instead of manual `partial void OnXChanged`.
- All `async` ViewModel methods avoid `ConfigureAwait(false)` so continuations stay on the UI thread; `ObservableCollection` modifications need no explicit dispatcher.

## Config & output paths (hardcoded)

| Path | Purpose |
| --- | --- |
| `C:\Projecten\Claude\AvaloniaAI\output\config.json` | Persisted API key (`{ "ApiKey": "…" }`) |
| `C:\Projecten\Claude\AvaloniaAI\output\ai_prompt.txt` | Written on each Analyze run — the raw prompt sent to Claude |
| `C:\Projecten\Claude\AvaloniaAI\scripts\sp_BlitzIndex.sql` | Source of the stored procedure (must be installed in `master` on localhost) |
