using System.Text.Json;
using Atm.Business.Common;
using Atm.Business.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atm.DataAccess.Persistence;

internal static class ConfigurationExtensions
{
    /// <summary>Stores a list of value objects as a JSON column.</summary>
    public static PropertyBuilder<List<T>> HasJsonConversion<T>(this PropertyBuilder<List<T>> builder)
    {
        var comparer = new ValueComparer<List<T>>(
            (a, b) => JsonSerializer.Serialize(a, AtmJson.Options) == JsonSerializer.Serialize(b, AtmJson.Options),
            v => JsonSerializer.Serialize(v, AtmJson.Options).GetHashCode(),
            v => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(v, AtmJson.Options), AtmJson.Options)!);

        builder.HasConversion(
                v => JsonSerializer.Serialize(v, AtmJson.Options),
                v => JsonSerializer.Deserialize<List<T>>(v, AtmJson.Options) ?? new List<T>(),
                comparer)
            .HasColumnType("nvarchar(max)")
            .IsRequired();
        return builder;
    }
}

internal sealed class AtmMachineConfiguration : IEntityTypeConfiguration<AtmMachine>
{
    public void Configure(EntityTypeBuilder<AtmMachine> builder)
    {
        builder.ToTable("Atms");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Code).HasMaxLength(50).IsRequired();
        builder.HasIndex(a => a.Code).IsUnique();
        builder.Property(a => a.Location).HasMaxLength(200);
        builder.Property(a => a.State).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.RowVersion).IsRowVersion();

        builder.HasMany(a => a.Cassettes).WithOne().HasForeignKey(c => c.AtmId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(a => a.Cassettes).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(a => a.PendingMovements);
        builder.Ignore(a => a.DomainEvents);
        builder.Ignore(a => a.DepositCassette);
        builder.Ignore(a => a.RecyclingCassettes);
    }
}

