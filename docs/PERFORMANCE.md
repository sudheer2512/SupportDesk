# SupportDesk — Performance

How the API was load-tested, what the numbers were before and after the fixes, what was changed and what is still open.

## 1. Method

**Harness:** `tests/SupportDesk.LoadTests` (console app, no extra packages). Each virtual user signs in once (bearer token)
and loops over a realistic agent session:

| Operation | Request |
|---|---|
| `list` / `list-next-page` | `GET /api/v1/tickets?sort={lastActivity\|created\|due\|priority}&limit=25`, then the next page through the keyset `cursor` |
| `queue` | `GET /api/v1/tickets?statusCategory=unresolved&sort=…&limit=50` |
| `detail`, `timeline`, `context` | `GET /api/v1/tickets/{id}`, `/timeline`, `/context` for a random ticket of the queue page |
| `reply` / `note` | every 5th iteration: `POST /replies` (forced, so collisions between virtual users are not errors) or `POST /notes` |
| `search`, `list-search` | `GET /api/v1/search?q=<term>` and `GET /api/v1/tickets?q=<term>` (10 rotating terms) |
| `analytics` | `GET /api/v1/analytics/query?metric=<catalogue metric>&dimension=<department\|priority\|channel\|assignee\|day>&period=last_30_days` |
| `queue-counts` | `GET /api/v1/tickets/queue-counts` (workbench badges) |

A 10 s warm-up (not measured) precedes every run. Latency is end-to-end client time; percentiles are nearest-rank.

```bash
dotnet build tests/SupportDesk.LoadTests -c Release
# optional data volume: create N tickets through the API first
dotnet tests/SupportDesk.LoadTests/bin/Release/net10.0/SupportDesk.LoadTests.dll http://127.0.0.21:5897 1 5 --seed-tickets 20000
# measured run: 20 virtual users for 60 s as the support supervisor (department-scoped ticket visibility)
dotnet tests/SupportDesk.LoadTests/bin/Release/net10.0/SupportDesk.LoadTests.dll http://127.0.0.21:5897 20 60 --markdown results.md
```

**API under test:** Release build, `ASPNETCORE_ENVIRONMENT=Production`, listening on loopback `127.0.0.21`, LocalDB
database `SupportDesk_Perf1` (`Database__InitializeMode=EnsureCreated`, `Seed__Demo=true`), rate limits raised. For the
latency runs the background workers were switched off (`Workers__Enabled=false`): a backlog of ~20k outbox events
from the volume seeding would otherwise compete for the same CPU (the outbox is measured separately, §4).

**Data:** demo seed (tenants `wings` and `northwind`) plus ~14,800 tickets created through the API in `wings` → 15,211
tickets, 4,614 search documents / 57,133 postings (indexing lagged behind the seeding), 4,549 ticket-metric rows.
The virtual users sign in as `priya@wings.demo` (support supervisor, department scope), the most common desk persona.

**Query analysis:** before each run the database's plan cache was cleared
(`ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE`); after it, `sys.dm_exec_query_stats` (top statements by
elapsed time and by logical reads, filtered to the database) and the missing-index DMVs were captured. EF Core
row-limit/`First` warnings were traced with a temporary `LogTo` hook that logged the stack of every occurrence through
the demo seed, the E2E suite, a load run and the recurring tasks.

**Machine:** laptop, Intel Core i7-1165G7 (4 cores / 8 threads), 40 GB RAM, NVMe SSD, Windows 11, .NET SDK 10.0.401,
SQL Server 2019 LocalDB 15.0. API, database and harness share the machine, and other development processes were running
— absolute numbers are pessimistic (20 virtual users saturate the CPU); compare runs with each other, not with production.

## 2. Results (20 virtual users, 60 s, ms)

| operation | baseline p50 / p95 / p99 | after fixes p50 / p95 / p99 | change (p50) |
|---|---|---|---|
| list | 557 / 5159 / 8294 | 214 / 1787 / 6290 | −62% |
| list-next-page | 490 / 2081 / 7493 | 165 / 1151 / 5003 | −66% |
| queue | 574 / 2485 / 6923 | 173 / 1678 / 7357 | −70% |
| queue-counts | 349 / 1555 / 3437 | 103 / 609 / 1313 | −70% |
| search | 1224 / 4949 / 9156 | 327 / 1603 / 2264 | −73% |
| list-search | 167 / 488 / 685 | 145 / 774 / 1194 | −13% |
| detail | 188 / 768 / 1012 | 144 / 757 / 2069 | −23% |
| timeline | 225 / 753 / 1009 | 180 / 782 / 1433 | −20% |
| context | 1260 / 3167 / 4268 | 918 / 2474 / 3668 | −27% |
| analytics | 637 / 3015 / 5546 | 579 / 4109 / 5836 | −9% |
| reply | 349 / 1534 / 1534 | 167 / 2519 / 4859 | −52% |
| note | 199 / 4004 / 4004 | 135 / 1536 / 1869 | −32% |
| **throughput / errors** | **23.6 req/s, 0 errors** | **36.1 req/s, 0 errors** | **+53%** |

Intermediate runs: code changes only (queue counts, search statistics) → 25.7 req/s, queue-counts p50 156 ms, search
747 ms; plus the indexes → 30.3 req/s, list p50 240 ms; plus the search visibility rewrite → 36.1 req/s.

**Light load (5 virtual users, final build), p50 / p95 / p99:** list 63 / 488 / 734 · list-next-page 61 / 603 / 729 ·
queue 73 / 472 / 658 · queue-counts 51 / 126 / 195 · detail 41 / 210 / 515 · timeline 45 / 271 / 715 · context
361 / 930 / 1375 · search 141 / 337 / 701 · list-search 46 / 167 / 269 · analytics 231 / 1079 / 1544 · reply
70 / 699 / 699 — 29.2 req/s, 0 errors.

