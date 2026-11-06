using Atm.Business.Abstractions;
using Atm.DataAccess.BackgroundJobs;
using Atm.DataAccess.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Atm.DataAccess;

public static class DependencyInjection
{
    public const string ConnectionStringName = "AtmDb";

    public static IServiceCollection AddDataAccess(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
                               ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        services.AddDbContext<AtmDbContext>(options => options
            .UseSqlServer(connectionString)
            // An ATM aggregate is always 1 ATM + 5 cassettes + a few balances; a single joined query is the right choice.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.MultipleCollectionIncludeWarning)));
        services.AddScoped<IAtmDbContext>(sp => sp.GetRequiredService<AtmDbContext>());

        services.AddOptions<BackgroundJobOptions>().Bind(configuration.GetSection(BackgroundJobOptions.SectionName));
        services.AddSingleton<OutboxProcessor>();
        services.AddSingleton<QuoteExpiryWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxProcessor>());
        services.AddHostedService(sp => sp.GetRequiredService<QuoteExpiryWorker>());
        return services;
    }

    /// <summary>Applies pending migrations, retrying while the database container is still starting.</summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtmDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Atm.DataAccess.Migrations");

        const int maxAttempts = 30;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("Database migrations applied");
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger.LogWarning("Database not ready (attempt {Attempt}/{Max}): {Message}", attempt, maxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }
}
