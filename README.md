# SupportDesk

SupportDesk is a multi-tenant customer service and support platform, inspired by Zoho Desk (an independent product,
not affiliated with Zoho). It covers ticketing, an omnichannel inbox and live chat, SLA management, automation and
blueprints, a knowledge base and customer portal, AI assistance, analytics, engineering escalation, administration and
SaaS subscriptions.

It is a modular monolith on **ASP.NET Core 10, SQL Server and SignalR**, with **Angular 21** web apps (agent desk,
customer portal) and an **Ionic** mobile app.

## Features

| Module | What it does |
|---|---|
| Ticket workspace | Queues and saved views (personal and shared, default-view precedence), keyset-paged list, three-panel workbench with tabs, bulk actions, recycle bin and restore, statuses and sub-statuses, SLA timers, resolve with customer notification or confirmation, close and reopen, merge, split, clone, links, watchers, tasks, time tracking, follow-ups, soft locks, presence and conflict handling |
| Composer | Replies, internal and handover notes, To/Cc/Bcc, templates with placeholders, attachments with malware scanning, server-side drafts, reply-collision review |
| Command Center | Personal and team KPIs, SLA risk board, workload, approvals, escalations, live updates |
| Inbox and live chat | E-mail, WhatsApp, SMS, Messenger, Instagram, phone and chat in one list; chat console with accept, transfer, per-agent capacity, templates, transcripts; embeddable chat widget with bot-first answers |
| Customers | Customer 360, contacts, identities, consent, portal access, duplicate detection and contact merge, organisation merge, customer groups, locations, service activities, ERP records |
| Products & Assets | Catalog with families, modules, versions and releases; installations, licences, assets, install base; contracts, entitlements, renewals; support-eligibility checks and audited overrides |
| SLA | Policies with conditions, targets per priority, pause statuses, multi-level escalations, business calendars and holidays, simulation, live breach board |
| Automation | Event, scheduled and webhook-triggered rules with versions, simulation, conflict checks and execution history; blueprints (guided state machines with mandatory fields and approvals); signed outgoing webhooks |
| Knowledge base | Rich-text articles with review and publish workflow, versions and diff, translations, access rules, feedback, community moderation, troubleshooting flows, import and export |
| AI | Summaries, classification, grounded reply drafts with citations, similar tickets, root cause, article drafts, natural-language analytics, semantic search, AI agents with human-approved proposals, anomaly detection, PII redaction and budgets (offline provider built in) |
| Analytics | Role dashboards, custom dashboards, ten standard reports, drill-down to tickets, Excel/CSV/PDF exports, scheduled e-mail reports |
| Engineering | Escalate tickets to a built-in board, Azure DevOps or GitHub; fix versions and releases flow back to tickets and customers |
| Teams & agents | Departments, teams, routing (round robin, load balanced, skills, territory), capacity, live monitor, shifts, leave with approvals, delegation |
| Customer portal | Per-brand portal with password or e-mail-code sign-in, ticket scopes (own, location, organisation), ticket form with deflection, replies, confirmation, reopen, CSAT, knowledge base, community, assistant, troubleshooting, anonymous web form |
| Mobile app | Agent and customer modes, offline cache, offline drafts and an idempotent send queue |
| Administration | Roles and permissions with escalation guard, ticket configuration and form builder, channels, brands, notifications and templates, security (MFA, password, sessions, IP allow-list, SSO), API keys and OAuth clients, Wings ERP, Teams/Slack, audit log with hash-chain verification, compliance (retention, legal holds, data-subject requests), configuration transfer, subscription and billing |
| Platform console | Tenants, plans, invoices, suspension, support access and dedicated tenant databases |
| Integration API | REST API (`/api/v1`, OpenAPI), API keys and OAuth client credentials, signed webhooks, SignalR hubs, Wings ERP ticket creation |

Some features are partial in this release. The [user guide](docs/USER-GUIDE.md) and [admin guide](docs/ADMIN-GUIDE.md)
mark them as **Partial** or **Not built yet**.

## Quick start (Windows, LocalDB)

Prerequisites:

* .NET 10 SDK
* Node.js 22.12 or later (Node 24 recommended) with npm 11
* SQL Server 2019+ or SQL Server Express LocalDB

```bash
cd clients
npm ci
npm run build          # desk → src/SupportDesk.Api/wwwroot, portal → wwwroot/portal
cd ..
dotnet run --project src/SupportDesk.Api --no-launch-profile --urls http://localhost:5830
```

The first start creates the database `SupportDesk` on `(localdb)\MSSQLLocalDB` and seeds demo data (about a minute).
Then open:

* http://localhost:5830 — agent desk
* http://localhost:5830/portal/wings — customer portal
* http://localhost:5830/docs — API reference (Swagger UI); the OpenAPI document is at `/openapi/v1.json`

