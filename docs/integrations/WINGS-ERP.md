# Wings ERP / Wings Books integration

SupportDesk talks to Wings ERP and Wings Books **only through their APIs** (HTTP/JSON) — never by reading or writing
the ERP database. Two directions are supported:

| Direction | What | Endpoint / component |
|---|---|---|
| ERP → SupportDesk | Raise a ticket from an ERP screen (sales invoice, order, receipt, delivery, AMC contract…) | `POST /api/v1/integrations/erp/tickets` |
| SupportDesk → ERP | Read customer masters, installations, licences, AMC contracts, transactions; update a record | `IErpConnector` (HTTP connector or simulator) |

Everything is per tenant: each tenant configures its own `Integration` row (`Type = WingsErp` or `WingsBooks`).

## 1. Configuration (Products & Assets → ERP integration, permission `admin.integrations`)

`GET/PUT /api/v1/integrations/erp/settings?system=WingsErp|WingsBooks`

| Setting | Meaning |
|---|---|
| `mode` | `simulator` (development/demo, deterministic data) or `http` (real Wings API) |
| `baseUrl` | API root, e.g. `https://erp.customer.example` (http mode) |
| `authType` | `OAuth2` (client credentials, default), `ApiKey` (`X-Api-Key` header) or `None` |
| `tokenUrl` | OAuth token endpoint; defaults to `{baseUrl}/oauth/token` |
| `clientId`, `clientSecret`, `scope` | OAuth client; the secret is write-only and stored encrypted (AES-GCM) |
| `apiKey` | API key (write-only, encrypted) |
| `timeoutSeconds` | Per-call timeout (5–300 s) on top of the shared resilience pipeline |
| `syncIntervalMinutes` | Scheduled sync interval (recurring task `erp-sync`, checked every 5 minutes) |
| `syncCustomers` / `syncInstallations` / `syncLicences` / `syncContracts` | What the sync imports |
| `createMissingCustomers` | Create customers that exist only in the ERP (sync and ERP ticket creation) |

Other admin endpoints: `POST …/test` (ping), `POST …/sync` (manual full sync, returns per-entity counts),
`GET …/logs?operation=sync|ticket|record` (integration log). Non-secret settings live in `Integration.SettingsJson`,
secrets in `Integration.SecretsProtected`.

The HTTP client is a named `HttpClient` (`wings-erp`) with the standard resilience handler: 2 retries for safe
methods only (record updates are never retried automatically), 30 s attempt timeout, circuit breaker. OAuth tokens
are cached per integration and credential until a minute before expiry; a 401 drops the token and retries once.
Every outbound call is written to `IntegrationLog` (method + URL without query string, status, duration; response
excerpts only on failure; credentials are never logged).

## 2. Creating tickets from ERP screens

```
POST /api/v1/integrations/erp/tickets
Authorization: Bearer <OAuth client token>     (or)    X-Api-Key: sdk_xxx_yyy
```

The caller needs the **`tickets:write`** scope (OAuth client or API key created under Administration → API access).
Agents with `tickets.create` may call it too.

```json
{
  "system": "WingsErp",
  "recordType": "SalesInvoice",
  "recordId": "b5945f8f-abf8-f95b-83f0-4e0c5abf13ba",
  "recordNumber": "SI/26-27/66994",
  "recordUrl": null,
  "context": { "screen": "Sales › Invoices › View", "branch": "Chennai – Head Office", "totalAmount": 50445.00 },
  "customerErpId": "0f12271f-367b-6b53-8b6d-bbf65282a0d3",
  "customerCode": "C-1001",
  "contactEmail": "ramesh@abcretail.demo",
  "contactName": "Ramesh Kumar",
  "contactPhone": null,
  "erpUser": "ramesh",
  "subject": "Tax split on invoice SI/26-27/66994 looks wrong",
  "description": "The invoice shows IGST, our auditor expects CGST + SGST…",
  "priority": "High",
  "productCode": "WERP",
  "clientRequestId": "SI-66994-1"
}
```

* `recordType`: `SalesInvoice`, `SalesOrder`, `Receipt`, `Delivery`, `CreditNote`, `AmcContract` (other values are
  stored as sent). `recordId` (GUID) and/or `recordNumber` are required; `context` is any JSON object (≤ 16 KB).
* Customer resolution: `customerErpId` → `customerCode` (ERP code, then customer code) → import from the ERP master
  when `createMissingCustomers` is on → otherwise **422 `unknown_customer`**.
