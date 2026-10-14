# SupportDesk

A multi-tenant customer service and support platform — ticketing, omnichannel inbox, SLA management, automation and
blueprints, knowledge base and customer portal, AI assistance, analytics, administration and SaaS subscriptions —
built as a modular monolith on **ASP.NET Core 10 + SQL Server + SignalR**, with **Angular 21** web apps and an
**Ionic** mobile app.

## Quick start (Windows, LocalDB)

Prerequisites: .NET SDK 10, Node 24 + npm 11, SQL Server LocalDB.

```bash
cd clients
npm ci
npm run build
cd ..
dotnet run --project src/SupportDesk.Api --no-launch-profile --urls http://localhost:5830
```

The first start creates the database `SupportDesk` on `(localdb)\MSSQLLocalDB` and seeds demo data (~1 minute).
Open http://localhost:5830 (agent desk), http://localhost:5830/portal/wings (customer portal),
http://localhost:5830/docs (API reference).

| Persona | Login | Password |
|---|---|---|
| Administrator (MFA: the e-mailed code is shown on screen in demo mode) | workspace `wings`, `admin@wings.demo` | `Desk@2026` |
| Support supervisor / agents | `priya@`, `arjun@`, `meera@`, `karthik@wings.demo` | `Desk@2026` |
| Other departments | `rahul@` (Implementation), `sana@` (Billing), `vikram@` (Customer Success), `neha@` (Knowledge), `dev@` (light agent), `lakshmi@` (Engineering), `anil@` (Analyst) `@wings.demo` | `Desk@2026` |
| Customer portal | `ramesh@abcretail.demo` (organisation), `lata@abcretail.demo` (branch), `joseph@abcretail.demo` (own) | `Portal@2026` |
| Second tenant | workspace `northwind`, `admin@northwind.demo` | `Desk@2026` |
| Platform operator | `platform@supportdesk.demo` | `Platform@2026` |

Development servers with live reload: `cd clients && npm run start:desk` (4830), `start:portal` (4831),
`start:mobile` (4832) — they proxy `/api` and `/hubs` to the API on 5830.

## Tests

```bash
dotnet test tests/SupportDesk.UnitTests
dotnet test tests/SupportDesk.IntegrationTests      # real API + throwaway LocalDB database per run
dotnet run --project tests/SupportDesk.LoadTests -- http://localhost:5830 20 60
cd clients && npm run check
```

## Repository layout

```
src/
  SupportDesk.Domain         entities and pure rules (business time, conditions, permissions, threading)
  SupportDesk.Contracts      API DTOs
  SupportDesk.Persistence    EF Core DbContext, tenant isolation, migrations
  SupportDesk.Application    use cases per module, engines, ports, outbox handlers, jobs, demo seed
  SupportDesk.Integrations   adapters: e-mail, messaging, AI providers, Wings ERP, Azure DevOps/GitHub, storage, cache
  SupportDesk.Workers        outbox dispatcher, durable jobs, recurring tasks
  SupportDesk.Api            REST API, SignalR hubs, auth, SPA hosting
  SupportDesk.WorkerHost     out-of-process worker service
clients/                     Angular workspace: @sd/core, @sd/ui, desk, portal, mobile (Ionic)
tests/                       unit, integration, load tests
docs/                        architecture, functional spec, development, deployment, security, guides
```

## Documentation

* [Architecture](docs/ARCHITECTURE.md) — module boundaries, tenancy, security, eventing, SLA engine, data model
* [Functional specification](docs/FUNCTIONAL-SPEC.md) — roles, use cases, business rules
* [Development guide](docs/DEVELOPMENT.md) — conventions for contributors
* [Deployment](docs/DEPLOYMENT.md) — IIS / Linux, configuration, scaling, backup
* [Security & compliance](docs/SECURITY.md)