internal sealed class CassetteConfigurationMap : IEntityTypeConfiguration<Cassette>
{
    public void Configure(EntityTypeBuilder<Cassette> builder)
    {
        builder.ToTable("Cassettes", t =>
        {
            t.HasCheckConstraint("CK_Cassettes_Slot", "[Slot] BETWEEN 1 AND 5");
            t.HasCheckConstraint("CK_Cassettes_Denomination", "[Denomination] IS NULL OR [Denomination] IN (20, 50, 100, 500)");
        });
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasIndex(c => new { c.AtmId, c.Slot }).IsUnique();
        builder.Property(c => c.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(c => c.Currency).HasConversion<string>().HasMaxLength(3);

        builder.HasMany(c => c.Balances).WithOne().HasForeignKey(b => b.CassetteId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Balances).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(c => c.TotalCount);
        builder.Ignore(c => c.TotalReserved);
        builder.Ignore(c => c.Available);
        builder.Ignore(c => c.FreeSpace);
        builder.Ignore(c => c.AvailableFillRatio);
        builder.Ignore(c => c.PhysicalFillRatio);
        builder.Ignore(c => c.IsRecycling);
        builder.Ignore(c => c.IsEmpty);
    }
}

internal sealed class CassetteBalanceConfiguration : IEntityTypeConfiguration<CassetteBalance>
{
    public void Configure(EntityTypeBuilder<CassetteBalance> builder)
    {
        builder.ToTable("CassetteBalances", t =>
        {
            t.HasCheckConstraint("CK_CassetteBalances_Count", "[Count] BETWEEN 0 AND 100");
            t.HasCheckConstraint("CK_CassetteBalances_Reserved", "[Reserved] >= 0 AND [Reserved] <= [Count]");
        });
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.HasIndex(b => new { b.CassetteId, b.Currency, b.Denomination }).IsUnique();
        builder.Property(b => b.Currency).HasConversion<string>().HasMaxLength(3);
        // Safety net: a writer that skipped the ATM lock would fail with a concurrency exception instead of losing an update.
        builder.Property(b => b.RowVersion).IsRowVersion();
        builder.Ignore(b => b.Available);
    }
}

internal sealed class CassetteMovementConfiguration : IEntityTypeConfiguration<CassetteMovement>
{
    public void Configure(EntityTypeBuilder<CassetteMovement> builder)
    {
        builder.ToTable("CassetteMovements");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).UseIdentityColumn();
        builder.Property(m => m.Type).HasConversion<string>().HasMaxLength(40);
        builder.Property(m => m.Currency).HasConversion<string>().HasMaxLength(3);
        builder.Property(m => m.ReferenceType).HasMaxLength(40).IsRequired();
        builder.Property(m => m.PerformedBy).HasMaxLength(100);
        builder.HasIndex(m => new { m.AtmId, m.CassetteId });
        builder.HasIndex(m => new { m.ReferenceType, m.ReferenceId });
        builder.HasOne<Cassette>().WithMany().HasForeignKey(m => m.CassetteId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WithdrawalQuoteConfiguration : IEntityTypeConfiguration<WithdrawalQuote>
{
    public void Configure(EntityTypeBuilder<WithdrawalQuote> builder)
    {
        builder.ToTable("WithdrawalQuotes");
        builder.HasKey(q => q.Id);
        builder.Property(q => q.Id).ValueGeneratedNever();
        builder.Property(q => q.CardHash).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(q => q.CardLast4).HasMaxLength(4).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(q => q.Currency).HasConversion<string>().HasMaxLength(3);
        builder.Property(q => q.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(q => q.ConfirmedOption).HasConversion<string>().HasMaxLength(10);
        builder.Property(q => q.CloseReason).HasMaxLength(100);
        builder.Property(q => q.Options).HasJsonConversion();
        builder.Property(q => q.DispensedNotes).HasJsonConversion();

        builder.HasMany(q => q.Reservations).WithOne().HasForeignKey(r => r.QuoteId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AtmMachine>().WithMany().HasForeignKey(q => q.AtmId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(q => new { q.AtmId, q.Status, q.ExpiresAt });
        builder.HasIndex(q => new { q.CardHash, q.Currency, q.BusinessDate, q.Status });
        builder.HasIndex(q => new { q.Status, q.ExpiresAt });
    }
}

internal sealed class QuoteReservationLineConfiguration : IEntityTypeConfiguration<QuoteReservationLine>
{
    public void Configure(EntityTypeBuilder<QuoteReservationLine> builder)
    {
        builder.ToTable("QuoteReservations");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.HasOne<Cassette>().WithMany().HasForeignKey(r => r.CassetteId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DepositConfiguration : IEntityTypeConfiguration<Deposit>
{
    public void Configure(EntityTypeBuilder<Deposit> builder)
    {
        builder.ToTable("Deposits");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.CardHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        builder.Property(d => d.CardLast4).HasMaxLength(4).IsFixedLength().IsUnicode(false);
        builder.Property(d => d.Placements).HasJsonConversion();
        builder.Property(d => d.ReturnedNotes).HasJsonConversion();
        builder.HasOne<AtmMachine>().WithMany().HasForeignKey(d => d.AtmId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(d => new { d.AtmId, d.CreatedAt });
    }
}

internal sealed class ReconciliationConfiguration : IEntityTypeConfiguration<Reconciliation>
{
    public void Configure(EntityTypeBuilder<Reconciliation> builder)
    {
        builder.ToTable("Reconciliations");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(r => r.CreatedBy).HasMaxLength(100).IsRequired();
        builder.Property(r => r.DecidedBy).HasMaxLength(100);
        builder.Property(r => r.Comment).HasMaxLength(500);
        builder.Property(r => r.TotalAbsoluteDifferenceTry).HasPrecision(18, 2);
        builder.Property(r => r.ApprovalThresholdTry).HasPrecision(18, 2);
        builder.Property(r => r.Lines).HasJsonConversion();
        builder.Ignore(r => r.RequiresApproval);
        builder.HasOne<AtmMachine>().WithMany().HasForeignKey(r => r.AtmId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.AtmId, r.Status });
    }
}

internal sealed class ExchangeRateConfiguration : IEntityTypeConfiguration<ExchangeRate>
{
    public static readonly DateTimeOffset SeedDate = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Configure(EntityTypeBuilder<ExchangeRate> builder)
    {
        builder.ToTable("ExchangeRates");
        builder.HasKey(r => r.Currency);
        builder.Property(r => r.Currency).HasConversion<string>().HasMaxLength(3);
        builder.Property(r => r.RateToTry).HasPrecision(18, 6);

        // Seed data; rates can be updated through PUT /api/exchange-rates/{currency}.
        builder.HasData(
            new ExchangeRate(Currency.TRY, 1m, SeedDate),
            new ExchangeRate(Currency.USD, 41.50m, SeedDate),
            new ExchangeRate(Currency.EUR, 48.25m, SeedDate));
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Type).HasMaxLength(100).IsRequired();
        builder.Property(m => m.Payload).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(2000);
        builder.HasIndex(m => new { m.ProcessedAt, m.OccurredAt });
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("IdempotencyRecords");
        builder.HasKey(r => r.Key);
        builder.Property(r => r.Key).HasMaxLength(100);
        builder.Property(r => r.RequestHash).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(r => r.ResponseJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.HasIndex(r => r.QuoteId);
    }
}
