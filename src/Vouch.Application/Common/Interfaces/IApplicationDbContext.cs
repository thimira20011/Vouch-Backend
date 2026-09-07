using Microsoft.EntityFrameworkCore;
using Vouch.Domain.Entities;

namespace Vouch.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<Campus> Campuses { get; }
    DbSet<User> Users { get; }
    DbSet<VouchRecord> Vouches { get; }
    DbSet<DailyMatch> Matches { get; }
    DbSet<DailyReflection> Reflections { get; }
    DbSet<Conversation> Conversations { get; }
    DbSet<Message> Messages { get; }
    DbSet<Report> Reports { get; }
    DbSet<Block> Blocks { get; }
    DbSet<IcebreakerPrompt> Icebreakers { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
