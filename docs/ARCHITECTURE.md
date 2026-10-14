# SupportDesk — Solution Architecture

SupportDesk is a multi-tenant customer service platform (ticketing, omnichannel, SLA, automation, knowledge, portal,
AI, analytics, administration, subscriptions). It is a **modular monolith**: one deployable API host (plus an optional
out-of-process worker host), strict module boundaries inside, an outbox-driven event stream between modules.

## 1. Solution layout

| Project | Responsibility | Depends on |
|---|---|---|
| `SupportDesk.Domain` | Entities, enums, pure rules: business-time calendar, condition evaluator, permission catalog, e-mail threading, text normalisation, event type names. No I/O. | — |
| `SupportDesk.Contracts` | Public API DTOs (requests/responses). Enums are strings on the wire. Shareable with .NET clients. | — |
| `SupportDesk.Persistence` | `DeskDbContext` (SQL Server), model configuration (keys, FKs, indexes), tenant isolation (query filters + write guard), `RequestContext`, tenant counters, dedicated-database routing, migrations. | Domain |
| `SupportDesk.Application` | Use-case services per module, engines (SLA, routing, entitlement, automation…), ports (`IFileStorage`, `ISystemMailer`, `IRealtimePublisher`, AI/email/messaging ports…), outbox handlers, jobs, recurring tasks, demo seed. | Domain, Contracts, Persistence |
| `SupportDesk.Integrations` | Adapters implementing ports: file storage, malware scanning, system mail, Redis/memory cache, e-mail providers (IMAP/SMTP, Microsoft 365, Gmail, file-drop), messaging (WhatsApp, SMS, Meta, telephony, Teams, push), AI providers, Wings ERP, Azure DevOps/GitHub. | Application |
| `SupportDesk.Workers` | Background engines: outbox dispatcher, durable job runner, recurring-task runner (all lease-based, multi-instance safe). | Application |
| `SupportDesk.Api` | ASP.NET Core host: REST controllers (`/api/v1`), OAuth token endpoint, JWKS, SignalR hubs (`/hubs/desk`, `/hubs/portal`), auth handlers, request-context middleware, problem details, rate limiting, security headers, OpenAPI (`/openapi/v1.json`, UI at `/docs`), health checks, OpenTelemetry, SPA hosting (`/` desk, `/portal/` customer portal). | Integrations, Workers |
| `SupportDesk.WorkerHost` | .NET Worker Service (Windows Service / systemd) running jobs and recurring tasks out of process. | Integrations, Workers |
| `clients/` | Angular 21 workspace: `@sd/core` (API clients, auth, realtime), `@sd/ui` (design system), `desk` (agent workbench + administration console), `portal` (customer portal), `mobile` (Ionic). | API |

EF Core is the repository/unit of work: application services use `DeskDbContext` directly; persistence concerns
(tenant filters, timestamps, append-only guards, rowversion) are enforced centrally in the DbContext.

## 2. Multi-tenancy

* **Shared database by default.** Every tenant-owned row implements `ITenantOwned` (`TenantId`).
* **Reads:** a named EF query filter `"tenant"` (`e.TenantId == RequestContext.TenantId`) on every tenant-owned entity.
  An unresolved tenant (`Guid.Empty`) returns no rows — the system fails closed. Platform code that must see all
  tenants uses `db.AllTenants<T>()` explicitly.
* **Writes:** `DeskDbContext.SaveChanges` stamps `TenantId` on inserts and throws `CrossTenantWriteException` for any
  insert/update/delete of another tenant's row (only provisioning code can enter a platform scope).
* **Tenant source of truth:** the validated JWT (`tid` claim), the API-key/OAuth-client record, the job/outbox record,
  or the server-side brand/host mapping for the anonymous portal. Never a client-supplied header or body value.
* **Dedicated databases:** `Tenant.DatabaseMode = Dedicated` + `DedicatedConnectionName` (a configuration key). The
  `TenantConnectionInterceptor` re-points the connection before it opens; `DatabaseInitializer` migrates dedicated
  databases at startup. The tenant catalog always lives in the shared database.
* **Caches:** `ITenantCache` prefixes keys with the tenant id. Files: storage keys start with the tenant id.
  Search index, AI retrieval chunks and realtime groups are tenant-scoped by construction.

## 3. Request pipeline

`ForwardedHeaders → ExceptionHandler (problem details) → security headers → static files → CORS → rate limiter →
authentication (JWT bearer or X-Api-Key) → RequestContextMiddleware → authorization → controllers / hubs → SPA fallback`.

`RequestContextMiddleware` loads the principal into the scoped `RequestContext`: agent (permissions, ticket/customer
scope, departments, teams — cached 30 s), contact (portal), API client (scopes → permissions, optionally intersected with
an "act-as" user), or platform operator. It rejects revoked sessions and blocks writes for suspended tenants (402).

