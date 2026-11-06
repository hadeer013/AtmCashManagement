using Atm.Business.Contracts;
using Atm.Business.Services;
using Microsoft.AspNetCore.Mvc;

namespace Atm.Api.Controllers;

/// <summary>Customer withdrawals (quote → confirm/cancel).</summary>
[ApiController]
[Route("api/atms/{atmId:guid}/withdrawal-quotes")]
public sealed class WithdrawalsController(WithdrawalService withdrawals) : ControllerBase
{
    public const string IdempotencyHeader = "Idempotency-Key";

    /// <summary>
    /// Step 1: returns the exact breakdown with the fewest notes, or LOWER/UPPER alternatives, and reserves the offered
    /// notes for 60 seconds. The daily card limit (confirmed + reserved) is checked here.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<QuoteResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<QuoteResponse>> CreateQuote(Guid atmId, CreateQuoteRequest request, CancellationToken ct)
    {
        var quote = await withdrawals.CreateQuoteAsync(atmId, request, ct);
        return CreatedAtAction(nameof(GetQuote), new { atmId, quoteId = quote.QuoteId }, quote);
    }

    /// <summary>Returns a quote and its current status.</summary>
    [HttpGet("{quoteId:guid}")]
    [ProducesResponseType<QuoteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<QuoteResponse> GetQuote(Guid atmId, Guid quoteId, CancellationToken ct)
        => withdrawals.GetQuoteAsync(atmId, quoteId, ct);

    /// <summary>
    /// Step 2: confirms the quote and debits the cash. Requires an Idempotency-Key header; repeating the request with
    /// the same key returns the same result (header Idempotent-Replayed: true) without debiting twice.
    /// </summary>
    [HttpPost("{quoteId:guid}/confirm")]
    [ProducesResponseType<WithdrawalResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<WithdrawalResponse>> Confirm(
        Guid atmId,
        Guid quoteId,
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        [FromBody] ConfirmQuoteRequest? request,
        CancellationToken ct)
    {
        var result = await withdrawals.ConfirmAsync(atmId, quoteId, idempotencyKey, request, ct);
        Response.Headers["Idempotent-Replayed"] = result.Replayed ? "true" : "false";
        return Ok(result.Response);
    }

    /// <summary>Cancels an active quote and releases its reserved notes. Cancelling twice is harmless.</summary>
    [HttpPost("{quoteId:guid}/cancel")]
    [ProducesResponseType<QuoteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<QuoteResponse> Cancel(Guid atmId, Guid quoteId, CancellationToken ct)
        => withdrawals.CancelAsync(atmId, quoteId, ct);
}

/// <summary>Customer deposits.</summary>
[ApiController]
[Route("api/atms/{atmId:guid}/deposits")]
public sealed class DepositsController(DepositService deposits) : ControllerBase
{
    /// <summary>
    /// Accepts recognised notes: into the matching recycling cassette, overflow into the deposit cassette, and anything
    /// that still does not fit (plus unrecognised notes) is returned to the customer.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<DepositResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DepositResponse>> Deposit(Guid atmId, DepositRequest request, CancellationToken ct)
    {
        var deposit = await deposits.DepositAsync(atmId, request, ct);
        return StatusCode(StatusCodes.Status201Created, deposit);
    }
}
