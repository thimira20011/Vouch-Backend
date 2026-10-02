using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Vouch.Infrastructure.Persistence;

public sealed class LegacySchemaBaseline(ApplicationDbContext db)
{
    public const string Initial = "20260917123615_AddAmbassadorInvites";
    public const string BeforeProtection = "20260927150805_Step19_MessageType";

    public async Task<string[]> IdentifyAsync(CancellationToken ct = default)
    {
        await db.Database.OpenConnectionAsync(ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var settings = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        if ((!string.IsNullOrEmpty(settings.SearchPath) && settings.SearchPath != "public") || !string.IsNullOrEmpty(settings.Options))
            throw new InvalidOperationException("Database operations support the public schema only. Custom Search Path/server Options require a separately reviewed upgrade path.");
        await using (var schema = new NpgsqlCommand("SELECT current_schema()", connection))
            if ((string?)await schema.ExecuteScalarAsync(ct) != "public")
                throw new InvalidOperationException("The active schema is not public. No baseline or upgrade was applied.");
        await using (var history = new NpgsqlCommand("""
            SELECT CASE WHEN to_regclass('public."__EFMigrationsHistory"') IS NULL THEN true ELSE
                (SELECT count(*) = 2 AND bool_and(
                    column_name = 'MigrationId' AND data_type = 'character varying' AND character_maximum_length = 150 AND is_nullable = 'NO'
                    OR column_name = 'ProductVersion' AND data_type = 'character varying' AND character_maximum_length = 32 AND is_nullable = 'NO')
                 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory')
                AND (SELECT count(*) = 1 AND bool_and(pg_get_constraintdef(oid) = 'PRIMARY KEY ("MigrationId")')
                     FROM pg_constraint WHERE conrelid = to_regclass('public."__EFMigrationsHistory"'))
            END
            """, connection))
            if (!(bool)(await history.ExecuteScalarAsync(ct))!)
                throw new InvalidOperationException("Migration history structure is not recognized. No baseline was adopted.");
        var tables = new List<string>();
        await using (var query = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory' ORDER BY tablename", connection))
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        if (applied.Length > 0)
        {
            var known = db.Database.GetMigrations().ToArray();
            if (!applied.SequenceEqual(known.Take(applied.Length))) throw new InvalidOperationException("Migration history is not a recognized prefix. Do not guess a baseline.");
            if (!await MatchesAsync(applied[^1], tables, connection, ct)) throw new InvalidOperationException("Schema differs from its recorded migration. Restore or review schema drift before upgrading.");
            return applied;
        }
        if (tables.Count == 0) return [];
        if (await MatchesAsync(BeforeProtection, tables, connection, ct)) return [Initial, BeforeProtection];
        if (await MatchesAsync(Initial, tables, connection, ct)) return [Initial];
        throw new InvalidOperationException("Untracked schema is not a recognized EnsureCreated schema. No baseline or data changes were made.");
    }

    private async Task<bool> MatchesAsync(string migrationId, List<string> actualTables, NpgsqlConnection connection, CancellationToken ct)
    {
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations[migrationId], db.Database.ProviderName!);
        var model = db.GetService<IModelRuntimeInitializer>().Initialize(migration.TargetModel, designTime: true);
        var expected = model.GetRelationalModel().Tables.OrderBy(t => t.Name).ToArray();
        if (!actualTables.SequenceEqual(expected.Select(t => t.Name))) return false;
        foreach (var table in expected)
        {
            var columns = new Dictionary<string, (string Type, bool Nullable)>();
            await using (var query = new NpgsqlCommand("""
                SELECT a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull, pg_get_expr(d.adbin, d.adrelid)
                FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
                WHERE n.nspname = 'public' AND c.relname = @table AND a.attnum > 0 AND NOT a.attisdropped
                """, connection))
            {
                query.Parameters.AddWithValue("table", table.Name);
                await using var reader = await query.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var name = reader.GetString(0);
                    columns[name] = (reader.GetString(1), reader.GetBoolean(2));
                    if (!reader.IsDBNull(3))
                    {
                        var defaultSql = reader.GetString(3);
                        var recognized = table.Name == "Messages" && name == "Type" && defaultSql == "0" ||
                            table.Name == "Users" && name == "EmailLookupHash" && defaultSql == "''::character varying";
                        if (!recognized) return false;
                    }
                }
            }
            if (columns.Count != table.Columns.Count()) return false;
            foreach (var column in table.Columns)
                if (!columns.TryGetValue(column.Name, out var actual) || actual != (column.StoreType, column.IsNullable)) return false;

            var indexes = new Dictionary<string, (bool Unique, string Columns)>();
            await using (var count = new NpgsqlCommand("""
                SELECT count(*) FROM pg_index i JOIN pg_class t ON t.oid = i.indrelid JOIN pg_namespace n ON n.oid = t.relnamespace
                WHERE n.nspname = 'public' AND t.relname = @table AND NOT i.indisprimary
                """, connection))
            {
                count.Parameters.AddWithValue("table", table.Name);
                if ((long)(await count.ExecuteScalarAsync(ct))! != table.Indexes.Count()) return false;
            }
            await using (var query = new NpgsqlCommand("""
                SELECT ci.relname, i.indisunique, string_agg(a.attname, ',' ORDER BY k.ordinality)
                FROM pg_index i JOIN pg_class t ON t.oid = i.indrelid JOIN pg_namespace n ON n.oid = t.relnamespace
                JOIN pg_class ci ON ci.oid = i.indexrelid
                JOIN LATERAL unnest(i.indkey) WITH ORDINALITY k(attnum, ordinality) ON true
                JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
                WHERE n.nspname = 'public' AND t.relname = @table AND NOT i.indisprimary
                    AND i.indpred IS NULL AND i.indexprs IS NULL AND i.indisvalid
                GROUP BY ci.relname, i.indisunique
                """, connection))
            {
                query.Parameters.AddWithValue("table", table.Name);
                await using var reader = await query.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) indexes[reader.GetString(0)] = (reader.GetBoolean(1), reader.GetString(2));
            }
            if (indexes.Count != table.Indexes.Count()) return false;
            foreach (var index in table.Indexes)
                if (!indexes.TryGetValue(index.Name, out var actual) || actual != (index.IsUnique, string.Join(',', index.Columns.Select(c => c.Name)))) return false;

            var constraints = new Dictionary<string, string>();
            await using (var query = new NpgsqlCommand("""
                SELECT con.conname, pg_get_constraintdef(con.oid)
                FROM pg_constraint con JOIN pg_class t ON t.oid = con.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace
                WHERE n.nspname = 'public' AND t.relname = @table AND con.contype IN ('p', 'f', 'u', 'c')
                """, connection))
            {
                query.Parameters.AddWithValue("table", table.Name);
                await using var reader = await query.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) constraints[reader.GetString(0)] = reader.GetString(1);
            }
            if (constraints.Count != table.ForeignKeyConstraints.Count() + table.UniqueConstraints.Count()) return false;
            static string Quote(string value) => '"' + value + '"';
            foreach (var key in table.UniqueConstraints)
            {
                var definition = (key == table.PrimaryKey ? "PRIMARY KEY" : "UNIQUE") + " (" + string.Join(", ", key.Columns.Select(c => Quote(c.Name))) + ")";
                if (!constraints.TryGetValue(key.Name, out var actual) || actual != definition) return false;
            }
            foreach (var foreign in table.ForeignKeyConstraints)
            {
                var definition = "FOREIGN KEY (" + string.Join(", ", foreign.Columns.Select(c => Quote(c.Name))) + ") REFERENCES " + Quote(foreign.PrincipalTable.Name)
                    + "(" + string.Join(", ", foreign.PrincipalColumns.Select(c => Quote(c.Name))) + ")";
                if (foreign.OnDeleteAction != ReferentialAction.NoAction) definition += " ON DELETE " + foreign.OnDeleteAction.ToString().ToUpperInvariant();
                if (!constraints.TryGetValue(foreign.Name, out var actual) || actual != definition) return false;
            }
        }
        return true;
    }

    public async Task AdoptAsync(string[] identified, CancellationToken ct = default)
    {
        if (identified.Length == 0 || (await db.Database.GetAppliedMigrationsAsync(ct)).Any()) return;
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var create = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" ("MigrationId" character varying(150) NOT NULL, "ProductVersion" character varying(32) NOT NULL, CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId"))
            """, connection, transaction);
        await create.ExecuteNonQueryAsync(ct);
        foreach (var id in identified)
        {
            await using var insert = new NpgsqlCommand("INSERT INTO \"__EFMigrationsHistory\" VALUES (@id, '10.0.11')", connection, transaction);
            insert.Parameters.AddWithValue("id", id);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
}
