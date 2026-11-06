using Atm.Business.Common;
using Atm.Business.Contracts;
using Atm.Business.Services;
using Microsoft.AspNetCore.Mvc;

namespace Atm.Api.Controllers;

/// <summary>Manual cash counts and four-eyes approval.</summary>
[ApiController]
public sealed class ReconciliationsController(ReconciliationService reconciliations) : ControllerBase
{
    /// <summary>
    /// Records the hand-counted notes of every cassette. Differences up to TRY 1,000 (absolute value) are applied
    /// immediately as RECONCILIATION_ADJUSTMENT movements; larger ones wait for approval by a different user.
    /// </summary>
    [HttpPost("api/atms/{atmId:guid}/reconciliations")]
    [ProducesResponseType<ReconciliationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReconciliationResponse>> Create(
        Guid atmId,
        CreateReconciliationRequest request,
        [FromHeader(Name = AtmsController.UserHeader)] string? user,
        CancellationToken ct)
    {
        var result = await reconciliations.CreateAsync(atmId, user, request, ct);
        return CreatedAtAction(nameof(Get), new { reconciliationId = result.Id }, result);
    }

    /// <summary>Reconciliation history of an ATM.</summary>
    [HttpGet("api/atms/{atmId:guid}/reconciliations")]
    public Task<List<ReconciliationResponse>> ListForAtm(Guid atmId, CancellationToken ct)
        => reconciliations.ListForAtmAsync(atmId, ct);

    [HttpGet("api/reconciliations/{reconciliationId:guid}")]
    [ProducesResponseType<ReconciliationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<ReconciliationResponse> Get(Guid reconciliationId, CancellationToken ct)
        => reconciliations.GetAsync(reconciliationId, ct);

    /// <summary>Approves a reconciliation awaiting approval and applies its adjustments. Must be a different X-User than the submitter.</summary>
    [HttpPost("api/reconciliations/{reconciliationId:guid}/approve")]
    [ProducesResponseType<ReconciliationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ReconciliationResponse> Approve(
        Guid reconciliationId,
        [FromHeader(Name = AtmsController.UserHeader)] string? user,
        [FromBody] ReconciliationDecisionRequest? request,
        CancellationToken ct)
        => reconciliations.ApproveAsync(reconciliationId, user, request, ct);

    /// <summary>Rejects a reconciliation awaiting approval; nothing is applied and the cash must be recounted.</summary>
    [HttpPost("api/reconciliations/{reconciliationId:guid}/reject")]
    [ProducesResponseType<ReconciliationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ReconciliationResponse> Reject(
        Guid reconciliationId,
        [FromHeader(Name = AtmsController.UserHeader)] string? user,
        [FromBody] ReconciliationDecisionRequest? request,
        CancellationToken ct)
        => reconciliations.RejectAsync(reconciliationId, user, request, ct);
}

/// <summary>Operations reporting, ledger consistency, exchange rates and outbox inspection.</summary>
[ApiController]
public sealed class ReportsController(
    ReportService reports,
    ConsistencyService consistency,
    ExchangeRateService rates,
    OutboxQueryService outbox) : ControllerBase
{
    /// <summary>
    /// Per ATM and cassette: note count, reserved and available notes; totals per currency and the overall total in TRY.
    /// Filter with ?atmId=.
    /// </summary>
    [HttpGet("api/reports/operations")]
    public Task<OperationsReport> Operations([FromQuery] Guid? atmId, CancellationToken ct)
        => reports.GetOperationsReportAsync(atmId, ct);

    /// <summary>Ledger consistency check across the whole network.</summary>
    [HttpGet("api/consistency-check")]
    public Task<ConsistencyReport> ConsistencyCheck(CancellationToken ct) => consistency.CheckAsync(null, ct);

    [HttpGet("api/exchange-rates")]
    public Task<List<ExchangeRateResponse>> ExchangeRates(CancellationToken ct) => rates.ListAsync(ct);

    /// <summary>Updates the TRY value of one unit of a foreign currency.</summary>
    [HttpPut("api/exchange-rates/{currency}")]
    public Task<ExchangeRateResponse> UpdateExchangeRate(Currency currency, UpdateExchangeRateRequest request, CancellationToken ct)
        => rates.UpdateAsync(currency, request, ct);

    /// <summary>Outbox events (e.g. low-cash alerts) and whether the background service has sent them.</summary>
    [HttpGet("api/outbox")]
    public Task<List<OutboxMessageResponse>> Outbox([FromQuery] bool pendingOnly = false, [FromQuery] int take = 100, CancellationToken ct = default)
        => outbox.ListAsync(pendingOnly, take, ct);
}
