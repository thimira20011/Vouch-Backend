using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.AiWingman;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;

namespace Vouch.Infrastructure.Persistence;

public static class DbInitializer
{
    public static async Task SeedAsync(ApplicationDbContext context, IPasswordHasher passwordHasher)
    {
        await context.Database.EnsureCreatedAsync();

        // 1. Seed Sri Lankan University Campuses
        if (!await context.Campuses.AnyAsync())
        {
            var campuses = new List<Campus>
            {
                new()
                {
                    Name = "Sabaragamuwa University of Sri Lanka",
                    Code = "SUSL",
                    DomainPattern = "@sab.ac.lk",
                    IsSoftLaunchUnlocked = false,
                    RequiredAmbassadorsForLaunch = 30
                },
                new()
                {
                    Name = "University of Moratuwa",
                    Code = "UOM",
                    DomainPattern = "@uom.lk",
                    IsSoftLaunchUnlocked = false,
                    RequiredAmbassadorsForLaunch = 30
                },
                new()
                {
                    Name = "University of Colombo",
                    Code = "UOC",
                    DomainPattern = "@cmb.ac.lk",
                    IsSoftLaunchUnlocked = false,
                    RequiredAmbassadorsForLaunch = 30
                },
                new()
                {
                    Name = "University of Peradeniya",
                    Code = "UOP",
                    DomainPattern = "@pdn.ac.lk",
                    IsSoftLaunchUnlocked = false,
                    RequiredAmbassadorsForLaunch = 30
                }
            };

            await context.Campuses.AddRangeAsync(campuses);
            await context.SaveChangesAsync();
        }

        // 2. Seed The Architect (Admin)
        var suslCampus = await context.Campuses.FirstAsync(c => c.Code == "SUSL");
        if (!await context.Users.AnyAsync(u => u.Role == UserRole.Architect))
        {
            var architect = new User
            {
                CampusId = suslCampus.Id,
                Email = "architect@vouch.ac.lk",
                PasswordHash = passwordHasher.HashPassword("ArchitectVouchPass2026!"),
                FullName = "The Architect",
                Role = UserRole.Architect,
                Status = AccountStatus.Active,
                Faculty = "Administration",
                Department = "Trust Systems",
                AcademicYear = 4,
                Bio = "Platform Overseer & Trust Integrity Monitor.",
                HasFoundingMemberBadge = true,
                TrustScore = 20.0
            };

            await context.Users.AddAsync(architect);
            await context.SaveChangesAsync();
        }

        // 3. Seed Daily Reflections
        if (!await context.Reflections.AnyAsync())
        {
            var reflections = new List<DailyReflection>
            {
                new()
                {
                    Category = IntellectualInterest.Philosophy,
                    Quote = "The soul becomes dyed with the color of its thoughts.",
                    Author = "Marcus Aurelius",
                    ThoughtProvokingQuestion = "What thought did you spend the most time dwelling on today?"
                },
                new()
                {
                    Category = IntellectualInterest.Literature,
                    Quote = "There is no friend as loyal as a book.",
                    Author = "Ernest Hemingway",
                    ThoughtProvokingQuestion = "Which sentence in a book felt like it was written directly for you?"
                },
                new()
                {
                    Category = IntellectualInterest.Architecture,
                    Quote = "Architecture is the learned game, correct and magnificent, of forms assembled in the light.",
                    Author = "Le Corbusier",
                    ThoughtProvokingQuestion = "How does the physical quiet of your surroundings affect your internal clarity?"
                },
                new()
                {
                    Category = IntellectualInterest.Music,
                    Quote = "Music produces a kind of pleasure which human nature cannot do without.",
                    Author = "Confucius",
                    ThoughtProvokingQuestion = "What rhythm or melody immediately returns you to a state of peace?"
                },
                new()
                {
                    Category = IntellectualInterest.Science,
                    Quote = "Somewhere, something incredible is waiting to be known.",
                    Author = "Carl Sagan",
                    ThoughtProvokingQuestion = "What mystery about nature or human consciousness fascinates you most?"
                },
                new()
                {
                    Category = IntellectualInterest.AcademicGoals,
                    Quote = "Wisdom is not a product of schooling but of the lifelong attempt to acquire it.",
                    Author = "Albert Einstein",
                    ThoughtProvokingQuestion = "What is one skill or virtue you wish to master before leaving university?"
                }
            };

            await context.Reflections.AddRangeAsync(reflections);
            await context.SaveChangesAsync();
        }

        // 4. Seed Curated Fallback Icebreakers (50 items)
        if (!await context.Icebreakers.AnyAsync())
        {
            var icebreakerEntities = CuratedIcebreakers.Library.Select(item => new IcebreakerPrompt
            {
                Category = item.Interest,
                PromptText = item.Text,
                Tags = new List<string> { item.Tag },
                IsStaticFallback = true
            });

            await context.Icebreakers.AddRangeAsync(icebreakerEntities);
            await context.SaveChangesAsync();
        }
    }
}
