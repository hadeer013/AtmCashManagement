using System.Diagnostics;
using Atm.Business.Algorithms;
using Atm.Business.Common;

namespace Atm.Business.Tests;

/// <summary>The acceptance tests from requirement B3, plus edge cases of the note selection algorithm.</summary>
public class NoteSelectorAcceptanceTests
{
    private static readonly AtmOptions Limits = new();

    private static Dictionary<int, int> Notes(params (int Denomination, int Count)[] notes)
        => notes.ToDictionary(n => n.Denomination, n => n.Count);

    private static void AssertBreakdown(Breakdown? actual, int amount, params (int Denomination, int Count)[] expected)
    {
        Assert.NotNull(actual);
        Assert.Equal(amount, actual.Amount);
        Assert.Equal(
            expected.OrderByDescending(e => e.Denomination).ToArray(),
            actual.Notes.Select(n => (n.Denomination, n.Count)).ToArray());
        Assert.Equal(amount, actual.Notes.Sum(n => n.Denomination * n.Count));
    }

    private static SelectionResult Select(Currency currency, Dictionary<int, int> available, int amount)
        => NoteSelector.Select(available, amount, Limits.GetDailyLimit(currency));

    [Fact(DisplayName = "B3 #1: TRY 100x0, 50x3, 20x10 / 160 -> exact 50x2 + 20x3")]
    public void Acceptance1()
    {
        var result = Select(Currency.TRY, Notes((100, 0), (50, 3), (20, 10)), 160);

        Assert.True(result.IsExact);
        AssertBreakdown(result.Exact, 160, (50, 2), (20, 3));
    }

    [Fact(DisplayName = "B3 #2: TRY 50x1, 20x2 / 80 -> lower 70 (50+20), upper 90 (50+20x2)")]
    public void Acceptance2()
    {
        var result = Select(Currency.TRY, Notes((50, 1), (20, 2)), 80);

        Assert.False(result.IsExact);
        AssertBreakdown(result.Lower, 70, (50, 1), (20, 1));
        AssertBreakdown(result.Upper, 90, (50, 1), (20, 2));
    }

    [Fact(DisplayName = "B3 #3: TRY 100x5, 20x10 / 130 -> lower 120 (100+20), upper 140 (100+20x2)")]
    public void Acceptance3()
    {
        var result = Select(Currency.TRY, Notes((100, 5), (20, 10)), 130);

        Assert.False(result.IsExact);
        AssertBreakdown(result.Lower, 120, (100, 1), (20, 1));
        AssertBreakdown(result.Upper, 140, (100, 1), (20, 2));
    }

    [Fact(DisplayName = "B3 #4: USD 100x2 / 250 -> lower 200 (100x2), upper none")]
    public void Acceptance4()
    {
        var result = Select(Currency.USD, Notes((100, 2)), 250);

        Assert.False(result.IsExact);
        AssertBreakdown(result.Lower, 200, (100, 2));
        Assert.Null(result.Upper);
    }

    [Fact(DisplayName = "B3 #5: TRY 500x1, 100x0, 20x5 / 600 -> exact 500 + 20x5")]
    public void Acceptance5()
    {
        var result = Select(Currency.TRY, Notes((500, 1), (100, 0), (20, 5)), 600);

        Assert.True(result.IsExact);
        AssertBreakdown(result.Exact, 600, (500, 1), (20, 5));
    }

    [Fact(DisplayName = "B3 #6: TRY 100x1, 50x4, 20x5 / 200 -> exact 100 + 50x2 (not 4x50, not 100 + 5x20)")]
    public void Acceptance6()
    {
        var result = Select(Currency.TRY, Notes((100, 1), (50, 4), (20, 5)), 200);

        Assert.True(result.IsExact);
        AssertBreakdown(result.Exact, 200, (100, 1), (50, 2));
        Assert.Equal(3, result.Exact!.NoteCount);
    }

