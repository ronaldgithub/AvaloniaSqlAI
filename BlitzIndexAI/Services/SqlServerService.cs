using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace BlitzIndexAI.Services;

public class SqlServerService
{
    private const string ConnectionString =
        "Data Source=localhost;Integrated Security=True;TrustServerCertificate=True;";

    private static readonly string OutputPath =
        Path.Combine(@"C:\Projecten\Claude\AvaloniaAI\output", "ai_prompt.txt");

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

    private static string EscapeName(string name) => name.Replace("]", "]]");
}
