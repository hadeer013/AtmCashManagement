# ATM Cash Management Backend

Cash management backend for a bank's ATM network: cassette configuration, cash loading, two-step withdrawals with
optimal note selection, deposits, manual-count reconciliation with four-eyes approval, an immutable movement ledger,
reporting and low-cash alerts.

**Stack:** ASP.NET Core 9 Web API · EF Core 9 (code-first, migrations) · SQL Server 2022 · xUnit · Docker Compose · Swagger · Postman

---

## 1. How to run

### With Docker (API + SQL Server)

```bash
docker compose up --build
```

| What | URL |
|---|---|
| Swagger UI | http://localhost:8080/swagger |
| Health check | http://localhost:8080/health |
| SQL Server | `localhost,14333` · user `sa` · password `Atm_Str0ng!Passw0rd` (override with `MSSQL_SA_PASSWORD`) |

The API waits for the SQL Server health check, then **applies EF Core migrations automatically on startup** (with a
retry loop while the database is still starting). Exchange rates are seeded by the initial migration.

### Without Docker

Requires the .NET 9 SDK and a SQL Server instance (LocalDB is the default in `appsettings.json`):

```bash
dotnet run --project src/Atm.Api
# or point it at another server:
ConnectionStrings__AtmDb="Server=localhost,14333;Database=AtmCashManagement;User Id=sa;Password=...;TrustServerCertificate=True" dotnet run --project src/Atm.Api
```

### Tests

```bash
dotnet test
```

| Project | What it covers |
|---|---|
| `tests/Atm.Business.Tests` (46 tests) | **The 8 B3 acceptance tests** (`NoteSelectorAcceptanceTests`) plus algorithm edge cases; domain rules: cassette configuration, lifecycle, capacity, fill-ratio dispensing, deposit routing/overflow, ledger replay, low-cash alerts, reconciliation threshold, four-eyes, card hashing. No database needed. |
| `tests/Atm.IntegrationTests` (24 tests) | Hosts the real API (`WebApplicationFactory`) against a **real SQL Server database** created for the run and dropped afterwards. Includes the **B6 concurrency tests** (30 parallel quotes for 10 notes, mixed parallel quotes, 10 parallel confirms with one idempotency key, the same card at two ATMs at once), quote expiry with a controllable clock, idempotency, daily limits, deposits, four-eyes approval, the outbox, ledger immutability and the consistency check. |

By default the integration tests use `(localdb)\MSSQLLocalDB`. To use another server (e.g. the compose SQL Server):

```bash
ATM_TEST_SQL_CONNECTION="Server=localhost,14333;User Id=sa;Password=Atm_Str0ng!Passw0rd;TrustServerCertificate=True" dotnet test
```

### Postman

Import `postman/ATM-Cash-Management.postman_collection.json` and `postman/ATM-Local.postman_environment.json`, then run
the collection with the Collection Runner (or `npx newman run postman/ATM-Cash-Management.postman_collection.json -e postman/ATM-Local.postman_environment.json`).

- **Folder 1** is the required end-to-end scenario, in order: create ATM → configure cassettes → load cash → put in
  service → request quote → confirm (plus an idempotent replay) → deposit → switch to maintenance → reconcile → report →
  consistency check.
- **Folder 2** demonstrates four-eyes approval: a large difference awaits approval, returning to service is blocked,
  self-approval is refused, a second user approves, and the ATM goes back into service.

Every request carries test assertions, and ids flow between requests through collection variables.

---

## 2. API overview

