using System.Data;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vouch.Application.Common.Interfaces;
using Vouch.Infrastructure.Security;

namespace Vouch.Infrastructure.Persistence;

public sealed record UpgradeIssue(string Table, Guid RowId, string Column, string Problem);
public sealed record UpgradeReport(int RowsInspected, int RowsToRewrite, IReadOnlyList<UpgradeIssue> Issues, IReadOnlyList<Guid[]> EmailCollisions)
{
    public bool CanApply => Issues.Count == 0 && EmailCollisions.Count == 0;
}

public sealed class ProtectedDataUpgrade(ApplicationDbContext db, AesEncryptionService encryption, IEmailLookup lookup)
{
    private sealed record Change(string Table, Guid Id, Dictionary<string, string?> Values);
    private sealed record Plan(UpgradeReport Report, List<Change> Changes);

    public async Task<UpgradeReport> RunAsync(bool apply = false, bool rotateLookupKey = false, CancellationToken ct = default)
    {
        await db.Database.OpenConnectionAsync(ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (!apply)
        {
            await using var read = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, read)) await readOnly.ExecuteNonQueryAsync(ct);
            var report = (await InspectAsync(connection, read, rotateLookupKey, ct)).Report;
            await read.RollbackAsync(ct);
            return report;
        }

        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var tableNames = ProtectedColumns.Strings.Concat(ProtectedColumns.Json).Select(c => c.Table).Distinct().ToArray();
        await using (var gate = new NpgsqlCommand("LOCK TABLE " + string.Join(", ", tableNames.Select(t => $"\"{t}\"")) + " IN ACCESS EXCLUSIVE MODE", connection, transaction))
            await gate.ExecuteNonQueryAsync(ct);
        var plan = await InspectAsync(connection, transaction, rotateLookupKey, ct);
        if (!plan.Report.CanApply) throw new InvalidOperationException("Protected-data upgrade has collisions or unreadable values. Run inspect and resolve the reported row IDs first.");
        foreach (var change in plan.Changes)
        {
            var assignments = string.Join(", ", change.Values.Keys.Select((name, i) => $"\"{name}\" = @p{i}"));
            await using var update = new NpgsqlCommand($"UPDATE \"{change.Table}\" SET {assignments} WHERE \"Id\" = @id", connection, transaction);
            update.Parameters.AddWithValue("id", change.Id);
            var index = 0;
            foreach (var value in change.Values.Values)
                update.Parameters.AddWithValue("p" + index++, NpgsqlTypes.NpgsqlDbType.Text, (object?)value ?? DBNull.Value);
            await update.ExecuteNonQueryAsync(ct);
        }
        await using var marker = new NpgsqlCommand("""
            INSERT INTO "ProtectionStates" ("Id", "LookupKeyCheck", "EncryptionCheck", "CompletedAt") VALUES (1, @lookup, @check, @now)
            ON CONFLICT ("Id") DO UPDATE SET "LookupKeyCheck" = EXCLUDED."LookupKeyCheck", "EncryptionCheck" = EXCLUDED."EncryptionCheck", "CompletedAt" = EXCLUDED."CompletedAt"
            """, connection, transaction);
        marker.Parameters.AddWithValue("lookup", lookup.Hash(DatabaseReadinessCheck.LookupCheckEmail));
        marker.Parameters.AddWithValue("check", encryption.Encrypt(DatabaseReadinessCheck.EncryptionCheckValue, "ProtectionStates.EncryptionCheck"));
        marker.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        await marker.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return plan.Report;
    }

    private async Task<Plan> InspectAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, bool rotateLookupKey, CancellationToken ct)
    {
        var issues = new List<UpgradeIssue>();
        var changes = new List<Change>();
        var emails = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        var rows = 0;
        var alreadyProtected = false;
        await using (var markerExists = new NpgsqlCommand("SELECT to_regclass('public.\"ProtectionStates\"') IS NOT NULL", connection, transaction))
        {
            if ((bool)(await markerExists.ExecuteScalarAsync(ct))!)
            {
                await using var marker = new NpgsqlCommand("SELECT \"LookupKeyCheck\", \"EncryptionCheck\" FROM \"ProtectionStates\" WHERE \"Id\" = 1", connection, transaction);
                await using var reader = await marker.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    alreadyProtected = true;
                    if (reader.GetString(0) != lookup.Hash(DatabaseReadinessCheck.LookupCheckEmail) && !rotateLookupKey)
                        issues.Add(new("ProtectionStates", Guid.Empty, "LookupKeyCheck", "Lookup key mismatch; explicit rotation is required."));
                    try
                    {
                        if (encryption.Decrypt(reader.GetString(1), "ProtectionStates.EncryptionCheck") != DatabaseReadinessCheck.EncryptionCheckValue)
                            throw new ProtectedDataException();
                    }
                    catch (ProtectedDataException)
                    {
                        issues.Add(new("ProtectionStates", Guid.Empty, "EncryptionCheck", "Existing key ring/canary cannot be authenticated. Restore the retained key before retrying."));
                    }
                }
            }
        }
        foreach (var group in ProtectedColumns.Strings.Concat(ProtectedColumns.Json).GroupBy(c => c.Table))
        {
            await using var exists = new NpgsqlCommand("SELECT to_regclass(@table) IS NOT NULL", connection, transaction);
            exists.Parameters.AddWithValue("table", "public.\"" + group.Key + "\"");
            if (!(bool)(await exists.ExecuteScalarAsync(ct))!) continue; // Empty installs have no application tables yet.
            await using var select = new NpgsqlCommand($"SELECT * FROM \"{group.Key}\" ORDER BY \"Id\"", connection, transaction);
            await using var reader = await select.ExecuteReaderAsync(ct);
            var fields = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToHashSet();
            while (await reader.ReadAsync(ct))
            {
                rows++;
                var id = reader.GetGuid(reader.GetOrdinal("Id"));
                var values = new Dictionary<string, string?>();
                var emailRaw = group.Key == "Users" ? reader.GetString(reader.GetOrdinal("Email")) : null;
                // Older registration encrypted Email/FullName/Bio; the old admin seed stored all three as plaintext.
                var legacyCipherIdentity = emailRaw is not null && !emailRaw.StartsWith("vouch:", StringComparison.Ordinal) && !emailRaw.Contains('@');
                foreach (var column in group)
                {
                    try
                    {
                        var ordinal = reader.GetOrdinal(column.Column);
                        if (reader.IsDBNull(ordinal)) continue;
                        var raw = reader.GetString(ordinal);
                        if (alreadyProtected && raw.Length > 0 && !raw.StartsWith("vouch:", StringComparison.Ordinal))
                            throw new ProtectedDataException(); // A completed conversion must never silently adopt tampered plaintext.
                        var plain = raw.StartsWith("vouch:", StringComparison.Ordinal) ? encryption.Decrypt(raw, column.Purpose)
                            : column.LegacyEncrypted && legacyCipherIdentity && raw.Length > 0 ? encryption.DecryptLegacy(raw) : raw;
                        if (column.Limit > 0 && plain.Length > column.Limit) throw new ArgumentException();
                        if (ProtectedColumns.Json.Contains(column))
                        {
                            if (column.Column == "DeepValues")
                                _ = JsonSerializer.Deserialize<List<string>>(plain) ?? throw new ArgumentException();
                            else _ = JsonSerializer.Deserialize<List<Vouch.Domain.Enums.IntellectualInterest>>(plain) ?? throw new ArgumentException();
                        }
                        if (column.Column is "Email" or "IntendedEmail")
                        {
                            plain = lookup.Normalize(plain);
                            if (!MailAddress.TryCreate(plain, out var address) || address.Address != plain) throw new ArgumentException();
                            var hash = lookup.Hash(plain);
                            var hashColumn = column.Column == "Email" ? "EmailLookupHash" : "IntendedEmailLookupHash";
                            var current = fields.Contains(hashColumn) && !reader.IsDBNull(reader.GetOrdinal(hashColumn)) ? reader.GetString(reader.GetOrdinal(hashColumn)) : null;
                            if (!string.IsNullOrEmpty(current) && !current.StartsWith("pending:", StringComparison.Ordinal) && current != hash && !rotateLookupKey)
                                issues.Add(new(group.Key, id, hashColumn, "Lookup key mismatch; use the explicit lookup-key rotation operation after backup."));
                            if (current != hash) values[hashColumn] = hash;
                            if (column.Column == "Email")
                            {
                                if (!emails.TryGetValue(hash, out var ids)) emails[hash] = ids = [];
                                ids.Add(id);
                            }
                        }
                        var expectedHeader = $"vouch:v1:{encryption.ActiveKeyId}:";
                        if (!raw.StartsWith(expectedHeader, StringComparison.Ordinal) && raw.Length > 0)
                            values[column.Column] = encryption.Encrypt(plain, column.Purpose);
                    }
                    catch (Exception ex) when (ex is ProtectedDataException or ArgumentException or JsonException or IndexOutOfRangeException)
                    {
                        issues.Add(new(group.Key, id, column.Column, "Unreadable, invalid or over-limit value; verify the legacy key/format and recover from a trusted backup."));
                    }
                }
                if (values.Count > 0) changes.Add(new(group.Key, id, values));
            }
        }
        var collisions = emails.Values.Where(ids => ids.Count > 1).Select(ids => ids.ToArray()).ToArray();
        return new(new(rows, changes.Count, issues, collisions), changes);
    }
}
