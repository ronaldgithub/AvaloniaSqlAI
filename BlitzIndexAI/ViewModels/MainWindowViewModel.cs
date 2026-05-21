using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BlitzIndexAI.Models;
using BlitzIndexAI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlitzIndexAI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "output", "config.json");

    private readonly SqlServerService _sql = new();
    private readonly ClaudeApiService _claude = new();

    public Func<string, Task<bool>>? ConfirmDialog { get; set; }

    public ObservableCollection<string> Databases { get; } = new();
    public ObservableCollection<string> Tables { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private string? _selectedDatabase;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateStatisticsCommand))]
    private string? _selectedTable;

    [ObservableProperty]
    private string _aiResponse = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Starting...";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteSqlCommand))]
    private bool _isBusy;

    private string _apiKey = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteSqlCommand))]
    private bool _hasSqlBlocks;

    public ObservableCollection<QueryStoreResult> QueryStoreResults { get; } = new();
    public ObservableCollection<BenchmarkStats>   BeforeStats        { get; } = new();
    public ObservableCollection<BenchmarkStats>   AfterStats         { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunBeforeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunAfterCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateStatisticsCommand))]
    private QueryStoreResult? _selectedQueryStoreResult;

    public MainWindowViewModel()
    {
        LoadConfig();
        _ = LoadDatabasesAsync();
    }

    partial void OnSelectedDatabaseChanged(string? value)
    {
        Tables.Clear();
        SelectedTable = null;
        if (value != null)
            _ = LoadTablesAsync(value);
    }

    private async Task LoadDatabasesAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = "Connecting to SQL Server localhost...";
            var dbs = await _sql.GetDatabasesAsync();
            Databases.Clear();
            foreach (var db in dbs) Databases.Add(db);
            StatusMessage = dbs.Count > 0
                ? $"Found {dbs.Count} StackOverflow database(s). Select one to continue."
                : "No StackOverflow* databases found on localhost.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connection error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadTablesAsync(string dbName)
    {
        try
        {
            IsBusy = true;
            StatusMessage = $"Loading tables from [{dbName}]...";
            var tables = await _sql.GetTablesAsync(dbName);
            Tables.Clear();
            foreach (var t in tables) Tables.Add(t);
            StatusMessage = $"Found {tables.Count} table(s). Select one and click Analyze.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading tables: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAnalyze() =>
        SelectedDatabase != null &&
        SelectedTable != null &&
        !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            StatusMessage = "Error: Claude API key not found. Set ANTHROPIC_API_KEY3 or add it to output\\config.json.";
            return;
        }

        IsBusy = true;
        AiResponse = string.Empty;
        HasSqlBlocks = false;
        try
        {
            StatusMessage = $"Running sp_BlitzIndex on [{SelectedDatabase}].[dbo].[{SelectedTable}]...";
            var prompt = await _sql.RunBlitzIndexAsync(SelectedDatabase!, SelectedTable!);

            StatusMessage = "Sending prompt to Claude...";
            var response = await _claude.SendPromptAsync(prompt, _apiKey);

            AiResponse = response;
            HasSqlBlocks = Regex.IsMatch(response, @"```sql", RegexOptions.IgnoreCase);

            StatusMessage = "Querying Query Store...";
            QueryStoreResults.Clear();
            var qsResults = await _sql.GetQueryStoreResultsAsync(SelectedDatabase!, SelectedTable!);
            foreach (var r in qsResults) QueryStoreResults.Add(r);

            StatusMessage = HasSqlBlocks
                ? $"Analysis complete. SQL scripts ready — click 'Execute SQL Script' to apply. ({qsResults.Count} Query Store result(s) found.)"
                : $"Analysis complete. No SQL scripts found. ({qsResults.Count} Query Store result(s) found.)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExecuteSql() =>
        HasSqlBlocks && !IsBusy && SelectedDatabase != null;

    [RelayCommand(CanExecute = nameof(CanExecuteSql))]
    private async Task ExecuteSqlAsync()
    {
        var blocks = ExtractSqlBlocks(AiResponse);
        if (blocks.Count == 0)
        {
            StatusMessage = "No SQL code blocks found in the response.";
            return;
        }

        if (ConfirmDialog != null)
        {
            var confirmed = await ConfirmDialog(
                $"Apply {blocks.Count} SQL script(s) to '{SelectedDatabase}'?\n\n" +
                "This will create or drop indexes on your database.");
            if (!confirmed) return;
        }

        IsBusy = true;
        try
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                StatusMessage = $"Executing script {i + 1} of {blocks.Count}...";
                await _sql.ExecuteScriptAsync(SelectedDatabase!, blocks[i]);
            }
            StatusMessage = $"Done! {blocks.Count} script(s) applied successfully to [{SelectedDatabase}].";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Execution error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRunBenchmark() => SelectedQueryStoreResult != null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunBenchmark))]
    private async Task RunBeforeAsync()
    {
        IsBusy = true;
        BeforeStats.Clear();
        try
        {
            StatusMessage = "Running BEFORE benchmark...";
            var stats = await _sql.RunWithStatisticsAsync(SelectedDatabase!, SelectedQueryStoreResult!.QueryText);
            foreach (var s in stats) BeforeStats.Add(s);
            StatusMessage = $"BEFORE complete — {BeforeStats.Count - 1} table(s) captured.";
        }
        catch (Exception ex) { StatusMessage = $"BEFORE error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRunBenchmark))]
    private async Task RunAfterAsync()
    {
        IsBusy = true;
        AfterStats.Clear();
        try
        {
            StatusMessage = "Running AFTER benchmark...";
            var stats = await _sql.RunWithStatisticsAsync(SelectedDatabase!, SelectedQueryStoreResult!.QueryText);
            foreach (var s in stats) AfterStats.Add(s);
            StatusMessage = $"AFTER complete — {AfterStats.Count - 1} table(s) captured.";
        }
        catch (Exception ex) { StatusMessage = $"AFTER error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private bool CanUpdateStatistics() => SelectedTable != null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUpdateStatistics))]
    private async Task UpdateStatisticsAsync()
    {
        IsBusy = true;
        try
        {
            StatusMessage = $"Updating statistics on [{SelectedTable}]...";
            await _sql.UpdateStatisticsAsync(SelectedDatabase!, SelectedTable!);
            StatusMessage = "Statistics updated.";
        }
        catch (Exception ex) { StatusMessage = $"Update statistics error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private void LoadConfig()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                if (doc.RootElement.TryGetProperty("_apiKey", out var el))
                    _apiKey = el.GetString() ?? string.Empty;
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
            _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY3") ?? string.Empty;
    }

    private static List<string> ExtractSqlBlocks(string response)
    {
        var result = new List<string>();
        var matches = Regex.Matches(response, @"```sql\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var block = m.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(block))
                result.Add(block);
        }
        return result;
    }
}