| Method | Endpoint | Purpose |
|---|---|---|
| POST | `/api/atms` | Create an ATM (`UNCONFIGURED`) |
| GET | `/api/atms`, `/api/atms/{id}` | ATMs, with notes per cassette and denomination (A12) |
| PUT | `/api/atms/{id}/cassettes` | Configure all 5 cassettes |
| POST | `/api/atms/{id}/cash-loads` | Record cash loaded by hand (`LOAD`) |
| POST | `/api/atms/{id}/cash-unloads` | Record cash removed, e.g. emptying the deposit cassette (`UNLOAD`) |
| POST | `/api/atms/{id}/state` | `{ "targetState": "IN_SERVICE" \| "MAINTENANCE" }` |
| POST | `/api/atms/{id}/withdrawal-quotes` | Quote: breakdown or alternatives; reserves the notes for 60 s |
| GET | `/api/atms/{id}/withdrawal-quotes/{quoteId}` | Quote status |
| POST | `/api/atms/{id}/withdrawal-quotes/{quoteId}/confirm` | Confirm (`Idempotency-Key` header, optional `{ "option": "LOWER" \| "UPPER" }`) |
| POST | `/api/atms/{id}/withdrawal-quotes/{quoteId}/cancel` | Cancel and release |
| POST | `/api/atms/{id}/deposits` | Deposit notes, plus the count of unrecognised notes |
| POST | `/api/atms/{id}/reconciliations` | Hand count (`X-User` header) |
| POST | `/api/reconciliations/{id}/approve` \| `/reject` | Four-eyes decision (`X-User` must differ from the submitter) |
| GET | `/api/atms/{id}/movements?slot=&type=` | Movement ledger |
| GET | `/api/atms/{id}/consistency-check`, `/api/consistency-check` | Stored balances vs. ledger |
| GET | `/api/reports/operations?atmId=` | Operations report |
| GET/PUT | `/api/exchange-rates`, `/api/exchange-rates/{currency}` | Rate table used for TRY values |
| GET | `/api/outbox?pendingOnly=` | Outbox events (low-cash alerts) and their sent state |
| GET | `/health` | Health check, including the database |

Enums are serialised as `SNAKE_CASE` strings (`IN_SERVICE`, `RECONCILIATION_ADJUSTMENT`, …).

### Error model

Every error is an RFC 7807 `application/problem+json` response with a stable `errorCode`, and where useful,
structured context such as `currentState`, `allowedStates` or `remaining`:

```json
{
  "type": "https://errors.atm-cash.local/operation-not-allowed-in-state",
  "title": "Conflict with current state",
  "status": 409,
  "detail": "Operation 'request withdrawal quote' is not allowed while the ATM is MAINTENANCE. Allowed state(s): IN_SERVICE.",
  "errorCode": "OPERATION_NOT_ALLOWED_IN_STATE",
  "currentState": "MAINTENANCE",
  "allowedStates": ["IN_SERVICE"],
  "traceId": "..."
}
```

| HTTP | Meaning | Examples |
|---|---|---|
| 400 | Invalid input | `VALIDATION_FAILED`, `INVALID_AMOUNT`, `INVALID_CASSETTE_CONFIGURATION`, `IDEMPOTENCY_KEY_REQUIRED`, `USER_HEADER_REQUIRED`, `QUOTE_OPTION_REQUIRED` |
| 403 | Not permitted | `SAME_USER_APPROVAL` |
| 404 | Not found | `ATM_NOT_FOUND`, `QUOTE_NOT_FOUND`, `RECONCILIATION_NOT_FOUND` |
| 409 | Conflicts with current state | `INVALID_STATE_TRANSITION`, `OPERATION_NOT_ALLOWED_IN_STATE`, `QUOTE_EXPIRED`, `QUOTE_CANCELLED`, `QUOTE_ALREADY_CONFIRMED`, `RECONCILIATION_PENDING`, `CASSETTE_NOT_EMPTY` |
| 422 | Business rule violated | `DAILY_LIMIT_EXCEEDED`, `NO_PAYABLE_AMOUNT`, `CURRENCY_NOT_AVAILABLE`, `CASSETTE_CAPACITY_EXCEEDED`, `IDEMPOTENCY_KEY_REUSED` |

---

## 3. Architecture and key design decisions

### Layered architecture with a rich domain

