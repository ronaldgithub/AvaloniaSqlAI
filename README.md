# BlitzIndex AI

A Windows desktop app that automates the sp_BlitzIndex → Claude AI → Execute SQL workflow for analyzing index recommendations on SQL Server databases.

## What it does

1. Connects to SQL Server (localhost, Windows Authentication) and lists `StackOverflow*` databases
2. Lets you pick a database and table
3. Runs `sp_BlitzIndex @AI=2` to generate an AI-ready index analysis prompt
4. Sends the prompt to Claude and displays the recommendations
5. Lets you execute the suggested SQL scripts directly against the target database (with a confirmation dialog)

## Prerequisites

- Windows, .NET 8 runtime
- SQL Server on `localhost` accessible via Windows Authentication
- `sp_BlitzIndex` installed in `master` (see `scripts/sp_BlitzIndex.sql`)
- An [Anthropic API key](https://console.anthropic.com/)

## Setup

1. Install `sp_BlitzIndex` in `master`:

   ```sql
   -- Run in SSMS connected to localhost, targeting master
   USE master;
   -- then execute scripts/sp_BlitzIndex.sql
   ```

2. Copy `config.json.example` to `output/config.json` and fill in your API key:

   ```json
   { "ApiKey": "sk-ant-..." }
   ```

   Alternatively, set the `ANTHROPIC_API_KEY3` environment variable.

3. Build and run:

   ```powershell
   dotnet build BlitzIndexAI
   dotnet run --project BlitzIndexAI
   ```

## Stack

- [Avalonia](https://avaloniaui.net/) 12 — cross-platform UI framework
- .NET 8
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM source generators
- Microsoft.Data.SqlClient — SQL Server connectivity
- Claude API (`claude-sonnet-4-6`) via direct HTTP
