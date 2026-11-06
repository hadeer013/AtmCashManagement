using Atm.Business.Common;
using Atm.Business.Contracts;
using Atm.Business.Abstractions;
using Atm.Business.Domain;
using Atm.Business.Security;

namespace Atm.Business.Services;

public sealed class DepositService(IAtmDbContext db, AtmTransactionRunner runner, ICardHasher cardHasher, TimeProvider time)
{
    public async Task<DepositResponse> DepositAsync(Guid atmId, DepositRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ValidationException(ErrorCodes.ValidationFailed, "Request body is required.");
        }

        if (request.UnrecognizedCount < 0)
        {
            throw new ValidationException(ErrorCodes.InvalidNoteCount, "unrecognizedCount cannot be negative.");
        }

        var notes = (request.Notes ?? []).Select(n => new DepositNoteInput(n.Currency, n.Denomination, n.Count)).ToList();
        if (notes.Count == 0 && request.UnrecognizedCount == 0)
        {
            throw new ValidationException(ErrorCodes.EmptyDeposit, "A deposit must contain at least one note.");
        }

        var card = string.IsNullOrWhiteSpace(request.CardNumber) ? null : cardHasher.Protect(request.CardNumber);
        var now = time.GetUtcNow();
        var depositId = Guid.NewGuid();

        return await runner.ExecuteAsync(atmId, atm =>
        {
            // Unrecognised notes are handed back and never touch a cassette.
            var outcome = notes.Count == 0
                ? new DepositOutcome([], [])
                : atm.AcceptDeposit(notes, new OperationContext(now, null, ReferenceTypes.Deposit, depositId));

            // Validate state even for deposits that contain only unrecognised notes.
            atm.EnsureState("deposit", AtmState.InService);

            var deposit = Deposit.Create(depositId, atm.Id, card?.Hash, card?.Last4, request.UnrecognizedCount, outcome, now);
            db.Deposits.Add(deposit);
            return Task.FromResult(ToResponse(deposit));
        }, cancellationToken);
    }

    private static DepositResponse ToResponse(Deposit deposit)
    {
        var accepted = deposit.Placements
            .Select(p => new DepositPlacementResponse(p.CassetteSlot, p.CassetteType, p.Currency, p.Denomination, p.Count))
            .ToList();
        var totals = deposit.Placements
            .GroupBy(p => p.Currency)
            .OrderBy(g => g.Key)
            .Select(g => new CurrencyAmountResponse(g.Key, g.Sum(p => p.Count), g.Sum(p => (long)p.Count * p.Denomination)))
            .ToList();
        var returned = deposit.ReturnedNotes
            .Select(r => new ReturnedNotesResponse(r.Currency, r.Denomination, r.Count, r.Reason))
            .ToList();

        return new DepositResponse(
            deposit.Id,
            deposit.AtmId,
            deposit.CreatedAt,
            accepted,
            totals,
            new DepositReturnedResponse(deposit.UnrecognizedCount, returned, deposit.UnrecognizedCount + returned.Sum(r => r.Count)));
    }
}