```
Atm.Api          Controllers, ProblemDetails mapping, Swagger, health check, startup migration
   │
   ▼
Atm.Business     Domain (AtmMachine aggregate, Cassette, WithdrawalQuote, Reconciliation, ...),
                 NoteSelector algorithm, use-case services, IAtmDbContext port, card hashing
   ▲
   │ implements
Atm.DataAccess   EF Core DbContext + mappings, migrations, SQL Server locking, outbox processor,
                 quote-expiry worker
```

- **The business rules live in the domain objects, not in the services.** `AtmMachine` is the aggregate root and owns
  its 5 cassettes. It enforces the lifecycle, the configuration rules (1 deposit + 4 recycling, 1–2 foreign), capacity,
  fill-ratio dispensing, deposit routing and reconciliation. The services are thin: open a transaction, lock, load,
  call the domain, save. All business rules can therefore be unit tested without a database.
- **The ATM is the consistency boundary.** Every rule that matters spans cassettes (foreign-currency count, fill-ratio
  choice, deposit overflow, reservation), so one ATM is also the unit of locking.
- **A monolith on purpose.** B6 (no double reservation) and B10 (outbox in the *same* transaction) both rely on a
  single database transaction. Splitting the system into services would turn every withdrawal into a distributed
  transaction for no benefit.
- **A pragmatic dependency rule.** The business layer depends on EF Core's provider-agnostic abstractions (`DbSet`,
  LINQ) through `IAtmDbContext`. It never depends on SQL Server: locking, the transaction wrapper and unique-violation
  detection sit behind that interface in `Atm.DataAccess`. A repository per aggregate would add boilerplate without
  changing the behaviour.

### Note selection (B3): bounded "fewest notes" dynamic programming

Greedy (largest note first) is wrong when stock is limited: for 160 from `50×3, 20×10` it takes `3×50` and gets stuck on 10.
`NoteSelector` solves a **bounded knapsack minimising the number of notes**:

- `best[s]` is the minimum number of notes that pays exactly `s` from the available stock, computed over every
  reachable amount (in units of the GCD of the denominations, i.e. 10).
- One pass answers all three questions:
  - **exact**: `best[requested]` is reachable;
  - **lower**: the largest reachable amount below the request;
  - **upper**: the smallest reachable amount above the request, but only up to the customer's **remaining** daily limit.
- **Tie-break:** among breakdowns with the same note count, more of the larger denominations wins. This makes results
  deterministic and keeps small notes in the machine.
- **Cost:** at most 400 notes and 20,000 amount units per currency, i.e. a few million integer operations, a few
  milliseconds. A unit test checks a fully loaded machine.
- **Input:** the selector receives only **available** notes (physical minus reserved), so it can never offer a note
  that another quote holds.

### Concurrency (B6)

Every operation that changes cash runs through `AtmTransactionRunner`:

```
BEGIN TRAN → SELECT … FROM Atms WITH (UPDLOCK, HOLDLOCK, ROWLOCK) → load aggregate → domain logic
          → SaveChanges (balances + ledger + outbox) → COMMIT
```

- **Pessimistic row lock on the ATM.** Quotes, confirms, deposits, loads, reconciliations and expiries for one ATM are
  serialised, so two quotes can never read the same "available" notes. Different ATMs never block each other. An ATM
  serves one customer at a time, so serialising per ATM costs nothing. It is also simpler to reason about than
  optimistic retries, and it gives deterministic results under load.
- **Cross-ATM daily limit.** B6 only mentions one ATM, but the daily limit has the same race across ATMs: one card at
  two machines at the same moment. The quote also takes `sp_getapplock` on `(card hash, currency)`. The lock order is
  always ATM → card, so there are no deadlocks.
- **Safety net.** `CassetteBalance` has a `rowversion` concurrency token, so a code path that skipped the lock would
  fail loudly instead of losing an update. A check constraint also keeps `0 ≤ Reserved ≤ Count ≤ 100` in the database.
