# SupportDesk — Functional Specification

## 1. Purpose and scope

SupportDesk is a multi-tenant customer service and support platform for organisations that sell and support products —
in particular ERP and business software. It covers the full service lifecycle: receiving requests on any channel,
working them in a fast agent workbench, honouring service commitments (SLAs), automating routine work, deflecting
demand through self-service, assisting agents with AI, collaborating with engineering, and analysing outcomes.

It provides three experiences:

| Experience | Audience | Entry |
|---|---|---|
| **Agent workbench** | Agents, supervisors, specialists, account managers, analysts | `/` (desk SPA) and the Ionic mobile app |
| **Administration console** | Tenant administrators | `/admin` inside the desk SPA (permission-gated) |
| **Customer portal** | Customers' staff (contacts) | `/portal/{brand}` or a custom domain per brand; chat widget on customer websites |
| **Platform console** | SaaS operators | `/platform` (operator login) |

## 2. Roles and permissions

Permissions are fine-grained codes (`docs/ARCHITECTURE.md` §3, `Domain/Rules/Permissions.cs`) bundled into roles.
Every role also carries a **ticket data scope** (All, Departments, Team, Own) and a **customer scope** (All,
Account-managed). Default roles created for every tenant:

| Role | Typical user | Key capabilities |
|---|---|---|
| Administrator | Support operations owner | Everything, including configuration, security, billing, compliance |
| Supervisor | Team lead / department head | Agent capabilities + transfer, bulk, delete/spam, approvals, monitoring, costs, team analytics, AI approvals, KB review/publish |
| Agent | Support engineer | Create/edit/reply/note/assign/escalate/resolve/reopen/merge/split tickets in own departments, log time, manage customers & contacts, author KB drafts, use AI |
| Light Agent | Collaborator / engineer | View tickets and add internal notes only; can never reply to customers or own tickets |
| Knowledge Manager | Documentation owner | Full knowledge base and community management |
| Analyst | Reporting user | Read-only tickets/customers plus organisation-wide analytics and exports |
| Portal contact | Customer staff | Own, location-wide or organisation-wide tickets (per contact), KB, community, contracts when permitted |
| API client | ERP/CRM/BI integration | Scope-limited (`tickets:read`, `tickets:write`, `customers:read` …) |
| Platform operator | SaaS provider staff | Tenants, plans, subscriptions, suspension, usage |

Custom roles can be created from the permission catalog (plan feature `custom_roles`).

## 3. Core use cases

### UC-1 Customer raises a request (any channel)
E-mail to a support mailbox, portal form, chat widget, WhatsApp, SMS, phone call (logged by an agent or the telephony
integration), social messaging, web form, mobile app, ERP screen or API. The system matches or creates the contact
(identity, e-mail, phone, domain → customer), threads replies into existing tickets (Message-ID/References, reply
token, ticket tag), ignores duplicates and auto-replies, evaluates support eligibility (contracts, warranties,
entitlements), applies the SLA policy, routes to a team/agent and acknowledges the customer.

### UC-2 Agent processes a ticket
In the three-panel workbench the agent sees the queue, the ticket conversation and the customer context (360°
summary, previous tickets, SLA, contracts, installations, ERP transactions, suggested articles, AI recommendations).
They reply (with templates, attachments, drafts, collision warnings), add internal or handover notes with
@mentions, update fields, log time, create tasks/checklists, schedule follow-ups, link/merge/split/clone tickets,
escalate, transfer (with supervisor approval where configured) and resolve with resolution code and root cause.
Customers may be asked to confirm closure.

### UC-3 The system enforces the SLA
First-response, next-response and resolution clocks run on the applicable calendar, pause in waiting statuses,
re-target on priority/contract changes, warn before breach, record breaches and execute escalation levels (notify,
reassign, raise priority). Every calculation is reproducible from stored policy snapshots and events.

