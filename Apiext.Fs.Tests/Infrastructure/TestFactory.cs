using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Apiext.Fs.Data;
using Apiext.Fs.Services;

namespace Apiext.Fs.Tests.Infrastructure;

/// <summary>
/// Builds IConfiguration and a SQLite-in-memory AppDbContext factory for service tests.
/// </summary>
internal static class TestFactory
{
    public static IConfiguration ConfigFor(string storeLocation) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StoreLocation"] = storeLocation
            })
            .Build();

    public static IConfiguration ConfigForStaticAccount(string storeLocation, string user, string password) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StoreLocation"]     = storeLocation,
                ["UseStaticAccount"]  = "true",
                ["StaticUser"]        = user,
                ["StaticPassword"]    = password
            })
            .Build();

    /// <summary>Creates an object index backed by a fresh in-memory database.</summary>
    public static IObjectIndexService CreateIndex(string dbName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();

        return new ObjectIndexService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ObjectIndexService>.Instance);
    }

    public static IDbContextFactory<AppDbContext> CreateInMemoryDbFactory(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new InMemoryDbContextFactory(options);
    }

    private sealed class InMemoryDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
