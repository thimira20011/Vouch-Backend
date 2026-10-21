using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Vouch.IntegrationTests.Infrastructure;

// Copies synthetic fixture rows into a newly created older schema. Never downgrade the
// current fixture to manufacture history once irreversible privacy migrations exist.
public static class PreviousSchemaData
{
    public static async Task CopyAsync(PostgresFixture source, Vouch.Infrastructure.Persistence.ApplicationDbContext destination, params string[] tables)
    {
        var target = new NpgsqlConnectionStringBuilder(destination.Database.GetConnectionString());
        if (target.Host != "127.0.0.1" || target.Username != "vouch_test_runner" || target.Database is null ||
            !target.Database.StartsWith("vouch_test_", StringComparison.Ordinal) || target.Database == source.Database.Name)
            throw new InvalidOperationException("Previous-schema data copies require a separate generated loopback test database.");
        await using var read = new NpgsqlConnection(source.Database.ConnectionString); await read.OpenAsync();
        await using var write = new NpgsqlConnection(target.ConnectionString); await write.OpenAsync();
        foreach (var table in tables)
        {
            if (table is not ("Campuses" or "Users" or "Reports" or "ProtectionStates")) throw new ArgumentException("Unsupported fixture table.");
            var names = new List<string>();
            await using (var columns = new NpgsqlCommand("SELECT column_name FROM information_schema.columns WHERE table_schema='public' AND table_name=@table ORDER BY ordinal_position", write))
            {
                columns.Parameters.AddWithValue("table", table); await using var reader = await columns.ExecuteReaderAsync();
                while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            }
            if (names.Count == 0) throw new InvalidOperationException("Previous-schema fixture table is missing.");
            static string Quote(string value) => '"' + value.Replace("\"", "\"\"") + '"';
            var projection = string.Join(",", names.Select(Quote));
            await using var select = new NpgsqlCommand($"SELECT {projection} FROM {Quote(table)}", read);
            await using var rows = await select.ExecuteReaderAsync();
            await using var copy = await write.BeginBinaryImportAsync($"COPY {Quote(table)} ({projection}) FROM STDIN (FORMAT BINARY)");
            while (await rows.ReadAsync())
            {
                await copy.StartRowAsync();
                for (var i = 0; i < rows.FieldCount; i++)
                {
                    if (rows.IsDBNull(i)) await copy.WriteNullAsync();
                    else await copy.WriteAsync(rows.GetValue(i), rows.GetPostgresType(i).Name);
                }
            }
            await copy.CompleteAsync();
        }
    }
}
