# Engineering escalation (Azure DevOps, GitHub, built-in tracker) and Teams / Slack

UC-9: defect tickets are escalated to engineering. A work item is created in (or linked from) a tracker, its state,
fix version, linked changes and releases flow back to every linked ticket, customers can be told when the fix ships,
and the ticket keeps its history even if the work item is deleted or moved.

| Tracker | When to use | Integration `Type` |
|---|---|---|
| **Azure DevOps Services** / **Azure DevOps Server (TFS)** | Engineering works in ADO (cloud or on-premises) | `AzureDevOps` |
| **GitHub** (github.com or GitHub Enterprise Server) | Engineering works in GitHub issues | `GitHub` |
| **Built-in board** | No external tracker: work items, releases and a kanban board inside SupportDesk | `LocalEngineering` |

Everything is per tenant (`Integration` rows, `admin.integrations`). Several trackers may be active at once (one
built-in board per tenant); agents choose the tracker when escalating. ADO and GitHub require the plan feature
`engineering_integration`; the built-in board is available on every plan.

## 1. Concepts

* **EngineeringLink** — a ticket's link to a work item with a snapshot (title, state, assignee, iteration, fix
  version, resolved flag, last sync). Unlinking removes it and records the link in the ticket timeline.
* **EngineeringItem** — the shared last-known state of one tracker item (one row per tracker + external id): state,
  fix version, release (name, released/deployed dates), linked PRs/changesets/builds, orphaned flag. Several tickets
  can link the same item; the panel shows the ticket and customer counts and "other tickets affected" (scope-filtered).
* **Orphaned** — the item was deleted (ADO `workitem.deleted`, GitHub 410 / `issues.deleted`, local delete) or moved
  (ADO project change, GitHub transfer / 301). The link keeps the last known data, an internal note is added and the
  assignee is notified; polling stops for orphaned items.
* **Sync-back rules** (Administration → Engineering, tenant setting `engineering`): internal note on resolve, internal
  note on release, notify the ticket assignee, tell the customer "fix released in version X" (reply template or the
  built-in text; placeholders `{{engineering.fixVersion}}`, `{{engineering.id}}`, `{{engineering.title}}` plus the usual
  `{{ticket.number}}`, `{{contact.name}}`), set the substatus **Engineering** on escalation (on the current status when it
  offers that substatus, otherwise on the configured escalation status), default tracker.

Every change publishes `engineering.item_updated` (aggregate `Ticket`, one event per linked ticket, payload compatible
with ticket events plus `action`, `becameResolved`, `becameReleased`, `becameOrphaned`, `fixVersion`, `releaseName`…).
Notes, notifications and customer replies are side effects of that event (outbox handler `engineering-sync-rules`,
idempotent per event/link), outbound webhook subscribers receive it, and automation rules can trigger on the internal
notes it adds.

## 2. Azure DevOps Services / Server (TFS)

**Connection** (Administration → Engineering → Add tracker → Azure DevOps):

| Field | Value |
|---|---|
| Organization / collection URL | Services: `https://dev.azure.com/{organization}` · Server: `https://tfs.example.com/tfs/DefaultCollection` |
| Project | The team project new work items are created in ("Load projects" lists what the token can see) |
| Authentication | **PAT** (Basic auth, empty user) or **OAuth** access token (Bearer) |
| API version | REST `7.1` (Azure DevOps Server 2022+; older servers that only speak 6.0/5.1 need a newer server or a proxy) |

**PAT scopes:** *Work Items — Read & write*, *Project and Team — Read*; add *Code — Read* to see linked pull
requests/commits and *Build — Read* / *Release — Read* if you want those artefacts. For Azure DevOps Server, create
the PAT on the server (user settings → Personal access tokens); the account needs Contributor rights in the project.

**On-premises / private networks:** tracker calls use the same SSRF guard as automation webhooks. A TFS server on a
private address requires `Automation:Webhooks:AllowPrivateNetworks=true` in the API configuration (link-local and
metadata addresses stay blocked).

