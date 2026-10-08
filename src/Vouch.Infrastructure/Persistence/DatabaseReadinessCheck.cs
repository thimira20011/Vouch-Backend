using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Vouch.Application.Common.Interfaces;

namespace Vouch.Infrastructure.Persistence;

public sealed class DatabaseReadinessCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public const string LookupCheckEmail = "lookup-key-check@vouch.invalid";
    public const string EncryptionCheckValue = "vouch-protection-ready-v1";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await VerifyAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                scope.ServiceProvider.GetRequiredService<IEmailLookup>(), scope.ServiceProvider.GetRequiredService<IEncryptionService>(), cancellationToken);
            await scope.ServiceProvider.GetRequiredService<Vouch.Infrastructure.Security.CampusBoundary>()
                .VerifyDeploymentAsync(scope.ServiceProvider.GetRequiredService<IApplicationDbContext>(), cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never expose provider diagnostics, protected values or keys in the readiness response.
            return HealthCheckResult.Unhealthy("Database schema/protected-data upgrade or key configuration requires attention.");
        }
    }

    public static async Task VerifyAsync(ApplicationDbContext db, IEmailLookup lookup, IEncryptionService encryption, CancellationToken ct = default)
    {
        if ((await db.Database.GetPendingMigrationsAsync(ct)).Any()) throw new InvalidOperationException("Database migrations are pending.");
        var state = await db.ProtectionStates.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
        if (state is null || state.LookupKeyCheck != lookup.Hash(LookupCheckEmail) ||
            encryption.Decrypt(state.EncryptionCheck, "ProtectionStates.EncryptionCheck") != EncryptionCheckValue)
            throw new InvalidOperationException("Protected-data conversion is missing or lookup/encryption keys differ from the database.");
        _ = await db.Users.AsNoTracking().Select(u => u.Email).FirstOrDefaultAsync(ct);
        if (await db.Users.AnyAsync(u => u.EmailLookupHash.Length != 64, ct))
            throw new InvalidOperationException("Email lookup backfill is incomplete.");
        await db.Database.OpenConnectionAsync(ct);
        await using var revocation = new Npgsql.NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM pg_trigger t JOIN pg_proc p ON p.oid = t.tgfoid
                JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE t.tgrelid = 'public."Users"'::regclass AND t.tgname = 'vouch_revoke_changed_sessions'
                AND NOT t.tgisinternal AND t.tgenabled IN ('O', 'A')
                AND n.nspname = 'public' AND p.proname = 'vouch_revoke_changed_sessions')
            """, (Npgsql.NpgsqlConnection)db.Database.GetDbConnection());
        if (!(bool)(await revocation.ExecuteScalarAsync(ct))!)
            throw new InvalidOperationException("The account session revocation trigger is missing or disabled.");
    }
}
