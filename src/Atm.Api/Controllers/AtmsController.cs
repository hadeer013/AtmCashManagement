using Atm.Business.Common;
using Atm.Business.Contracts;
using Atm.Business.Services;
using Microsoft.AspNetCore.Mvc;

namespace Atm.Api.Controllers;

/// <summary>ATM setup, lifecycle and cash handling by operations staff.</summary>
[ApiController]
[Route("api/atms")]
public sealed class AtmsController(AtmService atms, ConsistencyService consistency) : ControllerBase
{
    public const string UserHeader = "X-User";

    /// <summary>Creates an ATM in the UNCONFIGURED state.</summary>
    [HttpPost]
    [ProducesResponseType<AtmResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AtmResponse>> Create(CreateAtmRequest request, CancellationToken ct)
    {
        var atm = await atms.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { atmId = atm.Id }, atm);
    }

    /// <summary>Lists all ATMs with their cassettes.</summary>
    [HttpGet]
    public Task<List<AtmResponse>> List(CancellationToken ct) => atms.ListAsync(ct);

    /// <summary>Returns an ATM with its state and the notes in every cassette.</summary>
    [HttpGet("{atmId:guid}")]
    [ProducesResponseType<AtmResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<AtmResponse> Get(Guid atmId, CancellationToken ct) => atms.GetAsync(atmId, ct);

    /// <summary>
    /// Configures all 5 cassettes: exactly one DEPOSIT cassette and four RECYCLING cassettes (currency + denomination),
    /// 1–2 of them in a foreign currency. Allowed in UNCONFIGURED, CONFIGURED and MAINTENANCE.
    /// </summary>
    [HttpPut("{atmId:guid}/cassettes")]
    [ProducesResponseType<AtmResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<AtmResponse> ConfigureCassettes(
        Guid atmId,
        ConfigureCassettesRequest request,
        [FromHeader(Name = UserHeader)] string? user,
        CancellationToken ct)
        => atms.ConfigureCassettesAsync(atmId, request, user, ct);

    /// <summary>Records cash loaded by hand into recycling cassettes (LOAD movements). Allowed in CONFIGURED, LOADED and MAINTENANCE.</summary>
    [HttpPost("{atmId:guid}/cash-loads")]
    [ProducesResponseType<AtmResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<AtmResponse> LoadCash(
        Guid atmId,
        LoadCashRequest request,
        [FromHeader(Name = UserHeader)] string? user,
        CancellationToken ct)
        => atms.LoadCashAsync(atmId, request, user, ct);

    /// <summary>Records cash removed from cassettes (UNLOAD movements), e.g. emptying the deposit cassette. Allowed in LOADED and MAINTENANCE.</summary>
    [HttpPost("{atmId:guid}/cash-unloads")]
    [ProducesResponseType<AtmResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<AtmResponse> UnloadCash(
        Guid atmId,
        UnloadCashRequest request,
        [FromHeader(Name = UserHeader)] string? user,
        CancellationToken ct)
        => atms.UnloadCashAsync(atmId, request, user, ct);

    /// <summary>
    /// Changes the ATM state. Valid transitions: LOADED → IN_SERVICE, IN_SERVICE → MAINTENANCE, MAINTENANCE → IN_SERVICE.
    /// Switching to MAINTENANCE cancels active quotes and releases their reserved notes.
    /// </summary>
    [HttpPost("{atmId:guid}/state")]
    [ProducesResponseType<AtmResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<AtmResponse> ChangeState(
        Guid atmId,
        ChangeStateRequest request,
        [FromHeader(Name = UserHeader)] string? user,
        CancellationToken ct)
        => atms.ChangeStateAsync(atmId, request, user, ct);

    /// <summary>Immutable cassette movement ledger of an ATM, newest first.</summary>
    /// <param name="atmId">ATM id.</param>
    /// <param name="slot">Optional cassette slot filter.</param>
    /// <param name="type">Optional movement type filter, e.g. WITHDRAWAL or RECONCILIATION_ADJUSTMENT.</param>
    /// <param name="skip">Paging offset.</param>
    /// <param name="take">Page size (max 500).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{atmId:guid}/movements")]
    public Task<List<MovementResponse>> Movements(
        Guid atmId,
        [FromQuery] int? slot,
        [FromQuery] string? type,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        MovementType? movementType = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            movementType = Enum.TryParse<MovementType>(type.Replace("_", string.Empty), ignoreCase: true, out var parsed)
                           && Enum.IsDefined(parsed)
                ? parsed
                : throw new ValidationException(ErrorCodes.ValidationFailed,
                    $"Unknown movement type '{type}'. Valid: {string.Join(", ", Enum.GetValues<MovementType>().Select(t => t.ToApiName()))}.");
        }

        return atms.GetMovementsAsync(atmId, slot, movementType, skip, take, ct);
    }

    /// <summary>Compares stored cassette balances with balances recalculated from the movement ledger.</summary>
    [HttpGet("{atmId:guid}/consistency-check")]
    public Task<ConsistencyReport> ConsistencyCheck(Guid atmId, CancellationToken ct) => consistency.CheckAsync(atmId, ct);
}
