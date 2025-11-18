using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Postgres.Migrations;

/// <summary>
/// Design-time factory to allow `dotnet ef` to find JellyfinDbContext.
/// </summary>
public sealed class PostgresDesignTimeJellyfinDbFactory : IDesignTimeDbContextFactory<JellyfinDbContext>
{
    private const string DesignTimeConnection = "Host=localhost;Database=jellyfin;Username=jellyfin;Password=jellyfin";

    public JellyfinDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<JellyfinDbContext>();
        optionsBuilder.UseNpgsql(
            DesignTimeConnection,
            builder => builder.MigrationsAssembly(typeof(PostgresDesignTimeJellyfinDbFactory).Assembly.FullName));

        return new JellyfinDbContext(
            optionsBuilder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            new PostgresDatabaseProvider(NullLogger<PostgresDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
