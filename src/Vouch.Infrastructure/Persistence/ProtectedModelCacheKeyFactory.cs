using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Vouch.Infrastructure.Persistence;

public sealed class ProtectedModelCacheKeyFactory : IModelCacheKeyFactory
{
    // Converters capture a key ring. Never reuse a model built for another encryption service.
    public object Create(DbContext context, bool designTime) => context is ApplicationDbContext db
        ? (context.GetType(), db.Encryption, designTime) : (object)(context.GetType(), designTime);
}
