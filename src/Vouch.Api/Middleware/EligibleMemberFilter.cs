using System.Security.Claims;
using Vouch.Application.Common.Interfaces;
using Vouch.Infrastructure.Security;

namespace Vouch.Api.Middleware;

public sealed class EligibleMemberFilter(IApplicationDbContext db) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!Guid.TryParse(context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return Results.Unauthorized();
        await MemberEligibility.RequireActiveAsync(db, id, context.HttpContext.RequestAborted);
        return await next(context);
    }
}