### UC-4 Supervisor monitors operations
Command Center: live queues, SLA risk board with countdowns, team workload versus capacity and availability,
escalations and pending approvals; desktop/mobile notifications for warnings and breaches; drill into any ticket.

### UC-5 Customer tracks the outcome
In the portal the contact sees their tickets (scope-dependent), the public conversation, status in customer-friendly
terms, replies, adds information, reopens within the reopen window, confirms resolution and rates satisfaction.

### UC-6 Automation and blueprints
Administrators define trigger → conditions → actions rules (versioned, simulated, conflict-checked, loop-protected)
and state-transition blueprints with mandatory fields, validations, approvals and after-actions.

### UC-7 Knowledge and self-service
Knowledge managers author, review, translate and publish articles with visibility rules; customers search, browse,
follow guided troubleshooting flows, ask the community or the AI assistant before raising a ticket. Agents convert
resolved tickets into draft articles.

### UC-8 AI assistance
Classification, summarisation, grounded reply suggestions with citations, sentiment, duplicate detection, root-cause
suggestions, resolution-time prediction, anomaly alerts, natural-language analytics and configurable AI agents —
with human approval for consequential actions, budgets, audit and revert.

### UC-9 Engineering escalation
Defect tickets are escalated to engineering: work items are created/linked in Azure DevOps Server/TFS, GitHub or the
local tracker; state, fix version, releases and deployments synchronise back; customers are notified when the fix is
released; the ticket keeps its history even if the work item moves or closes.

### UC-10 Analytics
Role-specific dashboards and reports (volume, backlog, ageing, response/resolution times, FCR, SLA compliance,
reopens, escalations, utilisation, workload, productivity, channel performance, CSAT, cohorts, Pareto, recurring
issues, product quality) with drill-downs, period comparison, exports and scheduled delivery.

### UC-11 Administration
Organisation (departments, teams, agents, skills, shifts, leave, delegation, brands, portals, calendars), ticket
configuration (statuses and transitions, categories, fields, layouts/forms), SLA, automation, channels, notifications,
knowledge settings, AI (providers, prompts, budgets, agents), integrations/API credentials, security policies,
retention/legal hold/erasure, audit, configuration export/import/publish/rollback.

### UC-12 SaaS operation
Trial sign-up, plan selection, usage tracking and limits (agents, portal users, storage, channels, AI tokens,
attachments, API calls), invoices, plan changes, suspension and reactivation, optional dedicated databases.

## 4. Business rules (selection)

* A ticket belongs to exactly one department; agents see it only within their data scope (out-of-scope → not found).
* Status categories: Open, Assigned, InProgress, Waiting, Resolved, Closed. Configurable statuses map to them;
  transitions can be restricted; tickets in a blueprint change status only through blueprint transitions.
* Reopening: customers may reopen within the reopen window (default 30 days); later replies create a follow-up ticket.
* Auto-close: resolved tickets close after N idle days (default 7).
* Eligibility: active contract/entitlement for the installation → asset → product → customer-wide; expired or
  exhausted entitlements mark the ticket accordingly; authorised users can override with a reason (audited).
* Light agents cannot own tickets or reply to customers.
* Transfers out of departments flagged "transfer requires approval" create an approval request instead.
* Reply collision: if new public messages arrived since the agent loaded the ticket, sending requires confirmation.
* Merged tickets are closed and forward future customer replies to the surviving ticket.
* AI may only auto-reply in explicitly approved low-risk scenarios; financial, account, data and contract actions always need approval.
* Portal users never see internal notes, internal attachments, activity events or internal status names.

## 5. Non-functional requirements

Multi-tenant isolation in every layer; backend-enforced authorisation; auditability (ticket history + tamper-evident
audit log); idempotent channel ingestion and handlers; restart-safe background processing; SLA calculations in
business time with time zones and holidays; WCAG-conscious UI with light/dark themes and responsive layouts; API-first
(OpenAPI) with rate limiting; observability (structured logs, traces, health checks).