**Mappings:** item types (Bug → `Bug`, Feature → `User Story` / `Product Backlog Item` / `Feature`, Task → `Task`),
repro steps field (`Microsoft.VSTS.TCM.ReproSteps` for bugs, `System.Description` otherwise), severity
(`Microsoft.VSTS.Common.Severity`, values `1 - Critical` … `4 - Low`), priority (`Microsoft.VSTS.Common.Priority`, 1–4),
fix version (`Microsoft.VSTS.Build.IntegrationBuild` by default, or a custom field such as `Custom.FixVersion`),
optional customer impact (integer field), default area/iteration path, default tags. New items get a hyperlink back to
the ticket. **State mapping:** resolved states (default `Resolved`, `Done`) and released states (default `Closed`);
entries can also be `tag:x`.

**Service hooks** (Project settings → Service hooks → Web Hooks), one subscription per event:

| Event | Purpose |
|---|---|
| Work item updated (all fields; resource version 1.0) | state, fix version, assignee, title |
| Work item deleted / restored | orphan / un-orphan |
| Release deployment completed (classic releases) | items whose fix version equals the release name become released + deployed |

URL: the tracker's **Webhook URL** (`https://desk.example.com/api/v1/engineering/hooks/azure-devops/{trackerId}`).
Authentication: either an HTTP header `X-SupportDesk-Secret: {secret}` or **Basic authentication** with any user name
and the secret as password. The secret is shown once when the tracker is created or the secret is rotated. Deliveries
are de-duplicated by the service hook event id (`id`), unknown items are ignored, and the delivery log is under the
tracker's "Deliveries & call log".

**Polling fallback:** the recurring task `engineering-sync` (every 5 minutes) polls each active tracker whose poll
interval (default 15 minutes) has elapsed: `GET _apis/wit/workitems?ids=…&$expand=relations&errorPolicy=omit` in
batches of 100 (missing items → orphaned). "Sync now" runs it immediately.

## 3. GitHub

**Connection:** API URL `https://api.github.com` (GitHub Enterprise Server: `https://github.example.com/api/v3`), owner
and repository.

* **Token:** a fine-grained personal access token restricted to the repository with *Issues: Read and write* and
  *Metadata: Read* (add *Contents: Read* to read releases; *Pull requests: Read* for linked PRs). A classic token needs
  `repo` (or `public_repo`).
* **GitHub App:** App id, installation id and the App private key (PEM). Permissions: *Issues: Read and write*,
  *Metadata: Read*, *Contents: Read*, *Pull requests: Read*. SupportDesk signs an RS256 JWT and exchanges it for an
  installation token (cached ~1 hour).

**Mappings:** item types become labels (`bug`, `enhancement`, `task`), severity/priority become prefixed labels
(`severity:major`, `priority:high`), the **milestone is the fix version**. State mapping: resolved `closed` (issues
closed as *not planned* do not count), released `label:released`. A **published release** whose tag or name matches
the milestone (`v8.0.1` = `8.0.1`) marks linked issues as released. Issue numbers, `#45`, `owner/repo#45` and issue URLs
can be linked; issues of other repositories are rejected.

**Webhook** (Repository → Settings → Webhooks → Add webhook): payload URL = the tracker's Webhook URL
(`…/api/v1/engineering/hooks/github/{trackerId}`), content type `application/json`, secret = the tracker secret, events
*Issues*, *Releases* and *Pull requests* (merged PRs that reference `#issue` are added to the linked items). Requests
are verified with `X-Hub-Signature-256` (HMAC-SHA256 of the raw body) and de-duplicated by `X-GitHub-Delivery`;
a GitHub *ping* shows up as an ignored delivery. Polling works as for Azure DevOps (`GET /repos/{owner}/{repo}/issues/{n}`,
410 = deleted, 301 = transferred, plus the issue timeline for PRs and the release list).

## 4. Built-in board