- **Proof.** `ConcurrencyTests` fires 30 parallel quotes at a machine holding 10 notes. Exactly 10 succeed, the
  reserved count is exactly 10, and the ledger stays consistent.

### Two-step withdrawal and idempotency (B4)

- **Quote.**
  - The quote stores the offered options and reserves notes per cassette (`RESERVATION` movements and
    `QuoteReservations` rows).
  - When there are two alternatives, it reserves the per-denomination **maximum** of both. For example, lower
    `50+20` and upper `50+20+20` reserve `50×1, 20×2`, so either option can be confirmed and no other quote can be
    offered those notes.
- **Confirm.**
  - Under the ATM lock, the `Idempotency-Key` is looked up first. If the same key was used for the same request, the
    stored response is returned with `Idempotent-Replayed: true`. This happens even after the quote has expired, and
    two simultaneous retries cannot both debit.
  - A key that was used for a different request returns `IDEMPOTENCY_KEY_REUSED`.
  - Otherwise the reservation is released, the chosen notes are debited from the cassettes they were reserved in
    (`RESERVATION_RELEASE` + `WITHDRAWAL`), and the response is stored under the key in the same transaction. The
    key's primary-key constraint covers the remaining race (the same key at two ATMs).
- **Expiry (60 s)** is enforced in two ways:
  - **lazily**: every quote, confirm and cancel treats `now ≥ ExpiresAt` as expired and releases the notes;
  - **actively**: `QuoteExpiryWorker`, a hosted service, sweeps lapsed quotes.

  Correctness therefore never depends on the worker's timing. Time comes from an injected `TimeProvider`, so the
  tests move the clock forward instead of waiting.
- **Switching to maintenance** cancels every active quote and releases its notes, so cash is never held for a
  customer while staff are handling it.

### Daily limit and card data (B5)

- The used amount is the sum of **confirmed** amounts plus the **holds of active, unexpired quotes** for the card,
  currency and business day. It is computed from the quotes, so there is no separate counter that could drift.
- A quote's hold is its largest offered amount.
- Card numbers are never stored. Only `HMAC-SHA256(secret, PAN)` and the last four digits are kept. A *keyed* hash is
  used because a plain SHA-256 of a 16-digit number with a known BIN can be brute-forced. The secret comes from
  configuration or an environment variable.

### Movement ledger (B8)

- **One choke point.** Every balance change goes through `AtmMachine.ApplyMovement`, which updates the balance **and**
  appends a `CassetteMovement` in the same call. No code path can change a balance without writing a movement. The
  movements are persisted in the same `SaveChanges` and transaction as the balances.
- **Replay rules:**
  - `Count = LOAD − UNLOAD − WITHDRAWAL + DEPOSIT + RECONCILIATION_ADJUSTMENT (signed)`
  - `Reserved = RESERVATION − RESERVATION_RELEASE`

  Reservations never change the physical count. A confirm writes `RESERVATION_RELEASE` + `WITHDRAWAL`, which keeps
  the replay rules trivial.
- **Immutability is enforced twice:** the DbContext refuses to update or delete movements, and a SQL Server
  `INSTEAD OF UPDATE, DELETE` trigger rejects it at database level. An integration test checks both.
- **Each movement records** the balance after the change, the reference (quote, deposit, reconciliation, load), the
  user and a timestamp.
- **`/consistency-check`** recomputes every (cassette, currency, denomination) balance from the ledger in SQL and
  reports any difference. A test tampers with a stored balance and checks that the endpoint detects it.

### Reconciliation with four-eyes (B9)

- **The difference is computed per cassette, currency and denomination.** The deposit cassette is counted per
  currency and denomination because it holds a mix.
- **Valuation.** Each difference is valued in TRY with the seeded `ExchangeRates` table, and the rate used is stored
  on the reconciliation line.
- **Threshold.** The total compared with TRY 1,000 is the **sum of absolute differences**, so a TRY 600 surplus cannot
  hide a TRY 600 shortage.
