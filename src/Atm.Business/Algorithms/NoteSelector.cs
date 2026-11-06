using Atm.Business.Domain;

namespace Atm.Business.Algorithms;

/// <summary>A concrete way of paying an amount: notes per denomination (largest denomination first).</summary>
public sealed record Breakdown(int Amount, IReadOnlyList<NoteCount> Notes)
{
    public int NoteCount => Notes.Sum(n => n.Count);

    public override string ToString() => $"{Amount} = {string.Join(" + ", Notes.Select(n => $"{n.Denomination}x{n.Count}"))}";
}

public sealed record SelectionResult(Breakdown? Exact, Breakdown? Lower, Breakdown? Upper)
{
    public bool IsExact => Exact is not null;
    public bool HasAnyOption => Exact is not null || Lower is not null || Upper is not null;

    public static SelectionResult None { get; } = new(null, null, null);
}

/// <summary>
/// Bounded "minimum number of notes" knapsack.
///
/// A greedy largest-note-first approach is wrong for limited stock (e.g. 160 from 50x3 + 20x10: greedy takes
/// 3x50 and gets stuck on 10). Instead we run a dynamic programme over every reachable amount:
///   best[s] = minimum notes needed to pay exactly s with the available stock.
/// One pass answers all three questions:
///   * exact  — best[requested] is reachable,
///   * lower  — the largest reachable amount below the request,
///   * upper  — the smallest reachable amount above the request (bounded by the customer's remaining limit).
///
/// Amounts are processed in units of the greatest common divisor of the denominations (10 for 20/50/100/500).
/// Worst case per currency: 400 notes (4 cassettes x 100) and 20,000 amount units, i.e. a few million cheap
/// operations — negligible for a request.
///
/// Tie-break: when several breakdowns use the same (minimum) number of notes, the one using more of the larger
/// denominations wins, which keeps small notes in the machine and makes results deterministic.
/// </summary>
public static class NoteSelector
{
    private const int Unreachable = int.MaxValue;

    public static SelectionResult Select(IReadOnlyDictionary<int, int> availableNotes, int requestedAmount, int? maxUpperAmount = null)
    {
        ArgumentNullException.ThrowIfNull(availableNotes);
        if (requestedAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedAmount), "Requested amount must be positive.");
        }

        var stock = availableNotes
            .Where(n => n.Key > 0 && n.Value > 0)
            .OrderBy(n => n.Key)
            .Select(n => (Denomination: n.Key, Count: n.Value))
            .ToArray();

        if (stock.Length == 0)
        {
            return SelectionResult.None;
        }

        var unit = stock.Select(s => s.Denomination).Aggregate(Gcd);
        var maxUnits = checked((int)stock.Sum(s => (long)s.Denomination / unit * s.Count));

        var best = new int[maxUnits + 1];
        Array.Fill(best, Unreachable);
        best[0] = 0;

        // choice[i][s] = how many notes of stock[i] are used in the optimal way to make s from stock[0..i].
        var choice = new int[stock.Length][];

        for (var i = 0; i < stock.Length; i++)
        {
            var size = stock[i].Denomination / unit;
            var count = stock[i].Count;
            var next = new int[maxUnits + 1];
            var chosen = new int[maxUnits + 1];

            for (var s = 0; s <= maxUnits; s++)
            {
                var bestNotes = Unreachable;
                var bestK = 0;
                // Iterate from the most notes of this (larger) denomination down, keeping the first minimum:
                // this implements the "prefer larger denominations" tie-break.
                for (var k = Math.Min(count, s / size); k >= 0; k--)
                {
                    var previous = best[s - k * size];
                    if (previous == Unreachable)
                    {
                        continue;
                    }

                    var notes = previous + k;
                    if (notes < bestNotes)
                    {
                        bestNotes = notes;
                        bestK = k;
                    }
                }

                next[s] = bestNotes;
                chosen[s] = bestK;
            }

            best = next;
            choice[i] = chosen;
        }

        Breakdown Build(int units)
        {
            var notes = new List<NoteCount>();
            var remaining = units;
            for (var i = stock.Length - 1; i >= 0; i--)
            {
                var k = choice[i][remaining];
                if (k > 0)
                {
                    notes.Add(new NoteCount(stock[i].Denomination, k));
                    remaining -= k * (stock[i].Denomination / unit);
                }
            }

            return new Breakdown(units * unit, notes);
        }

        if (requestedAmount % unit == 0 && requestedAmount / unit <= maxUnits && best[requestedAmount / unit] != Unreachable)
        {
            return new SelectionResult(Build(requestedAmount / unit), null, null);
        }

        Breakdown? lower = null;
        // Largest s with s * unit < requested.
        var lowerStart = Math.Min(maxUnits, (requestedAmount - 1) / unit);
        for (var s = lowerStart; s >= 1; s--)
        {
            if (best[s] != Unreachable)
            {
                lower = Build(s);
                break;
            }
        }

        Breakdown? upper = null;
        // Smallest s with s * unit > requested, not exceeding the customer's remaining limit.
        var upperLimitUnits = maxUpperAmount is null ? maxUnits : Math.Min(maxUnits, maxUpperAmount.Value / unit);
        for (var s = requestedAmount / unit + 1; s <= upperLimitUnits; s++)
        {
            if (best[s] != Unreachable)
            {
                upper = Build(s);
                break;
            }
        }

        return new SelectionResult(null, lower, upper);
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