    [Fact(DisplayName = "B3 #7: TRY 500x1, 100x3, 20x20 / 1000 -> exact 500 + 100x3 + 20x10")]
    public void Acceptance7()
    {
        var result = Select(Currency.TRY, Notes((500, 1), (100, 3), (20, 20)), 1000);

        Assert.True(result.IsExact);
        AssertBreakdown(result.Exact, 1000, (500, 1), (100, 3), (20, 10));
    }

    [Fact(DisplayName = "B3 #8: TRY 100x3, 50x1, 20x1 / 110 -> lower 100 (100), upper 120 (100+20)")]
    public void Acceptance8()
    {
        var result = Select(Currency.TRY, Notes((100, 3), (50, 1), (20, 1)), 110);

        Assert.False(result.IsExact);
        AssertBreakdown(result.Lower, 100, (100, 1));
        AssertBreakdown(result.Upper, 120, (100, 1), (20, 1));
    }

    // ------------------------------------------------------------------ edge cases

    [Fact]
    public void Upper_alternative_is_not_offered_above_the_remaining_daily_limit()
    {
        var result = NoteSelector.Select(Notes((50, 1), (20, 2)), 80, maxUpperAmount: 80);

        AssertBreakdown(result.Lower, 70, (50, 1), (20, 1));
        Assert.Null(result.Upper);
    }

    [Fact]
    public void Upper_alternative_equal_to_the_remaining_limit_is_offered()
    {
        var result = NoteSelector.Select(Notes((50, 1), (20, 2)), 80, maxUpperAmount: 90);

        AssertBreakdown(result.Upper, 90, (50, 1), (20, 2));
    }

    [Fact]
    public void Amount_that_is_not_a_multiple_of_ten_gets_alternatives()
    {
        var result = NoteSelector.Select(Notes((20, 5)), 15);

        Assert.False(result.IsExact);
        Assert.Null(result.Lower);
        AssertBreakdown(result.Upper, 20, (20, 1));
    }

    [Fact]
    public void Amount_above_everything_in_the_machine_offers_only_the_maximum()
    {
        var result = NoteSelector.Select(Notes((100, 2), (20, 1)), 10_000);

        AssertBreakdown(result.Lower, 220, (100, 2), (20, 1));
        Assert.Null(result.Upper);
    }

    [Fact]
    public void Empty_stock_has_no_options()
    {
        var result = NoteSelector.Select(Notes((100, 0), (50, 0)), 100);

        Assert.False(result.HasAnyOption);
    }

    [Fact]
    public void Greedy_trap_is_solved()
    {
        // Greedy takes 500 and is left with 80 that cannot be paid; the optimum avoids the 500 note.
        var result = NoteSelector.Select(Notes((500, 1), (100, 0), (50, 1), (20, 4)), 130);

        AssertBreakdown(result.Exact, 130, (50, 1), (20, 4));
    }

    [Fact]
    public void Among_equal_note_counts_larger_denominations_are_preferred()
    {
        // 100x2 + 20x2 (4 notes) beats 100 + 50x2 + 20x2 (5 notes).
        var result = NoteSelector.Select(Notes((100, 2), (50, 2), (20, 5)), 240);

        AssertBreakdown(result.Exact, 240, (100, 2), (20, 2));
    }

    [Fact]
    public void Full_machine_is_solved_quickly()
    {
        var stock = Notes((500, 100), (100, 100), (50, 100), (20, 100));
        var stopwatch = Stopwatch.StartNew();

        var result = NoteSelector.Select(stock, 66_990);

        stopwatch.Stop();
        Assert.False(result.IsExact);
        Assert.Equal(66_980, result.Lower!.Amount);
        Assert.Equal(67_000, result.Upper!.Amount);
        Assert.True(stopwatch.ElapsedMilliseconds < 2_000, $"took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Non_positive_amount_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NoteSelector.Select(Notes((20, 1)), 0));
    }
}
