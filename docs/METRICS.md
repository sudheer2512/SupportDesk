# SupportDesk — Metric definitions

One catalogue defines every number SupportDesk shows: dashboards, the report library, exports, scheduled e-mails and
the AI assistant's natural-language analytics all read metrics through `MetricCatalog`
(`src/SupportDesk.Application/Analytics/MetricCatalog.cs`). This document is the prose version; a unit test checks
that every catalogue key is documented here.

## 1. Where the numbers come from

* **`TicketMetric`** — one row per ticket, recomputed by the outbox handler `analytics.ticket-metrics` (order 10, first
  of all handlers) on every committed `ticket.*`, `sla.breached|met|escalated` and `satisfaction.received` event, from
  the ticket, its conversation threads, activity history, SLA milestones and time entries. The computation is a pure
  upsert (`TicketMetricCalculator`), so it is safe to run again at any time.
* **Catch-up** — the recurring task `analytics.rollup` (every 15 minutes, per tenant) recomputes rows that are missing
  or stale (the ticket's `rowversion` moved on since the row was computed — e.g. time logged without an event).
* **`AgentMetric`** — daily per-agent roll-up on the tenant's local calendar (replies, notes, first responses,
  resolutions, logged minutes, scheduled minutes, CSAT responses, SLA breaches, reopens, open workload).
* **`BacklogSnapshot`** — end-of-day backlog per department × priority with ageing buckets.
* **Backfill** — the durable job `analytics.metrics-backfill` recomputes every ticket row and the roll-ups of the last
  N days. It is queued on demand (`POST /api/v1/analytics/metrics/rebuild`, `analytics.team`) and automatically when
  a tenant has ticket history but no roll-ups yet. Re-running it rewrites the same rows.

Excluded everywhere: spam, merged-away and deleted tickets (`TicketMetric.Excluded`).

## 2. Conventions

| Topic | Definition |
| --- | --- |
| Time zone | Periods, days, weeks (Monday start) and months use the tenant's default time zone. |
| Periods | `today`, `yesterday`, `this_week`, `last_week`, `last_7_days`, `this_month`, `last_month`, `last_30_days`, `last_90_days`, `this_quarter`, `this_year`, `last_365_days`, `all_time`, `custom` (inclusive local dates). Current periods end now. |
| Comparison | `previous` = the same length immediately before; `last_year` = the same local dates one year earlier. Change % = (value − compare) ÷ \|compare\| × 100. |
| Date basis | Each metric places a ticket in a period by **creation**, **final resolution**, **closure**, **open at the end of the period / bucket**, or (agent metrics) the **day** of the activity. |
| Calendar minutes | SQL `DATEDIFF(minute, from, to)` semantics (minute boundaries crossed). |
| Business minutes | Working minutes of the ticket's business calendar between the same instants: the calendar captured by its SLA instance; otherwise its department's calendar; otherwise the tenant default; otherwise 24×7. Holidays are excluded. Waiting/paused time is **not** removed (that is what SLA clocks do). |
| Resolution | Created → final resolution. Reopening clears the resolution; the next resolution replaces it. A reopened ticket is counted once, at its last resolution. |
| First response | Created → first public agent reply. |
| Median / p90 | `PERCENTILE_CONT` semantics (linear interpolation between ranks). |
| Rounding | Hours and scores 2 decimals, percentages 1 decimal, minutes 1 decimal. |
| Data scope | Agents count only tickets they may see (ticket data scope). Without `analytics.team` the scope is personal: tickets assigned to the caller. Supervisors and analysts (`analytics.team`) see their full ticket scope (department, team or all). Agent daily metrics follow the same rule over agents (own, department/team members, all). |
| Filters | department, team, agent, channel, priority, customer, product, module, version, category (category or subcategory), customer tier, status. Agent daily metrics accept only department, team and agent. |
| Backlog reconstruction | "Open at instant T" = created before T and not resolved before T (using the final resolution time), so a ticket that was resolved and later reopened counts as open between the two. |

## 3. Ticket metrics

### Volume

| Key | Label | Unit | Aggregation | Basis | Definition |
| --- | --- | --- | --- | --- | --- |
| `ticket_count` | Tickets created | tickets | count | created | Tickets created in the period. |
| `resolved_tickets` | Tickets resolved | tickets | count | resolved | Tickets whose final resolution falls in the period. |
| `closed_tickets` | Tickets closed | tickets | count | closed | Tickets moved to a Closed status in the period. |
| `open_tickets` | Open tickets (created in period) | tickets | count | created | Tickets created in the period that are still open now. |
| `backlog` | Backlog | tickets | count | open at end | Tickets open at the end of the period (or of each time bucket). |
| `avg_open_age_hours` | Average age of open tickets | hours | average | open at end | Mean calendar hours since creation of the backlog tickets at the end of the period. |

### Response and resolution times

All in hours. "Business" variants use business minutes (see conventions); the others calendar minutes.

| Key | Statistic | Population |
| --- | --- | --- |
| `avg_first_response_hours` | mean | tickets created in the period with a first response |
| `median_first_response_hours` | median | same |
| `p90_first_response_hours` | 90th percentile | same |
| `avg_first_response_business_hours` | mean (business) | same |
| `median_first_response_business_hours` | median (business) | same |
| `p90_first_response_business_hours` | 90th percentile (business) | same |
| `avg_resolution_hours` | mean | tickets resolved in the period |
| `median_resolution_hours` | median | same |
| `p90_resolution_hours` | 90th percentile | same |
| `avg_resolution_business_hours` | mean (business) | same |
| `median_resolution_business_hours` | median (business) | same |
| `p90_resolution_business_hours` | 90th percentile (business) | same |