To use another SQL Server, set `ConnectionStrings__SupportDesk`. To reset the demo, stop the API, drop the database and
start again.

### Demo logins

| Persona | Login | Password |
|---|---|---|
| Administrator (MFA by e-mail code; the code is shown on screen in demo mode) | workspace `wings`, `admin@wings.demo` | `Desk@2026` |
| Support supervisor | `priya@wings.demo` | `Desk@2026` |
| Support agents | `arjun@`, `meera@`, `karthik@wings.demo` | `Desk@2026` |
| Other departments | `rahul@`, `divya@` (Implementation), `sana@` (Billing), `vikram@` (Customer Success), `neha@` (Knowledge manager), `dev@` (light agent), `lakshmi@` (Engineering manager), `anil@` (Analyst) `wings.demo` | `Desk@2026` |
| Customer portal (brand `wings`) | `ramesh@abcretail.demo` (organisation), `lata@abcretail.demo` (location), `joseph@abcretail.demo` (own tickets) | `Portal@2026` |
| Second tenant | workspace `northwind`, `admin@northwind.demo` | `Desk@2026` |
| Platform operator | `platform@supportdesk.demo` (at `/platform/login`) | `Platform@2026` |

### Development servers

For live reload, run the API on port 5830 and start a client:

```bash
cd clients
npm run start:desk     # http://localhost:4830
npm run start:portal   # http://localhost:4831
npm run start:mobile   # http://localhost:4832
```

They proxy `/api` and `/hubs` to the API on 5830. See [MOBILE.md](docs/MOBILE.md) for native Android and iOS builds.

## Tests

```bash
dotnet test tests/SupportDesk.UnitTests
dotnet test tests/SupportDesk.IntegrationTests      # real API host + a throwaway LocalDB database per run
dotnet run --project tests/SupportDesk.LoadTests -- http://localhost:5830 20 60
cd clients && npm run check                         # type-checks the libraries, desk, portal and mobile apps
```

End-to-end browser tests live in `tests/SupportDesk.E2ETests`. They drive the desk and portal in Chrome against a
running API; see that project for its prerequisites and settings.

## Repository layout

```
src/
  SupportDesk.Domain         entities and pure rules (business time, conditions, permissions, threading)
  SupportDesk.Contracts      API DTOs
  SupportDesk.Persistence    EF Core DbContext, tenant isolation, migrations
  SupportDesk.Application    use cases per module, engines, ports, outbox handlers, jobs, demo seed
  SupportDesk.Integrations   adapters: e-mail, messaging, AI providers, Wings ERP, Azure DevOps/GitHub, storage, cache
  SupportDesk.Workers        outbox dispatcher, durable jobs, recurring tasks
  SupportDesk.Api            REST API, SignalR hubs, auth, chat widget, SPA hosting
  SupportDesk.WorkerHost     out-of-process worker service
clients/                     Angular workspace: @sd/core, @sd/ui, desk, portal, mobile (Ionic)
tests/                       unit, integration, end-to-end and load tests
tools/                       developer tools (api-endpoint-groups.mjs generates the endpoint table in docs/API.md)
docs/                        guides and reference documentation
```

## Documentation

| Document | Audience | Contents |
|---|---|---|
| [User guide](docs/USER-GUIDE.md) | Agents, supervisors, customers | Everyday tasks in the desk, portal and mobile app |
| [Admin guide](docs/ADMIN-GUIDE.md) | Tenant administrators, platform operators | Every administration area, the platform console and the permission reference |
| [API reference](docs/API.md) | Integrators | Authentication, errors, pagination, webhooks, realtime hubs, endpoint groups and verified recipes |
| [Wings ERP integration](docs/integrations/WINGS-ERP.md) | Integrators, administrators | ERP ticket creation, context panel, sync rules, simulator |
| [Engineering integration](docs/integrations/ENGINEERING.md) | Administrators, integrators | Azure DevOps, GitHub and the built-in board |
| [Functional specification](docs/FUNCTIONAL-SPEC.md) | Product, QA | Roles, use cases, business rules |
| [Architecture](docs/ARCHITECTURE.md) | Developers | Module boundaries, tenancy, security, eventing, SLA engine, data model |
| [Development guide](docs/DEVELOPMENT.md) | Developers | Running locally, module and frontend conventions |
| [Deployment](docs/DEPLOYMENT.md) | Operators | IIS and Linux, configuration, scaling, dedicated databases, backup, upgrades |
| [Security & compliance](docs/SECURITY.md) | Security reviewers | Threats and controls |
| [Metrics](docs/METRICS.md) | Analysts, developers | Analytics metric definitions |
| [Mobile](docs/MOBILE.md) | Developers | Mobile app architecture, offline design, native builds and push |
| [Performance](docs/PERFORMANCE.md) | Operators, developers | Performance and load-testing notes |