* Contact resolution: by e-mail/phone (matched or created, linked to the customer), else the customer's primary contact.
* `productCode` (default `WERP` / `WBKS`) classifies the ticket and picks the customer's active installation, so support
  eligibility and SLA are evaluated as for any ticket.
* The originating record becomes the ticket's **origin external reference** (`TicketExternalReference`, `IsOrigin =
  true`, record GUID, number, URL and the context JSON). The ticket channel is `Erp`.
* `clientRequestId` makes the call idempotent: a retry returns the same ticket with **200** instead of **201**.

Response (`201 Created`):

```json
{
  "ticketId": "d07a3d57-…", "number": 1194, "displayNumber": "#1194", "status": "Open",
  "entitlementStatus": "Entitled", "customerId": "…", "customerName": "ABC Retail Pvt Ltd", "contactId": "…",
  "portalUrl": "/portal/wings/tickets/d07a3d57-…", "deskUrl": "/tickets/d07a3d57-…"
}
```

`portalUrl` is absolute when the brand has a custom domain or `Portal:PublicBaseUrl` is configured.

### Context panel

* `GET /api/v1/integrations/erp/records/{type}/{id}?system=WingsErp` returns the record from the ERP plus the
  SupportDesk customer it belongs to and the tickets that reference it (agents limited to their own accounts only see
  records of those customers).
* The ticket workbench context panel (`GET /api/v1/tickets/{id}/context`, `relatedTransactions`) lists the origin
  record plus the customer's 5 latest invoices and 3 latest orders (`ErpTicketContextContributor`, 5-second budget,
  never fails the panel).
* Customer 360 → ERP records: `GET /api/v1/customers/{id}/erp` (master, outstanding balance, invoices, orders,
  receipts, AMC contracts, installations).

### Updating ERP records

The cross-module port `IErpRecordUpdater` (used by automation rules/workflows) calls
`PATCH {baseUrl}/api/v1/records/{type}/{id}` (or `/records/{type}/by-number/{number}`) with
`{"changes": {...}, "source": "SupportDesk", "reference": "<correlation id>"}` and expects an `ErpUpdateResult`.
Each update is audited and logged.

## 3. ERP API contract (what SupportDesk calls in `http` mode)

All responses are JSON, camelCase, dates `YYYY-MM-DD`, timestamps ISO-8601 UTC, money as decimals in the document
currency. Lists are paged: `?page=1&pageSize=200` → `{"items": [...], "nextPage": 2 | null}`.

| Call | Purpose | Body type |
|---|---|---|
| `GET /api/v1/ping` | Connection test | `ErpPingResult {ok, system, version, serverTime}` |
| `GET /api/v1/customers?modifiedSince=` | Customer masters (paged) | `ErpCustomerMaster` |
| `GET /api/v1/customers/{id}` · `GET /api/v1/customers/by-code/{code}` | One master (404 when unknown) | `ErpCustomerMaster` |
| `GET /api/v1/installations?modifiedSince=` · `GET /api/v1/customers/{id}/installations` | Installations (paged) | `ErpInstallationRecord` |
| `GET /api/v1/licences?modifiedSince=` · `GET /api/v1/customers/{id}/licences` | Licences (paged) | `ErpLicenceRecord` |
| `GET /api/v1/amc-contracts?modifiedSince=` · `GET /api/v1/customers/{id}/amc-contracts` | AMC / support contracts (paged) | `ErpAmcContractRecord` |
| `GET /api/v1/customers/{id}/invoices?top=` | Sales invoices, newest first | `ErpPage<ErpTransaction>` |
| `GET /api/v1/customers/{id}/orders?top=` | Sales orders | `ErpPage<ErpTransaction>` |
| `GET /api/v1/customers/{id}/transactions?type=&from=&top=` | All customer documents (ledger view) | `ErpPage<ErpTransaction>` |
| `GET /api/v1/records/{type}/{id}` | One document (404 when unknown) | `ErpTransaction` |
| `PATCH /api/v1/records/{type}/{id}` · `…/by-number/{number}` | Update a record | `ErpRecordUpdate` → `ErpUpdateResult` |

Wire types (C# records in `SupportDesk.Contracts.Erp`):

* **ErpCustomerMaster** `{id, code, name, legalName, gstin, pan, email, phone, website, industry, region, city, state,
  country, isActive, isBlocked, creditLimit, currency, branches: [ErpBranch], modifiedAt}`
* **ErpBranch** `{id, code, name, addressLine1, city, state, postalCode, phone, isHeadOffice}`
* **ErpInstallationRecord** `{id, customerId, branchId, productCode, version, build, name, installationCode,
  environment (Production|Staging|Test|Training), deploymentMode (OnPremise|Cloud|Hybrid|Hosted), operatingSystem,
  databaseVersion, userCount, installedOn, goLiveOn, status (Active|Suspended|Decommissioned), modules: [name], modifiedAt}`
* **ErpLicenceRecord** `{id, customerId, installationId, productCode, keyMasked, edition, seats, issuedOn, expiresOn,
  status, modifiedAt}` — keys are always masked by the ERP.
* **ErpAmcContractRecord** `{id, customerId, number, name, contractType (Amc|AnnualSupport|Warranty|Subscription),
  startDate, endDate, value, currency, autoRenew, status (Active|Expired|Cancelled|Draft), salesOrderNumber,
  productCodes, onsite, modifiedAt}`
* **ErpTransaction** `{system, recordType, id, number, customerId, customerCode, customerName, date, dueDate, status,
  taxableAmount, taxAmount, totalAmount, balanceAmount, currency, placeOfSupply, tax: {cgst, sgst, igst, cess},
  lines: [{lineNo, itemCode, description, hsnSac, quantity, unit, rate, discount, taxableValue, taxRate, taxAmount,
  amount}], reference, url}`

## 4. Synchronisation rules (`ErpSyncService`)

Runs manually (`POST …/sync`) or on schedule (`erp-sync`, incremental with `modifiedSince = lastSync − 15 min`). A
SQL Server application lock (`sp_getapplock`, per integration) prevents overlapping runs; a second run gets
**409 `sync_running`**. Each run writes one `IntegrationLog` row (`operation = sync`) with the per-entity summary.

| Entity | Match order | ERP-owned fields (refreshed) | Desk-owned fields (never overwritten) |
|---|---|---|---|
| Customer | `ErpCustomerId` → unlinked `ErpCode` → unlinked `Code` | ERP id/code; status only when the ERP blocks/deactivates | Name, tier, account manager, group, domains, notes, custom fields; e-mail/phone/website/industry/region only filled when empty |
| Location | `ErpLocationId` → unlinked branch `Code` within the customer | ERP id; address fields filled when empty | everything else |
| Installation | `ErpInstallationId` → unlinked `InstallationCode` → unlinked same product + name | version/release, environment, deployment, OS, DB, users, dates, status, enabled modules, location | name, server configuration, integrations, notes |
| Licence | `ErpLicenceId` → unlinked same installation + product | all licence fields | — |
| Contract | `ErpContractId` → unlinked `Number` within the customer | type, period, status (except desk suspensions/cancellations), value, currency, auto-renew, ERP reference; entitlement periods follow | name, SLA policy, support hours, coverage, billing, prepaid hours, notes |

New AMC contracts get one support entitlement per product code. Contract changes queue the
`contracts.reevaluate_eligibility` job for the customer, so open tickets pick up the new eligibility and SLA.
Customers deleted in SupportDesk are skipped (with a warning) instead of being re-created.

## 5. Simulator

`mode = simulator` (default for new tenants and the demo) generates a deterministic ERP from:

* the customers that already carry an `ErpCustomerId` in SupportDesk (it stands in for the ERP database), plus two
  ERP-only accounts (`C-1009` Horizon Agro Exports, `C-1010` Vista Builders & Developers) that a first sync imports;
* the integration's `simulatorAnchor` date (stored in the settings), so seeded data and simulated data agree and ids
  and numbers are stable across restarts.

Per customer it produces branches (HO, BR2), a production ERP installation (version by tier), a POS installation for
retail/FMCG/healthcare customers, a UAT installation for Platinum customers, licences, the current and previous AMC
contract, and about a year of sales orders, GST invoices (CGST + SGST inside Karnataka, IGST otherwise, INR, SAC codes),
receipts and deliveries. Invoice GUIDs are deterministic (`DeterministicGuid`), so `GET …/records/SalesInvoice/{id}`
resolves them. `PATCH` updates accept `status`, `remarks`, `supportTicket`, `followUpDate`, `deliveryDate` and are
kept in memory. Wings Books (`system = WingsBooks`) shares the customer master and offers invoices and receipts only.
