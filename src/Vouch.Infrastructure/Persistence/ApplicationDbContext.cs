using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Vouch.Application.Common.Interfaces;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;

namespace Vouch.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext, IApplicationDbContext
{
    internal IEncryptionService Encryption { get; }
    private readonly IEmailLookup _emailLookup;

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IEncryptionService encryption, IEmailLookup emailLookup)
        : base(options)
    {
        Encryption = encryption;
        _emailLookup = emailLookup;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.ReplaceService<IModelCacheKeyFactory, ProtectedModelCacheKeyFactory>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
        => SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareProtectedData();
        var campuses = ChangeTracker.Entries<User>().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .SelectMany(e => new[] { e.Entity.CampusId, e.Property(u => u.CampusId).OriginalValue }).ToHashSet();
        var changedVouches = ChangeTracker.Entries<VouchRecord>().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        var voucherIds = changedVouches.SelectMany(e => new[] { e.Entity.VoucherUserId, e.Property(v => v.VoucherUserId).OriginalValue }).ToArray();
        if (voucherIds.Length > 0)
            foreach (var id in await Users.Where(u => voucherIds.Contains(u.Id)).Select(u => u.CampusId).Distinct().ToListAsync(cancellationToken)) campuses.Add(id);
        if (campuses.Count == 0) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await using var transaction = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        // Serialize eligibility-affecting writes per campus so concurrent recalculations cannot leave an old gate value.
        foreach (var campusId in campuses.Order())
            await Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Campuses\" WHERE \"Id\" = {campusId} FOR UPDATE", cancellationToken);
        var count = await base.SaveChangesAsync(false, cancellationToken);
        foreach (var campusId in campuses)
        {
            var campus = await Campuses.AsNoTracking().SingleOrDefaultAsync(c => c.Id == campusId, cancellationToken);
            if (campus is null) continue;
            var readiness = await LaunchEligibility.CalculateAsync(this, campus.Id, campus.RequiredAmbassadorsForLaunch,
                campus.RequiredVouchesPerAmbassador, cancellationToken);
            await Campuses.Where(c => c.Id == campusId).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.LaunchReadinessScore, readiness.LaunchReadinessPercentage)
                .SetProperty(c => c.IsSoftLaunchUnlocked, readiness.IsGatePassed), cancellationToken);
            var tracked = ChangeTracker.Entries<Campus>().SingleOrDefault(e => e.Entity.Id == campusId);
            if (tracked is not null)
            {
                tracked.Entity.LaunchReadinessScore = readiness.LaunchReadinessPercentage;
                tracked.Entity.IsSoftLaunchUnlocked = readiness.IsGatePassed;
            }
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        if (acceptAllChangesOnSuccess) ChangeTracker.AcceptAllChanges();
        return count;
    }

    private void PrepareProtectedData()
    {
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.Entity is User user)
            {
                user.Email = _emailLookup.Normalize(user.Email);
                user.EmailLookupHash = _emailLookup.Hash(user.Email);
            }
            if (entry.Entity is AmbassadorInvite invite)
            {
                if (invite.IntendedEmail is not null) invite.IntendedEmail = _emailLookup.Normalize(invite.IntendedEmail);
                invite.IntendedEmailLookupHash = invite.IntendedEmail is null ? null : _emailLookup.Hash(invite.IntendedEmail);
            }
            foreach (var column in ProtectedColumns.Strings.Where(c => c.Limit > 0 && c.Table == entry.Metadata.GetTableName()))
                if (entry.Property(column.Column).CurrentValue is string value && value.Length > column.Limit)
                    throw new ArgumentException($"{column.Purpose} must not exceed {column.Limit} characters.");
        }
    }

    public DbSet<Campus> Campuses => Set<Campus>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AmbassadorInvite> AmbassadorInvites => Set<AmbassadorInvite>();
    public DbSet<EmailVerification> EmailVerifications => Set<EmailVerification>();
    public DbSet<VouchRecord> Vouches => Set<VouchRecord>();
    public DbSet<DailyMatch> Matches => Set<DailyMatch>();
    public DbSet<DailyReflection> Reflections => Set<DailyReflection>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<Block> Blocks => Set<Block>();
    public DbSet<IcebreakerPrompt> Icebreakers => Set<IcebreakerPrompt>();
    public DbSet<ProtectionState> ProtectionStates => Set<ProtectionState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ProtectionState>(builder =>
        {
            builder.HasKey(s => s.Id);
            builder.Property(s => s.LookupKeyCheck).HasMaxLength(64);
            builder.Property(s => s.EncryptionCheck).HasColumnType("text");
        });

        // Value Comparers for Collections
        var stringListComparer = new ValueComparer<List<string>>(
            (c1, c2) => c1 != null && c2 != null && c1.SequenceEqual(c2),
            c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
            c => c.ToList());

        var interestsComparer = new ValueComparer<List<IntellectualInterest>>(
            (c1, c2) => c1 != null && c2 != null && c1.SequenceEqual(c2),
            c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
            c => c.ToList());

        // Campus Configuration
        modelBuilder.Entity<Campus>(builder =>
        {
            builder.HasKey(c => c.Id);
            builder.HasIndex(c => c.Code).IsUnique();
            builder.Property(c => c.Name).HasMaxLength(150);
            builder.Property(c => c.Code).HasMaxLength(20);
            builder.Property(c => c.DomainPattern).HasMaxLength(100);
        });

        // User Configuration
        modelBuilder.Entity<User>(builder =>
        {
            builder.HasKey(u => u.Id);
            builder.HasIndex(u => u.EmailLookupHash).IsUnique();
            builder.Property(u => u.EmailLookupHash).HasMaxLength(64).IsRequired();
            builder.HasIndex(u => new { u.CampusId, u.Status });

            builder.Property(u => u.Email).HasMaxLength(120);
            builder.Property(u => u.FullName).HasMaxLength(120);
            builder.Property(u => u.Bio).HasMaxLength(280);
            builder.Property(u => u.Faculty).HasMaxLength(100);
            builder.Property(u => u.Department).HasMaxLength(100);

            builder.Property(u => u.DeepValues)
                .HasConversion(
                    v => Encryption.Encrypt(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), "Users.DeepValues"),
                    v => JsonSerializer.Deserialize<List<string>>(Encryption.Decrypt(v, "Users.DeepValues"), (JsonSerializerOptions?)null) ?? new List<string>())
                .Metadata.SetValueComparer(stringListComparer);

            builder.Property(u => u.IntellectualInterests)
                .HasConversion(
                    v => Encryption.Encrypt(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), "Users.IntellectualInterests"),
                    v => JsonSerializer.Deserialize<List<IntellectualInterest>>(Encryption.Decrypt(v, "Users.IntellectualInterests"), (JsonSerializerOptions?)null) ?? new List<IntellectualInterest>())
                .Metadata.SetValueComparer(interestsComparer);

            builder.HasOne(u => u.Campus)
                .WithMany(c => c.Users)
                .HasForeignKey(u => u.CampusId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // VouchRecord Configuration (REQ-6: Unique pair Voucher -> Target)
        modelBuilder.Entity<VouchRecord>(builder =>
        {
            builder.HasKey(v => v.Id);
            builder.HasIndex(v => new { v.TargetUserId, v.VoucherUserId }).IsUnique();

            builder.HasOne(v => v.TargetUser)
                .WithMany(u => u.VouchesReceived)
                .HasForeignKey(v => v.TargetUserId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(v => v.VoucherUser)
                .WithMany(u => u.VouchesGiven)
                .HasForeignKey(v => v.VoucherUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // DailyMatch Configuration
        modelBuilder.Entity<DailyMatch>(builder =>
        {
            builder.HasKey(m => m.Id);
            builder.HasIndex(m => new { m.CampusId, m.CycleDate });
            builder.HasIndex(m => new { m.UserAId, m.CycleDate });
            builder.HasIndex(m => new { m.UserBId, m.CycleDate });

            builder.HasOne(m => m.UserA)
                .WithMany()
                .HasForeignKey(m => m.UserAId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(m => m.UserB)
                .WithMany()
                .HasForeignKey(m => m.UserBId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Conversation Configuration
        modelBuilder.Entity<Conversation>(builder =>
        {
            builder.HasKey(c => c.Id);
            builder.HasOne(c => c.UserA)
                .WithMany()
                .HasForeignKey(c => c.UserAId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(c => c.UserB)
                .WithMany()
                .HasForeignKey(c => c.UserBId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Message Configuration
        modelBuilder.Entity<Message>(builder =>
        {
            builder.HasKey(m => m.Id);
            builder.HasIndex(m => new { m.ConversationId, m.SentAt });

            // Step 19: Body max length matches FluentValidation rule (2000 chars)
            builder.Property(m => m.Body).HasMaxLength(2000).IsRequired();

            // Step 19: MessageType stored as int column; entity default = Text (1)
            builder.Property(m => m.Type).HasColumnName("Type");

            builder.HasOne(m => m.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(m => m.Sender)
                .WithMany()
                .HasForeignKey(m => m.SenderId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Report Configuration
        modelBuilder.Entity<Report>(builder =>
        {
            builder.HasKey(r => r.Id);
            builder.HasIndex(r => new { r.ReportedUserId, r.Status });

            builder.HasOne(r => r.Reporter)
                .WithMany(u => u.ReportsSubmitted)
                .HasForeignKey(r => r.ReporterId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(r => r.ReportedUser)
                .WithMany(u => u.ReportsAgainst)
                .HasForeignKey(r => r.ReportedUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Block Configuration
        modelBuilder.Entity<Block>(builder =>
        {
            builder.HasKey(b => new { b.BlockerId, b.BlockedUserId });

            builder.HasOne(b => b.Blocker)
                .WithMany(u => u.BlockedUsers)
                .HasForeignKey(b => b.BlockerId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(b => b.BlockedUser)
                .WithMany(u => u.BlockedByUsers)
                .HasForeignKey(b => b.BlockedUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // IcebreakerPrompt Configuration
        modelBuilder.Entity<IcebreakerPrompt>(builder =>
        {
            builder.HasKey(i => i.Id);
            builder.Property(i => i.PromptText).HasMaxLength(500);
            builder.Property(i => i.Tags)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
                .Metadata.SetValueComparer(stringListComparer);
        });

        // DailyReflection Configuration
        modelBuilder.Entity<DailyReflection>(builder =>
        {
            builder.HasKey(r => r.Id);
            builder.Property(r => r.Quote).HasMaxLength(500);
            builder.Property(r => r.Author).HasMaxLength(150);
            builder.Property(r => r.ThoughtProvokingQuestion).HasMaxLength(500);
        });

        // AmbassadorInvite Configuration (REQ-A1, Step 8)
        modelBuilder.Entity<AmbassadorInvite>(builder =>
        {
            builder.HasKey(i => i.Id);
            builder.HasIndex(i => i.TokenHash).IsUnique();
            builder.Property(i => i.TokenHash).HasMaxLength(64).IsRequired();
            builder.Property(i => i.IntendedEmail).HasMaxLength(120);
            builder.Property(i => i.IntendedEmailLookupHash).HasMaxLength(64);

            builder.HasOne(i => i.Campus)
                .WithMany()
                .HasForeignKey(i => i.CampusId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmailVerification>(builder =>
        {
            builder.HasKey(v => v.Id);
            builder.HasIndex(v => v.TokenHash).IsUnique();
            builder.HasIndex(v => v.UserId).IsUnique(); // one current challenge per user
            builder.Property(v => v.TokenHash).HasMaxLength(64).IsRequired();
            builder.Property(v => v.EmailLookupHash).HasMaxLength(64).IsRequired();
            builder.HasOne(v => v.User).WithMany().HasForeignKey(v => v.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        foreach (var column in ProtectedColumns.Strings)
        {
            var entity = modelBuilder.Model.GetEntityTypes().Single(e => e.GetTableName() == column.Table);
            var property = modelBuilder.Entity(entity.ClrType).Property(column.Column).HasColumnType("text")
                .HasConversion(new ValueConverter<string, string>(
                    value => Encryption.Encrypt(value, column.Purpose), value => Encryption.Decrypt(value, column.Purpose)));
            property.Metadata.SetMaxLength(null);
        }
    }
}
