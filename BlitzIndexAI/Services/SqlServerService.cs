using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using BlitzIndexAI.Models;
using Microsoft.Data.SqlClient;

namespace BlitzIndexAI.Services;

public class SqlServerService
{
    private const string ConnectionString =
        "Data Source=localhost;Integrated Security=True;TrustServerCertificate=True;";

    private static readonly string OutputPath =
        Path.Combine(AppContext.BaseDirectory, "output", "ai_prompt.txt");

    public async Task<List<string>> GetDatabasesAsync()
    {
        var result = new List<string>();
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT name FROM sys.databases WHERE name LIKE 'StackOverflow%' ORDER BY name", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }

    public async Task<List<string>> GetTablesAsync(string dbName)
    {
        var result = new List<string>();
        var sql = $"""
            SELECT TABLE_NAME
            FROM [{EscapeName(dbName)}].INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = 'dbo' AND TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_NAME
            """;
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }

    public async Task<string> RunBlitzIndexAsync(string dbName, string tableName)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        const string sql = """
            EXEC dbo.sp_BlitzIndex
                @DatabaseName = @db,
                @SchemaName   = N'dbo',
                @TableName    = @tbl,
                @AI           = 2
            """;

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("@db", dbName);
        cmd.Parameters.AddWithValue("@tbl", tableName);

        await using var reader = await cmd.ExecuteReaderAsync();

        string? prompt = null;
        do
        {
            if (reader.FieldCount > 0 && reader.GetName(0) == "AI Prompt")
            {
                if (await reader.ReadAsync() && !reader.IsDBNull(0))
                {
                    var sqlXml = reader.GetSqlXml(0);
                    var doc = XDocument.Load(sqlXml.CreateReader());
                    prompt = doc.Root?.Value;
                }
                break;
            }
        }
        while (await reader.NextResultAsync());

        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException(
                "sp_BlitzIndex returned no 'AI Prompt' result set. " +
                "Ensure the procedure is installed in master and @AI=2 is supported.");

        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
        await File.WriteAllTextAsync(OutputPath, prompt, Encoding.UTF8);
        return prompt;
    }

    public async Task<List<QueryStoreResult>> GetQueryStoreResultsAsync(string dbName, string tableName)
    {
        var result = new List<QueryStoreResult>();

        await using var checkConn = new SqlConnection(ConnectionString);
        await checkConn.OpenAsync();
        await using var checkCmd = new SqlCommand(
            "SELECT is_query_store_on FROM sys.databases WHERE name = @db", checkConn);
        checkCmd.Parameters.AddWithValue("@db", dbName);
        var isOn = (bool?)await checkCmd.ExecuteScalarAsync();
        if (isOn != true) return result;

        var sql = $"""
            SELECT TOP 20
                qt.query_sql_text,
                rs.count_executions,
                CAST(rs.avg_logical_io_reads AS BIGINT)         AS avg_logical_reads,
                CAST(rs.avg_duration / 1000.0 AS DECIMAL(10,2)) AS avg_duration_ms
            FROM [{EscapeName(dbName)}].sys.query_store_query_text qt
            JOIN [{EscapeName(dbName)}].sys.query_store_query        q  ON qt.query_text_id = q.query_text_id
            JOIN [{EscapeName(dbName)}].sys.query_store_plan          p  ON q.query_id       = p.query_id
            JOIN [{EscapeName(dbName)}].sys.query_store_runtime_stats rs ON p.plan_id        = rs.plan_id
            WHERE CAST(p.query_plan AS NVARCHAR(MAX)) LIKE N'%MissingIndex%'
              AND qt.query_sql_text LIKE N'%' + @tbl + N'%'
            ORDER BY rs.avg_logical_io_reads DESC;
            """;

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        cmd.Parameters.AddWithValue("@tbl", tableName);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new QueryStoreResult
            {
                QueryText       = reader.GetString(0),
                ExecutionCount  = reader.GetInt64(1),
                AvgLogicalReads = reader.GetInt64(2),
                AvgDurationMs   = (double)reader.GetDecimal(3)
            });
        }
        return result;
    }

    public async Task ExecuteScriptAsync(string dbName, string sql)
    {
        var csb = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = dbName };
        await using var conn = new SqlConnection(csb.ConnectionString);
        await conn.OpenAsync();

        var batches = Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        foreach (var batch in batches)
        {
            var trimmed = batch.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            await using var cmd = new SqlCommand(trimmed, conn) { CommandTimeout = 300 };
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task<List<BenchmarkStats>> RunWithStatisticsAsync(string dbName, string queryText)
    {
        var messages = new System.Collections.Concurrent.ConcurrentBag<string>();
        var csb = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = dbName };
        await using var conn = new SqlConnection(csb.ConnectionString);
        conn.InfoMessage += (_, e) => { foreach (SqlError err in e.Errors) messages.Add(err.Message); };
        await conn.OpenAsync();

        var sql = "SET STATISTICS TIME ON;\nSET STATISTICS IO ON;\n" + queryText;
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        await using var reader = await cmd.ExecuteReaderAsync();
        do { while (await reader.ReadAsync()) { } } while (await reader.NextResultAsync());

        return ParseStatisticsMessages(messages);
    }

    private static List<BenchmarkStats> ParseStatisticsMessages(IEnumerable<string> messages)
    {
        var result = new List<BenchmarkStats>();
        long cpuMs = 0, elapsedMs = 0;

        var ioPattern   = new Regex(@"Table '([^']+)'\.\s+Scan count \d+,\s+logical reads (\d+),\s+physical reads (\d+).*?read-ahead reads (\d+)", RegexOptions.IgnoreCase);
        var timePattern = new Regex(@"CPU time = (\d+) ms.*?elapsed time = (\d+) ms", RegexOptions.IgnoreCase);

        foreach (var msg in messages)
        {
            var ioMatch = ioPattern.Match(msg);
            if (ioMatch.Success)
            {
                result.Add(new BenchmarkStats
                {
                    Label          = ioMatch.Groups[1].Value,
                    LogicalReads   = long.Parse(ioMatch.Groups[2].Value),
                    PhysicalReads  = long.Parse(ioMatch.Groups[3].Value),
                    ReadAheadReads = long.Parse(ioMatch.Groups[4].Value)
                });
                continue;
            }
            var timeMatch = timePattern.Match(msg);
            if (timeMatch.Success)
            {
                cpuMs     = Math.Max(cpuMs,     long.Parse(timeMatch.Groups[1].Value));
                elapsedMs = Math.Max(elapsedMs, long.Parse(timeMatch.Groups[2].Value));
            }
        }

        result.Add(new BenchmarkStats
        {
            Label     = "Query Time",
            CpuMs     = cpuMs.ToString(),
            ElapsedMs = elapsedMs.ToString()
        });
        return result;
    }

    public async Task UpdateStatisticsAsync(string dbName, string tableName)
    {
        var sql = $"UPDATE STATISTICS [{EscapeName(dbName)}].[dbo].[{EscapeName(tableName)}];";
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync();
    }

    private static string EscapeName(string name) => name.Replace("]", "]]");
}
