using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Vouch.Infrastructure.Security;

namespace Vouch.Infrastructure.Persistence;

public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args).Build();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(RequiredConfiguration.Read(config, "ConnectionStrings:DefaultConnection")).Options;
        return new ApplicationDbContext(options, new AesEncryptionService(config), new EmailLookup(config));
    }
}
