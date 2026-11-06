using Atm.Business.Common;
using Atm.Business.Domain;
using Atm.Business.Services;
using Atm.DataAccess.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Atm.DataAccess.BackgroundJobs;

public sealed class BackgroundJobOptions
{
    public const string SectionName = "BackgroundJobs";

    public bool Enabled { get; set; } = true;
    public int OutboxPollIntervalMilliseconds { get; set; } = 2000;
    public int OutboxBatchSize { get; set; } = 50;
    public int QuoteExpiryPollIntervalMilliseconds { get; set; } = 5000;
}

/// <summary>
/// Reads unprocessed outbox events, "publishes" them (logs) and marks them as sent. Rows are claimed with
/// UPDLOCK + READPAST so several API instances can run the processor without handling the same row twice.
/// Delivery is at-least-once.
/// </summary>
public sealed class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<BackgroundJobOptions> options,
    TimeProvider time,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Outbox processing failed");
            }

            await Delay(options.Value.OutboxPollIntervalMilliseconds, stoppingToken);
        }
    }

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtmDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var batch = await db.OutboxMessages
            .FromSqlRaw(
                "SELECT TOP ({0}) * FROM dbo.OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK) WHERE ProcessedAt IS NULL ORDER BY OccurredAt",
                options.Value.OutboxBatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in batch)
        {
            try
            {
                Publish(message);
                message.MarkProcessed(time.GetUtcNow());
            }
            catch (Exception ex)
            {
                message.MarkFailed(ex.Message);
                logger.LogError(ex, "Failed to publish outbox message {MessageId}", message.Id);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return batch.Count;
    }

    private void Publish(OutboxMessage message)
    {
        // A real system would push to a message broker / alerting system here.
        if (message.Type == nameof(LowCashAlert))
        {
            logger.LogWarning("LOW CASH ALERT {MessageId}: {Payload}", message.Id, message.Payload);
        }
        else
        {
            logger.LogInformation("Outbox event {Type} {MessageId}: {Payload}", message.Type, message.Id, message.Payload);
        }
    }

    internal static async Task Delay(int milliseconds, CancellationToken token)
    {
        try
        {
            await Task.Delay(milliseconds, token);
        }
        catch (TaskCanceledException)
        {
        }
    }
}

/// <summary>
/// Releases reservations of quotes whose 60-second validity has lapsed. Expiry is also applied lazily whenever a
/// quote or ATM is touched, so correctness never depends on this worker's timing.
/// </summary>
public sealed class QuoteExpiryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<BackgroundJobOptions> options,
    TimeProvider time,
    ILogger<QuoteExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Quote expiry sweep failed");
            }

            await OutboxProcessor.Delay(options.Value.QuoteExpiryPollIntervalMilliseconds, stoppingToken);
        }
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        List<Guid> atmIds;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AtmDbContext>();
            var now = time.GetUtcNow();
            atmIds = await db.Quotes.AsNoTracking()
                .Where(q => q.Status == QuoteStatus.Active && q.ExpiresAt <= now)
                .Select(q => q.AtmId)
                .Distinct()
                .ToListAsync(cancellationToken);
        }

        var expired = 0;
        foreach (var atmId in atmIds)
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<WithdrawalService>();
            expired += await service.ExpireLapsedQuotesAsync(atmId, cancellationToken);
        }

        if (expired > 0)
        {
            logger.LogInformation("Released reservations of {Count} expired quote(s)", expired);
        }

        return expired;
    }
}