### Effort

| Key | Label | Unit | Definition |
| --- | --- | --- | --- |
| `avg_handle_minutes` | Average handle time | minutes | Mean minutes logged in time entries per ticket resolved in the period (no entries = 0). |
| `handle_hours` | Handle time logged | hours | Total hours logged on tickets resolved in the period. |

### Quality

| Key | Label | Unit | Basis | Definition |
| --- | --- | --- | --- | --- |
| `fcr_rate` | First contact resolution | percent | resolved | Share of resolved tickets resolved with at most one public agent reply and never reopened. |
| `reopen_rate` | Reopen rate | percent | resolved | Share of resolved tickets reopened at least once. |
| `reopened_tickets` | Reopened tickets | tickets | created | Tickets created in the period that were reopened at least once. |
| `escalated_tickets` | Escalated tickets | tickets | created | Tickets created in the period escalated manually or by an SLA escalation level. |
| `escalation_rate` | Escalation rate | percent | created | Share of tickets created in the period that were escalated. |
| `transfers` | Department transfers | tickets | created | Sum of department transfers on tickets created in the period. |
| `frustrated_tickets` | Tickets with frustrated customers | tickets | created | Tickets whose detected customer sentiment is Frustrated. |

### SLA

A milestone is **met** when completed in time and **breached** when its clock passed the target (a late completion
stays breached). Pending milestones are excluded from compliance until decided.

| Key | Label | Unit | Definition |
| --- | --- | --- | --- |
| `sla_breaches` | Tickets with SLA violations | tickets | Tickets created in the period with at least one breached milestone (first response, any next-response cycle, resolution). |
| `sla_compliance` | SLA compliance | percent | Share of tickets created in the period with a decided milestone that breached none. |
| `first_response_sla_compliance` | First response SLA met | percent | Met ÷ (met + breached) first-response milestones of tickets created in the period. |
| `resolution_sla_compliance` | Resolution SLA met | percent | Met ÷ (met + breached) resolution milestones of tickets created in the period. |
| `next_response_breaches` | Next-response breaches | tickets | Breached next-response cycles on tickets created in the period. |

### Satisfaction

| Key | Label | Unit | Definition |
| --- | --- | --- | --- |
| `csat_avg` | Average CSAT | score | Mean rating (1–5) of rated tickets created in the period. |
| `csat_responses` | CSAT responses | tickets | Rated tickets among those created in the period. |
| `csat_positive_rate` | CSAT (positive share) | percent | Share of ratings of 4 or 5. |

## 4. Agent daily metrics

From `AgentMetric` (dimensions: total, day, week, month, agent).

| Key | Label | Unit | Definition |
| --- | --- | --- | --- |
| `agent_replies` | Agent replies | replies | Public agent replies sent on the days of the period. |
| `logged_hours` | Hours logged | hours | Time entries started on the days of the period, by the agent who logged them. |
| `utilisation` | Utilisation | percent | Logged minutes ÷ scheduled minutes. Scheduled minutes: the agent's shift assignment (else default shift), else the working minutes of their first department's calendar; an 8-hour weekday when only a 24×7 calendar exists; 0 on approved leave. |

Other roll-up fields used by the agent report: *assigned* (tickets whose latest assignment to the agent happened that
day), *notes* (internal and handover notes), *open now* (tickets assigned at the end of the day) and *capacity*
(`Agent.MaxOpenTickets`); workload = open ÷ capacity.

## 5. Dimensions

`day`, `week`, `month` (tenant calendar; series are continuous — empty count buckets are 0, empty averages are blank),
`department`, `team`, `assignee`, `channel`, `priority`, `status_category`, `customer`, `customer_tier`, `product`,
`module`, `version`, `category`, `issue_type`, `root_cause`, `sentiment`. A missing value is reported as its own row
(key `none`, e.g. "Unassigned").

## 6. Derived analyses

* **Top-N** — categorical breakdown sorted by value (default 10, max 100).
* **Pareto** — count/sum metric by a categorical dimension, sorted descending with share and cumulative share; the
  "vital few" are the rows needed to reach 80 %. Rows beyond the limit are folded into "Other".
* **Cohorts** — tickets grouped by the local week they were created; for 1, 2, 3, 7, 14 and 30 days the share of the
  cohort resolved within that many calendar days of creation, counting only tickets at least that old.
* **Ageing** — open tickets at the end of the period by calendar age: < 1 day, 1–3, 3–7, 7–30, 30+ days (with the
  number currently in SLA breach).
* **Drill-down** — the tickets behind any ticket metric (its population in the period, optionally one time bucket or one
  dimension value), keyset paged by creation time.
* **Customer health score** (customer health report) — 100 − 40 × breach share − 20 × reopen share − 20 × escalation
  share − 10 × (4 − CSAT when CSAT is below 4), clamped to 0–100. A ranking aid, not a catalogue metric.

## 7. AI assistant

`AnalyticsCatalog` (natural-language analytics) exposes `ticket_count`, `open_tickets`, `resolved_tickets`,
`sla_breaches`, `avg_resolution_hours`, `avg_first_response_hours`, `csat_avg`, `reopened_tickets`,
`escalated_tickets` and `frustrated_tickets`, taking their labels, units, aggregation and date basis from this catalogue,
resolving periods with the same function and applying the same data scope — its answers equal `/api/v1/analytics/query`.