- **Up to the threshold:** `RECONCILIATION_ADJUSTMENT` movements are written immediately.
- **Above it:** the reconciliation is stored as `AWAITING_APPROVAL` and **no balance changes until a different
  `X-User` approves it**. While it waits:
  - the ATM cannot go back into service;
  - cash and configuration changes are blocked;
  - a second reconciliation cannot be opened.
- **Rejecting** leaves the balances unchanged, so the cash can be recounted.
- **Staleness check.** At approval the system checks that the balances have not changed since the count.

### Deposits (B7)

1. Notes go to the matching recycling cassette. With two matching cassettes, the less-filled one is used first.
2. Overflow goes to the deposit cassette.
3. Anything that still does not fit is returned with reason `CASSETTE_FULL`.

Unrecognised notes are only counted and returned; they never touch a cassette or the ledger. The response lists,
per cassette, how many notes of each currency and denomination went there, and lists the returned notes separately.

### Reporting and low-cash alerts (B10)

- **The operations report** shows, per ATM and per cassette, the count, reserved and available notes and a low-cash
  flag, plus a per-denomination summary (A12), totals per currency and the overall TRY total. A network-wide
  aggregate is included.
- **Low-cash alerts.** When a recycling cassette's **available** notes cross below 10% of capacity (< 10 notes), the
  aggregate raises a `LowCashAlert` domain event. An alert fires on the downward crossing only, so repeated
  withdrawals do not flood the outbox. The alerts are also raised when an ATM enters service with low cassettes.
- **Transactional outbox.** `AtmDbContext.SaveChanges` turns domain events into `OutboxMessages` rows **in the same
  transaction** as the operation.
- **Outbox processor.** `OutboxProcessor`, a hosted service, claims batches with `UPDLOCK, READPAST`, so several API
  instances can run it safely. It logs each event and marks it as sent. Delivery is at-least-once.

---

## 4. Assumptions (ambiguities and the decisions taken)

