using Atm.Business.Common;

namespace Atm.Business.Domain;

public sealed record CassetteConfiguration(int Slot, CassetteType Type, Currency? Currency, int? Denomination);

/// <summary>
/// One of the 5 physical cassettes of an ATM. A recycling cassette holds a single currency/denomination;
/// the deposit cassette holds a mix, so contents are always modelled as per-(currency, denomination) balances.
/// </summary>
public class Cassette
{
    public const int Capacity = 100;

    private readonly List<CassetteBalance> _balances = new();

    private Cassette()
    {
    }


    public Guid Id { get; private set; }
    public Guid AtmId { get; private set; }
    public int Slot { get; private set; }
    public CassetteType Type { get; private set; }
    public Currency? Currency { get; private set; }
    public int? Denomination { get; private set; }

    public IReadOnlyCollection<CassetteBalance> Balances => _balances;

    public int TotalCount => _balances.Sum(b => b.Count);
    public int TotalReserved => _balances.Sum(b => b.Reserved);
    public int Available => TotalCount - TotalReserved;
    public int FreeSpace => Capacity - TotalCount;

    /// <summary>Fill ratio used for dispensing: notes that can still be dispensed / capacity.</summary>
    public decimal AvailableFillRatio => (decimal)Available / Capacity;

    /// <summary>Physical fill ratio used for routing deposits: physical notes / capacity.</summary>
    public decimal PhysicalFillRatio => (decimal)TotalCount / Capacity;

    public bool IsRecycling => Type == CassetteType.Recycling;
    public bool IsEmpty => TotalCount == 0 && TotalReserved == 0;

    internal static Cassette Create(Guid atmId, CassetteConfiguration configuration)
    {
        var cassette = new Cassette { Id = Guid.NewGuid(), AtmId = atmId, Slot = configuration.Slot };
        cassette.ApplyConfiguration(configuration);
        return cassette;
    }

    public bool Accepts(Currency currency, int denomination)
        => IsRecycling && Currency == currency && Denomination == denomination;

    public bool HasConfiguration(CassetteConfiguration configuration)
        => Type == configuration.Type
           && (Type == CassetteType.Deposit
               || (Currency == configuration.Currency && Denomination == configuration.Denomination));

    public int CountOf(Currency currency, int denomination)
        => _balances.FirstOrDefault(b => b.Currency == currency && b.Denomination == denomination)?.Count ?? 0;

    internal void Reconfigure(CassetteConfiguration configuration)
    {
        if (!IsEmpty)
        {
            throw new InvalidOperationException("A cassette must be empty before it can be reconfigured.");
        }

        ApplyConfiguration(configuration);
        _balances.Clear();
    }

    internal CassetteBalance GetOrCreateBalance(Currency currency, int denomination)
    {
        var balance = _balances.FirstOrDefault(b => b.Currency == currency && b.Denomination == denomination);
        if (balance is not null)
        {
            return balance;
        }

        if (IsRecycling && !Accepts(currency, denomination))
        {
            throw new InvalidOperationException(
                $"Cassette {Slot} is configured for {Currency} {Denomination} and cannot hold {currency} {denomination}.");
        }

        balance = CassetteBalance.Create(currency, denomination);
        _balances.Add(balance);
        return balance;
    }

    private void ApplyConfiguration(CassetteConfiguration configuration)
    {
        Type = configuration.Type;
        Currency = configuration.Type == CassetteType.Recycling ? configuration.Currency : null;
        Denomination = configuration.Type == CassetteType.Recycling ? configuration.Denomination : null;
    }
}

/// <summary>Stored balance of one currency/denomination inside a cassette.</summary>
public class CassetteBalance
{
    private CassetteBalance()
    {
    }

    public Guid Id { get; private set; }
    public Guid CassetteId { get; private set; }
    public Currency Currency { get; private set; }
    public int Denomination { get; private set; }

    /// <summary>Physical notes in the cassette (including reserved ones).</summary>
    public int Count { get; private set; }

    /// <summary>Notes held by active withdrawal quotes. They are still physically present.</summary>
    public int Reserved { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public int Available => Count - Reserved;

    internal static CassetteBalance Create(Currency currency, int denomination)
        => new() { Id = Guid.NewGuid(), Currency = currency, Denomination = denomination };

    internal void Apply(int countDelta, int reservedDelta)
    {
        var newCount = Count + countDelta;
        var newReserved = Reserved + reservedDelta;
        if (newCount < 0 || newReserved < 0 || newReserved > newCount)
        {
            throw new InvalidOperationException(
                $"Invalid balance change for {Currency} {Denomination}: count {Count}->{newCount}, reserved {Reserved}->{newReserved}.");
        }

        Count = newCount;
        Reserved = newReserved;
    }
}
