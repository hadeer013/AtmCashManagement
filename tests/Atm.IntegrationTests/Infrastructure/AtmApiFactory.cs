using Atm.DataAccess.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Atm.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API in-memory against a real SQL Server database created for this test run (and dropped
/// afterwards). Set ATM_TEST_SQL_CONNECTION to point at another server, e.g. the docker-compose SQL Server:
///   Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=True
/// </summary>
public sealed class AtmApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string DefaultServer = "Server=(localdb)\\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True";

    public AtmApiFactory()
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATM_TEST_SQL_CONNECTION") ?? DefaultServer)
        {
            InitialCatalog = $"AtmIntegrationTests_{Guid.NewGuid():N}"
        };
        ConnectionString = builder.ConnectionString;
    }

    public string ConnectionString { get; }

    /// <summary>Controllable clock, so the 60-second quote expiry can be tested without waiting.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:AtmDb", ConnectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        // Workers are driven explicitly by the tests (deterministic); the hosted loops stay off.
        builder.UseSetting("BackgroundJobs:Enabled", "false");
        builder.UseSetting("CardHashing:Secret", "integration-test-secret-0123456789");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public Task InitializeAsync()
    {
        _ = Server; // starts the host and applies migrations
        return Task.CompletedTask;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AtmDbContext>();
            SqlConnection.ClearAllPools();
            await db.Database.EnsureDeletedAsync();
        }

        await base.DisposeAsync();
    }

    public async Task<T> WithDbAsync<T>(Func<AtmDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AtmDbContext>());
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<AtmApiFactory>
{
    public const string Name = "api";
}