| # | Ambiguity | Decision |
|---|---|---|
| 1 | A6 says that 1 to 2 recycling cassettes "may" be foreign. Is a foreign cassette mandatory? | **Mandatory:** at least 1 and at most 2 foreign recycling cassettes. USD and EUR may be mixed. As a result there are always 2–3 TRY cassettes. |
| 2 | A9 asks for "the closest amount", B3 for two alternatives. | B3 wins: always return LOWER and/or UPPER. A9 is then satisfied either way. |
| 3 | B4: with two alternatives, which notes are reserved, and what does confirm pay? | Reserve the per-denomination maximum of both alternatives. Confirm must name `option` when the quote has two, and the unused reserved notes are released. |
| 4 | What counts towards the daily limit while a quote with two alternatives is active? | The **largest** offered amount. |
| 5 | The requested amount itself is above the remaining daily limit. | Rejected with `DAILY_LIMIT_EXCEEDED`, with `remaining` in the response. No lower alternative is offered. |
| 6 | "Daily" limit: which day? | Calendar day in **Europe/Istanbul** (configurable: `Atm:BusinessTimeZone`). A quote counts against the day it was created. |
| 7 | B2 fill ratio for duplicate denominations: physical or available? Drain one cassette or alternate? | **Available** fill ratio (count − reserved) / capacity. The fuller cassette is drained first; ties go to the lower slot number. For deposits the **less**-filled matching cassette is filled first. |
| 8 | B2 lists cash loading only for UNCONFIGURED/CONFIGURED/MAINTENANCE, so cash could not be topped up in LOADED. | Loading is also allowed in **LOADED**, before the first IN_SERVICE. |
| 9 | Explicit transitions | `LOADED → IN_SERVICE`, `IN_SERVICE ⇄ MAINTENANCE`. `UNCONFIGURED → CONFIGURED` happens by configuring and `CONFIGURED → LOADED` by loading cash. Anything else returns `INVALID_STATE_TRANSITION`. |
| 10 | Reconfiguring a cassette that still holds notes. | Rejected (`CASSETTE_NOT_EMPTY`); unload it first. An unchanged cassette may keep its notes. |
| 11 | UNLOAD is a ledger type, but no requirement describes unloading. | Added `cash-unloads` (LOADED or MAINTENANCE). The deposit cassette can only be emptied completely. |
| 12 | B9 "total TRY value of the differences": net or absolute? | **Sum of absolute values.** Strictly greater than 1,000 needs approval; exactly 1,000 does not. |
| 13 | B9: is the adjustment written before or after approval? | **After** approval. Balances never change without the second user. |
| 14 | B9: what happens on rejection? | Added `reject`. Nothing is applied and staff recount. Any user, including the submitter, may reject; approval must be by a different user. User names are compared case-insensitively. |
| 15 | Which cassettes must be counted in a reconciliation? | All 5. The deposit cassette is counted per currency and denomination; a pair that is not listed counts as 0. |
| 16 | Reconciliation in UNCONFIGURED/CONFIGURED | Allowed in MAINTENANCE and CONFIGURED (where it is trivial). UNCONFIGURED has no cassettes. |
| 17 | Does a deposit need a card, and is an account credited? | The card is optional (it is hashed if given). Crediting the customer account is out of scope. |
| 18 | B7 "for each note, which cassette it went to" | Grouped per (cassette, currency, denomination) with a count, rather than one line per physical note. |
| 19 | Low-cash alert: available or physical count? When does it fire? | **Available** notes < 10% of capacity, on the downward crossing, and on entering service. Only recycling cassettes are checked. |
| 20 | Amounts that are not a multiple of 10, or ≤ 0 | ≤ 0 is rejected. Other amounts get alternatives (15 → upper 20). |
| 21 | Idempotency key scope | Global and unique, stored with a hash of (ATM, quote, option). Only successful confirms are stored, so a failed confirm can be retried with the same key. |
| 22 | Exchange rates | Seed data: TRY 1, USD 41.50, EUR 48.25 (placeholders, editable via `PUT /api/exchange-rates/{currency}`). The rate used is stored on each reconciliation line. |
| 23 | Card number format | 12–19 digits; spaces and dashes are ignored. A Luhn check is not enforced, so test cards work. |
| 24 | Several active quotes at one ATM | Allowed, because B6 assumes concurrent quotes. Each quote reserves its own notes. |

---

## 5. Unfinished items and known limitations

- **No authentication or authorisation.** `X-User` is trusted as given, as the assignment allows. In production it
  would come from an identity provider, and approval rights would depend on the user's role.
- **No physical dispense feedback.** A jam or partial dispense after confirm, or a customer not taking the cash, is
  not modelled. Confirm assumes the cash was paid out.
- **No notes-per-transaction cap.** Real dispensers cap a payout at about 40–50 notes; this is not enforced.
- **Expiry granularity.** Lapsed reservations are released immediately whenever the ATM is touched, and otherwise by
  the background sweep (every 5 s by default). Between those two events the operations report can still show the
  notes as reserved.
- **Idempotency records** are kept indefinitely. A retention job (e.g. delete after 24 h) would be added in production.
- **Outbox delivery** only logs the event (no broker integration). There is no retry backoff or dead-letter handling
  beyond the `Attempts` and `LastError` fields.
- **Exchange-rate history.** Rates are a current-value table with no history or effective dates; the rate actually
  used is snapshotted on each reconciliation instead.
- **Ledger growth.** The consistency check recomputes balances from the full ledger. At large scale it would use
  periodic snapshots or checkpoints.
- **Demo secrets.** The compose file uses demo values for the SA password and the card-hash secret; real deployments
  must provide their own.

---

## 6. Use of AI tools


- **Tool:** Claude Code (Anthropic's Claude) in VS Code.
- **What it was used for:** I gave claude code the whole RSD file and tell him to locate all missing business logic,
contractdictions and also the edge cases .Then the tool started building the system from A to Z with correspoinding 
  files.