Administration → Engineering → Add tracker → **Built-in engineering board** (or "Enable the built-in board"). The board
lives at **/engineering** (nav: Operations → Engineering, permissions `engineering.view` / `engineering.manage`):

* Work items `ENG-n` with type, title, description/repro steps, severity, priority, assignee (any active user holding
  `engineering.manage` — light agents included), area/iteration, tags, product, fix version; states **New → Active →
  Resolved → Closed** (drag and drop, or the keyboard "Move to" menu on each card).
* Releases: plan a version, assign work items, **Publish** (optionally "Mark deployed"): every assigned item becomes
  released in that version (resolved items close) and linked tickets get notes / customer notifications.
* Deleting a work item orphans its ticket links (last known data kept).

Default role grants: Light Agent `engineering.view` + `engineering.manage` (engineers), Agent `engineering.view` +
`engineering.link`, Supervisor adds `engineering.manage`, Administrator everything.

## 5. Escalating from a ticket

Ticket view → **Engineering** tab (`engineering.link` to escalate/link, `tickets.view`/`engineering.view` to read):

1. **Escalate** prefills the work item from the ticket: summary, problem description, customer messages, support
   analysis (internal notes), product / module / version / installation, customer impact (distinct customers of this
   ticket and its linked tickets). Choose tracker, type, priority, severity, tags (ADO: area/iteration; built-in:
   assignee) and create. A timeline entry is written and, when configured, the substatus becomes **Engineering**.
2. **Link existing** accepts `1234`, `#1234`, `AB#1234`, `ENG-12`, `owner/repo#45` or the item URL.
3. Each link shows live state (stale items are re-read when the panel opens), fix version, release, linked PRs /
   changesets, last sync and errors, "N tickets · M customers" (other tickets affected), refresh, open in tracker and
   unlink.

API (`/api/v1/engineering`): `GET tickets/{id}?refresh=`, `GET tickets/{id}/prefill`, `POST tickets/{id}/escalate`,
`POST tickets/{id}/links`, `DELETE tickets/{id}/links/{linkId}`, `POST …/links/{linkId}/refresh`,
`GET …/links/{linkId}/tickets`; board: `GET board`, `GET work-items` (keyset paging: `cursor`), `GET/PUT/DELETE
work-items/{id}`, `POST work-items`, `POST work-items/{id}/move`, `GET/POST releases`, `PUT releases/{id}`,
`POST releases/{id}/publish`; admin (`/engineering/admin`): `trackers` CRUD, `test`, `projects`, `sync`,
`rotate-webhook-secret`, `deliveries`, `logs`, `settings`.

## 6. Microsoft Teams and Slack

Administration → **Teams & Slack** (`/admin/collaboration`, `admin.integrations`): add incoming-webhook channels and
map events to them — *new urgent ticket* (minimum priority per channel), *SLA breach*, *escalation*, *mention* (an agent
@mentioned in an internal note), *escalated to engineering*, *engineering fix done*, *fix released*. Optionally limit a
channel to departments. Messages are Adaptive Cards (Teams) or Block Kit (Slack) with ticket facts and an **Open in
SupportDesk** deep link (`Communication:PublicBaseUrl` / `App:PublicUrl` + `/tickets/{id}`).

* Teams: channel → *Workflows* → "Post to a channel when a webhook request is received" (or a classic incoming webhook
  connector); paste the URL. Slack: create an app with *Incoming Webhooks* and add a webhook to the channel.
* Webhook URLs are secrets (AES-GCM, write-only; the list shows a short hint) and never appear in logs.
* SSRF protection reuses the automation webhook transport: https only, no credentials in the URL, private/loopback
  addresses blocked unless `Automation:Webhooks:AllowPrivateNetworks=true` (metadata/link-local always blocked), every
  resolved address re-checked at connect time, redirects not followed.
* Deliveries are durable jobs (`collaboration.send`, retried on timeouts/429/5xx); "Send test" posts a sample card and
  the post log lists every attempt. Agents can **Share to Teams/Slack** from the internal-note composer.
