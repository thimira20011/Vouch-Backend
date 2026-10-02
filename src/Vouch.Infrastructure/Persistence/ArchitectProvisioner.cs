using System.Text;
using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common.Interfaces;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;

namespace Vouch.Infrastructure.Persistence;

public sealed class ArchitectProvisioner(ApplicationDbContext db, IEmailLookup lookup, IEncryptionService encryption, IPasswordHasher hasher)
{
    public async Task<Guid> ProvisionAsync(string email, string password, string fullName, string campusCode, bool resetExistingPassword = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12 || Encoding.UTF8.GetByteCount(password) > 72)
            throw new ArgumentException("Bootstrap password must be 12–72 UTF-8 bytes and contain at least 12 characters.");
        if (string.IsNullOrWhiteSpace(fullName) || fullName.Trim().Length > 120) throw new ArgumentException("Bootstrap full name must contain 1–120 characters.");
        var normalized = lookup.Normalize(email);
        if (!System.Net.Mail.MailAddress.TryCreate(normalized, out var address) || address.Address != normalized || normalized.Length > 120)
            throw new ArgumentException("Bootstrap email must be a valid address of at most 120 characters.");
        await DatabaseReadinessCheck.VerifyAsync(db, lookup, encryption, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(72672604)", ct);
        var hash = lookup.Hash(normalized);
        var existing = await db.Users.SingleOrDefaultAsync(u => u.Role == UserRole.Architect, ct);
        if (existing is not null)
        {
            if (existing.EmailLookupHash != hash || existing.Status != AccountStatus.Active)
                throw new InvalidOperationException("An Architect already exists. Bootstrap does not change credentials, promote accounts or reactivate users.");
            if (!hasher.VerifyPassword(password, existing.PasswordHash))
            {
                if (!resetExistingPassword) throw new InvalidOperationException("An Architect already exists with another password. Explicit --reset-admin-password true is required to rotate that password.");
                existing.PasswordHash = hasher.HashPassword(password);
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return existing.Id;
        }
        if (await db.Users.AnyAsync(u => u.EmailLookupHash == hash, ct)) throw new InvalidOperationException("The bootstrap email belongs to an existing non-Architect account. No account was promoted.");
        var campus = await db.Campuses.SingleOrDefaultAsync(c => c.Code == campusCode, ct) ?? throw new ArgumentException("Bootstrap campus code was not found. Seed reference data first.");
        var user = new User
        {
            CampusId = campus.Id, Email = normalized, FullName = fullName.Trim(), PasswordHash = hasher.HashPassword(password),
            Faculty = "Administration", Department = "Trust Systems", AcademicYear = 1,
            Role = UserRole.Architect, Status = AccountStatus.Active
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return user.Id;
    }
}
