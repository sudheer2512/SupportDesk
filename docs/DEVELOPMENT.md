# SupportDesk — Development Guide & Conventions

## Run locally

```bash
dotnet build SupportDesk.slnx
dotnet run --project src/SupportDesk.Api --no-launch-profile --urls http://localhost:5830
```
Development (`ASPNETCORE_ENVIRONMENT=Development`) applies migrations to LocalDB database `SupportDesk` and seeds demo
data on first start (~20 s). API docs: http://localhost:5830/docs. Web clients: see `clients/` (`npm run start:desk`
→ 4830, `start:portal` → 4831, `start:mobile` → 4832; they proxy `/api` and `/hubs` to 5830).

Demo logins (tenant **wings**, password `Desk@2026`): `admin@wings.demo` (Administrator, MFA by e-mail code — the
code is shown on screen in demo mode), `priya@` (Support supervisor), `arjun@`, `meera@`, `karthik@` (support agents),
`rahul@`/`divya@` (Implementation), `sana@` (Billing), `vikram@` (Customer Success), `neha@` (Knowledge manager),
`dev@` (light agent / engineer), `lakshmi@` (Engineering manager), `anil@` (Analyst). Portal (brand `wings`, password
`Portal@2026`): `ramesh@abcretail.demo` (organisation scope), `lata@abcretail.demo` (location scope),
`joseph@abcretail.demo` (own tickets). Second tenant **northwind**: `admin@northwind.demo` / `Desk@2026`.
Platform operator: `platform@supportdesk.demo` / `Platform@2026`.

Reset the demo database: stop the API, drop LocalDB database `SupportDesk`, start again.

## Module conventions (backend)

* **Where code goes.** A module owns `src/SupportDesk.Application/<Module>/`, its registration file
  `<Module>/<Module>Module.cs` (`Add<Module>Module`), DTOs in `src/SupportDesk.Contracts/<Module>/`, controllers in
  `src/SupportDesk.Api/Controllers/<Module>/`, adapters in `src/SupportDesk.Integrations/<Area>/`, tests in
  `tests/*/<Module>/`. Do not edit another module's files; extend through the documented ports instead.
* **Services** are scoped classes injecting `DeskDbContext db`, `RequestContext ctx`, and kernel services
  (`IEventPublisher`, `IJobQueue`, `IAuditWriter`, `TenantSettingsService`, `LimitGuard`, `FileService`,
  `HtmlSanitization`, `ITenantCache`, `NotificationService`). Time comes from `db.Clock.UtcNow` (never `DateTime.UtcNow`).
* **Authorisation in the service:** `ctx.Require(Permissions.X)` / `ctx.RequireAny(...)`; scope ticket queries with
  `db.Tickets.VisibleTo(ctx, db)` or `TicketService.EnsureVisibleAsync`. Out-of-scope → `NotFoundException` (404).
* **Errors:** throw `DomainException(code, message, status)`, `NotFoundException`, `ForbiddenException`,
  `ConflictException`, `ValidationException(fieldErrors)`; the API maps them to problem details with `code`.
* **Events:** `events.Publish(EventTypes.X, aggregateType, id, payload)` inside the same unit of work as the change.
  React to events by implementing `IOutboxHandler` (idempotent; pick an `Order` from `HandlerOrder`).
* **Background work:** `IJobHandler` / `JobHandler<TPayload>` for durable jobs (`IJobQueue.Enqueue`), `IRecurringTask`
  for periodic work (fan out per tenant with `TenantScopeRunner`). Handlers must be idempotent (leases can expire).
* **History & audit:** `TicketHistory.Record(...)` for ticket activity; `IAuditWriter.Write(AuditActions.X, …)` for
  sensitive administrative actions (permissions, settings, exports, deletions, AI actions, credentials).
* **Rich text** is always passed through `HtmlSanitization.Sanitize`. **Secrets** through `ISecretProtector`.
* **New entities:** put them in a new file under `src/SupportDesk.Domain/<Module>/`, configure them with an
  `IEntityTypeConfiguration<T>` class in `src/SupportDesk.Persistence/Configurations/` (picked up automatically),
  access with `db.Set<T>()`. Tenant-owned entities derive from `TenantEntity`/`AuditedEntity`. **Do not add EF
  migrations in feature branches** — migrations are regenerated centrally when branches are merged. To run against a
  model that has new entities, use a private database with `Database__InitializeMode=EnsureCreated`.
* **DTOs** are records in Contracts; enum-valued fields are strings; dates UTC. Controllers are thin
  (`DeskControllerBase`, route `api/v1/...`), return DTOs, never entities.
* **Demo data** for a module: implement `IDemoSeedContributor` (ordered) and register it; use
  `DemoSeedContext` (ids of seeded users/customers/products/tickets, `AsAgentAsync`, `AsSystemAsync`, clock pinning).
* **Tests:** unit tests for pure rules (`tests/SupportDesk.UnitTests`); integration tests against LocalDB through
  `DeskApiFactory` (`tests/SupportDesk.IntegrationTests/Infrastructure`), each test creating its own tenant; security
  regression tests in `tests/SupportDesk.IntegrationTests/Security`.
* **Browser E2E tests** (`tests/SupportDesk.E2ETests`, xunit + Playwright on the installed Google Chrome — no Playwright
  browser download): the fixture builds the SPAs (`npm run build`), starts the API's build output as a separate process
  on a free port of `127.0.0.21` against a throwaway database `SupportDesk_E2E_<random>` with the demo seed, waits for
  `/health/ready` and a working demo login, and afterwards kills the process by PID and drops the database.
  `dotnet test tests/SupportDesk.E2ETests` (~2 min with the SPA build already done). Environment switches:
  `SUPPORTDESK_E2E=0` skips the suite, `SUPPORTDESK_E2E_SKIP_CLIENT_BUILD=1` reuses the built `wwwroot`,
  `SUPPORTDESK_E2E_HEADED=1` shows the browser, `SUPPORTDESK_E2E_CHROME` points at another Chrome. Failures leave
  screenshots and the API log in `TestResults/e2e`. Every test also fails on a Content-Security-Policy violation.
* **Load tests:** `tests/SupportDesk.LoadTests` — see `docs/PERFORMANCE.md`.

## Frontend conventions

See `clients/CONVENTIONS.md`: standalone components, signals, OnPush, lazy feature routes, API clients only in
`@sd/core`, UI primitives from `@sd/ui`, no mock data — every screen calls the real API.

## Tooling notes

* A GateGuard hook asks for facts before the first write of each file and before destructive shell commands;
  answer briefly and truthfully, then retry.
* The running API locks its DLLs: stop it before `dotnet build` of the solution.
