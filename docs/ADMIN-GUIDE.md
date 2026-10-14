# SupportDesk administration guide

This guide is for **tenant administrators**, who configure one SupportDesk workspace, and **platform operators**, who
run the SaaS service for all tenants. Agents and supervisors should read [USER-GUIDE.md](USER-GUIDE.md). Integrators
should read [API.md](API.md). Installation and server configuration are in [DEPLOYMENT.md](DEPLOYMENT.md).

Every administration screen calls the same REST API that integrations use, and every rule below is enforced on the
server. Hiding a menu never replaces a permission check. Sensitive changes are written to the tamper-evident audit log.
Items marked **Partial** or **Not built yet** describe gaps in the current release.

Contents

* Part A — [Tenant administration](#part-a--tenant-administration)
  1. [The Administration home](#1-the-administration-home)
  2. [Roles and permissions](#2-roles-and-permissions)
  3. [Ticket configuration](#3-ticket-configuration)
  4. [SLA policies and business calendars](#4-sla-policies-and-business-calendars)
  5. [Departments, teams, agents and routing](#5-departments-teams-agents-and-routing)
  6. [Channels](#6-channels)
  7. [Automation rules and blueprints](#7-automation-rules-and-blueprints)
  8. [Webhooks](#8-webhooks)
  9. [AI](#9-ai)
  10. [Knowledge settings](#10-knowledge-settings)
  11. [Brands and portals](#11-brands-and-portals)
  12. [Notifications and e-mail templates](#12-notifications-and-e-mail-templates)
  13. [Security and sign-in](#13-security-and-sign-in)
  14. [API keys and OAuth clients](#14-api-keys-and-oauth-clients)
  15. [Wings ERP and Wings Books](#15-wings-erp-and-wings-books)
  16. [Engineering trackers](#16-engineering-trackers)
  17. [Microsoft Teams and Slack](#17-microsoft-teams-and-slack)
  18. [Audit log](#18-audit-log)
  19. [Compliance](#19-compliance)
  20. [Configuration transfer](#20-configuration-transfer)
  21. [Subscription and billing](#21-subscription-and-billing)
* Part B — [Platform operator console](#part-b--platform-operator-console)
* [Permission reference](#permission-reference)

---

# Part A — Tenant administration

## 1. The Administration home

Open **Administration** in the left rail. The home page shows one card per area you can manage, grouped as
Organisation, Ticketing, Channels & portal, Automation & AI, Security & compliance, Integrations and Subscription. A card
appears when you hold **any** of its permissions.

| Card | Route | Permission (any of) |
|---|---|---|
| Roles & permissions | `/admin/roles` | `admin.roles` |
| Departments & teams | `/teams/departments` | `agents.manage`, `supervisor.monitor` |
| Agents | `/teams/agents` | `agents.manage`, `supervisor.monitor` |
| Shifts, leave & delegation | `/teams/shifts` | `agents.manage`, `supervisor.monitor` |
| Ticket configuration | `/admin/ticket-config` | `admin.ticket_config` |
| Fields & form layouts | `/admin/ticket-config/fields` | `admin.ticket_config` |
| SLA policies | `/sla/policies` | `admin.sla`, `supervisor.monitor` |
| Business hours & holidays | `/sla/calendars` | `admin.sla`, `supervisor.monitor` |
| Channels | `/admin/channels` | `admin.channels` |
| Brands & portals | `/admin/brands` | `admin.organisation` |
| Notifications & e-mail templates | `/admin/notifications` | `admin.organisation`, `admin.channels` |
| Automation & blueprints | `/automation` | `admin.automation` |
| AI | `/admin/ai` | `admin.ai` |
| Security & sign-in | `/admin/security` | `admin.security` |
| Audit log | `/admin/audit` | `admin.audit` |
| Compliance | `/admin/compliance` | `admin.compliance` |
| Configuration transfer | `/admin/config` | `admin.config_transfer` |
| API keys & OAuth clients | `/admin/api-credentials` | `admin.integrations` |
| Webhooks | `/admin/webhooks` | `admin.integrations` |
| Wings ERP & Books | `/products/erp` | `admin.integrations` |
| Engineering | `/admin/engineering` | `admin.integrations` |
| Teams & Slack | `/admin/collaboration` | `admin.integrations` |
| Subscription & billing | `/subscription` | `admin.billing` |

Some cards open screens outside `/admin`, because supervisors use the same screens. Read access is often wider than
write access; for example supervisors can read SLA policies but only `admin.sla` can change them.

Configuration changes apply to the next request. Open desks refresh automatically.

---

## 2. Roles and permissions

### Built-in roles

Every new workspace gets six **system roles**. They are read-only.

| Role | Ticket scope | Customer scope | Purpose |
|---|---|---|---|
| Administrator | All | All | Every permission |
| Supervisor | Departments | All | Agent work plus transfer, bulk, delete, approvals, monitoring, costs, team analytics, KB review and publish, AI approvals |
| Agent | Departments | All | Day-to-day ticket work, customers, KB drafts, AI assistance, engineering links |
| Light Agent | Departments | All | View tickets and add internal notes |
| Knowledge Manager | All | All | Knowledge base authoring, review, publishing and settings |
| Analyst | All | All | Read-only reporting, including costs |

The exact permissions of each role are in the [permission reference](#permission-reference).

### Data scope

Each role has a **ticket scope** and a **customer scope**. A user with several roles gets the broadest scope of them.
Records outside the scope are invisible: the API answers 404, as if they did not exist.

| Ticket scope | Sees |
|---|---|
| Own | Tickets assigned to or raised by the agent |
| Team | Tickets of the agent's teams |
| Departments | Tickets of the agent's departments |
| All | All tickets |

| Customer scope | Sees |
|---|---|
| Account-managed | Only customers the user manages as account manager |
| All | All customers |

### To create a custom role

Custom roles need the plan feature `custom_roles` (Trial, Professional and Enterprise). On other plans **New role** is
disabled.

1. Open **Roles & permissions**.
2. Select **New role**, or open a system role and select **Clone to customise**.
3. Enter a **Name** (2–100 characters, unique) and a **Description**.
4. Choose the **Data scope** for tickets and customers.
5. Optionally turn on **Light agent role**.
6. Tick permissions in the matrix. Use **Filter permissions** to find one. **Sensitive** badges mark high-impact
   permissions.
7. Add members under **Members**. Changes apply immediately to their open sessions.
8. Select **Create role** (or **Save role**).

### Light agents

A user is a light agent when their user type is light agent, their agent record says so, or **any** of their roles is a
light-agent role. Whatever the permissions say, light agents can never reply to customers, own (be assigned) tickets,
transfer, bulk-edit or delete. Light agents count against a separate plan limit; by default it equals the plan's agent
limit. The limit is checked when you add or reactivate an agent.

### Escalation guard

You can never give away more than you have. The API answers **403 `privilege_escalation`** when you:

* create, change or delete a role that contains a permission you do not hold, or whose data scope is broader than
  yours (on update this applies to the role's current contents too, so you cannot edit such a role at all);
* add a user to, or remove a user from, such a role (you must be able to manage every role the user holds);
* choose such a role as the SSO default role;
* create an API key or OAuth client whose scopes grant permissions you lack, or act as a more powerful user.

In the matrix, permissions you cannot grant show a lock: "You do not hold this permission, so you cannot grant it."

### Last-administrator rule

The workspace must always keep at least one administrator: an **active user holding a role that contains
`admin.roles`**. The API answers **409 `last_administrator`** to any change that would remove the last one:

* removing `admin.roles` from a role;
* deleting an administrative role;
* changing a user's roles;
* deactivating or deleting the user.

Make someone else an administrator first.

### To delete a role

* System roles cannot be deleted or changed (409 `system_role_read_only`).
* A role used as the SSO default role cannot be deleted (409 `role_in_use`). Choose another default first.
* A role with members asks **Move members to**, then **Delete role**.

---

## 3. Ticket configuration

Open **Ticket configuration**. The sections are **Statuses**, **Categories & issue types**, **Custom fields**,
**Form layouts** and **Numbering & lifecycle**. You need `admin.ticket_config`.

### Statuses

Every status belongs to one fixed category: Open, Assigned, In progress, Waiting, Resolved or Closed. Reports, SLAs and
the portal rely on the categories.

| Field | Notes |
|---|---|
| Name | 1–100 characters, unique |
| Category | Fixed for built-in statuses and for statuses in use |
| Colour | Hex `#rrggbb` |
| Label shown to customers | Keeps internal names private in the portal (for example "Received" for Open) |
| Default status of its category | One default per category |
| Pause SLA resolution clocks while in this status | Stops the next-response and resolution clocks |
| Allowed next statuses | **Any status (no restriction)**, or a list of allowed targets |
| Substatuses | Up to 20 |
| Active | Built-in statuses cannot be deactivated |

Rules:

* Every category must keep at least one active status (409 `status_category_uncovered`).
* Built-in statuses cannot be deleted (409 `system_status`). Rename or deactivate them instead.
* A status used by tickets cannot be deleted (409 `status_in_use`).
* Tickets that follow a blueprint ignore the transition list; the blueprint decides.

### Categories and issue types

* **Categories** have two levels (category and subcategory). Each can be limited to one department, and can be offered
  in the portal (**Customers can choose it in the portal**) or kept for agents only.
* **Issue types** have a name and description.
* Items in use cannot be deleted (409 `category_in_use`, `issue_type_in_use`). Deactivate them instead.

**Priorities and severities** are fixed values (Low, Medium, High, Critical; Cosmetic, Minor, Major, Critical). You can
change their labels and colours only.

**Not built yet:** resolution codes, root causes and close reasons have no admin screen. They are seeded. To change
them, import them with [configuration transfer](#20-configuration-transfer) (section "Pick lists").

### Numbering and lifecycle

| Setting | Default | Range |
|---|---|---|
| Prefix | `#` | Up to 10 characters |
| Next ticket number | starts at 1000 | Read-only; numbers are gap-free per workspace |
| Reopen window (days) | 30 | 0–365. Customer replies inside the window reopen the ticket; later replies create a new ticket. |
| Auto-close resolved tickets after (days) | 7 | 0–365 (0 = never) |
| Soft lock (minutes) | 10 | 1–240 |
| Customers may reopen resolved tickets (within the window) | on | |
| Ask customers to confirm a resolution | off | Forces the resolve option for agents |
| Require a resolution code when resolving | off | |
| Acknowledge new e-mail tickets automatically | on | Where the mailbox allows it |
| Most tickets per merge | 10 | 1–50 |
| Allow merging tickets of different customers / departments | on / on | |
| Allow splitting messages into a new ticket | on | |
| Send a survey when a ticket is resolved | on | |
| Survey link valid for (days) | 14 | 1–90 |

Select **Save settings**.

The workspace setting **AllowAgentsToOverrideEntitlement** (on by default) lets any agent with `tickets.edit` override
support eligibility. **Not built yet:** it has no screen. Change it with configuration transfer or the API.

### Custom fields

Custom fields exist for **tickets**, **contacts** and **customers** (tabs "Ticket fields", "Contact fields",
"Customer fields").

| Item | Rule |
|---|---|
| Key | 2–63 characters, `a-z`, digits and `_`, starting with a letter. Cannot change later. |
| Type | Single line text, Multi-line text, Whole number, Decimal number, Currency, Date, Date and time, Checkbox, Picklist, Multi-select picklist, Web address, E-mail address, Phone number, Record lookup (Product, Customer, Contact, User, Installation). Cannot change later. |
| Picklist options | 1–500 options |
| Validation | Pattern and maximum length (text), minimum and maximum (numbers) |
| Portal | **Customers see it in the portal**; **Customers can fill it in when raising a ticket** |
| Required | Always, or (ticket fields) **only before a ticket may enter these statuses** |
| Departments | Ticket fields can be limited to departments |

Agents always see every active field. Deleting a field removes it from layouts but keeps stored values hidden. You must
type the key to confirm.

### Form layouts

Layouts arrange ticket fields for the **Agent ticket form** or the **Portal (customer) form**.

1. Open **Form layouts** and create a layout.
2. Set the **Form**, an optional **Department**, and a **Brand** (portal forms) or **Channel** (agent forms).
3. In the builder, select **Add section** (up to 20). Give each section a title and 1, 2 or 3 columns.
4. Drag fields from **Available fields**. Tick **Required** where needed. Subject and Department are always required.
5. Check the **Preview** tab, then save.

The most specific active layout wins: department (and brand or channel) first, then the default layout of the form.
Only one active layout may cover the same form, department, brand and channel. Portal forms accept only portal-capable
fields (department, priority, category, product, installation and portal-editable custom fields). Layouts are for
tickets only.

---

## 4. SLA policies and business calendars

Open **SLA**. Supervisors can read; changing policies and calendars needs `admin.sla` (calendars also
`admin.organisation`).

### How a policy is chosen

1. The policy of the ticket's active **contract**, if any.
2. Otherwise the first matching active policy in **evaluation order** (reorder with the arrows, then **Save order**).
3. Otherwise the **default** policy.

**Partial:** the department's "Default SLA policy" field is stored but not used in this selection.

The business calendar comes from the contract's support hours, then the policy's calendar, then the department's
calendar, then 24×7.

### To create a policy

1. Open **Policies** and create a policy.
2. Choose the **calendar**, **pause statuses**, and whether to **use contract support hours**.
3. Add **conditions** (up to 3 levels deep) on tier, contract, priority, department, product, category, channel and more.
4. Set **targets per priority** (Critical, High, Medium, Low): First response, Next response and Resolution, each with
   a business-hours flag and **Warn at %** (default 75). Targets range from 1 minute to 365 days.
5. Add **escalation levels** (up to 10). Each has a milestone, a trigger (percentage of target, or minutes before or
   after the breach), an action (Notify, Reassign, RaisePriority, NotifyAndReassign) and who to notify. The assignee and
   department supervisors are always notified.
6. Use **Simulate against a ticket** to see which policy would apply and why, with due times. It uses the unsaved form.
7. Save. Saving creates a new version and recalculates open tickets.

The SLA monitor runs every 30 seconds. The first-response clock never pauses.

### Business calendars and holidays

A calendar has a time zone, weekly hours (or 24×7) and holidays (date, name, **Every year**). A calendar in use cannot
be deleted. Use the business-time preview to check a calculation.

To import holidays, upload a CSV file with `date,name[,recurring]` per line:

* Separators: comma, semicolon or tab. A header row starting with `date`, blank lines and `#` comments are skipped.
* Dates: `yyyy-MM-dd`, `dd-MM-yyyy`, `dd/MM/yyyy`, `d MMM yyyy` and similar.
* Recurring: `yes`, `y`, `true`, `1` or `recurring`.
* Up to 2000 lines and 512 KB. The result shows "X added · Y updated · Z skipped" and up to 50 line errors.

**Caution:** **Replace all existing holidays** deletes the current holidays even if every imported line fails.

---

## 5. Departments, teams, agents and routing

These screens live under **Teams & Agents**. Supervisors (`supervisor.monitor`, `agents.manage`) use them too.

### Departments

| Field | Notes |
|---|---|
| Name, code, e-mail alias, head | |
| Routing team | The team that receives the department's new tickets |
| Business calendar | Empty means 24×7 |
| Visible in the customer portal | Offers the department on portal forms |
| Transfers out need supervisor approval | Department transfers create an approval request |

Departments can be archived and restored (`admin.organisation`).

### Teams and routing

A team has members and a lead. Its routing settings decide who gets new tickets:

| Setting | Values |
|---|---|
| Assignment mode | Manual, Round robin, Load balanced (default), Skill based, Territory |
| Only agents who are Online | on by default |
| Skip agents who are off shift | uses shift assignments |
| Team cap | Maximum open tickets for the team |
| Fallback queue | Where tickets go when nobody qualifies |

A customer's dedicated agent always wins. Routing skips inactive and light agents, non-members, agents on leave or
delegated, agents who are not online or off shift (when those settings are on), and agents at capacity. Use
**Test routing** to preview the assignment of a sample ticket; nothing is saved.

**Partial:** skill-based routing adds the agent's average skill level; it does not match the skills a ticket needs.

### Agents

* **Invite** (`admin.users`): e-mail, name, roles, departments (required), supervised departments, teams and the light
  agent flag. The plan's seat limit is checked. The invitation code is valid for 72 hours.
* **Routing profile** (`admin.users` or `agents.manage`): max open tickets (1–1000, default 25), **Max concurrent
  chats** (0–50, default 3), languages, skills with proficiency 1–5, and routing attributes (territory, product,
  customer, language, category, customer tier).
* **Deactivate** asks what happens to the agent's open tickets: route again, give them to an agent, or return them to
  the queue. Deactivation also respects the last-administrator rule.

### Skills, shifts, leave and delegation

* **Skills**: a catalogue used by routing profiles.
* **Shifts**: patterns with a time zone, colour and weekly hours (up to 4 intervals a day). Assign agents from a date,
  optionally until a date. Overlapping assignments are closed automatically. Shifts only affect teams that skip
  off-shift agents. **Partial:** there is no roster grid and no default shift per agent.
* **Leave**: types Leave, Sick, Training, Public holiday, Other. Requests go to the person the agent reports to, then a
  department supervisor, then anyone with `approvals.decide`. A manager's entry is approved at once. Approved leave
  removes the agent from routing; existing tickets stay.
* **Delegation**: an away agent, a delegate and a date range. While active, routing skips the away agent, manual
  assignments and (by default) approvals go to the delegate, and open tickets can move to the delegate.

---

<!-- CHANNELS -->

---

## 7. Automation rules and blueprints

Open **Automation** (`admin.automation`). The tabs are **Rules**, **Executions** and **Blueprints**.

### Rules

Each rule is **trigger → conditions → actions**.

| Trigger | Fires when |
|---|---|
| `ticket.created`, `ticket.updated` (optionally only for listed fields), `ticket.status_changed`, `ticket.assigned` | Ticket changes |
| `ticket.reply_added`, `ticket.customer_replied`, `ticket.note_added` | Conversation activity |
| `sla.warning`, `sla.breached` | SLA milestones |
| `customer.activity` | Customer events |
| `scheduled` | Every 5 minutes to 7 days (default 60 minutes), up to 500 tickets a run |
| `webhook` | An external system posts to the rule's URL with its secret (see [API.md](API.md#automation-rule-webhooks)) |

* **Conditions**: nested AND/OR groups up to 4 levels, with operators such as is, any of, contains, pattern, empty,
  numeric and date comparisons, within the last, changed to and changed from.
* **Actions**: assign, transfer department, escalate; set priority, severity, status, substatus, category, issue type,
  custom field; add or remove tags; add note, reply with template, notify agents, send message (e-mail, SMS,
  WhatsApp); add watcher, create task, create child ticket; invoke webhook (signed, with retries), update ERP record;
  request approval; AI classify and summarise. Placeholders such as `{{ticket.number}}` and `{{cf:<key>}}` work in
  texts. There are no delete, merge or spam actions.

Workflow:

1. Create the rule and **Save draft**.
2. **Simulate** it against one ticket or the latest N tickets (up to 50). The trace shows each condition and whether
   each action would run. Nothing changes.
3. Check **conflicts** (two rules setting the same field differently, competing assignments, tag conflicts, stop
   processing).
4. **Publish**. Only the published version runs. Then switch the rule **Active**.
5. Order rules within a trigger. Tick **Stop processing later rules when this rule matches** where needed.

The **Versions** tab offers diff, publish and roll back. **Executions** shows every run (Succeeded, Partially Failed,
Failed, Skipped Loop, Not Matched, Simulated) with the condition trace and **Retry failed actions**.

Safeguards: each event is processed once per rule version; chains stop at depth 5; a rule never fires on events its own
actions caused; at most 20 runs per rule per ticket per hour.

### Blueprints

Blueprints guide tickets through fixed states (needs the plan feature `blueprints`).

1. Scope the blueprint by department and/or category. It attaches automatically to **new** tickets that match; the most
   specific match wins.
2. Define **states**. Each maps to a ticket status and substatus and can be initial or final.
3. Define **transitions**: from, to, allowed roles (empty = anyone who may edit the ticket), mandatory fields (for
   example severity, category, product, version, installation, resolution code, root cause, custom fields), comment
   required, approval (a person, a role or department supervisors), a validation condition, and "after" actions.
4. Save and publish. A blueprint in use cannot be deleted.

Agents run transitions from the ticket's **Blueprint** tab. Direct status changes are blocked for blueprint tickets,
except for `admin.automation` holders, who can also remove a ticket from its blueprint.

**Partial:** "department supervisors" approvals can be decided by anyone with `approvals.decide`.

---

## 8. Webhooks

Open **Webhooks** (`admin.integrations`) to send signed HTTP callbacks to other systems.

1. Select **New webhook**.
2. Enter a **Name** and **Payload URL** (absolute http or https, no credentials).
3. Pick **Events**, or **All events (current and future)**.
4. Optionally enter a **Signing secret** (at least 16 characters); otherwise one is generated.
5. Save. The secret is shown **once**. Give it to the receiving system.

The list shows status (Active, Failing, Disabled), consecutive failures and the last delivery. The menu offers **Edit**,
**Send test ping**, **Rotate secret** and **Delete**. **Deliveries** opens the delivery log with filters, payloads,
responses and **Redeliver**.

Failed deliveries retry with back-off from 30 seconds to 12 hours and stop after 8 attempts. After 20 consecutive
failures the webhook is disabled and administrators are notified. Fix the receiver, enable the webhook, then redeliver.
Webhooks cannot call private or loopback addresses unless the server setting `Automation:Webhooks:AllowPrivateNetworks`
is on. The payload format and signature check are in [API.md](API.md#8-outgoing-webhooks).

---

## 9. AI

Open **AI** (`admin.ai`). The tabs are **Provider & budget**, **Features**, **Auto-reply scenarios**,
**Prompt templates**, **AI agents**, **Usage & cost** and **Execution log**. AI needs the plan feature `ai`; AI agents
need their own plan feature.

| Area | What you configure |
|---|---|
| Model provider | Provider (the built-in offline **Local** provider, OpenAI, Azure OpenAI, an OpenAI-compatible endpoint, or Anthropic), model, endpoint and API key (write-only, encrypted). Use the provider test before you switch. |
| Embeddings & knowledge index | Embedding provider and model; **Reindex knowledge now** |
| Budget & cost | Monthly budget (default 100), currency, action when exceeded (**Block** or **Warn**), input and output cost per 1,000 tokens |
| Privacy & retention | **Redact PII** before text goes to an external provider; how long execution logs are kept (default 180 days) |
| Features | Turn individual features on or off (summaries, drafts, classification, search, analytics questions…) and set the minimum confidence (default 0.75) |
| Auto-reply scenarios | Which situations an AI agent may answer automatically |
| Prompt templates | Versioned prompts per feature |
| AI agents | Triage, InfoCollector, KnowledgeResolver and AutoResponder, each in **SuggestOnly** or **AutoLowRisk** mode. Auto-replies are blocked for critical tickets, frustrated customers and suspected prompt injection. |
| Usage & cost, Execution log | Spend and tokens by feature, model and month; every AI call with its outcome |

Proposals that AI agents create are reviewed by users with `ai.approve_actions` on the AI Assistant's **Proposals** tab.

---

## 10. Knowledge settings

There is no separate knowledge-settings page.

* **Categories** of the knowledge base are managed under **Knowledge Base › Categories** (`kb.publish` or
  `admin.knowledge`).
* Portal options (knowledge base, community, AI assistant, languages) are set per brand under
  [Brands and portals](#11-brands-and-portals).
* **Reindex knowledge now** is on the AI settings page.
* `admin.knowledge` also opens knowledge-base analytics and export.

---

## 11. Brands and portals

Open **Brands & portals** (`admin.organisation`). Each brand has its own portal, colours, sender identity and footers.
A second brand needs the plan feature `multi_brand`; the number of brands is capped by the plan (Trial 2, Starter 1,
Professional 3, Enterprise unlimited).

### To set up a brand

1. **Identity**: **Brand name** and **Portal address** (the slug in `/portal/{slug}`: 2–64 lower-case letters, digits
   and hyphens, globally unique; reserved words such as `admin`, `api`, `portal`, `widget`, `hubs` are refused).
   Choose **Primary colour** and **Accent colour**.
2. **Logo & favicon**: PNG, JPEG, GIF or WebP up to 2 MB (after the brand is saved).
3. **Portal**: **Portal title**, **Welcome message**, **Default department for portal tickets** (empty: routing
   decides), **Default language** and **Languages offered**, and **Customer sign-in** mode.
4. Switch **Help center is online** on, and choose the features: **Knowledge base**, **Community forums**,
   **Live chat widget**, **AI assistant**, **Anonymous tickets** and **Self-registration**.
5. **Contact & footers**: support e-mail and phone, **Portal footer (HTML)** and **E-mail footer / signature (HTML)**
   (added to replies, acknowledgements and surveys for this brand).

Use the card menu to **Open the portal** or **Make default brand**. The default brand's portal cannot be switched off,
and the default brand cannot be deleted. Brands with tickets or channels cannot be deleted (409 `brand_in_use`).

### Custom domains

Enter a **Domain** and create the CNAME record shown on the page (the target is the server setting
`Portal:CustomDomainTarget`, or the public host). **Check DNS** compares the addresses and shows **Connected**,
**Points elsewhere** or **Not found**. It does not prove ownership, and HTTPS for the domain must be set up on the web
server (see [DEPLOYMENT.md](DEPLOYMENT.md)).

**Partial:** the portal does not yet answer on custom domains. Customers must use `/portal/{slug}/`. Links that SupportDesk
generates for a brand with a custom domain point at that domain and will not work until host routing is added.

**Partial:** the **Customer sign-in** modes "Single sign-on only" and "Both" are stored, but there is no customer SSO.
"Single sign-on only" only hides the password tab; customers sign in with e-mail codes.

---

## 12. Notifications and e-mail templates

Open **Notifications & e-mail templates** (`admin.organisation` or `admin.channels`).

### Delivery defaults

The matrix sets, per event, which channels agents get by default: **In-app**, **E-mail**, **Push** and **Digest**.
Events: Ticket assigned to me, Mentioned in a note, Customer replied, Ticket escalated, SLA at risk, SLA breached,
Approval requested, Approval decided, Follow-up due, AI proposal, Anomaly detected, Chat waiting, System messages.

* **Quiet hours** (from, until, time zone) delay e-mail and push. Critical alerts ignore quiet hours.
* **Digest frequency** is Hourly or Daily (**Daily digest at**, default 08:00). With both e-mail and digest on, the event
  goes into the digest.

**Partial:** the page says agents can override these defaults in their profile. The API supports it, but the desk has no
personal notification page yet.

### E-mail templates

| Template | Audience | Placeholders |
|---|---|---|
| New ticket acknowledgement | Customers | `{{contact.name}}`, `{{brand.name}}`, `{{ticket.number}}`, `{{ticket.subject}}`, `{{portal.url}}` |
| Satisfaction survey | Customers | `{{ticket.number}}`, `{{ticket.subject}}`, `{{agent.name}}`, `{{survey.link}}`, `{{survey.buttons}}`, `{{survey.expires}}`, `{{brand.name}}` |
| Agent notification e-mail | Agents | `{{user.name}}`, `{{notification.title}}`, `{{notification.body}}`, `{{notification.link}}` |
| Agent digest e-mail | Agents | `{{user.name}}`, `{{digest.count}}`, `{{digest.items}}` |

To customise one, open it, edit the **Subject** and **Body (HTML)**, insert placeholders from the chips, and turn on
**Use this custom template**. The preview fills in sample values and, for customer templates, adds the brand's e-mail
footer. Unknown placeholders are refused. **Reset to default** deletes your version. There is no test send.

**Partial:** the survey e-mail links to a portal page that does not exist yet (`/portal/{brand}/csat/{token}`).
Customers can still rate from the ticket page in the portal.

---

## 13. Security and sign-in

Open **Security & sign-in** (`admin.security`). The sections are **Policies**, **Single sign-on** and
**Active sessions**.

### MFA

| Policy | Effect |
|---|---|
| Optional | Each agent decides whether to use an authenticator app |
| Required for administrators | Users who can manage roles or security must verify; without an authenticator they get an e-mailed code |
| Required for everyone | Every agent must verify |

Single sign-on users are verified by your identity provider.

### Password and session policy

| Setting | Default | Range |
|---|---|---|
| Minimum length | 10 | 8–128 |
| Upper-case, lower-case, digit / symbol | on / off | |
| Remember previous passwords | 5 | 0–24 |
| Expire after (days) | 0 (never) | 0–365 |
| Lock after failed attempts | 5 | 3–20 |
| Lockout duration (minutes) | 15 | 1–1440 |
| Access token lifetime (minutes) | 15 | 5–60 |
| Idle timeout (minutes) | 480 | 5–10080, not above the absolute lifetime |
| Absolute session lifetime (hours) | 72 | 1–720 |
| Concurrent sessions per user | 10 | 1–50; the oldest session is signed out |

### Agent IP allow-list

1. Turn on **Restrict agent access to these networks**.
2. Enter addresses or CIDR ranges, one per line (up to 100). Select **Add my address** to include yours.
3. Save. The API refuses to save a list that would lock you out.

The list applies to agents only: at password sign-in and on every agent API request (403 `ip_not_allowed`). Customers,
API clients and the platform operator are not affected.

**Behind a reverse proxy or load balancer**, the client address comes from `X-Forwarded-For`. By default the API trusts
that header only from a proxy on the same host (loopback). If your proxy or load balancer runs elsewhere, list it in the
server settings `ForwardedHeaders:KnownProxies` (IP addresses) or `ForwardedHeaders:KnownNetworks` (CIDR ranges).
Otherwise every request appears to come from the proxy, and the allow-list (and per-IP rate limits) see the wrong
address. `ForwardedHeaders:TrustAll=true` trusts any sender; use it only when the edge always overwrites the header and
Kestrel cannot be reached directly. See [DEPLOYMENT.md](DEPLOYMENT.md).

### Single sign-on (OpenID Connect)

SSO needs the plan feature `sso` (Trial and Enterprise). You can prepare it on any plan.

1. Register SupportDesk in your identity provider with the **Redirect (callback) URI** shown on the page
   (`{base}/api/v1/auth/sso/callback`).
2. Enter the **Authority (issuer)** (https), **Client id**, **Client secret** (write-only) and **Scopes** (default
   `openid profile email`).
3. Select **Test connection**. It reads the provider's discovery document and signing keys.
4. Set the **E-mail claim** (default `email`) and **Display-name claim** (default `name`). Agents are matched by e-mail.
5. Optionally turn on **Create agents automatically at their first SSO sign-in** and choose the **Role for new agents**.
6. Turn on **Single sign-on is on**. Optionally **Require SSO** (agents cannot use passwords; administrators still can).

The page also shows the **Sign-in start URL** (`{base}/api/v1/auth/sso/{workspace}/start`).

**Partial:** the desk sign-in page has no SSO button and does not complete the SSO hand-off yet. Do not turn on
**Require SSO** until it does. Also, the SSO callback does not check the IP allow-list (the next request does), and
just-in-time provisioning does not check the seat limit.

### Active sessions

Lists signed-in agent sessions with IP, device, MFA or password, and times. **Revoke** ends one session (its whole
refresh-token family). **Sign this user out everywhere** ends all of a user's sessions. Revoked sessions fail on their
next request.

---

## 14. API keys and OAuth clients

Open **API keys & OAuth clients** (`admin.integrations`).

1. Select **New API key** or **New OAuth client**.
2. Enter a **Name** (for example "Wings ERP sync").
3. Tick **Scopes**. Locked scopes grant permissions you do not hold.
4. For API keys, optionally choose **Act as user** (the key never exceeds that user's permissions).
5. Set **Expires on** (recommended; at most 3 years).
6. Copy the secret now. It is shown **only once**.

You need organisation-wide data scope to issue credentials. API keys can only be revoked (create a new one to rotate).
OAuth clients have **Rotate secret** and **Revoke**; revoked clients' tokens stop working at once. The scope list, the
scope-to-permission mapping and request examples are in [API.md](API.md#2-authentication).

**Partial:** the scopes `admin:read` and `integrations:write` grant no permissions in this release, and analytics beyond
personal data needs the `*` scope.

---

## 15. Wings ERP and Wings Books

Open **Wings ERP & Books** (`/products/erp`, `admin.integrations`). There is a tab per system.

1. Choose the **Mode**: **Simulator (development / demo)** or **Wings API (HTTP)**.
2. For HTTP, enter the base URL and authentication (OAuth 2.0 client credentials, API key or none). Secrets are
   write-only and encrypted.
3. Choose what to sync (customer masters and branches, installations and licences, AMC contracts) and whether to
   create customers that exist only in the ERP.
4. Set the sync interval (5–10080 minutes). **Test connection**, then **Sync now**.

The status shows Not configured, Disabled, Connected or Error, with the last sync and error. The integration log lists
sync runs, tickets created from the ERP and record updates. Fields owned by the desk (tier, account manager, SLA,
coverage) are never overwritten. For ERP screens to create tickets, issue an API key or OAuth client with the
`tickets:write` scope. Full details: [integrations/WINGS-ERP.md](integrations/WINGS-ERP.md).

---

<!-- ENGINEERING-COLLAB -->

---

<!-- AUDIT-COMPLIANCE-CONFIG-BILLING -->

---

# Part B — Platform operator console

<!-- PLATFORM -->

---

## Permission reference

Generated from `src/SupportDesk.Domain/Rules/Permissions.cs`. ✓ means the default role template holds the permission.
⚠ marks permissions flagged as sensitive. Light agents lose reply, assign, transfer, delete and bulk rights whatever
their roles say.

| Permission | Description | Administrator | Supervisor | Agent | Light agent | Knowledge manager | Analyst |
|---|---|:-:|:-:|:-:|:-:|:-:|:-:|
| **Tickets** | |  |  |  |  |  |  |
| `tickets.view` | View tickets within the role's data scope | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `tickets.create` | Create and clone tickets | ✓ | ✓ | ✓ |  |  |  |
| `tickets.edit` | Edit ticket fields | ✓ | ✓ | ✓ |  |  |  |
| `tickets.reply` | Send public replies to customers | ✓ | ✓ | ✓ |  |  |  |
| `tickets.note` | Add internal notes | ✓ | ✓ | ✓ | ✓ | ✓ |  |
| `tickets.assign` | Assign and reassign tickets | ✓ | ✓ | ✓ |  |  |  |
| `tickets.transfer` | Transfer tickets between departments | ✓ | ✓ |  |  |  |  |
| `tickets.escalate` | Escalate tickets | ✓ | ✓ | ✓ |  |  |  |
| `tickets.resolve` | Resolve and close tickets | ✓ | ✓ | ✓ |  |  |  |
| `tickets.reopen` | Reopen resolved/closed tickets | ✓ | ✓ | ✓ |  |  |  |
| `tickets.merge` | Merge tickets | ✓ | ✓ | ✓ |  |  |  |
| `tickets.split` | Split tickets | ✓ | ✓ | ✓ |  |  |  |
| `tickets.delete` ⚠ | Delete tickets and mark spam | ✓ | ✓ |  |  |  |  |
| `tickets.bulk` | Bulk-update tickets | ✓ | ✓ |  |  |  |  |
| `tickets.time` | Log time on tickets | ✓ | ✓ | ✓ |  |  |  |
| `tickets.view_costs` ⚠ | See support costs and billable amounts | ✓ | ✓ |  |  |  | ✓ |
| `tickets.override_entitlement` ⚠ | Override support eligibility | ✓ | ✓ |  |  |  |  |
| `tickets.export` | Export ticket lists | ✓ | ✓ |  |  |  |  |
| **Customers** | |  |  |  |  |  |  |
| `customers.view` | View customers and contacts | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `customers.manage` | Create and edit customers, contacts and locations | ✓ | ✓ | ✓ |  |  |  |
| `contacts.merge` ⚠ | Merge duplicate contacts | ✓ | ✓ |  |  |  |  |
| `customers.merge` ⚠ | Merge duplicate customer organisations | ✓ | ✓ |  |  |  |  |
| **Products** | |  |  |  |  |  |  |
| `products.view` | View products, installations and assets | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `products.manage` | Manage products, versions, installations and assets | ✓ |  |  |  |  |  |
| `contracts.view` | View contracts and entitlements | ✓ | ✓ | ✓ |  |  | ✓ |
| `contracts.manage` ⚠ | Manage contracts and entitlements | ✓ | ✓ |  |  |  |  |
| **Knowledge** | |  |  |  |  |  |  |
| `kb.view_internal` | Read internal knowledge articles | ✓ | ✓ | ✓ | ✓ | ✓ |  |
| `kb.author` | Write and edit article drafts | ✓ | ✓ | ✓ |  | ✓ |  |
| `kb.review` | Review and approve articles | ✓ | ✓ |  |  | ✓ |  |
| `kb.publish` | Publish, archive and expire articles | ✓ | ✓ |  |  | ✓ |  |
| `community.moderate` | Moderate community forums | ✓ | ✓ |  |  | ✓ |  |
| **Collaboration** | |  |  |  |  |  |  |
| `approvals.decide` | Approve or reject approval requests | ✓ | ✓ |  |  |  |  |
| `supervisor.monitor` | Supervisor monitoring (live queues, agent status) | ✓ | ✓ |  |  |  |  |
| `agents.manage` | Manage agent availability, shifts, leave and delegation | ✓ | ✓ |  |  |  |  |
| `engineering.link` | Create and link engineering work items | ✓ | ✓ | ✓ |  |  |  |
| `engineering.view` | View the engineering board, work items and releases | ✓ | ✓ | ✓ | ✓ |  |  |
| `engineering.manage` | Create, update and move engineering work items; publish releases | ✓ | ✓ |  | ✓ |  |  |
| **Analytics** | |  |  |  |  |  |  |
| `analytics.view` | View personal dashboards and reports | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `analytics.team` | View team, department and organisation analytics | ✓ | ✓ |  |  |  | ✓ |
| `analytics.export` | Export reports (CSV/Excel/PDF) | ✓ | ✓ |  |  |  | ✓ |
| `analytics.schedule` | Schedule report delivery | ✓ | ✓ |  |  |  | ✓ |
| **AI** | |  |  |  |  |  |  |
| `ai.use` | Use AI assistance (summaries, suggestions, classification) | ✓ | ✓ | ✓ |  | ✓ |  |
| `ai.approve_actions` ⚠ | Approve, apply and revert AI-proposed actions | ✓ | ✓ |  |  |  |  |
| **Administration** | |  |  |  |  |  |  |
| `admin.users` ⚠ | Manage agents and users | ✓ |  |  |  |  |  |
| `admin.roles` ⚠ | Manage roles and permissions | ✓ |  |  |  |  |  |
| `admin.organisation` | Manage departments, teams, brands and calendars | ✓ |  |  |  |  |  |
| `admin.ticket_config` | Manage ticket fields, statuses, categories and layouts | ✓ |  |  |  |  |  |
| `admin.sla` | Manage SLA policies and calendars | ✓ |  |  |  |  |  |
| `admin.automation` | Manage automation rules and blueprints | ✓ |  |  |  |  |  |
| `admin.channels` ⚠ | Manage email accounts and messaging channels | ✓ |  |  |  |  |  |
| `admin.knowledge` | Manage knowledge base settings and portals | ✓ |  |  |  | ✓ |  |
| `admin.ai` ⚠ | Manage AI models, prompts, budgets and agents | ✓ |  |  |  |  |  |
| `admin.integrations` ⚠ | Manage integrations, API keys, OAuth clients and webhooks | ✓ |  |  |  |  |  |
| `admin.security` ⚠ | Manage password, session, MFA and SSO policies | ✓ |  |  |  |  |  |
| `admin.audit` | View audit logs and verify integrity | ✓ |  |  |  |  |  |
| `admin.compliance` ⚠ | Retention, legal holds, data export and erasure | ✓ |  |  |  |  |  |
| `admin.billing` ⚠ | Manage subscription, plan and billing | ✓ |  |  |  |  |  |
| `admin.config_transfer` ⚠ | Export, import, publish and roll back configuration | ✓ |  |  |  |  |  |

58 permissions.
