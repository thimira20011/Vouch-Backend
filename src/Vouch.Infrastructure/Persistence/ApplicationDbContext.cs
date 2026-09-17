using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Vouch.Application.Common.Interfaces;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;

namespace Vouch.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext, IApplicationDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Campus> Campuses => Set<Campus>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AmbassadorInvite> AmbassadorInvites => Set<AmbassadorInvite>();
    public DbSet<VouchRecord> Vouches => Set<VouchRecord>();
    public DbSet<DailyMatch> Matches => Set<DailyMatch>();
    public DbSet<DailyReflection> Reflections => Set<DailyReflection>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<Block> Blocks => Set<Block>();
    public DbSet<IcebreakerPrompt> Icebreakers => Set<IcebreakerPrompt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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
            builder.HasIndex(u => u.Email).IsUnique();
            builder.HasIndex(u => new { u.CampusId, u.Status });

            builder.Property(u => u.Email).HasMaxLength(120);
            builder.Property(u => u.FullName).HasMaxLength(120);
            builder.Property(u => u.Bio).HasMaxLength(280);
            builder.Property(u => u.Faculty).HasMaxLength(100);
            builder.Property(u => u.Department).HasMaxLength(100);

            builder.Property(u => u.DeepValues)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
                .Metadata.SetValueComparer(stringListComparer);

            builder.Property(u => u.IntellectualInterests)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<IntellectualInterest>>(v, (JsonSerializerOptions?)null) ?? new List<IntellectualInterest>())
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
            builder.HasIndex(i => i.Token).IsUnique();
            builder.Property(i => i.Token).HasMaxLength(128).IsRequired();
            builder.Property(i => i.IntendedEmail).HasMaxLength(120);

            builder.HasOne(i => i.Campus)
                .WithMany()
                .HasForeignKey(i => i.CampusId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