## 3. What was slow and what changed

| Finding (DMVs) | Fix |
|---|---|
| **Global search** ran its BM25 statistics as two queries (`COUNT`, `AVG(TermCount)`) over the permission-filtered documents; the ticket filter `EntityType <> 'Ticket' OR EntityId IN (visible tickets)` could only be evaluated row by row (a lookup into Tickets per document): ~44,600 logical reads and ~590 ms per statement, three such statements per search. | One statistics query; the filter split into `UNION ALL` branches so SQL Server joins the visible ticket ids once (41,000 → 360 reads); `SearchDocuments (TenantId, EntityType, EntityId)` now `INCLUDE (Visibility, CustomerId, TermCount)` so the permission filters and statistics never touch the wide rows. (`SearchEngine`, `SearchServices`, `ModelConfiguration`) |
| **Ticket list pages** (lastActivity/created/priority sorts) scanned and sorted the whole Tickets table (3,500 reads) because the agent visibility predicate (department / assignee / watched) needed columns the keyset indexes did not have. | `IX_Tickets_Keyset (TenantId, LastActivityAt, Id)` and `(TenantId, CreatedAt)` include department, team, assignee, status category, deleted flag (and the queue columns); new `IX_Tickets_PriorityKeyset (TenantId, Priority, LastActivityAt)`: an ordered scan of a narrow index that stops after one page. |
| **Queue badges**: 8 separate `COUNT` queries per call, each scanning the visible open tickets. | One conditional-aggregation query (+1 for "watching", which needs a subquery) answered from `IX_Tickets_Keyset`. (`TicketQueryService.QueueCountsAsync`) |
| AI resolution-ETA and history queries (`ORDER BY ResolvedAt DESC` with category/priority/product filters) scanned Tickets. | Filtered `IX_Tickets_Resolved (TenantId, ResolvedAt) WHERE ResolvedAt IS NOT NULL INCLUDE (…)`. |
| **Outbox dispatcher** resolved *every* registered handler (17) from DI for *each* handler run, and looked up the delivery row before every insert. | Interested handlers per event type computed once; each run builds only its handler (cached `ActivatorUtilities` factory); first-attempt deliveries are inserted without the lookup; claimed events are processed in `OccurredAt` order (OUTPUT does not guarantee it). The per-event time of cheap handlers roughly halved (realtime 13 → 7 ms, webhooks 10 → 5.6 ms; the two windows had a similar but not identical event mix). |
| **AI classification corpus** was reloaded (2,000 rows) and the local model retrained whenever the labelled-ticket count changed — on almost every `ticket.created` during a burst (53 reloads in a few minutes). | `AiCorpusCache` checks for new labelled tickets at most once a minute. |
| EF Core warnings "row limiting operator without OrderBy" / "First without OrderBy" (SLA monitor, ticket housekeeping, KB maintenance, KB reviewers, search prefix expansion, portal related articles, AI probes, subscription/AI-configuration reads, calendar usage list). | Deterministic ordering everywhere (the SLA monitor now takes the most urgent milestones first); aggregate probes use `SingleOrDefault`. A traced run logs none. |

Index changes live in `src/SupportDesk.Persistence/ModelConfiguration.cs` (the shared migration is regenerated
centrally); for the measurement they were applied to the existing database by hand with the equivalent DDL.

## 4. Outbox throughput

With the AI features of the demo tenant enabled, one API instance processed **≈6–9 events/s** (≈5.8/s for a pure
`ticket.created` backlog). Per `ticket.created` the handler chain costs ≈175 ms, of which **≈110–135 ms is the AI ticket
handler** (local classification + triage agent, CPU-bound in-process); automation ≈30 ms, search indexing ≈12 ms. The
dispatcher change above removed most of the framework overhead but cannot help the AI work itself.

Tried and reverted: processing different aggregates in parallel inside one instance (4 lanes) **lowered** throughput to
1.6 events/s — handlers serialise on hot rows (`AiUsage` and `UsageRecords` `MERGE … WITH (HOLDLOCK)`, `AiAgents.RunCount`,
which every event updates). Several worker instances would hit the same contention.

## 5. Open items (not fixed here)

* **`sort=due`** orders by `COALESCE(DueAt, '9999-12-31')`, which no index can serve: it still scans and sorts the visible
  tickets (≈1 s under load at 15k tickets; most of the remaining list p95/p99). Fix: a persisted computed sort key on
  `Ticket` + index `(TenantId, DueSortKey, Id) INCLUDE (visibility columns)` (entity change in the tickets module).
* **Analytics queries** load whole `TicketMetrics` rows into memory and aggregate there (≈600 reads, ≈0.6–0.9 s under
  load). Aggregating in SQL or projecting only the needed columns would cut this substantially.
* **Ticket context** runs ~12 sequential queries plus every contributed panel; parallelising independent lookups or
  caching the customer summary would roughly halve it.
* **Hot counter rows** (`AiUsage`, `UsageRecords`, `AiAgents.RunCount`) prevent parallel event processing; buffering usage
  increments (e.g. per-instance accumulation flushed every few seconds) would unlock it.
* **AI ticket handler cost** (~110 ms per new ticket) bounds event throughput per instance; moving classification/triage
  to a durable job (separate queue with its own concurrency) would keep SLA/notification handling responsive during
  ticket bursts.
* The `TicketTags` lookup used by list mapping reads ~2,900 pages per call at this size — worth a look with the real
  tag distribution.
