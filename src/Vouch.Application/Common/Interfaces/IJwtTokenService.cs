using Vouch.Domain.Entities;

namespace Vouch.Application.Common.Interfaces;

public interface IJwtTokenService
{
    string GenerateToken(User user);
}