**Authorisation is enforced in services** (`ctx.Require(permission)`, data-scope queries such as
`db.Tickets.VisibleTo(ctx, db)`), not only by controller attributes or the UI. Out-of-scope records return 404.

## 4. Identity & security

* Agents: password (PBKDF2 via ASP.NET Identity hasher) + optional/required MFA (TOTP or e-mailed OTP), lockout,
  password policy & history, expiry. Tokens: ES256 JWT access tokens (15 min by policy) + rotating refresh tokens
  (httpOnly SameSite=Strict cookie for web; body for mobile) with **reuse detection** (replayed refresh token revokes the
  whole family). Idle and absolute session lifetimes; concurrent-session limit.
* Single sign-on: per-tenant OpenID Connect (Authorization Code + PKCE), id_token validated against the IdP JWKS,
  optional auto-provisioning; the SPA receives a one-time 60-second handoff code.
* Integrations: OAuth 2.0 client credentials (`POST /oauth/token`) and static API keys (`sdk_{prefix}_{secret}`, only
  hashes stored), both scope-limited. JWKS and discovery at `/.well-known/*`.
* Customers (portal): password or e-mail OTP; tokens of type `contact` cannot call agent endpoints.
* Secrets at rest (channel credentials, MFA secrets, API tokens): AES-256-GCM with a master key (`Security:MasterKey`).
* Rich text is sanitised server-side (HtmlSanitizer allow-list); uploads are size-limited, type-checked by extension
  and magic bytes, SHA-256 hashed and malware-scanned (EICAR signature built in, ClamAV optional, fail-closed);
  attachments are served with `Content-Disposition: attachment` unless an inline-safe type, plus a sandbox CSP.
* CSRF: APIs use bearer tokens (not cookies); the only cookie (refresh) is SameSite=Strict, path-scoped and requires
  `X-Requested-With: SupportDesk`. XSS: strict CSP on the SPAs, sanitised HTML, Angular escaping.
* Tamper-evident audit: `AuditLog` rows are sealed into per-tenant SHA-256 hash chains by `AuditSealerTask`; the
  database rejects updates to sealed rows; `AuditVerifier` reports the first broken link.

## 5. Reliable eventing

Business changes and their events are committed atomically (**transactional outbox**, `OutboxEvents`). The
`OutboxDispatcher` claims due events with a lease (`UPDLOCK, READPAST`), runs each interested `IOutboxHandler` in its
own scope with a system context for the event's tenant, and records per-handler success in `OutboxDeliveries` so a
retry never repeats a completed handler. Failures back off (5 s·4ⁿ, max 15 min) and dead-letter after 8 attempts.
Expired leases (crashed instance) are reclaimed — restart-safe.

Handler order: metrics (10) → SLA (20) → search (30) → automation (40) → AI (50) → notifications (60) → realtime (70)
→ webhooks (80) → integrations (90). Events carry `CorrelationId`, `CausationId` and `ChainDepth`; automation refuses to
run beyond the configured chain depth (trigger-loop protection).

Durable **jobs** (`BackgroundJobs`) use the same lease/backoff model plus optional idempotency keys. **Recurring tasks**
(`IRecurringTask`) run under a per-task database lease so only one instance runs each tick (SLA monitor every 30 s,
audit sealer, housekeeping, mailbox polling, analytics roll-ups, scheduled reports, retention…).

## 6. Ticket core

* Numbers: per-tenant gap-free counter (`TenantCounters`, `UPDLOCK/HOLDLOCK`), displayed with the tenant prefix.
* Status: configurable `TicketStatus` rows mapped to fixed categories (Open, Assigned, InProgress, Waiting, Resolved,
  Closed); optional allowed-transition lists; blueprint workflows override manual changes.
* Conversation: `TicketThread` (public messages, internal/handover notes) with a per-ticket gap-free `Seq` allocated
  under the ticket's rowversion; channel messages (`Message`) keep provider metadata. The timeline merges threads and
  `TicketEvent` activity chronologically.
* Concurrency: optimistic (`rowversion`) — edits must send the `rowVersion` they loaded; conflicts return 409 with the
  current ticket. **Reply collision:** replies send `basedOnSeq`; if others posted since, the API returns 409
  `reply_collision` with the new entries. Presence over SignalR shows who is viewing/replying; optional soft locks.
* Idempotency: `clientRequestId` on ticket creation, replies and notes (offline mobile sync, retries).
* History: every change writes `TicketEvent` (with from/to values); sensitive actions also write `AuditLog`.

## 7. SLA engine

