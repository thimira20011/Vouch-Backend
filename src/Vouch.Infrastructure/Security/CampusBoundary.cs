using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Vouch.Application.Common;
using Vouch.Application.Common.Interfaces;

namespace Vouch.Infrastructure.Security;

public sealed class CampusBoundary(IConfiguration configuration)
{
    public string? Code { get; } = configuration["Tenancy:CampusCode"];
    public async Task RequireAsync(IApplicationDbContext db, Guid campusId, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(Code) && !await db.Campuses.AnyAsync(c => c.Id == campusId && c.Code == Code, ct))
            throw new EligibilityException("Campus is unavailable on this deployment.");
    }
    public async Task VerifyDeploymentAsync(IApplicationDbContext db, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(Code) && (!await db.Campuses.AnyAsync(c => c.Code == Code, ct) ||
            await db.Users.AnyAsync(u => u.Campus.Code != Code, ct) ||
            await db.AmbassadorInvites.AnyAsync(i => i.Campus.Code != Code, ct)))
            throw new InvalidOperationException("The campus deployment requires a configured campus and no private accounts from other campuses. Use a separate database per campus.");
    }
}
