# SupportDesk API reference for integrators

This guide explains how to call the SupportDesk REST API, receive webhooks and connect to the realtime hubs. Every
recipe in section 12 was run against a local API (Development environment, demo tenant `wings`) with the exact curl
commands and C# code shown here.

The live, machine-readable contract is always available from a running API:

* OpenAPI document: `GET /openapi/v1.json`
* Swagger UI: `/docs`

Contents

1. [Base URL and conventions](#1-base-url-and-conventions)
2. [Authentication](#2-authentication)
3. [Tenant resolution](#3-tenant-resolution)
4. [Errors](#4-errors)
5. [Pagination](#5-pagination)
6. [Concurrency and idempotency](#6-concurrency-and-idempotency)
7. [Rate limits](#7-rate-limits)
8. [Outgoing webhooks](#8-outgoing-webhooks)
9. [Incoming webhooks and integrations](#9-incoming-webhooks-and-integrations)
10. [Realtime (SignalR)](#10-realtime-signalr)
11. [Endpoint groups](#11-endpoint-groups)
12. [Recipes](#12-recipes)

---

## 1. Base URL and conventions

| Item | Value |
|---|---|
| Base URL | Your desk host, for example `https://desk.example.com` (local development: `http://localhost:5830`) |
| REST prefix | `/api/v1/…`. There is only one version. |
| OAuth and keys | `/oauth/token`, `/.well-known/jwks.json`, `/.well-known/openid-configuration` |
| Realtime hubs | `/hubs/desk`, `/hubs/portal`, `/hubs/chat` |
| Chat widget | `/widget/widget.js` |
| Health | `/health/live`, `/health/ready` |

Conventions:

* JSON bodies use **camelCase**. Enum values are **strings** (`"High"`, `"Email"`).
* Dates are ISO-8601 **UTC**. Ids are GUIDs.
* API responses include properties whose value is `null`. Webhook payloads omit them.
* Every response that passes the request pipeline carries an `X-Correlation-Id` header. Quote it when you report a
  problem.
* Rich text (`…Html` fields) is sanitised on the server with an allow-list.

---

## 2. Authentication

Choose the credential by caller:

| Caller | Credential | Lifetime |
|---|---|---|
| Server-to-server integration | API key in `X-Api-Key`, or an OAuth 2.0 client-credentials token | Key: until revoked or expired. Token: 1 hour. |
| Interactive agent app | Agent JWT from `POST /api/v1/auth/login` (+ MFA) and refresh tokens | Access token: 15 minutes by default (tenant policy, 5–60). |
| Customer-facing app | Portal (contact) JWT from `POST /api/v1/portal/{brand}/auth/…` | At least 15 minutes. |
| SaaS operator | Platform JWT from `POST /api/v1/platform/auth/login` | 30 minutes. |

All tokens are ES256-signed JWTs (issuer `supportdesk`, audience `supportdesk-api`). A token's `typ` claim decides which
endpoints accept it: `agent` and `client` tokens call agent endpoints; `contact` tokens only call `/api/v1/portal/…`;
`platform` tokens only call `/api/v1/platform/…`. A contact token on an agent endpoint gets **403**. A missing or
invalid credential gets **401** with an empty body.

### 2.1 Agent login, MFA and refresh

```bash
curl -s -X POST "$BASE/api/v1/auth/login" -H "Content-Type: application/json" -H "X-Client: mobile" \
  -d '{"tenant":"wings","email":"admin@wings.demo","password":"Desk@2026"}'
```

`tenant` is the workspace slug. The response is a `LoginResponse`:

```json
{ "status": "mfa_required", "accessToken": null, "expiresAt": null, "refreshToken": null, "user": null,
  "challengeId": "b59c5048-9261-40f2-7782-08df29ea0b23", "methods": ["email"], "demoCode": "878906",
  "message": "Enter the code from your authenticator app or the code we e-mailed you." }
```

| `status` | HTTP | Next step |
|---|---|---|
| `ok` | 200 | Use `accessToken`. |
| `mfa_required` | 200 | Call `POST /api/v1/auth/mfa`. An e-mail code is always sent; `methods` lists `totp` when the user enrolled an authenticator. `demoCode` is filled only in demo mode. |
| `password_expired` | 200 | Call `POST /api/v1/auth/change-expired-password` `{tenant, email, currentPassword, newPassword}`. |
| `locked` | 423 | Wait until the time in `message`. |
| `ip_not_allowed`, `sso_required` | 403 | The workspace policy blocks password sign-in from here. |
| `invalid` | 401 | Wrong workspace, e-mail or password. |

```bash
# CHALLENGE_ID = challengeId from the login response; MFA_CODE = the e-mailed (or demo) code
curl -s -X POST "$BASE/api/v1/auth/mfa" -H "Content-Type: application/json" -H "X-Client: mobile" \
  -d "{\"challengeId\":\"$CHALLENGE_ID\",\"code\":\"$MFA_CODE\",\"method\":\"email\"}"
```

The e-mail code has 6 digits, is valid for 10 minutes and allows 5 attempts. `method` defaults to `totp`.

**Refresh tokens.** Browser clients receive the refresh token as the httpOnly cookie `sd_rt`
(`Path=/api/v1/auth`, `SameSite=Strict`). Clients that send `X-Client: mobile` receive `refreshToken` in the body
instead, and no cookie.

```bash
# Body-based refresh (mobile and server clients)
curl -s -X POST "$BASE/api/v1/auth/refresh" -H "Content-Type: application/json" -H "X-Client: mobile" \
  -d "{\"refreshToken\":\"$REFRESH_TOKEN\"}"
```

Cookie-based refresh needs the header `X-Requested-With: SupportDesk` (CSRF defence); without it the API answers 400.
Refresh tokens rotate on every use. Replaying an old refresh token revokes the whole session family.
`POST /api/v1/auth/logout` (optional body `{refreshToken}`) ends the session.

Other endpoints: `change-password`, `password-reset` and `password-reset/confirm`, `mfa/setup`, `mfa/enable`,
`mfa/disable`, and single sign-on (`GET sso/{tenant}/start?returnUrl=`, `GET sso/callback`, `POST sso/complete
{code}`), all under `/api/v1/auth`.

### 2.2 API keys

An administrator with `admin.integrations` creates keys under **Administration › API keys & OAuth clients**, or with
the API:

```bash
curl -s -X POST "$BASE/api/v1/admin/api-keys" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"CRM sync","scopes":["tickets:write","customers:write"],"actAsUserId":null,"expiresAt":null}'
```

```json
{ "key": { "id": "e0567d9d-…", "name": "CRM sync", "prefix": "32s3t6qt33", "scopes": ["tickets:write","customers:write"],
           "actAsUserId": null, "status": "Active", "expiresAt": null, "rateLimitPerMinute": 300, "…": "…" },
  "secret": "sdk_32s3t6qt33_0r9A3HECsT0fhWgdPPwwDkZFqiT_dZg6" }
```

The `secret` is shown **once**; only its SHA-256 hash is stored. Send it on every request:

```bash
curl -s "$BASE/api/v1/tickets?limit=5" -H "X-Api-Key: $SD_API_KEY"
```

Rules enforced when a key is created:

* `name` 2–100 characters, at least one scope, `expiresAt` between 5 minutes and 3 years ahead (or `null`).
* You need an organisation-wide data scope, and the scopes may not grant permissions you lack
  (403 `privilege_escalation`).
* `actAsUserId` (optional) must be an active user whose permissions are within yours. The key then acts with the
  **intersection** of its scope permissions and that user's permissions, and replies are authored as that user. The
  data scope is not narrowed: API callers always see all tickets and customers of the tenant.

Revoke a key with `DELETE /api/v1/admin/api-keys/{id}`. `GET /api/v1/admin/api-scopes` lists the scopes with the
permissions each grants.

### 2.3 Scopes

A scope `module:write` grants every permission of that module. `module:read` grants only the `*.view` permissions.

| Scope | Permissions granted |
|---|---|
| `tickets:read` | `tickets.view` |
| `tickets:write` | every `tickets.*` permission (view, create, edit, reply, note, assign, transfer, escalate, resolve, reopen, merge, split, delete, bulk, time, view_costs, override_entitlement, export) |
| `customers:read` / `customers:write` | `customers.view` / + `customers.manage`, `customers.merge` |
| `products:read` / `products:write` | `products.view` / + `products.manage` |
| `contracts:read` / `contracts:write` | `contracts.view` / + `contracts.manage` |
| `kb:read` / `kb:write` | `kb.view_internal` / + `kb.author`, `kb.review`, `kb.publish` |
| `analytics:read` | `analytics.view`, `analytics.team` (team-wide reporting; an API client has no tickets of its own) |
| `admin:write` | every `admin.*` permission |
| `ai:write` | `ai.use`, `ai.approve_actions` |
| `*` | every permission, including future ones |
| `admin:read` | `admin.audit` (read the audit log; other settings have no read-only permission) |
| `integrations:write` | `admin.integrations`, `engineering.view`, `engineering.link` |

Notes:

* `contacts.merge`, `community.moderate`, `approvals.decide`, `supervisor.monitor`, `agents.manage`,
  `engineering.manage`, `analytics.export` and `analytics.schedule` are only reachable with `*`.
* The same rule (`ApiScopeRules`) computes a credential's permissions and checks every request, so the table above is
  exactly what a key can do.
* SLA breach lists need `admin.sla`, `supervisor.monitor` or `admin.organisation` — `admin:write` or `*` for a key,
  or a supervisor's bearer token (recipe 12.7).

### 2.4 OAuth 2.0 client credentials

Create a client with `POST /api/v1/admin/oauth-clients` `{name, scopes[], expiresAt?}`. The response holds
`client.clientId`, the one-time `clientSecret` and `tokenEndpoint`. Rotate with `POST …/oauth-clients/{id}/rotate-secret`
and revoke with `DELETE …/oauth-clients/{id}`. Clients have no act-as user.

```bash
curl -s -X POST "$BASE/oauth/token" \
  -d grant_type=client_credentials -d client_id="$CLIENT_ID" -d client_secret="$CLIENT_SECRET" -d "scope=tickets:write"
# or HTTP Basic client authentication:
curl -s -X POST "$BASE/oauth/token" -u "$CLIENT_ID:$CLIENT_SECRET" -d grant_type=client_credentials
```

```json
{ "access_token": "eyJhbGciOiJFUzI1NiIs…", "token_type": "Bearer", "expires_in": 3600, "scope": "tickets:write" }
```

* `scope` (space-separated) is intersected with the client's scopes; omit it to get all of them.
* Errors: 400 `{"error":"unsupported_grant_type"}` or `{"error":"invalid_request"}`, 401 `{"error":"invalid_client"}`.
* A revoked client's tokens stop working on their next request (401 `client_disabled`).

### 2.5 JWKS and discovery

* `GET /.well-known/openid-configuration` returns the issuer (`supportdesk`), `token_endpoint`, `jwks_uri`,
  `grant_types_supported` (`client_credentials`), the token auth methods (`client_secret_post`,
  `client_secret_basic`) and `scopes_supported`.
* `GET /.well-known/jwks.json` returns the public EC P-256 signing key(s) (`alg` ES256) for validating tokens offline.

### 2.6 Portal (contact) tokens

Customer-facing apps sign contacts in per brand. `{brand}` is the brand slug, for example `wings`.

| Endpoint | Body |
|---|---|
| `GET /api/v1/portal/{brand}/config` | — (brand colours, features, auth mode) |
| `POST /api/v1/portal/{brand}/auth/login` | `{email, password}` |
| `POST /api/v1/portal/{brand}/auth/otp/request` | `{email}` — e-mails a 6-digit code |
| `POST /api/v1/portal/{brand}/auth/otp/verify` | `{email, code}` |
| `POST /api/v1/portal/{brand}/auth/register` | `{fullName, email, password, phone?}` (when the brand allows self-registration) |
| `POST /api/v1/portal/{brand}/auth/refresh`, `…/auth/logout` | as for agents; cookie `sd_prt`, `Path=/api/v1/portal` |

```bash
curl -s -X POST "$BASE/api/v1/portal/wings/auth/login" -H "Content-Type: application/json" -H "X-Client: mobile" \
  -d '{"email":"ramesh@abcretail.demo","password":"Portal@2026"}'
```

The response is `{status, accessToken, expiresAt, refreshToken, contact, demoCode, message}`. Contact tokens call the
signed-in portal endpoints (`/api/v1/portal/{brand}/tickets`, `…/files`, `…/account/…`). Ticket visibility follows the
contact's portal scope (Own, Location or Organisation).

---

## 3. Tenant resolution

The tenant is **never** taken from client input. There is no tenant header. The API derives it from server-side
records only:

| Credential or route | Tenant source |
|---|---|
| Agent, contact, client and platform JWTs | the signed `tid` claim |
| `X-Api-Key` | the key record |
| OAuth client token | the client record (copied into `tid` when the token is issued) |
| `/api/v1/portal/{brand}/…` | the brand slug, looked up server-side; a contact token only works for brands of its own tenant (404 otherwise) |
| `/api/v1/channels/webhooks/{provider}/{webhookKey}` | the channel account that owns the key |
| `/api/v1/engineering/hooks/{provider}/{trackerId}` | the tracker integration |
| `/api/v1/automation/webhooks/{webhookKey}` | the automation rule |
| `/api/v1/chat/{widgetKey}/…` and `/hubs/chat` | the chat widget key and the signed visitor token |

The `tenant` field of the login body only names the workspace to sign in to. Once set for a request, the tenant cannot
change. Every query is filtered by tenant in the data layer, and writes to another tenant's rows are rejected.

Requests can also be refused by tenant state: while a tenant is suspended, any non-GET call answers
**402 `tenant_suspended`**, except `/api/v1/subscription`, `/api/v1/auth` and `/api/v1/portal`.

---

## 4. Errors

Errors use RFC 7807 problem details (`application/problem+json`) with a stable `code`:

```json
{ "title": "Some fields need attention.", "status": 400,
  "detail": "One or more fields are invalid: subject: A subject is required.",
  "instance": "/api/v1/tickets", "code": "validation_failed",
  "errors": { "subject": "A subject is required." }, "correlationId": "4bf92f3577b34da6a3ce929d0e0e4736" }
```

* `errors` maps each field to **one message string**.
* Branch on `code`, not on `title` (titles are for people and may change).
* `correlationId` equals the `X-Correlation-Id` response header (also on errors). Quote it when you report a problem.

| HTTP | `code` | Meaning |
|---|---|---|
| 400 | `validation_failed` | Field errors in `errors`, a bad cursor, or an unreadable body |
| 401 | `session_revoked`, `account_disabled`, `client_disabled` | Credential no longer valid |
| 402 | `tenant_suspended`, `limit_exceeded` (+ `limit`), `feature_not_in_plan` | Subscription state or plan limit |
| 403 | `forbidden`, `ip_not_allowed`, `privilege_escalation` | Missing permission, IP allow-list, or a grant above your own rights |
| 404 | `not_found` | Missing **or outside your data scope** (out-of-scope records always look missing) |
| 409 | `concurrency_conflict` | Stale `rowVersion`; tickets also return `current` (the latest ticket) |
| 409 | `reply_collision` | New public entries since `basedOnSeq`; see `currentSeq`, `newEntries` |
| 409 | `ticket_locked`, `duplicate_contact`, `conflict`, `sync_running`, `plan_usage_exceeded` | State conflicts |
| 422 | `invalid_transition`, `missing_required_fields`, `unknown_customer`, … | Business rule refused the change (default status of domain errors) |
| 429 | `rate_limited` | Slow down; see `Retry-After` |
| 202 | `approval_required` (+ `approvalId`) | Accepted but queued for approval (for example a department transfer) |
| 500 | `internal_error` | Unexpected; quote the correlation id |

Other shapes you may meet:

* Malformed JSON or wrong types fail model binding before the service runs. The body is the framework's
  `ValidationProblemDetails` (`errors` maps each field to an **array**, plus `traceId`) **without** a `code`.
* 401 and 403 from authentication itself have an empty body.
* `/oauth/token` uses OAuth's `{"error": "…"}` format.

---

## 5. Pagination

Large lists use **keyset pagination**:

```
GET /api/v1/tickets?limit=50
→ { "items": [ … ], "nextCursor": "eyJ2Ijoi…", "total": null }
GET /api/v1/tickets?limit=50&cursor=eyJ2Ijoi…
→ { "items": [ … ], "nextCursor": null, "total": null }
```

* Pass `nextCursor` back unchanged as `cursor`. `nextCursor: null` means the last page. Cursors are opaque.
* `limit` defaults to 50; the maximum is 200 for tickets, customers and contacts.
* `includeTotal=true` fills `total` (an extra count query; use it on the first page only).
* Keep the same filters and sort while you page.

Keyset paging is used by tickets, deleted tickets, customers, contacts, installations, contracts, conversations and
their messages, SLA breaches, engineering work items, audit, sessions, portal tickets, agents and the platform lists.
A few small admin lists use page numbers (`{items, page, pageSize, total}`), for example webhook deliveries.

Ticket list filters (`GET /api/v1/tickets`): `q`, `statusId`, `statusCategory`, `priority`, `assigneeId`, `unassigned`,
`mine`, `watching`, `departmentId`, `teamId`, `customerId`, `contactId`, `productId`, `channel`, `tag`, `slaState`,
`overdue`, `escalated`, `createdFrom`, `createdTo`, `viewId`. Comma-separated values are allowed. `sort` is
`lastActivity` (default), `created`, `number`, `due` or `priority`; `dir` is `asc` or `desc`.

---

## 6. Concurrency and idempotency

### Optimistic concurrency (`rowVersion`)

Tickets, customers, contacts and other edited records carry a `rowVersion` (base64, for example `"AAAAAAAAJxw="`).
Send back the value you loaded:

```bash
# TICKET_ROW_VERSION = rowVersion from GET /api/v1/tickets/$TICKET_ID
curl -s -X PATCH "$BASE/api/v1/tickets/$TICKET_ID" -H "X-Api-Key: $SD_API_KEY" -H "Content-Type: application/json" \
  -d "{\"rowVersion\":\"$TICKET_ROW_VERSION\",\"priority\":\"Critical\"}"
```

* `PATCH /api/v1/tickets/{id}` requires `rowVersion`. Only the fields you send change; list fields to clear in
  `"clear": ["productId"]`.
* If someone saved in between, the API answers **409 `concurrency_conflict`**. For tickets the body includes
  `current`: the full latest ticket with its new `rowVersion`. Merge your change and retry.
* `PUT` on customers and contacts **replaces** the record: send every field you want to keep, plus `rowVersion`.

### Reply collision

`POST /api/v1/tickets/{id}/replies` accepts `basedOnSeq`: the last thread number you displayed (`lastThreadSeq` of the
ticket). If newer **public** entries by someone else exist, the API answers **409 `reply_collision`**:

```json
{ "title": "New messages were added to this ticket while you were writing. Review them, then send again.",
  "status": 409, "code": "reply_collision", "currentSeq": 3, "newEntries": [ { "seq": 1, "kind": "CustomerMessage", "…": "…" } ] }
```

Show `newEntries` to the user, then resend with `basedOnSeq: currentSeq`, or with `"force": true`.

For API keys and OAuth clients without an act-as user, every newer public entry counts (including the key's own earlier
replies), so pass the `currentSeq` you last saw.

### Idempotency (`clientRequestId`)

Send a unique `clientRequestId` when you create tickets, replies or notes. Retrying with the same key never creates a
duplicate:

| Endpoint | Replay result |
|---|---|
| `POST /api/v1/tickets` | the existing ticket (status **201** again) |
| `POST /api/v1/tickets/{id}/replies`, `…/notes` | the existing thread entry (200). Keys are unique per tenant across all tickets. |
| `POST /api/v1/integrations/erp/tickets` | the existing ticket with status **200** instead of 201 |
| `POST /api/v1/portal/{brand}/tickets`, `…/replies` | the existing ticket or entry (keys are namespaced per contact) |
| `POST /api/v1/automation/webhooks/{key}` | header `Idempotency-Key` instead |

Use a key derived from your own record (for example `crm-case-88412`) so that retries after a crash still match.

---

## 7. Rate limits

| Policy | Applies to | Limit (default configuration) | Partition |
|---|---|---|---|
| `auth` | sign-in, refresh, `/oauth/token`, portal auth, portal submissions and assistant, sign-up | 30 per minute (`RateLimits:AuthPerMinute`) | client IP |
| `public` | portal content, chat widget API, CSAT, engineering hooks, automation webhooks | 240 per minute (`RateLimits:PublicPerMinute`) | client IP |
| `api` | all agent endpoints and signed-in portal endpoints | token bucket: burst 300, about 21 per second (`RateLimits:ApiPerMinute` = 1200) | per signed-in user or API client; anonymous calls per IP |

Exceeding a limit returns **429** with `Retry-After` (seconds) and `{"title": …, "status": 429, "code": "rate_limited"}`.
Back off and retry after that many seconds. Channel provider webhooks and the SignalR hubs have no limiter.
Anonymous portal submissions are also capped at 5 per IP per hour per brand.

API keys and OAuth clients are held to their own `rateLimitPerMinute` (shown on the key or client; default 600 for keys,
300 for OAuth clients), capped by the API-wide limit. Users behind one office address each get their own budget.

---

## 8. Outgoing webhooks

### Subscribing

Administrators (`admin.integrations`) manage subscriptions under **Administration › Webhooks** or with the API:

| Method and path | Purpose |
|---|---|
| `GET /api/v1/webhook-subscriptions` | List |
| `GET /api/v1/webhook-subscriptions/event-types` | The subscribable event names |
| `POST /api/v1/webhook-subscriptions` | Create `{name, url, events[], isActive, secret?}` |
| `GET`, `PUT`, `DELETE /api/v1/webhook-subscriptions/{id}` | Read, update, delete |
| `POST …/{id}/enable`, `POST …/{id}/disable` | Switch on or off |
| `POST …/{id}/rotate-secret` | New signing secret (returned once) |
| `POST …/{id}/ping` | Send a signed `webhook.ping` now and return the result |
| `GET …/{id}/deliveries?status=&page=&pageSize=` | Delivery log |
| `GET …/{id}/deliveries/{deliveryId}` | One delivery with its payload |
| `POST …/{id}/deliveries/{deliveryId}/redeliver` | Queue a delivery again |

Rules: `url` is an absolute http(s) URL without credentials (≤ 450 characters). `events` holds at least one public event
or `"*"`. A custom `secret` needs at least 16 characters; if you omit it, the API generates `whsec_…` and returns it once
in `secret`. Calls to private and loopback addresses are blocked unless `Automation:Webhooks:AllowPrivateNetworks` is
true; link-local and cloud metadata addresses are always blocked.

### Events

| Area | Events |
|---|---|
| Tickets | `ticket.created`, `ticket.updated`, `ticket.status_changed`, `ticket.assigned`, `ticket.reply_added`, `ticket.customer_replied`, `ticket.note_added`, `ticket.merged`, `ticket.escalated`, `ticket.deleted` |
| SLA | `sla.warning`, `sla.breached`, `sla.met` (a milestone completed within its target) |
| Customers | `contact.created`, `contact.updated`, `contact.merged`, `customer.created`, `customer.updated` |
| Other | `article.published`, `message.received`, `message.status_changed`, `approval.requested`, `approval.decided`, `satisfaction.received`, `engineering.item_updated` |

### Request

Each delivery is an HTTP `POST` with `Content-Type: application/json; charset=utf-8` and these headers:

| Header | Value |
|---|---|
| `User-Agent` | `SupportDesk-Webhooks/1.0` |
| `X-SupportDesk-Event` | the event type, for example `ticket.created` |
| `X-SupportDesk-Delivery` | the delivery id; **the same on every retry and redelivery** |
| `X-SupportDesk-Timestamp` | Unix seconds of this attempt (not signed) |
| `X-SupportDesk-Signature` | `sha256=` + lowercase hex HMAC-SHA256 of the raw body |

Body (an envelope; nulls are omitted):

```json
{
  "id": "b94281e8-df91-4ef8-8c80-08df29ea0b23",
  "type": "ticket.created",
  "occurredAt": "2026-10-14T12:19:41.4202687Z",
  "tenantId": "47c5fd37-5602-427d-69c7-08df29ea0b20",
  "data": {
    "ticketId": "25739b5b-df95-4f25-8c48-08df29ea0b23",
    "number": 1210,
    "ticket": {
      "id": "25739b5b-df95-4f25-8c48-08df29ea0b23", "number": 1210,
      "subject": "Printer driver crashes after the 4.2 update", "status": "Assigned", "statusCategory": "Assigned",
      "priority": "High", "channel": "Api", "departmentId": "a7920f57-…", "teamId": "02f5945d-…",
      "assigneeId": "c2ffd2de-…", "contactId": "9a4e1917-…", "dueAt": "2026-10-15T11:19:41.0079745Z",
      "createdAt": "2026-10-14T12:19:41.0079745Z", "updatedAt": "2026-10-14T12:19:41.4203272Z", "deleted": false
    }
  }
}
```

`data` holds the event payload:

* ticket events: `ticketId`, `number`, optional `changes` (`[{field, from, to}]`), `threadId`, `note`, `notifyCustomer`;
* SLA events: `ticketId`, `number`, `instanceId`, `milestoneId`, `milestone` (`FirstResponse`, `NextResponse`,
  `Resolution`), `dueAt`, `escalationLevel`;
* customer, contact and article events: `id`, `name`, `changes`.

Events about a ticket also carry `data.ticket`, a snapshot taken when the delivery was created.

### Verifying the signature

Compute HMAC-SHA256 over the **exact raw request body** with the subscription secret (the whole string, including the
`whsec_` prefix) as the key. Compare in constant time. Parse the JSON only after the check passes.

Node.js (Express):

```js
import express from 'express';
import { createHmac, timingSafeEqual } from 'node:crypto';

const secret = process.env.SUPPORTDESK_WEBHOOK_SECRET;
const app = express();

function isValid(rawBody, header) {
  if (!header?.startsWith('sha256=')) return false;
  const expected = 'sha256=' + createHmac('sha256', secret).update(rawBody).digest('hex');
  const a = Buffer.from(expected), b = Buffer.from(header);
  return a.length === b.length && timingSafeEqual(a, b);
}

app.post('/hooks/supportdesk', express.raw({ type: 'application/json' }), (req, res) => {
  if (!isValid(req.body, req.get('X-SupportDesk-Signature'))) return res.sendStatus(401);
  const event = JSON.parse(req.body.toString('utf8'));
  // De-duplicate on req.get('X-SupportDesk-Delivery') before acting on event.type / event.data.
  res.sendStatus(200);
});

app.listen(3000);
```

C# (ASP.NET Core minimal API):

```csharp
using System.Security.Cryptography;
using System.Text;

app.MapPost("/hooks/supportdesk", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var rawBody = await reader.ReadToEndAsync();
    if (!WebhookSignature.IsValid(secret, rawBody, request.Headers["X-SupportDesk-Signature"]))
        return Results.Unauthorized();
    var deliveryId = request.Headers["X-SupportDesk-Delivery"].ToString();   // de-duplicate on this
    var envelope = JsonDocument.Parse(rawBody).RootElement;
    // envelope.GetProperty("type"), envelope.GetProperty("data") …
    return Results.Ok();
});

static class WebhookSignature
{
    /// <summary>X-SupportDesk-Signature is "sha256=" + lowercase hex HMAC-SHA256(secret, raw request body).</summary>
    public static bool IsValid(string secret, string rawBody, string? signatureHeader)
    {
        if (signatureHeader is null || !signatureHeader.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        var expected = "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(rawBody)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signatureHeader));
    }
}
```

Both checks were run against real deliveries from the API.

Receiver guidance:

* Answer **2xx** quickly and process asynchronously. Anything else, including 3xx and 4xx, counts as a failure.
  Redirects are not followed. The request times out after 10 seconds (`Automation:Webhooks:DefaultTimeoutSeconds`).
* De-duplicate on `X-SupportDesk-Delivery` (or the envelope `id` per subscription). The timestamp header is not part of
  the signature, so do not rely on it for replay protection.
* Deliveries can arrive out of order. Use `occurredAt` and `data.ticket.updatedAt`, or re-read the record.

### Retries and redelivery

A failed delivery is retried up to 8 attempts in total:

| After attempt | Next try in |
|---|---|
| 1 | 30 seconds |
| 2 | 2 minutes |
| 3 | 10 minutes |
| 4 | 30 minutes |
| 5 | 2 hours |
| 6 | 6 hours |
| 7 | 12 hours |
| 8 | none — the delivery becomes `DeadLetter` |

Delivery statuses: `Pending`, `Succeeded`, `Failed` (will retry), `DeadLetter`. A subscription becomes `Failing` after
a failed delivery and `Active` again after a success. After **20 consecutive failures** it is `Disabled`
automatically, the change is audited and administrators are notified. Enable it again with `POST …/enable`, then
redeliver what you missed with `POST …/deliveries/{deliveryId}/redeliver` (attempts restart). Pings never change the
counters.

---

## 9. Incoming webhooks and integrations

### Channel provider callbacks

Messaging providers call `GET|POST /api/v1/channels/webhooks/{provider}/{webhookKey}`. The channel's admin page shows
the full URL. The body limit is 2 MB. Every call is verified before anything is stored:

| Provider | Verification |
|---|---|
| `MetaCloud` (WhatsApp) and `MetaGraph` (Messenger, Instagram) | GET subscription handshake: `hub.mode=subscribe` and `hub.verify_token` must match the channel's verify token; the API echoes `hub.challenge`. POST: `X-Hub-Signature-256` = `sha256=` + hex HMAC-SHA256(app secret, raw body). |
| `Twilio` (SMS, WhatsApp) | `X-Twilio-Signature` = Base64 HMAC-SHA1(auth token, URL + sorted form fields). |
| `GenericSms`, `GenericVoice` | `X-Signature` = hex HMAC-SHA256(webhook secret, raw body); the `sha256=` prefix is optional. |

Responses: 200 accepted, 400 malformed payload, 401 bad signature, 403 failed handshake, 404 unknown key or inactive
channel, 410 tenant suspended, 413 body too large. Redeliveries are de-duplicated per provider message id.

### Wings ERP and Wings Books

ERP screens create tickets with `POST /api/v1/integrations/erp/tickets` and keep the ERP record's GUID and number on the
ticket. See [recipe 12.5](#125-create-a-ticket-from-wings-erp) and [integrations/WINGS-ERP.md](integrations/WINGS-ERP.md)
for the full contract, customer matching and sync rules. `GET /api/v1/integrations/erp/records/{type}/{id}?system=`
returns an ERP record with the tickets that reference it.

Any integration can attach its own record to a new ticket with `origin` on `POST /api/v1/tickets`:
`{"system": "MyCrm", "recordType": "Case", "recordId": null, "recordNumber": "CASE-1001", "url": "https://…", "context": {…}}`.
It appears in the ticket's `externalReferences`.

### Engineering trackers

Azure DevOps service hooks and GitHub webhooks post to `POST /api/v1/engineering/hooks/{provider}/{trackerId}`
(`provider` = `github`, or `azure-devops` with the aliases `azuredevops`, `ado`, `tfs`). GitHub is verified with
`X-Hub-Signature-256`; Azure DevOps with the header `X-SupportDesk-Secret` or Basic authentication carrying the tracker's
webhook secret. Responses: 200 processed or `duplicate`, 202 `ignored` (tracker disabled), 400, 401, 404. See
[integrations/ENGINEERING.md](integrations/ENGINEERING.md).

### Automation rule webhooks

A rule with the `webhook` trigger gets a URL `POST /api/v1/automation/webhooks/{webhookKey}` and a secret.

```bash
curl -s -X POST "$BASE/api/v1/automation/webhooks/$RULE_WEBHOOK_KEY" \
  -H "X-Webhook-Secret: $RULE_WEBHOOK_SECRET" -H "Idempotency-Key: build-4711" -H "Content-Type: application/json" \
  -d '{"ticketNumber": 1210, "build": "4.2.1"}'
```

The JSON body (≤ 256 KB) must reference a ticket with `ticketId`, `ticketNumber` or `ticket: {id | number}`. The API
answers **202** `{ruleId, ticketId, ticketNumber, status, executionId, message}`. The other body fields are available to
the rule's conditions and actions.

---

## 10. Realtime (SignalR)

The hubs use the SignalR JSON protocol (WebSockets, Server-Sent Events or long polling). Pass the access token as the
`access_token` query parameter (accepted only on `/hubs/…`) or as a bearer header. **API-key and OAuth-client
principals cannot connect**; use an agent or contact token.

```js
import * as signalR from '@microsoft/signalr';

const connection = new signalR.HubConnectionBuilder()
  .withUrl(`${BASE}/hubs/desk`, { accessTokenFactory: () => accessToken })
  .withAutomaticReconnect()
  .build();

connection.on('ticket.changed', (e) => console.log('changed', e.ticketId, e.type, e.changes));
connection.on('ticket.thread', (e) => console.log('new entry', e.ticketId, e.seq, e.kind));
await connection.start();
await connection.invoke('JoinTicket', ticketId);
```

### `/hubs/desk` (agents)

On connect, the connection joins the tenant, user and department groups (all departments when the agent's ticket scope
is All).

| Client → server | Purpose |
|---|---|
| `JoinTicket(ticketId)` | Follow one ticket (access-checked); also marks you as viewing |
| `LeaveTicket(ticketId)` | Stop following |
| `SetActivity(ticketId, "viewing" \| "replying" \| "noting")` | Presence |
| `SetAvailability("Online" \| "Away" \| "Busy" \| "Offline")` | Your availability |

| Server → client | Payload |
|---|---|
| `ticket.changed` | `{ticketId, number, type, changes:[{field, from, to}], actorName}` |
| `ticket.thread` | `{ticketId, threadId, seq, kind, visibility}` |
| `ticket.presence` | `{ticketId, viewers:[{userId, name, activity}]}` |
| `sla.alert` | `{ticketId, number, milestone, kind: "warning" \| "breach", dueAt}` |
| `notification` | a notification `{id, type, title, body, link, severity, requiresAck, …}` |
| `agent.availability` | `{userId, availability}` |
| `chat.waiting` | `{conversationId, ticketId, number, visitor, assigneeId}` |
| `conversation.updated` | `{conversationId, channel, status, ticketId, assignedUserId, lastMessageAt, …}` |
| `config.changed` | `{area, entityId}` |

A client in several groups can receive the same event twice (for example through the ticket group and the department
group). Make handlers idempotent. Events are pushed only after the change is committed.

### `/hubs/portal` (contacts)

The connection joins the contact's group. There are no client methods. It receives `ticket.thread` (public entries
only) and `ticket.changed` `{ticketId, number}` for tickets the contact **raised**.

### `/hubs/chat` (chat widget)

Visitors connect with `?conversation={id}&visitor_token={token}` from `POST /api/v1/chat/{widgetKey}/sessions`
(methods `Send(text)`, `Typing(isTyping)`). Agents connect with their token and use `JoinConversation(id)`,
`LeaveConversation(id)`, `AgentSend(id, text)` and `AgentTyping(id, isTyping)`. Events: `chat.message(message,
conversationId)` (two arguments), `chat.status` and `chat.typing`. The embeddable widget does all of this for you
(recipe 12.9).

---

## 11. Endpoint groups

This table is generated from the code by `node tools/api-endpoint-groups.mjs --openapi <url-or-file>`. It joins the
OpenAPI tags with each controller's summary, caller policy and the permissions referenced by the controller and the
services it uses. A single operation usually needs only one of the listed permissions; the OpenAPI document and the
service code have the exact rule.

Callers: **Agent** = agent JWT, API key or OAuth client token; **Contact** = portal token; **Platform** = operator token;
**anonymous** = no credential (protected by keys, secrets or signatures as described).

| Base path | Group (tag) | Ops | Caller | Purpose | Permissions checked |
|---|---|---:|---|---|---|
| `/` | OAuth | 3 | anonymous | OAuth 2.0 client-credentials token endpoint and the public signing keys (JWKS / discovery). | — |
| `/api/v1` | Files | 2 | Agent | File uploads (validated, scanned) and attachment downloads (authorised through the owning ticket). | `admin.ticket_config`, `supervisor.monitor`, `tickets.assign`, `tickets.edit`, `tickets.note`, `tickets.time`, `tickets.view`, `tickets.view_costs` |
| `/api/v1` | TicketItems | 4 | Agent | Task, time-entry and follow-up items addressed by their own id. | `admin.ticket_config`, `supervisor.monitor`, `tickets.assign`, `tickets.edit`, `tickets.note`, `tickets.time`, `tickets.view`, `tickets.view_costs` |
| `/api/v1/admin` | AdminApiCredentials | 8 | Agent | API keys and OAuth clients (admin.integrations). | `admin.integrations` |
| `/api/v1/admin` | AdminRoles | 11 | Agent | Roles & permissions (admin.roles; the role list is also readable with admin.users for role pickers) and user role assignment. | `admin.roles`, `admin.users` |
| `/api/v1/admin/ai` | AiAdmin | 15 | Agent | AI administration (admin.ai): configuration, prompt templates, usage & cost, execution log, provider test, agents. | `admin.ai`, `admin.billing`, `ai.approve_actions` |
| `/api/v1/admin/audit` | Audit | 5 | Agent | Audit viewer (admin.audit): search/filter, detail with before/after diff, CSV export and hash-chain verification. | `admin.audit` |
| `/api/v1/admin/brands` | AdminBrands | 9 | Agent | Brands & portals (admin.organisation). | `admin.organisation` |
| `/api/v1/admin/compliance` | Compliance | 17 | Agent | Compliance (admin.compliance): retention policies, legal holds and data-subject requests. | `admin.compliance` |
| `/api/v1/admin/config` | ConfigTransfer | 12 | Agent | Configuration transfer (admin.config_transfer): export, import into drafts, resolve, publish, snapshots and rollback. | `admin.config_transfer` |
| `/api/v1/admin/notifications` | AdminNotifications | 7 | Agent | Notification defaults and e-mail templates (admin.organisation or admin.channels). | `admin.channels`, `admin.organisation` |
| `/api/v1/admin/security` | AdminSecurity | 8 | Agent | Security policies, SSO and active sessions (admin.security). | `admin.security` |
| `/api/v1/admin/support-access` | SupportAccess | 3 | Agent | Platform support-access requests as seen by the tenant (admin.security or admin.compliance): approve, reject, revoke. There is no impersonation; an approved grant only unlocks read-only diagnostics for the operator, until it expires. | `admin.compliance`, `admin.security` |
| `/api/v1/admin/ticket-config` | AdminTicketConfig | 30 | Agent | Ticket configuration (admin.ticket_config): statuses, categories, issue types, display, numbering & lifecycle, custom fields and form layouts. | `admin.ticket_config` |
| `/api/v1/ai` | Ai | 25 | Agent | Agent-facing AI assistance: classification, summaries, replies, search, insights, analytics, proposals, anomalies. | `admin.ai`, `ai.approve_actions`, `ai.use`, `analytics.team`, `analytics.view`, `kb.author`, `kb.view_internal`, `supervisor.monitor`, `tickets.edit`, `tickets.transfer`, `tickets.view` |
| `/api/v1/analytics` | Analytics | 12 | Agent | Analytics queries over the metric catalogue (whitelisted metric × dimension × filter × period — never SQL from clients), standard reports, drill-down to tickets and metrics maintenance. Results respect the caller's analytics scope. | `analytics.export`, `analytics.schedule`, `analytics.team`, `analytics.view` |
| `/api/v1/analytics/dashboards` | AnalyticsDashboards | 6 | Agent | Analytics dashboards. | `analytics.team`, `analytics.view` |
| `/api/v1/analytics/exports` | AnalyticsExports | 4 | Agent | Analytics exports. | `analytics.export` |
| `/api/v1/analytics/schedules` | AnalyticsSchedules | 7 | Agent | Analytics schedules. | `analytics.schedule`, `analytics.team` |
| `/api/v1/approvals` | Approvals | 3 | Agent | Approval requests (transfers, blueprint transitions, AI actions, custom). | `approvals.decide`, `tickets.edit`, `tickets.note` |
| `/api/v1/assets` | Assets | 4 | Agent | Assets. | `products.manage`, `products.view` |
| `/api/v1/auth` | Auth | 14 | Agent (sign-in endpoints anonymous) | Agent/administrator authentication, MFA, sessions, password management and single sign-on. | `admin.roles`, `admin.security` |
| `/api/v1/automation` | AutomationRules | 22 | Agent | Automation rules: designer catalog, CRUD, versions, activation/order, validation, simulation, conflicts, execution history. | `admin.automation`, `admin.integrations` |
| `/api/v1/automation/blueprints` | Blueprints | 10 | Agent | Blueprint (state-transition) workflow administration. | `admin.automation`, `tickets.edit`, `tickets.view` |
| `/api/v1/automation/webhooks/{webhookKey}` | InboundWebhooks | 1 | anonymous | Inbound webhook trigger for automation rules. Anonymous: authenticated by the rule's secret in X-Webhook-Secret; the tenant is resolved from the rule. Optional Idempotency-Key header de-duplicates retries. | — |
| `/api/v1/brand-assets/{fileId}` | BrandAssets | 1 | anonymous | Public brand logos and favicons (unauthenticated, unguessable ids, inline-safe images only). | — |
| `/api/v1/channels` | Channels | 14 | Agent | Channel accounts (mailboxes, WhatsApp, SMS, chat widgets, voice lines, social pages) and the channel simulator. Requires admin.channels. | `admin.channels` |
| `/api/v1/channels/webhooks/{provider}/{webhookKey}` | ChannelWebhooks | 2 | anonymous | Anonymous provider callbacks. The webhook key identifies the channel (and tenant); the provider adapter verifies the signature (X-Hub-Signature-256, X-Twilio-Signature, X-Signature) before anything is stored. Redeliveries are idempotent. | — |
| `/api/v1/chat/{widgetKey}` | Chat | 10 | anonymous | Anonymous live-chat endpoints used by the embeddable widget. Sessions are authorised with the signed visitor token returned when the chat starts (header X-Visitor-Token). Cross-origin by design (the widget runs on customer sites). | — |
| `/api/v1/collaboration` | Collaboration | 9 | Agent | Microsoft Teams / Slack incoming-webhook channels (admin.integrations: event mappings, send test, delivery log) and sharing a ticket to a channel (tickets.note). | `admin.integrations`, `tickets.note` |
| `/api/v1/community` | Community | 11 | Agent | Community moderation and agent answers. | `community.moderate`, `tickets.create`, `tickets.reply` |
| `/api/v1/compliance/downloads/{tenantId}/{token}` | ComplianceDownloads | 1 | anonymous | One-time download of a data-subject export. Anonymous: the unguessable token is the credential (single use, expiring); the tenant id in the path selects the tenant (and its database) and must match the token's request. | — |
| `/api/v1/contacts` | Contacts | 14 | Agent | Contacts: search, detail, identities, consent, portal access, duplicate detection and merge. | `contacts.merge`, `customers.manage`, `customers.view` |
| `/api/v1/contracts` | Contracts | 12 | Agent | Support contracts, entitlements, renewals, consumption and eligibility preview. | `contracts.manage`, `contracts.view`, `customers.view`, `tickets.create`, `tickets.view_costs` |
| `/api/v1/conversations` | Conversations | 11 | Agent | Omnichannel inbox (all channels), conversation messages, delivery status and agent live-chat actions. | `admin.channels`, `supervisor.monitor`, `tickets.reply`, `tickets.transfer`, `tickets.view` |
| `/api/v1/customer-groups` | CustomerGroups | 4 | Agent | Customer groups (segments such as "Key Accounts"). | `contacts.merge`, `contracts.manage`, `contracts.view`, `customers.manage`, `customers.view`, `products.manage`, `products.view`, `tickets.create` |
| `/api/v1/customers` | Customers | 20 | Agent | Customer organisations: search, detail, Customer 360, hierarchy, locations, departments and their ERP records. | `admin.integrations`, `contacts.merge`, `contracts.manage`, `contracts.view`, `customers.manage`, `customers.merge`, `customers.view`, `products.manage`, `products.view`, `tickets.create` |
| `/api/v1/engineering` | Engineering | 21 | Agent | Engineering escalation from tickets (engineering.link / tickets.view) and the built-in engineering board (engineering.view / engineering.manage). Permissions and ticket scope are enforced in the services. | `engineering.link`, `engineering.manage`, `engineering.view`, `tickets.edit`, `tickets.view` |
| `/api/v1/engineering/admin` | EngineeringAdmin | 13 | Agent | Engineering administration (admin.integrations): trackers, mappings, webhook secrets, sync, logs and auto-notify settings. | `admin.integrations` |
| `/api/v1/engineering/hooks/{provider}/{trackerId}` | EngineeringHooks | 1 | anonymous | Inbound tracker webhooks (anonymous): Azure DevOps service hooks and GitHub webhooks. Authenticated by the tracker's webhook secret (GitHub HMAC signature / X-SupportDesk-Secret or Basic auth); the tenant comes from the tracker. | — |
| `/api/v1/installations` | Installations | 5 | Agent | Installations (customer sites running a product), with environment details, licences and assets. | `products.manage`, `products.view` |
| `/api/v1/integrations/erp` | Erp | 7 | Agent | Wings ERP / Wings Books integration. ERP screens call POST /tickets (OAuth client or API key with tickets:write); agents read ERP records for the ticket context panel; administrators (admin.integrations) configure the connection, test it, run a sync and read the integration log. | `admin.integrations`, `customers.view`, `tickets.create` |
| `/api/v1/kb` | Knowledge | 37 | Agent | Knowledge base for agents: categories, articles (versions, diff/restore, workflow, translations, attachments, access rules), feedback review, ticket → article, import/export, analytics and guided-troubleshooting flows. | `admin.knowledge`, `analytics.team`, `community.moderate`, `kb.author`, `kb.publish`, `kb.review`, `kb.view_internal` |
| `/api/v1/licences` | Licences | 4 | Agent | Licences. | `products.manage`, `products.view` |
| `/api/v1/me` | Me | 7 | Agent | The signed-in agent: profile, availability, sessions and notification preferences. | `tickets.assign`, `tickets.bulk`, `tickets.delete`, `tickets.reply`, `tickets.transfer` |
| `/api/v1/me/devices` | Devices | 3 | Agent | Push-notification devices of the signed-in agent (mobile app). | — |
| `/api/v1/meta` | Meta | 3 | Agent | Form/filter metadata for the workbench. | — |
| `/api/v1/notifications` | Notifications | 5 | Agent | In-app notifications for the signed-in agent. | — |
| `/api/v1/org` | Organisation | 18 | Agent | Departments, teams (members, lead, routing configuration), routing preview, skills and role options. | `admin.organisation`, `admin.sla`, `admin.users`, `agents.manage`, `supervisor.monitor`, `tickets.reply`, `tickets.view` |
| `/api/v1/org` | Workforce | 17 | Agent | Shifts and assignments, leave (with approvals), delegation and the team schedule. | `admin.organisation`, `admin.sla`, `admin.users`, `agents.manage`, `approvals.decide`, `supervisor.monitor`, `tickets.edit`, `tickets.note` |
| `/api/v1/org/agents` | Agents | 14 | Agent | Agents: list, invite, profile, access, routing profile, availability, avatar, deactivate/reactivate, plan seats. | `admin.organisation`, `admin.sla`, `admin.users`, `agents.manage`, `supervisor.monitor` |
| `/api/v1/org/monitor` | Monitor | 1 | Agent | Supervisor live monitor snapshot (refreshed by the desk on realtime events). | `admin.users`, `agents.manage`, `supervisor.monitor`, `tickets.assign` |
| `/api/v1/org/public` | OrganisationPublic | 2 | anonymous | Anonymous endpoints: accepting an agent invitation (sets the password) and avatar images. | — |
| `/api/v1/platform` | PlatformTenants | 9 | Platform | Tenants: overview, list/search/detail, suspend, reactivate, cancel, extend trial, change plan, dedicated database. | — |
| `/api/v1/platform/audit` | PlatformAudit | 3 | Platform | Platform audit trail and its hash-chain verification. | — |
| `/api/v1/platform/auth/login` | PlatformAuth | 1 | anonymous | Platform operator sign-in (SaaS administration console). | — |
| `/api/v1/platform/invoices` | PlatformInvoices | 5 | Platform | Invoices across tenants: list, detail, PDF, mark paid (offline payments), void. | — |
| `/api/v1/platform/plans` | PlatformPlans | 5 | Platform | Plans CRUD. | — |
| `/api/v1/platform/support-access` | PlatformSupportAccess | 3 | Platform | Support access requests (operator side) and read-only diagnostics while an approved grant is active. | — |
| `/api/v1/portal/{brand}` | PortalAuth | 7 | anonymous | Customer portal configuration and contact authentication. The brand slug is resolved server-side to its tenant. | — |
| `/api/v1/portal/{brand}` | PortalContent | 20 | anonymous | Public portal content (anonymous or signed-in contact): home, knowledge base, search, deflection suggestions, guided troubleshooting, community reading, support contacts, assistant and anonymous submissions. The brand slug is resolved server-side; a contact token only works for brands of its own tenant. | — |
| `/api/v1/portal/{brand}` | PortalTickets | 24 | Contact | Signed-in contact endpoints: tickets (scope-enforced), uploads, community posting and account. | — |
| `/api/v1/portal/{brand}/devices` | PortalDevices | 3 | Contact | Push-notification devices of the signed-in portal contact (mobile app, customer mode). | — |
| `/api/v1/product-families` | ProductFamilies | 4 | Agent | Product families. | `products.manage`, `products.view`, `tickets.view` |
| `/api/v1/products` | Products | 15 | Agent | Product catalog: families, products, modules, versions and releases; install-base statistics. | `products.manage`, `products.view`, `tickets.view` |
| `/api/v1/public/csat/{token}` | Csat | 2 | anonymous | Public customer-satisfaction survey (link from the survey e-mail/message). The token is single-purpose and expires. | — |
| `/api/v1/search` | QuickSearch | 4 | Agent | Workbench look-ups: quick search (palette / header box) and the customer / contact / installation pickers of the ticket form. | `customers.view`, `products.view`, `tickets.create`, `tickets.edit`, `tickets.view` |
| `/api/v1/search` | Search | 3 | Agent | Global search across tickets, articles, contacts, customers and community topics (permission-aware). | `admin.knowledge`, `admin.security`, `community.moderate`, `customers.view`, `kb.author`, `kb.publish`, `kb.review`, `kb.view_internal`, `tickets.view` |
| `/api/v1/service-activities` | ServiceActivities | 5 | Agent | Service visits and scheduled activities (calendar). | `customers.manage`, `customers.view` |
| `/api/v1/signup` | Signup | 4 | anonymous | Public trial sign-up: slug availability, public plans, start (e-mails a code) and verify (provisions the workspace). | — |
| `/api/v1/sla` | SlaAdmin | 19 | Agent | SLA policies (targets, escalations, order, simulation), business calendars and holidays, live breaches. | `admin.organisation`, `admin.sla`, `admin.users`, `agents.manage`, `supervisor.monitor` |
| `/api/v1/subscription` | Subscription | 11 | Agent | The tenant administrator's subscription and billing (admin.billing). Routed under /api/v1/subscription, which stays writable while the tenant is suspended so the plan can be changed and invoices paid. | `admin.billing` |
| `/api/v1/templates` | Templates | 4 | Agent | Reply templates / macros. | `admin.ticket_config`, `supervisor.monitor`, `tickets.assign`, `tickets.edit`, `tickets.note`, `tickets.time`, `tickets.view`, `tickets.view_costs` |
| `/api/v1/tickets` | Tickets | 43 | Agent | Ticket lifecycle, conversation, collaboration and workbench context. | `admin.automation`, `admin.ticket_config`, `approvals.decide`, `contracts.view`, `customers.view`, `products.view`, `supervisor.monitor`, `tickets.assign`, `tickets.bulk`, `tickets.create`, `tickets.delete`, `tickets.edit`, `tickets.escalate`, `tickets.merge`, `tickets.note`, `tickets.override_entitlement`, `tickets.reopen`, `tickets.reply`, `tickets.resolve`, `tickets.split`, `tickets.time`, `tickets.transfer`, `tickets.view`, `tickets.view_costs` |
| `/api/v1/tickets/{id}/blueprint` | TicketBlueprint | 3 | Agent | A ticket's blueprint: current state, transitions available to the caller, transition execution. | `admin.automation`, `tickets.edit`, `tickets.view` |
| `/api/v1/views` | Views | 4 | Agent | Saved views (personal and shared). | `admin.ticket_config`, `supervisor.monitor`, `tickets.assign`, `tickets.edit`, `tickets.note`, `tickets.time`, `tickets.view`, `tickets.view_costs` |
| `/api/v1/webhook-subscriptions` | WebhookSubscriptions | 13 | Agent | Outbound webhook subscriptions (admin.integrations): signed event deliveries, delivery log, redelivery and test ping. | `admin.integrations` |
| `/widget` | Widget | 2 | anonymous | Serves the embeddable chat widget (/widget/widget.js) and its demo page (/widget/demo.html) from embedded resources. | — |

_725 operations in 78 groups (generated by tools/api-endpoint-groups.mjs)._

---

## 12. Recipes

The curl examples use three shell variables:

```bash
export BASE=https://desk.example.com              # local: http://localhost:5830
export SD_API_KEY=sdk_xxxxxxxxxx_yyyyyyyyyyyyyyyy   # an API key (section 2.2)
export TOKEN=eyJhbGciOiJFUzI1NiIs…                 # an agent access token (section 2.1), where noted
```

The C# examples share this set-up (.NET 8 or later, no extra packages):

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

var http = new HttpClient { BaseAddress = new Uri("https://desk.example.com/") };
http.DefaultRequestHeaders.Add("X-Api-Key", Environment.GetEnvironmentVariable("SD_API_KEY"));
```

### 12.1 Create a ticket

Scope `tickets:write`. Only `subject` is required. `newContact` finds a contact by e-mail or phone, or creates one.

```bash
curl -s -X POST "$BASE/api/v1/tickets" \
  -H "X-Api-Key: $SD_API_KEY" -H "Content-Type: application/json" \
  -d '{
        "subject": "Printer driver crashes after the 4.2 update",
        "descriptionHtml": "<p>Since Monday every invoice print crashes the client.</p>",
        "channel": "Api",
        "priority": "High",
        "newContact": { "fullName": "Kavya Rao", "email": "kavya@example.com" },
        "tags": ["printing"],
        "clientRequestId": "crm-case-88412"
      }'
```

```csharp
var response = await http.PostAsJsonAsync("api/v1/tickets", new
{
    subject = "Printer driver crashes after the 4.2 update",
    descriptionHtml = "<p>Since Monday every invoice print crashes the client.</p>",
    channel = "Api",
    priority = "High",
    newContact = new { fullName = "Kavya Rao", email = "kavya@example.com" },
    tags = new[] { "printing" },
    clientRequestId = "crm-case-88412"
});
response.EnsureSuccessStatusCode();
var ticket = await response.Content.ReadFromJsonAsync<JsonElement>();
var ticketId = ticket.GetProperty("id").GetGuid();
var lastSeq = ticket.GetProperty("lastThreadSeq").GetInt32();
Console.WriteLine($"{ticket.GetProperty("displayNumber")} {ticket.GetProperty("status")}");
```

The response is **201** with the full ticket (`id`, `number`, `displayNumber`, `status`, `rowVersion`, `lastThreadSeq`,
SLA due dates, entitlement…). Routing and automation run as for any new ticket. Other useful fields: `customerId`,
`contactId`, `departmentId`, `teamId`, `assigneeId`, `productId`, `installationId`, `categoryId`, `customFields`
(`{"key": value}`), `attachmentFileIds` (from `POST /api/v1/files`), `origin` (section 9) and `skipRouting`.
Valid `channel` values: `Email`, `Portal`, `WebForm`, `Chat`, `WhatsApp`, `Phone`, `Sms`, `Facebook`, `Instagram`,
`MobileApp`, `Api`, `WalkIn`, `Erp`, `Internal` (default).

### 12.2 Reply to a ticket

Scope `tickets:write` (permission `tickets.reply`). The reply goes out on the ticket's channel (e-mail for API tickets
with a contact e-mail).

```bash
curl -s -X POST "$BASE/api/v1/tickets/$TICKET_ID/replies" \
  -H "X-Api-Key: $SD_API_KEY" -H "Content-Type: application/json" \
  -d '{
        "bodyHtml": "<p>Thanks Kavya, we are looking into it.</p>",
        "basedOnSeq": 0,
        "clientRequestId": "crm-reply-1"
      }'
```

```csharp
var reply = await http.PostAsJsonAsync($"api/v1/tickets/{ticketId}/replies", new
{
    bodyHtml = "<p>Thanks Kavya, we are looking into it.</p>",
    basedOnSeq = lastSeq,
    clientRequestId = $"crm-reply-{ticketId:N}-1"
});
if (reply.StatusCode == HttpStatusCode.Conflict)
{
    var problem = await reply.Content.ReadFromJsonAsync<JsonElement>();
    if (problem.GetProperty("code").GetString() == "reply_collision")
    {
        // Someone posted while we were composing: show problem.newEntries to the user, then
        // resend with basedOnSeq = problem.currentSeq (or force = true).
    }
}
reply.EnsureSuccessStatusCode();
var thread = await reply.Content.ReadFromJsonAsync<JsonElement>();
Console.WriteLine($"reply seq {thread.GetProperty("seq")}, delivery {thread.GetProperty("deliveryStatus")}");
```

Optional fields: `to`, `cc`, `bcc` (e-mail tickets), `attachmentFileIds`, `statusId` (change the status in the same
call), `templateId`, `force`. Internal notes use `POST …/notes` `{bodyHtml, kind: "note" | "handover", mentionUserIds}`.

### 12.3 Search

```bash
# Global search: tickets, articles, contacts, customers, community (permission-aware)
curl -s "$BASE/api/v1/search?q=printer" -H "X-Api-Key: $SD_API_KEY"

# Ticket list with filters, sorted, keyset-paged
curl -s "$BASE/api/v1/tickets?q=printer&statusCategory=Open,Assigned,InProgress&priority=High&sort=lastActivity&dir=desc&limit=50" \
  -H "X-Api-Key: $SD_API_KEY"
```

```csharp
var results = await http.GetFromJsonAsync<JsonElement>("api/v1/search?q=" + Uri.EscapeDataString("printer driver"));
foreach (var hit in results.GetProperty("hits").EnumerateArray())
    Console.WriteLine($"{hit.GetProperty("type")}: {hit.GetProperty("title")}");

string? cursor = null;
do
{
    var url = "api/v1/tickets?statusCategory=Assigned&priority=High&limit=50" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
    var page = await http.GetFromJsonAsync<JsonElement>(url);
    foreach (var t in page.GetProperty("items").EnumerateArray()) Console.WriteLine(t.GetProperty("displayNumber"));
    cursor = page.GetProperty("nextCursor").GetString();
} while (cursor is not null);
```

Global search returns `{query, hits: [{type, id, title, subtitle, snippetHtml, score, url}]}`. The desk's quick look-up
is `GET /api/v1/search/quick?q=&limit=6` (`{tickets, customers, contacts}`); semantic search is `POST /api/v1/ai/search`
(needs `ai.use`).

### 12.4 Create or update a contact (upsert)

There is no upsert endpoint. Create the contact; if the e-mail already belongs to an active contact the API answers
**409 `duplicate_contact`**, and you update that contact instead. Scope `customers:write`.

```bash
# 1. Try to create
curl -s -X POST "$BASE/api/v1/contacts" -H "X-Api-Key: $SD_API_KEY" -H "Content-Type: application/json" \
  -d '{"fullName":"Kavya Rao","email":"kavya@example.com","jobTitle":"Finance lead","preferredChannel":"Email"}'

# 2. On 409 duplicate_contact: find the contact and read it (for rowVersion and the fields to keep)
curl -s "$BASE/api/v1/contacts?q=kavya@example.com" -H "X-Api-Key: $SD_API_KEY"
curl -s "$BASE/api/v1/contacts/$CONTACT_ID" -H "X-Api-Key: $SD_API_KEY"

# 3. Replace it (PUT sends the whole record, including rowVersion)
curl -s -X PUT "$BASE/api/v1/contacts/$CONTACT_ID" -H "X-Api-Key: $SD_API_KEY" -H "Content-Type: application/json" \
  -d "{\"fullName\":\"Kavya Rao\",\"email\":\"kavya@example.com\",\"jobTitle\":\"Finance lead\",\"preferredChannel\":\"Email\",\"customerId\":$CUSTOMER_ID_JSON,\"rowVersion\":\"$ROW_VERSION\"}"
```

```csharp
var contact = new Dictionary<string, object?>
{
    ["fullName"] = "Kavya Rao", ["email"] = "kavya@example.com", ["jobTitle"] = "Finance lead", ["preferredChannel"] = "Email"
};
var created = await http.PostAsJsonAsync("api/v1/contacts", contact);
if (created.StatusCode == HttpStatusCode.Conflict)
{
    // 409 duplicate_contact: the e-mail already belongs to an active contact, so update that one.
    var matches = await http.GetFromJsonAsync<JsonElement>("api/v1/contacts?q=" + Uri.EscapeDataString("kavya@example.com"));
    var id = matches.GetProperty("items")[0].GetProperty("id").GetGuid();
    var current = await http.GetFromJsonAsync<JsonElement>($"api/v1/contacts/{id}");
    contact["customerId"] = current.GetProperty("customerId").ValueKind == JsonValueKind.Null ? null : current.GetProperty("customerId").GetGuid();
    contact["rowVersion"] = current.GetProperty("rowVersion").GetString();   // PUT replaces the record: send every field you want to keep
    var updated = await http.PutAsJsonAsync($"api/v1/contacts/{id}", contact);
    updated.EnsureSuccessStatusCode();
}
else created.EnsureSuccessStatusCode();
```

Customers work the same way with `POST|PUT /api/v1/customers[/{id}]`. Look a customer up by name, code, ERP code,
domain, e-mail or phone with `GET /api/v1/customers?q=`. A duplicate customer `code` answers 400 with `errors.code`.
`SaveCustomerRequest` also carries `erpCustomerId` and `erpCode` for ERP-linked customers.

### 12.5 Create a ticket from Wings ERP

Scope `tickets:write` (API key or OAuth client). The ERP record's GUID and number are stored on the ticket as its
**origin external reference**, and the channel is `Erp`.

```bash
curl -s -X POST "$BASE/api/v1/integrations/erp/tickets" \
  -H "X-Api-Key: $SD_API_KEY" -H "Content-Type: application/json" \
  -d '{
        "system": "WingsErp",
        "recordType": "SalesInvoice",
        "recordId": "b5945f8f-abf8-f95b-83f0-4e0c5abf13ba",
        "recordNumber": "SI/26-27/66994",
        "context": { "screen": "Sales > Invoices > View", "branch": "Chennai - Head Office", "totalAmount": 50445.00 },
        "customerCode": "C-1001",
        "contactEmail": "ramesh@abcretail.demo",
        "contactName": "Ramesh Kumar",
        "erpUser": "ramesh",
        "subject": "Tax split on invoice SI/26-27/66994 looks wrong",
        "description": "The invoice shows IGST; our auditor expects CGST + SGST.",
        "priority": "High",
        "productCode": "WERP",
        "clientRequestId": "SI-66994-1"
      }'
```

```csharp
var erp = await http.PostAsJsonAsync("api/v1/integrations/erp/tickets", new
{
    system = "WingsErp",
    recordType = "SalesInvoice",
    recordId = Guid.Parse("b5945f8f-abf8-f95b-83f0-4e0c5abf13ba"),
    recordNumber = "SI/26-27/66994",
    context = new { screen = "Sales > Invoices > View", totalAmount = 50445.00m },
    customerCode = "C-1001",
    contactEmail = "ramesh@abcretail.demo",
    subject = "Tax split on invoice SI/26-27/66994 looks wrong",
    description = "The invoice shows IGST; our auditor expects CGST + SGST.",
    priority = "High",
    productCode = "WERP",
    clientRequestId = "SI-66994-1"
});
erp.EnsureSuccessStatusCode();   // 201 the first time, 200 when clientRequestId was seen before
var created = await erp.Content.ReadFromJsonAsync<JsonElement>();
Console.WriteLine($"{created.GetProperty("displayNumber")} {created.GetProperty("deskUrl")}");
```

Response (**201**, `Location: /api/v1/tickets/{id}`; a retry with the same `clientRequestId` returns **200** and the
same ticket):

```json
{ "ticketId": "9f795f14-f58e-4b13-7f72-08df29ea0b23", "number": 1209, "displayNumber": "#1209", "status": "In progress",
  "entitlementStatus": "Entitled", "customerId": "12ac4725-…", "customerName": "ABC Retail Pvt Ltd",
  "contactId": "2c542c82-…", "portalUrl": "/portal/wings/tickets/9f795f14-…", "deskUrl": "/tickets/9f795f14-…" }
```

`GET /api/v1/tickets/{ticketId}` then shows the record in `externalReferences`:

```json
{ "system": "WingsErp", "recordType": "SalesInvoice", "recordId": "b5945f8f-abf8-f95b-83f0-4e0c5abf13ba",
  "recordNumber": "SI/26-27/66994", "url": null, "isOrigin": true,
  "context": { "screen": "Sales > Invoices > View", "branch": "Chennai - Head Office", "totalAmount": 50445 } }
```

Rules (details in [integrations/WINGS-ERP.md](integrations/WINGS-ERP.md)):

* `recordType` and `subject` are required, plus `recordId` and/or `recordNumber`, plus `customerErpId` or `customerCode`.
* The customer is matched by ERP id, then code; an unknown customer answers **422 `unknown_customer`** unless the
  integration may create ERP-only customers.
* The contact is matched or created by e-mail or phone, otherwise the customer's primary contact is used.
* `productCode` (default `WERP`, or `WBKS` for Wings Books) picks the product and installation, so eligibility and SLA
  apply as usual.
* To find tickets for an ERP record later: `GET /api/v1/integrations/erp/records/{recordType}/{recordId}?system=WingsErp`.

### 12.6 Query analytics

Analytics reads are `GET` requests with query parameters, never SQL. List metrics, dimensions, filters and periods with
`GET /api/v1/analytics/catalog`.

```bash
curl -s "$BASE/api/v1/analytics/query?metric=ticket_count&dimension=week&period=last_90_days&compare=previous" \
  -H "Authorization: Bearer $TOKEN"
```

```csharp
var result = await http.GetFromJsonAsync<JsonElement>(
    "api/v1/analytics/query?metric=ticket_count&dimension=week&period=last_90_days&compare=previous");
Console.WriteLine($"total {result.GetProperty("total")}, rows {result.GetProperty("rows").GetArrayLength()}");
```

Response: `{metric, dimension, period, comparePeriod, total, totalCount, compareTotal, deltaPercent, truncated,
rows: [{key, label, value, count, compareValue, deltaPercent}]}`.

| Parameter | Values |
|---|---|
| `metric` | for example `ticket_count`, `resolved_tickets`, `backlog`, `median_first_response_business_hours`, `sla_compliance`, `csat_avg`, `fcr_rate` (about 40 in the catalogue) |
| `dimension` | `none`, `day`, `week`, `month`, `department`, `team`, `assignee`, `channel`, `priority`, `status_category`, `customer`, `customer_tier`, `product`, `module`, `version`, `category`, `issue_type`, `root_cause`, `sentiment` |
| `period` | `today`, `yesterday`, `this_week`, `last_week`, `last_7_days`, `this_month`, `last_month`, `last_30_days`, `last_90_days`, `this_quarter`, `this_year`, `last_365_days`, `all_time`, `custom` (+ `from`, `to` as `yyyy-MM-dd`) |
| `compare` | `previous`, `last_year` |
| filters | `department`, `team`, `assignee`, `channel`, `priority`, `customer`, `product`, `module`, `version`, `category`, `tier`, `statusCategory` (comma-separated) |

Related endpoints: `breakdown` (several `metrics` side by side), `pareto`, `cohorts`, `ageing`, `drilldown` (the tickets
behind a number: `key` or `bucket`, `cursor`, `pageSize`), `reports` and `reports/{key}`.

**Scope matters.** Results are limited to the caller's analytics scope. A supervisor or analyst token (with
`analytics.team`) sees the data scope; an API key with `analytics:read` (which includes `analytics.team`) sees all of
the tenant's tickets.

### 12.7 List SLA breaches

Needs `admin.sla`, `supervisor.monitor` or `admin.organisation`: a supervisor's token, or a key with `admin:write` or
`*`.

```bash
curl -s "$BASE/api/v1/sla/breaches/summary" -H "Authorization: Bearer $TOKEN"
curl -s "$BASE/api/v1/sla/breaches?state=Breached&limit=50" -H "Authorization: Bearer $TOKEN"
```

```csharp
using var desk = new HttpClient { BaseAddress = new Uri("https://desk.example.com/") };
var login = await desk.PostAsJsonAsync("api/v1/auth/login", new { tenant = "wings", email = "priya@wings.demo", password = "Desk@2026" });
var session = await login.Content.ReadFromJsonAsync<JsonElement>();   // status "ok" here; "mfa_required" needs POST api/v1/auth/mfa
desk.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());

var summary = await desk.GetFromJsonAsync<JsonElement>("api/v1/sla/breaches/summary");
var breaches = await desk.GetFromJsonAsync<JsonElement>("api/v1/sla/breaches?state=Breached&limit=50");
Console.WriteLine($"breached {summary.GetProperty("breached")}, at risk {summary.GetProperty("atRisk")}");
```

* Summary: `{atRisk, breached, breachedToday, paused, dueNextHour}`.
* List parameters: `state` (`AtRisk`, `Breached`, `All`), `departmentId`, `milestone` (`FirstResponse`, `NextResponse`,
  `Resolution`), `cursor`, `limit`. Items include `ticketId`, `number`, `subject`, `priority`, `milestone`, `dueAt`,
  `breachedAt`, `targetMinutes`, `elapsedMinutes` and the assignee.
* With only `tickets:read`, filter the ticket list instead: `GET /api/v1/tickets?slaState=AtRisk,Breached`.
* To be told as it happens, subscribe a webhook to `sla.warning` and `sla.breached` (recipe 12.8).

### 12.8 Subscribe a webhook and verify the signature

Needs `admin.integrations` (an administrator token, or a key with `admin:write`).

```bash
curl -s -X POST "$BASE/api/v1/webhook-subscriptions" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"CRM receiver","url":"https://crm.example.com/hooks/supportdesk","events":["ticket.created","ticket.reply_added","sla.breached"],"isActive":true}'
# → { "id": "a9209b4c-…", "status": "Active", "secret": "whsec_PHyW2K1Oms…", … }   (store the secret now)

curl -s -X POST "$BASE/api/v1/webhook-subscriptions/$WEBHOOK_ID/ping" -H "Authorization: Bearer $TOKEN"
curl -s "$BASE/api/v1/webhook-subscriptions/$WEBHOOK_ID/deliveries?pageSize=20" -H "Authorization: Bearer $TOKEN"
```

```csharp
using var admin = new HttpClient { BaseAddress = new Uri("https://desk.example.com/") };
admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
var hook = await admin.PostAsJsonAsync("api/v1/webhook-subscriptions", new
{
    name = "CRM receiver",
    url = "https://crm.example.com/hooks/supportdesk",
    events = new[] { "ticket.created", "ticket.reply_added", "sla.breached" },
    isActive = true
});
hook.EnsureSuccessStatusCode();
var subscription = await hook.Content.ReadFromJsonAsync<JsonElement>();
var signingSecret = subscription.GetProperty("secret").GetString();   // shown once — store it securely
var ping = await admin.PostAsync($"api/v1/webhook-subscriptions/{subscription.GetProperty("id")}/ping", null);
Console.WriteLine((await ping.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status"));   // Succeeded or Failed
```

Then verify each delivery as shown in [section 8](#verifying-the-signature).

### 12.9 Embed the chat widget

An administrator creates a **Chat** channel under **Administration › Channels**. Its page shows the widget key
(`wk_…`) and a **Copy embed code** button. Paste the snippet before `</body>` on your website:

```html
<script src="https://desk.example.com/widget/widget.js" data-widget-key="wk_XXXXXXXXXXXXXXXXXXXX" async></script>
```

Optional attributes: `data-name` and `data-email` (prefill for signed-in visitors), `data-open="true"` (open on
load), `data-position="left"`. The widget renders in a shadow root, uses the brand colours, talks to
`/api/v1/chat/{widgetKey}/…` with a signed visitor token and receives live updates over `/hubs/chat` (with a polling
fallback). The widget endpoints allow any origin, so no CORS set-up is needed. Preview it at
`/widget/demo.html?widgetKey=wk_…`.

There is no C# step for the widget. A server-side integration that wants chat transcripts reads the ticket created at
handoff (channel `Chat`) or subscribes to `ticket.created` webhooks.

### 12.10 Submit a ticket from your own web form

The brand must allow **anonymous tickets** (Administration › Brands & portals); otherwise the endpoint answers 404. No
credential is needed. The endpoint accepts JSON, `application/x-www-form-urlencoded` or `multipart/form-data`.

```bash
# JSON
curl -s -X POST "$BASE/api/v1/portal/wings/webform" -H "Content-Type: application/json" \
  -d '{"name":"Kavya Rao","email":"kavya@example.com","subject":"Need help with GST returns","description":"How do I file GSTR-1 from Wings?","website":""}'

# Classic form post
curl -s -X POST "$BASE/api/v1/portal/wings/webform" \
  --data-urlencode "name=Kavya Rao" --data-urlencode "email=kavya@example.com" \
  --data-urlencode "subject=Question about e-way bills" --data-urlencode "description=How do I generate an e-way bill?" \
  --data-urlencode "website="
# → {"accepted":true,"displayNumber":"#1211","message":"Thank you — your ticket #1211 has been created. We will reply by e-mail."}
```

```csharp
using var anon = new HttpClient { BaseAddress = new Uri("https://desk.example.com/") };
var submitted = await anon.PostAsJsonAsync("api/v1/portal/wings/webform", new
{
    name = "Kavya Rao",
    email = "kavya@example.com",
    subject = "Question about e-way bills",
    description = "How do I generate an e-way bill from a delivery note?",
    website = ""          // honeypot: must stay empty
});
submitted.EnsureSuccessStatusCode();
var result = await submitted.Content.ReadFromJsonAsync<JsonElement>();
Console.WriteLine($"accepted {result.GetProperty("accepted")}, {result.GetProperty("displayNumber")}");
```

* Fields: `name` (2–120), `email`, `phone` (optional), `subject` (3–250), `description` (plain text) or
  `descriptionHtml`, optional `departmentId`, `categoryId`, `productId` and `customFields` (JSON only).
  `GET /api/v1/portal/{brand}/submit/form` lists the allowed departments, categories, products and fields.
* **Spam protection without a captcha:** keep a hidden `website` field empty (a honeypot), and send `formStartedAt`
  (epoch milliseconds when the form was shown). Submissions with the honeypot filled, or sent within 2.5 seconds, get a
  fake success and create nothing. Each IP may submit 5 tickets per hour per brand (then 429).
* The contact is matched by e-mail or phone, or created. The ticket channel is `WebForm`.
* A classic HTML form post receives the JSON result (there is no redirect). A browser `fetch` from another origin needs
  that origin in the API's `Cors:Origins` setting.
* Signed-in portal apps use `POST /api/v1/portal/{brand}/tickets` with a contact token instead.