Policy selection: contract SLA → first matching policy (conditions on tier, contract, priority, department, product,
category, channel…) → default. Calendar: contract support hours → policy calendar → department calendar → 24×7.
Milestones: first response, next response (a new cycle after each customer message once responded), resolution.
Statuses flagged *pauses SLA* stop the resolution/next-response clocks. Elapsed business minutes are banked at every
pause, re-target and calendar switch; due dates are recomputed from the running segment. Each instance stores an
immutable snapshot of the policy and calendar (reproducible calculations) and every change writes an `SlaEvent`.
The monitor raises warnings (percentage of target), breaches (re-checking elapsed time) and multi-level escalations
(notify, reassign, raise priority; levels may fire before or after the breach).

## 8. Realtime

SignalR hubs: `/hubs/desk` (agents) and `/hubs/portal` (contacts). Groups: tenant, user, department, ticket, contact,
conversation — joined only after server-side access checks. Presence is stored in the distributed cache (Redis in
production) so it works across instances; with `Redis:Configuration` set, SignalR uses the Redis backplane.
Realtime pushes are emitted by the outbox `RealtimeOutboxHandler` after commit (never for uncommitted data).

## 9. Data model (summary)

GUID primary keys everywhere (sequential GUIDs for clustered-index locality), UTC `datetime2`, `decimal(18,4)` money,
`rowversion` on aggregates edited concurrently (Ticket, Customer, Contact, Contract, Installation, Article, rules,
workflows, SLA policy/instance, channel accounts, settings). Enums are stored as strings except ordered ones
(priority, severity, status category, scopes) stored as ints for range queries. JSON columns end in `Json`.
Filtered indexes keep hot working sets small (open tickets by assignee/department/due, active SLA milestones,
pending outbox/jobs/deliveries). Keyset pagination for ticket lists (`(TenantId, LastActivityAt, Id)` and other sort keys).

Areas: Platform (Tenant, Plan, Subscription, UsageRecord, BillingInvoice, Brand, TenantSetting, AuditLog,
OutboxEvent, BackgroundJob, ConfigurationSnapshot, RetentionPolicy, LegalHold, DataRequest, StoredFile) ·
Identity (User, Role, Permission, RolePermission, UserRole, UserSession, OtpChallenge) · Organisation (Department,
Team, Agent, skills, routing attributes, shifts, leave, delegation, BusinessCalendar, holidays) · Customers (Customer,
CustomerGroup, CustomerLocation, CustomerDepartment, Contact, ContactIdentity, consents, merge log, ServiceActivity) ·
Products (family, product, module, version, release, Installation, Licence, CustomerAsset) · Service (Contract,
Entitlement, TimeEntry) · Tickets (Ticket, TicketStatus, categories, issue types, lookups, TicketThread, TicketEvent,
TicketAttachment, TicketLink, TicketWatcher, TicketTask, Tag/TicketTag, drafts, templates, follow-ups, external
references, EngineeringLink, ApprovalRequest, SavedView, SatisfactionResponse) · Configuration (custom fields, form
layouts) · SLA (SlaPolicy, SlaTarget, SlaEscalationLevel, SlaInstance, SlaMilestone, SlaEvent) · Automation
(AutomationRule, RuleVersion, RuleExecution, Workflow, WorkflowVersion, Webhook, WebhookDelivery) · Knowledge (KbCategory,
Article, ArticleVersion, ArticleTranslation, attachments, access rules, ArticleFeedback, KnowledgeChunk, community
topics/posts/votes, TroubleshootingFlow) · Communication (ChannelAccount, Conversation, Message, MessageDelivery,
InboundEvent, CallRecord, Notification + preferences/settings/deliveries, PushDevice) · AI (AiConfiguration,
AiPromptTemplate, AiExecution, AiUsage, AiAgent, AiActionProposal, AnomalyAlert) · Analytics (TicketMetric,
AgentMetric, BacklogSnapshot, ScheduledReport, Dashboard) · Integration (Integration, IntegrationLog, ApiClient, ApiKey)
· Search (SearchDocument, SearchPosting — an inverted index that works on every SQL Server edition including LocalDB).

## 10. Deployment topology

* **Single node:** API (with workers) + SQL Server; in-memory cache; local file storage.
* **Scaled:** N API nodes behind a load balancer (sticky sessions not required) with Redis (cache, presence, SignalR
  backplane), a shared file store (SMB/NFS path or blob-backed `IFileStorage`), and one or more `SupportDesk.WorkerHost`
  instances (API nodes set `Workers:Jobs=false`, `Workers:Recurring=false`; keep `Workers:Outbox=true` on API nodes so
  realtime pushes originate where hubs run — leases make every worker safe to run on several nodes).
* **Enterprise isolation:** selected tenants on dedicated databases via configuration-named connection strings.

See `docs/DEVELOPMENT.md` for conventions and `docs/DEPLOYMENT.md` for IIS/Linux instructions.
