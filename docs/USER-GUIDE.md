# SupportDesk user guide

This guide is for **agents**, **supervisors** and **customers**. It explains how to do everyday work in the agent desk,
the customer portal and the mobile app. Administrators should also read [ADMIN-GUIDE.md](ADMIN-GUIDE.md).
Integrators should read [API.md](API.md).

What you can see depends on your **role** and its **data scope**. If a menu, tab or button is missing, you probably lack
the permission for it. The [permission reference](ADMIN-GUIDE.md#permission-reference) lists which default role holds
which permission. Items marked **Not built yet** or **Partial** describe gaps in the current release.

Contents

1. [Getting started](#1-getting-started)
2. [Command Center](#2-command-center)
3. [Ticket workspace](#3-ticket-workspace)
4. [Working on a ticket](#4-working-on-a-ticket)
5. [The composer](#5-the-composer)
6. [Side panels contributed by other modules](#6-side-panels-contributed-by-other-modules)
7. [Inbox and live chat](#7-inbox-and-live-chat)
8. [Customers](#8-customers)
9. [Products & Assets](#9-products--assets)
10. [Knowledge base](#10-knowledge-base)
11. [Automation: what agents see](#11-automation-what-agents-see)
12. [AI Assistant](#12-ai-assistant)
13. [Analytics](#13-analytics)
14. [Engineering board](#14-engineering-board)
15. [Teams & Agents and SLA (supervisors)](#15-teams--agents-and-sla-supervisors)
16. [Notifications](#16-notifications)
17. [Customer portal](#17-customer-portal)
18. [Mobile app](#18-mobile-app)

---

## 1. Getting started

### To sign in

1. Open the desk, for example `https://desk.example.com/`.
2. Enter your **Workspace**. This is your company's SupportDesk name, for example `acme`.
3. Enter your **Work e-mail** and **Password**, then select **Sign in**.

The page remembers your workspace and e-mail in this browser. It never stores your password.

| Message | Meaning |
|---|---|
| "The workspace, e-mail or password is incorrect." | Check all three fields. |
| "Too many failed attempts. Try again after HH:mm UTC." | Your account is locked for a while (HTTP 423). |
| "Too many attempts. Wait a minute…" | The sign-in rate limit was hit. |
| "This workspace is suspended…" | The subscription is suspended. Ask your administrator. |
| IP or SSO message (HTTP 403) | Your network is not on the allow-list, or your workspace requires single sign-on. |
| "Your session ended. Please sign in again." | Your session expired or was revoked. |

### Two-step verification (MFA)

You see **Two-step verification** when you turned MFA on, or when your workspace requires it.

* **Authenticator app** works if you enrolled one. It is selected by default.
* **E-mail code** is always offered.

Enter the **Verification code** and select **Verify**. In a demo environment, a callout shows the code for you.

To turn MFA on or off for yourself, open the avatar menu › **Two-factor authentication**. Select **Set up two-factor**,
scan the QR code, enter the 6-digit code, then select **Turn on**.

### Passwords

* To change your password, open the avatar menu › **Change password**. New passwords need at least 12 characters.
* **Forgot your password?** Ask your administrator to reset it.
* **Not built yet:** the desk has no self-service reset or expired-password form. The API supports both.

### Single sign-on (SSO)

**Partial.** Administrators can configure OpenID Connect SSO, and the API implements the full flow. The desk sign-in page
has no "Sign in with SSO" button yet, and it does not complete the SSO hand-off. Use your password until this ships.

### Workspace, availability and theme

The top bar shows search, **New ticket**, the notifications bell, your availability, the theme toggle and your avatar.

| Availability | Meaning |
|---|---|
| Online | Receive new assignments. |
| Away | Temporarily away. |
| Busy | Do not route new work. |
| Offline | Not working. |

Routing only sends new tickets to **Online** agents when the team requires it. Shifts and idle time do not change your
availability. Set it yourself.

The theme follows your operating system at first. The sun/moon button (or **Shift+T**) switches between light and dark.
After you switch, the desk no longer follows the system setting.

Your profile (avatar menu › **My profile**, or `g m`) holds your display name, time zone, language and e-mail
signature. The signature is added to outgoing e-mail replies. It does not appear in the composer.

### Command palette

Press **Ctrl+K** (⌘K on a Mac) or **/**, or click the search box. You can:

* go to any module you can access;
* create a ticket;
* open a ticket by number (`#1042` or `1042`), or search tickets by subject, contact or customer;
* set your availability, change your password, toggle the theme or sign out;
* switch ticket views, and run actions on the open ticket (reply, note, assign to me, escalate, change status, resolve).

The palette remembers your five most recent commands. It does not search customers yet.

### Keyboard shortcuts

Press **?** to see every shortcut that is active on the current page. Two-key shortcuts such as `g t` must be pressed
within 1.2 seconds. Single-key shortcuts do nothing while you type in a field.

| Keys | Action | Where |
|---|---|---|
| Ctrl/⌘+K | Open or close the command palette | Everywhere, even in fields |
| / | Open search | Everywhere |
| ? | Show keyboard shortcuts | Everywhere |
| Shift+T | Toggle light/dark theme | Everywhere |
| c | New ticket | Needs `tickets.create` |
| g c / g t / g i | Command Center / Tickets / Inbox | If you can open the module |
| g u / g p / g s | Customers / Products & Assets / SLA | If you can open the module |
| g k / g a | Knowledge Base / Analytics | If you can open the module |
| g m | My profile | Everywhere |
| j / k | Next / previous ticket in the list | Tickets |
| g q | Show the queue list | Tickets |
| r / n | Reply / internal note | Open ticket |
| a | Assign to me | Open ticket |
| e / s | Escalate / change status | Open ticket |
| u | Back to the list | Open ticket |
| Shift+X | Close the ticket tab | Open ticket |
| Ctrl/⌘+Enter | Send from the composer | Composer |
| Ctrl/⌘+K | Insert a link | Inside the editor |
| @ | Mention a colleague | Internal notes |

In the ticket list, use ↑/↓, PgUp/PgDn and Home/End to move, **Enter** to open and **Space** to select.

---

## 2. Command Center

The **Command Center** (`g c`) is your live overview. Agents see their own work. Supervisors see their departments.

| Tile | Opens |
|---|---|
| My open tickets, My overdue, My due today | Your queues |
| Unassigned (supervisors: "my departments") | The Unassigned queue |
| SLA at risk, Overdue, Escalated | The matching queue |
| Pending approvals | The Approvals page |

The cards below the tiles show:

* **SLA risk board**: open tickets that are at risk or breached, soonest first, with live countdowns.
* **My open tickets by status**, plus your next five due tickets.
* **Team workload** (supervisors) or **My team** (agents): open tickets against capacity, with live availability.
* **Customer replies & mentions**: your latest twelve.
* **Pending approvals**: approve or reject here if you hold `approvals.decide`. You cannot decide your own request.
* **Escalations**: open escalated tickets.

The page updates in real time. Select **Enable desktop alerts** to get browser notifications for warnings.

---

## 3. Ticket workspace

Open **Tickets** (`g t`). The workspace has three panes: queues and views on the left, the list in the middle, and
customer context on the right when a ticket is open. You can drag the pane borders. Up to 12 tickets stay open as tabs.

### Queues

| Queue | Shows |
|---|---|
| My open tickets | Open tickets assigned to you |
| Unassigned | Open tickets with no assignee |
| All open | Every open ticket in your scope |
| Overdue | Open tickets past their due date (red badge) |
| SLA at risk | Open tickets whose SLA is at risk (red badge) |
| Escalated | Open escalated tickets |
| Watching | Open tickets you watch |
| Waiting | Tickets in a Waiting status |
| Deleted tickets | The recycle bin (needs `tickets.delete`) |

You only ever see tickets inside your role's data scope.

### To create a saved view

1. In the **Views** section, select **New view**.
2. Enter a **Name**.
3. Choose **Who can use it**: **Personal** ("Only you") or **Shared** ("Everyone in the help desk"). Shared views need
   `supervisor.monitor`.
4. Build the **Conditions** with AND/OR groups. You can filter on subject, priority, severity, status, channel,
   department, team, assignee ("me" works for whoever opens the view), product, category, tags, SLA state,
   entitlement, brand, dates, reopen count, CSAT and more.
5. Pick **Sort by** and a **Position**.
6. Optionally tick **Save the current column layout with this view** and **Open this view by default**.

Use the view's menu to **Edit…**, **Duplicate…** or **Delete**. The owner or a supervisor can edit or delete a view.

### Which view opens by default

When you open **Tickets** without choosing a queue, the desk picks the first match:

1. The queue or view named in the link (`/tickets?queue=…` or `?view=…`).
2. On the first visit in this browser tab: your **personal default** view, otherwise the **shared default** view.
3. The queue or view you last used in this browser.
4. **My open tickets**.

There is one personal default per person and one shared default per workspace. Departments and teams cannot have their
own default.

### The ticket list

* Default columns: #, Subject, Status, Priority, Due (SLA countdown), Assignee, Customer, Updated. Use the column
  chooser to add Channel, Department, Team, Product, Category, Severity, Created, Tags or Replies.
* Sort by #, Priority, Due, Updated or Created.
* Bold rows with a dot have an unread customer reply. A red arrow means escalated. A red left border means breached.
* Type in **Filter by #, subject, customer…** to narrow the list.
* The list loads 50 tickets at a time. Scroll down or select **Load more**.

### To update many tickets at once

You need `tickets.bulk` (supervisors and administrators by default). You can select up to 500 tickets.

1. Tick the tickets.
2. Use **Assign**, **Status** or **Priority**, or open **More** for: add or remove tags, **Transfer…**, **Watch**,
   **Close**, **Mark as spam…** and **Delete…**.
3. Read the result: "X of Y tickets updated". Tickets that failed stay selected, with the reason.

Transfer needs `tickets.transfer`. Close needs `tickets.resolve` and does not ask customers to confirm. Spam and
delete need `tickets.delete`, and delete asks you to type `DELETE n`.

### To restore a deleted ticket

1. Open the **Deleted tickets** queue. It lists deleted and spam tickets, newest first.
2. Select **Restore** and confirm.

Anyone with `tickets.delete` can restore. The recycle bin is not emptied automatically. Tickets under a legal hold cannot
be deleted.

---

## 4. Working on a ticket

Open a ticket from the list, by number in the palette, or with a link such as `/tickets/1042`.

### Layout

* The **header** shows the number, subject, presence avatars, facts and SLA chips.
* The **timeline** shows the conversation. Internal and handover notes are amber. Toggle **Activity** to show field
  changes, and use **Expand all** / **Collapse all**.
* The **composer** is at the bottom.
* The **side tabs** are Details, Tasks, Time, People, Follow-ups and Related, plus tabs from other modules.
* The **context pane** shows the customer, contact, SLA timers per milestone, contracts, installations, previous tickets,
  ERP references, suggested articles and AI recommendations.

### Statuses

Every status belongs to one fixed category: Open, Assigned, In progress, Waiting, Resolved or Closed. New workspaces get
these statuses. Administrators can add more and restrict which status can follow which.

| Status | Category | Notes |
|---|---|---|
| Open | Open | Default for new tickets. The portal shows "Received". |
| Assigned | Assigned | Set automatically when an Open ticket is assigned. |
| In Progress | In progress | Set automatically when you reply to an Open or Assigned ticket. |
| Waiting on Customer | Waiting | **Pauses the SLA.** Sub-statuses: Information requested, Confirmation requested, Scheduling. |
| Waiting on Third Party | Waiting | Does not pause the SLA. Sub-statuses: Engineering, Vendor, Partner. |
| Resolved | Resolved | Auto-closes after a number of days (default 7). |
| Closed | Closed | Can still be reopened. |

The demo data also has **Escalated to Engineering** (Waiting). A customer reply moves a Waiting ticket back to In
Progress, or to Open if it has no assignee.

To change the status, press `s` or use the status picker in the header. The list shows only the statuses allowed next.
Some custom fields are **required for a status**. You must fill them before you can move the ticket there. Tickets that
follow a blueprint cannot change status freely; use the Blueprint tab instead.

### SLA timers

The header shows chips for **First response** (until someone responds), then **Next response**, and **Resolve**.

| Chip | Meaning |
|---|---|
| Due in 1h 05m (green) | On track |
| Due in … (amber) | At risk: past the warning threshold (75 % of the target by default) |
| Breached 12m ago (red) | The target was missed |
| Paused (grey) | The ticket is in a status that pauses the SLA |
| Met | The milestone was achieved |

**Partial:** the header chips use the ticket's overall SLA state. After one milestone breaches, every chip shows red.
The context pane shows the true state of each milestone. The first-response clock never pauses.

### To resolve a ticket

1. Select **Resolve…** (or use the palette).
2. Enter a **Resolution code** (required if your workspace uses codes), **Root cause** and **Resolution notes**.
   Resolution notes are shared with the customer when you notify them.
3. Choose the options:
   * **Notify the customer** (on by default) sends the resolution notice and the satisfaction survey.
   * **Ask the customer to confirm the fix (keeps it open until they reply)** marks the ticket "Awaiting customer
     confirmation". It needs a contact. Your workspace may force this option.
4. Select **Resolve**.

Resolved tickets close automatically after the configured number of days.

### To close or reopen a ticket

* **Close**: More actions (⋯) › **Close**. You need `tickets.resolve`.
* **Reopen**: select **Reopen…** and enter a **Reason**. You need `tickets.reopen`. The reopen count goes up and the
  SLA clocks resume. Merged tickets cannot be reopened.
* A customer reply reopens a resolved ticket. It also reopens a closed ticket within the reopen window (default 30 days).
  After that, the reply becomes a new ticket.

### To escalate a ticket

Press `e` or select **Escalate…**. Enter a **Reason**. Optionally choose **Escalate to** (that person becomes a watcher)
and keep **Raise the priority one level** ticked. The escalation level goes up by one.

### Merge, split, clone and link

| Action | How | Rules |
|---|---|---|
| Merge | More actions › **Merge tickets into this…**, pick up to 10 duplicates, add a note | Needs `tickets.merge`. Conversation, attachments, time, watchers, tags and engineering links move to this ticket. The duplicates close with a "Duplicate" link. |
| Split | More actions › **Split…**, pick messages, set the **New subject** | Needs `tickets.split`. The messages move to a new ticket for the same customer, linked as a child by default. |
| Clone | More actions › **Clone** | Needs `tickets.create`. Copies fields, tags and description, not the conversation. |
| Link | More actions › **Link to ticket…** | Types: Related to, Duplicate of, Blocks, Caused by. The Related tab shows both directions. |

### Watchers, tasks, time and follow-ups

* **People** tab: add a **Watcher**, **Collaborator** or **Temporary specialist** (with an end date). Anyone can add
  or remove themselves. Others need `tickets.edit`. Mentioning someone in a note makes them a collaborator.
* **Tasks** tab: add tasks, one level of subtasks and checklist items, each with an assignee and due date.
* **Time** tab: enter hours and minutes, start time, activity, notes and **Billable**, then **Log time**. You need
  `tickets.time`. Billable time uses up prepaid-hours contracts. Costs show only with `tickets.view_costs`.
* **Follow-ups** tab: pick **Remind at** and who to remind, add a note, then **Schedule**. You get a "Follow up on #N"
  notification when it is due.

### Tags and custom fields

The **Details** tab holds tags, category, product, version, installation, language and your department's custom fields.
Edit them, then select **Save**. The Facts list shows dates, SLA policy, contract, time spent and CSAT.

### When someone else changed the ticket

If another person saved the ticket after you opened it, the desk shows **Someone else changed this ticket**. The table
compares the saved value with yours. Select **Keep theirs** or **Re-apply my change**.

### To lock a ticket while you work

Select More actions › **Lock while I work on it**. Others see "Locked by …" and cannot edit, assign, transfer, change
status or resolve for 10 minutes. You can **Unlock** at any time. Supervisors can unlock other people's locks.
Locks do not block replies, notes, tags or links.

### Presence

Avatars in the header show who else has the ticket open. A pulsing ring and a banner such as "Priya is writing a
reply" tell you someone is typing.

---

## 5. The composer

### To reply to a customer

1. Select **Reply** (or press `r`).
2. Check the recipients. For e-mail tickets, **To** holds the contact's address. Use the **Cc** and **Bcc** links to add
   more. Other channels show "Sent to … via …"; the reply goes out on the ticket's channel.
3. Write your reply. Insert a **Template** if you like.
4. Attach files with the paperclip.
5. Select **Send**, or use the arrow to **Send and set status** in one step.

### Internal notes and handovers

* **Internal note** (`n`): only agents see it. Type `@` to mention a colleague and notify them.
* **Handover**: a note for the next agent ("What should the next agent know?").

Light agents can add notes but cannot reply.

### Templates

The **Templates** menu groups templates by category, most used first. A template can be personal, team, department or
global. These placeholders are filled in:

| Placeholder | Value |
|---|---|
| `{{contact.name}}` | Contact's first name, or "there" |
| `{{contact.fullName}}` | Contact's full name |
| `{{ticket.number}}`, `{{ticket.subject}}` | Ticket number and subject |
| `{{customer.name}}` | Customer organisation |
| `{{agent.name}}` | Your name |

**Not built yet:** the desk has no screen to create or edit templates (use the API). Templates do not run macro
actions.

### Attachments

The size limit comes from your plan (25 MB by default). Executables and active content (for example `.exe`, `.js`,
`.html`, `.svg`, `.docm`) are blocked. Every file is scanned for malware.

### Drafts

The composer saves a draft on the server about a second after you stop typing. You see "Draft saved". Drafts come back
when you reopen the ticket, on any device. **Discard** deletes the draft. Bcc is not kept in drafts.

### Collision review

If a customer or colleague posted while you were writing, sending stops and you see "N new entries were added while you
were writing." Read them, then choose:

* **I've reviewed them — keep editing**, or
* **Send anyway**.

---

## 6. Side panels contributed by other modules

| Tab | Appears when | What it does |
|---|---|---|
| **Blueprint** | Your plan includes blueprints | Shows the workflow, the current state and the transitions you may run. A transition may ask for mandatory fields and a comment. Select **Move to ‹state›**, or **Request approval** when the step needs approval. |
| **AI** | You have `ai.use` and your plan includes AI | Summary, sentiment, expected resolution, classification (**Apply selected**), a grounded **Draft reply** with citations (**Insert into composer**), similar and duplicate tickets (merge), likely root cause, **Draft article** and **Run agents**. |
| **Engineering** | You have `engineering.link` or `engineering.view` | Linked work items with state, fix version and release. **Escalate** creates a work item with repro steps filled from the ticket. **Link existing** links an item by id or URL. |

The composer also gets **Draft with AI** (Reply tab) and **Share to Teams/Slack** (Internal note tab).

---

## 7. Inbox and live chat

Open **Inbox** (`g i`). It lists conversations from e-mail, WhatsApp, SMS, live chat, phone, Messenger and Instagram.

* Filter by channel, status (Active, Waiting for agent, With agent, Bot, Open, Closed, All) and assignee.
* A pill shows how many chats wait for an agent.
* The list updates live. It loads 40 conversations at a time.

Tickets are created automatically from conversations. You cannot convert or link a conversation by hand.

* A WhatsApp, SMS, Messenger or Instagram message threads onto the customer's open ticket, or creates one.
* Missed calls and voicemails create tickets.
* A chat becomes a ticket when it is handed to a person.

Only live chats have an inline reply. For other channels, select **Reply on ticket #N**. The reply goes out on the
original channel. For WhatsApp, a banner shows whether the 24-hour service window is open. When it is closed, replies are
sent as an approved template.

### To accept a chat

Open **Inbox** › **Live chat**. The **Waiting** list is oldest first. Select **Accept**. The chat is assigned to you and
the visitor sees "‹your name› joined the chat." If someone was faster, you see "Another agent already accepted this
chat."

You hear a chime and get a desktop alert when a chat starts waiting. Use **Sound on/off** to mute it.

### Chat capacity

Each agent has a **Max concurrent chats** limit (default 3, set by a manager in the routing profile). The console shows
"active/limit". When you are full, **Accept** is disabled and a banner says "You're at your chat limit". End or
transfer a chat to free a slot. A limit of 0 means you take no chats.

### To transfer a chat

1. Select **Transfer**.
2. Choose **Another agent** in the same department, or **Another department** (needs `tickets.transfer`). For a
   department you may leave the agent empty; the chat then waits in that department's queue.
3. Add a **Note for the next agent** (optional), then confirm.

The ticket follows the chat. If the department requires approval, you see "Transfer sent for approval".

### Templates, typing and identity

* Press **Enter** to send and **Shift+Enter** for a new line.
* The **Templates** menu works once the chat has a ticket.
* You see "‹visitor› is typing…" while the visitor types.
* A banner says whether the visitor verified their e-mail with a code. Agents cannot verify a visitor.

### To end a chat

Select **End chat**. The visitor sees "This chat has ended. Thank you for contacting us!" A **Chat transcript** entry
is added to the ticket.

---

## 8. Customers

Open **Customers** (`g u`). The tabs are Customers, Contacts, Duplicates, Service activities and Groups. You need
`customers.view` to read and `customers.manage` to edit. Some roles only see the customers they manage as account
manager.

### Customer 360

Open a customer to see the 360 page.

* **Overview**: open, overdue and escalated tickets, SLA breaches, CSAT, average resolution, contracts, installations,
  charts, "Outstanding complaints & escalations", recent conversations, hierarchy and upcoming activities.
* **Tickets**, **Contacts** (with possible duplicates), **Locations** and departments.
* **Installations & assets** (needs `products.view`) and **Contracts** (needs `contracts.view`), with **Check support
  eligibility**.
* **Activities** (calendar) and **ERP records** (invoices, orders, receipts, AMC contracts; read-only).

A customer's **Tier** (Platinum, Gold, Silver, Bronze, Standard) drives SLA policy selection. E-mail **domains** link new
contacts from that domain to the customer automatically.

### Contacts

A contact page shows the profile, channel identities, consent, portal access and tickets. From **Customer portal** you can
**Send invitation** (code valid 72 hours) or **Send password reset** (30 minutes). Portal access is None, Own, Location
or Organisation; see [ticket scopes](#ticket-scopes).

### To find and merge duplicate contacts

You need `contacts.merge`.

1. Open **Duplicates**. Choose a **Minimum match** (40, 60 or 85 %) and select **Rescan**.
2. Select **Review & merge** on a pair.
3. Choose the contact to keep. The default has more tickets.
4. Tick **Use merged value** for any field you want from the other contact.
5. Confirm.

Matching uses e-mail, phone, name similarity and the customer. Tickets, conversations, messages, identities, CSAT and
community posts move to the kept contact. The other contact becomes **Merged** and read-only. **There is no unmerge.**

### To merge two customer organisations

You need `customers.merge`.

1. On the Customer 360 page select **Merge…**.
2. Search for the duplicate.
3. Choose which customer to keep and which values to take.
4. Review, then select **Merge customers**.

Contacts, tickets, locations, contracts, installations, assets, activities and subsidiaries move to the kept customer.
The other customer is hidden and cannot be restored.

### Groups

A customer belongs to at most one group, for example "Key Accounts". Groups are used in list filters, saved views and
knowledge-base access rules. **Partial:** SLA and routing conditions cannot use groups yet.

### Service activities

Use **Service activities** to plan onsite visits, remote sessions, training, maintenance, upgrades, calls and meetings.
Statuses are Scheduled, In progress, Completed, Cancelled and Missed. Tick **Visible to the customer in the portal** to
show an activity to the customer.

### Consent

Each contact has a consent grid: Email, SMS, WhatsApp and Phone, by Transactional, Marketing and Surveys. An opt-out
blocks outbound messages on that channel.

**Not built yet:** deleted customers can be restored through the API only.

---

## 9. Products & Assets

Open **Products & Assets** (`g p`).

| Tab | Needs | Contains |
|---|---|---|
| Catalog, Families | `products.view` | Products, modules, versions (with end of support) and releases |
| Installations | `products.view` | Customer sites running a product: environment, deployment, version, modules |
| Licences | `products.view` | Masked keys, edition, seats, expiry |
| Assets | `products.view` | Serial numbers, asset tags, warranty |
| Install base | `products.view` | Installations per version, end-of-support exposure, tickets per installation |
| Contracts, Renewals | `contracts.view` | Support contracts, entitlements and upcoming renewals |
| ERP integration | `admin.integrations` | ERP connection (administrators) |

Only administrators can change the catalog by default (`products.manage`). Supervisors can manage contracts
(`contracts.manage`).

### Contracts and entitlements

A contract has a type (Warranty, AMC, Annual support, Prepaid hours, Time and material, Subscription), a period, an
optional SLA policy and support-hours calendar, a value, and covered channels. **Entitlements** limit cover to a
product, installation or asset, and can carry a number of incidents or hours.

The contract page shows value, hours used, tickets covered, consumption by month and time entries.

### Renewals

The **Renewals** tab shows expired, critical (7 days or less) and soon (30 days or less) contracts. The customer's
account manager gets a reminder 30 and 7 days before a contract ends. **Not built yet:** there is no renew action.

### Support eligibility

SupportDesk checks each ticket against the customer's active contracts. It runs when a ticket is created and when its
customer, product, installation or asset changes.

| Result | Meaning |
|---|---|
| Entitled | An active contract covers the ticket. The note shows the contract and balance. |
| Not entitled | No cover, or the allowance is used up. |
| Expired | Only ended contracts exist. |
| Entitlement unknown | The ticket has no customer. |
| Overridden | Someone overrode the result. |

You see the result as a badge in the ticket header, in the Details facts and in the context pane. To check before you
create a ticket, use **Check support eligibility** on the Customer 360 page. The check never blocks work. The contract's
SLA policy and support hours do apply to the ticket.

### To override eligibility

Select More actions › **Override entitlement…**, enter a **Reason** and optionally a contract to bill. You need
`tickets.override_entitlement`. Agents with `tickets.edit` can also override while the workspace setting allows it,
and it does by default. Every override is audited.

---

## 10. Knowledge base

Open **Knowledge Base** (`g k`). The tabs are Articles, Categories, Feedback, Community, Troubleshooting and Analytics.

| Permission | Lets you |
|---|---|
| `kb.view_internal` | Read internal articles |
| `kb.author` | Write drafts, submit for review, import |
| `kb.review` | Approve or reject |
| `kb.publish` | Publish, archive, manage categories and flows |
| `community.moderate` | Moderate the community |

### To write an article

1. Select **New article**.
2. Enter the **Title** and **Body**. The editor supports headings, lists, links, code, images, YouTube and tables.
3. Fill in the summary, keywords, category, type, tags, product, related articles, brands, language, owner, review date
   and expiry date.
4. Choose the **Visibility**: Public, Signed-in customers, Internal, or Restricted.
5. Press **Ctrl+S** to save.

For **Restricted** articles, add access rules. Each rule combines customer, customer group, tier and product. A reader
needs to match one rule.

### Review and publish

| State | Next step | Who |
|---|---|---|
| Draft | **Submit for review** | `kb.author` |
| In review | **Approve** or **Reject** (a note is required) | `kb.review` |
| Approved | **Publish** | `kb.publish` |
| Published | **Archive** | `kb.publish` |
| Archived, Expired | **Reopen as draft** | `kb.author` |

If you hold both `kb.review` and `kb.publish`, you can publish a draft directly. Editing a published article creates a
new draft version; the published version stays live until you publish again. Articles expire on their expiry date, and
owners get "Review due" reminders. There is no scheduled publishing.

### Versions and translations

* **Versions**: view, diff or restore any version. A restore creates a new draft version.
* **Translations**: add a language, translate, **Submit for review**, then **Publish**. When the source article is
  republished, older translations become **Outdated**. There is no machine translation.

### Feedback

Portal readers rate articles (helpful or not, 1–5 stars, a comment). Review them under **Feedback** and select
**Mark reviewed**.

### Community moderation

Under **Community**, moderators can edit, pin, close, hide and unhide topics and replies, accept an answer, and
**Convert to ticket**. Hiding and closing need a reason. Agents can **Reply as an agent**. Converting copies only the
topic text, not the replies.

### Troubleshooting flows

A flow is a decision tree of **Question**, **Solution**, **Article** and **Escalate** steps. Build it in the designer,
fix the problems the validator lists, **Preview** it, then **Publish**. Customers run flows in the portal. An
**Escalate** step offers to create a ticket with the answers attached. Agents cannot run flows from a ticket.

### Using the knowledge base on a ticket

The context pane lists **Suggested articles** with a match score. The AI tab can **Draft article** from a resolved
ticket. **Draft with AI** cites articles in reply drafts.

---

## 11. Automation: what agents see

Automation rules and blueprints are configured by administrators (`admin.automation`); see the admin guide. As an agent
you see their effects:

* timeline entries such as "Rule '…' applied: …";
* automatic assignment, priority, tags, notes and replies;
* the **Blueprint** tab, where you run the next step of a guided process;
* approval requests on the **Approvals** page when a step needs one.

**Not built yet:** agent macros. Templates insert text only.

---

## 12. AI Assistant

Open **AI Assistant**. You need `ai.use` (or `ai.approve_actions` for proposals).

| Tab | Needs | What it does |
|---|---|---|
| Ask analytics | `ai.use` + `analytics.view` | Ask a question about your ticket data in plain words. The answer is a chart or table. The AI never writes SQL. |
| Semantic search | `ai.use` | Search tickets and articles by meaning. |
| Proposals | `ai.approve_actions` | Review actions proposed by AI agents: **Approve & apply**, **Reject** or **Revert**. |
| Anomalies | `supervisor.monitor`, `analytics.team` or `admin.ai` | Daily volume spikes by product, version, customer or category. **Acknowledge** or **Dismiss**. |

Proposal actions include classification, priority, tags, notes, information requests and replies. High-risk proposals
ask for confirmation. An approved reply is sent under your name. You can revert classification, priority and tags.
Proposals expire after 14 days.

On a ticket, use the **AI** tab and **Draft with AI** (see [section 6](#6-side-panels-contributed-by-other-modules)).
Always review AI drafts before you send them. A badge shows whether a draft is grounded in knowledge.

The demo uses a built-in offline provider. Your administrator may connect OpenAI, Azure OpenAI, an OpenAI-compatible
service or Anthropic, and may set a monthly budget.

---

## 13. Analytics

Open **Analytics** (`g a`). With `analytics.view` you see your own tickets ("Personal analytics"). With
`analytics.team` you see your data scope (team, department or organisation).

### Dashboards

Four built-in dashboards are provided:

| Dashboard | Default period | Focus |
|---|---|---|
| My performance | Last 30 days | Your open and resolved tickets, first response, CSAT |
| Team operations | Last 7 days | Backlog, SLA compliance, agent scorecard, channels |
| Service performance | Last 30 days | Resolution time, departments, escalations, backlog |
| Executive overview | Last 90 days | Demand, CSAT, products, top customers, tiers |

To build your own, select **New dashboard**, then **Edit layout** and add widgets (KPI, line, bar, pie or table). Pick
a metric, a **Break down by** dimension, filters and a period. Only `analytics.team` holders can share a dashboard.

### Reports

Ten standard reports are available: volume and backlog, response and resolution times, SLA compliance, FCR and reopens,
channels, satisfaction, agent productivity, recurring issues (Pareto), product quality and customer health. There is no
free-form report builder.

Use the filter bar to choose a period (Today to All time, or a custom range), a comparison period, and filters such as
department, team, agent, channel, priority, customer, product and tier.

### To drill down

Click a KPI, a row or a time bucket. The **Tickets behind this number** drawer lists the tickets. Select one to open it.

### To export

Select **Export** and choose Excel, CSV or PDF. You need `analytics.export`. Exports run in the background and appear
under **Exports** with a **Download** button.

### To schedule a report

You need `analytics.schedule`.

1. Open **Scheduled** and create a schedule.
2. Choose the report, period, format, filters, frequency (Daily, Weekly or Monthly), time and time zone.
3. Add up to 20 e-mail recipients.

The report is built with your data scope and e-mailed as an attachment. Use **Run now** to test it.

---

## 14. Engineering board

Open **Engineering**. The board needs the built-in tracker to be enabled by an administrator.

| Permission | Lets you |
|---|---|
| `engineering.link` | Escalate a ticket or link a work item |
| `engineering.view` | See the board |
| `engineering.manage` | Edit and move work items, publish releases |

The columns are **New** (escalated, not started), **Active**, **Resolved** (fixed, waiting for a release) and
**Closed** (shown with **Show closed**). Drag cards between columns, or use **Move to**.

### To escalate a ticket to engineering

1. Open the ticket's **Engineering** tab.
2. Under **Escalate**, choose the tracker, type, priority and severity. The repro steps are filled from the ticket.
3. Optionally tick **Set the ticket substatus "Engineering"**.
4. Select **Create work item**.

Use **Link existing** to link an item by id or URL (for example `ENG-12`). One work item can cover many tickets.

### Releases

Create a release with a version and planned date. **Publish** it to stamp the fix version on its items, close the
resolved ones and update the linked tickets. Then **Mark deployed**.

When a work item is resolved or released, linked tickets get an internal note and the assignee is notified. If your
administrator enabled it, the customer also gets a "fix released" reply.

External trackers (Azure DevOps, GitHub) send their status back to SupportDesk. Jira is not supported.

---

## 15. Teams & Agents and SLA (supervisors)

### Live monitor

Open **Teams & Agents** › **Monitor**. You need `supervisor.monitor`.

* KPIs: agents online, open tickets, unassigned, SLA at risk, SLA breached and chats waiting.
* Agents table: availability, load bar (open against capacity), in progress, overdue, chats, idle time, and badges for
  on leave, off shift and delegated.
* Queues per team, the 50 riskiest tickets, and waiting chats.
* Change an agent's availability, or **Reassign** an at-risk ticket to a less loaded agent.

### Agents and routing

Each agent has a **Routing profile**: max open tickets (default 25), max concurrent chats (default 3), languages,
skills (1–5) and routing attributes. Teams assign work **Manual**, **Round robin**, **Load balanced** (default),
**Skill based** or by **Territory**. Routing skips agents who are offline, on leave, delegated, off shift (if the team
asks) or at capacity. Use **Test routing** to see who would get a ticket and why.

**Partial:** skill-based routing uses the agent's average skill level, not the skills the ticket needs.

### Leave

1. Open **Shifts & leave** and request leave (Leave, Sick, Training, Public holiday, Other).
2. The request goes to the person you report to, then a department supervisor, then anyone with `approvals.decide`.
3. Approved leave removes the agent from routing. Existing tickets are not reassigned.

### Delegation

Open **Delegations** to set an out-of-office delegate for a date range. While it is active, routing skips the away agent,
manual assignments and approvals go to the delegate, and open tickets can optionally move to the delegate.

### Shifts

Create shift patterns with weekly hours and assign agents. Shifts matter only for teams that **Skip agents who are off
shift**. Agents without a shift count as always on. The calendar tab shows leave, delegations and holidays.

### SLA

Open **SLA** (`g s`). Supervisors can read policies and calendars; changing them needs `admin.sla`.

* **Live breaches** lists at-risk and breached tickets with KPIs (at risk, breached, breached today, due in the next
  hour, paused). Filter by state, milestone and department.
* **Policies** shows targets and escalation levels. **Simulate against a ticket** explains which policy would apply.
* **Calendars & holidays** shows business hours and holidays.

---

## 16. Notifications

The bell shows unread notifications (latest 20). Select one to open it. Some items need **Acknowledge**. Select
**Mark all read** to clear the rest. The full list is at `/notifications`, with filters for unread and
needs-acknowledgement.

| Type | You get it when |
|---|---|
| Assignments | A ticket is assigned to you |
| Mentions | Someone @mentions you |
| Customer replies | A customer replies on your ticket |
| Escalations | A ticket is escalated |
| SLA warnings / breaches | A target is at risk or missed |
| Approval requests / decisions | An approval needs you, or yours was decided |
| Follow-ups | A follow-up reminder is due |
| AI proposals, Anomalies | AI needs a decision, or a spike is found |
| Waiting chats, Chat transfers | A chat waits or is transferred to you |
| System | For example, a webhook was disabled |

Warnings and critical items also appear as toasts and, after you allow it, desktop notifications.

**Not built yet:** a personal notification preferences page. Your administrator sets the defaults for channels, quiet
hours and digests. The API already supports personal overrides.

---

## 17. Customer portal

This section is for **customers** and for agents who help them.

The portal is at `/portal/{brand}/`, for example `https://desk.example.com/portal/wings/`. If you open `/portal/`
without a brand, the page asks for your portal name. Each brand has its own colours, welcome text and features.
Features the brand switches off (knowledge base, community, self-registration) are hidden.

**Partial:** administrators can record a custom domain for a brand, but the portal does not answer on it yet. Use the
`/portal/{brand}/` address.

### To sign in

Open **Sign in** and choose a method.

* **Password**: enter your e-mail and password. After 5 wrong attempts the account locks for 15 minutes. You can still
  sign in with a code.
* **E-mail code**: open the **E-mail code** tab, enter your e-mail and select **Send code**. Enter the 6-digit code
  within 10 minutes. Select **Send a new code** if it did not arrive.

If the brand allows it, select **Create an account** (under "New here?"). Enter your name, work e-mail, optional phone
and a password, then **Create account**. If your e-mail domain belongs to a customer, you are linked to that customer.

**Forgot your password?** Select **Forgot your password? Sign in with an e-mail code**. **Not built yet:** you cannot
replace a forgotten password, because **Account › Password** always asks for the current one. Keep signing in with
e-mail codes.

### Ticket scopes

Support decides how much you see. Ask them to change it.

| Scope | You see |
|---|---|
| Own | The tickets you raised |
| Location | Your tickets and your company's tickets at your location (branch) |
| Organisation | All tickets of your company |
| None | No portal access |

With more than one scope, a switcher appears above the list: **Mine**, **My location**, **My organisation**. The list
shows **Open**, **Awaiting you**, **Closed** and **All**. "Awaiting you" means support replied or asked you to confirm.

Location and organisation users also see **Contracts**, **Products & licences**, **Installations** and **Maintenance**
under **Account**.

### To create a ticket

1. Select **Submit a ticket**.
2. Enter a **Subject**. Matching articles appear as you type. If one solves your problem, select **This solved my
   problem**.
3. Fill in the form: department, category, product, installation, priority and any extra fields your support team asks
   for. Leave priority as **Normal (let support decide)** if you are unsure.
4. Describe the problem and drag files onto the form to attach them.
5. Select **Submit ticket**. The confirmation page links to your new ticket.

### To reply to a ticket

Open the ticket, write your reply, attach files if needed, and select **Send reply** (or press **Ctrl+Enter**).
You only see public messages. A reply to a resolved ticket, or to a closed ticket within 30 days, reopens it. After
that, submit a new ticket.

### To confirm a resolution

When support asks you to confirm, the ticket shows **Is your issue resolved?**

* Select **Yes** to close the ticket. You can add a comment.
* Select **No**, describe **What is still not working?**, and select **Send and keep it open**. The ticket reopens.

### To reopen a ticket

Select **Reopen**, enter a reason, and select **Reopen ticket**. The page shows the last date you can reopen.

### To rate the support you received

On a resolved or closed ticket, **How did we do?** offers 1–5 stars and a comment. Select **Submit rating**. You can
rate each ticket once.

**Partial:** the satisfaction survey e-mail links to a portal page that does not exist yet. Rate the ticket from the
ticket page instead.

### Knowledge base and search

* The home page shows categories, popular and recent articles, announcements and troubleshooting guides.
* Use the search box (at least 2 characters) to find articles and community topics.
* On an article, answer **Was this article helpful?** If not, you can **Submit a ticket**.
* Signed-in users may see more articles than anonymous visitors.

### Community

Anyone can read the community. Sign in to post.

1. Select **Start a topic**. Choose **Question** or **Discussion**, enter a title and details, add up to 5 tags, and
   select **Post topic**. Answer with **Post reply**.
2. Up-vote helpful topics and replies. You cannot vote on your own posts.
3. If you asked a question, **accept** the reply that answered it.

Replies from support staff carry an **Agent** badge. Moderators can hide or close topics.

### Assistant

If the brand enables it, select **Ask** to open the assistant. It answers from the knowledge base and shows numbered
sources. When it cannot help, select **Create a ticket**; your question is copied into the form. The conversation is lost
when you reload the page.

### Troubleshooting guides

Open **Help** and choose a guide. Answer each question. Select **Back** or **Start over** at any time. When a step
solves the problem, say so. If nothing works, select **Create a ticket**; the steps you tried are attached.

### Submitting without an account

If the brand allows anonymous tickets, **Submit a ticket** works without signing in. Enter your name, e-mail, subject and
description, then **Submit ticket**. You get a ticket number and replies by e-mail. Anonymous forms cannot take
attachments.

### Live chat on a website

Your support team can add a chat widget to its website. Enter your name, e-mail and question, then **Start chat**. The
assistant may answer first. Select **Talk to a person** to reach an agent. You can verify your e-mail with a code, end the
chat and rate it.

### Your account

* **Profile**: name, phone, job title, language and time zone. Your sign-in e-mail can only be changed by support.
* **Notifications**: your preferred channel, language and consent for e-mail, WhatsApp and SMS messages.
* **Password**: change your password (you need the current one).

Replies arrive through the ticket's channel. Portal tickets get replies by e-mail. The ticket page and list update live
while you have them open.

---

## 18. Mobile app

The SupportDesk app (Ionic) works for agents and customers. Technical details are in [MOBILE.md](MOBILE.md).

### To sign in

1. On the sign-in screen choose **Agent** or **Customer**.
2. **Agent**: enter your workspace, work e-mail and password, then your verification code.
3. **Customer**: enter the help center name, your e-mail, then a password or **E-mail me a code**.

The app keeps you signed in on this device. There is no SSO, self-registration, password change or app lock in the app.

### Agent mode

| Tab | What you can do |
|---|---|
| Home | Set availability, see queue counts, next SLA deadlines and pending approvals |
| Tickets | Filter (Mine, Unassigned, Overdue, At risk, Escalated, Waiting, Watching, All open), search, swipe to **Assign to me** or **Close** |
| Ticket | See SLA chips, change status, priority and assignee, read the timeline, reply or add a note with templates and photos |
| Alerts | Open notifications and acknowledge critical alerts |
| Approvals | Approve or reject with a note |
| Me | Availability, appearance, unsent messages, sign out |

### Customer mode

| Tab | What you can do |
|---|---|
| My tickets | Switch scope, filter Open, Needs me, Closed, All, and search |
| Request | Reply, confirm a resolution (**Yes, close it** / **No, still a problem**), reopen, rate |
| New request | The same form as the portal, with article suggestions and attachments |
| Help | Search and read articles, and say whether they helped |
| Me | Appearance, unsent messages, sign out |

Customer mode does not include the community, assistant, troubleshooting guides, profile editing or contracts. Use the
web portal for those.

### Working offline

The app keeps a copy of the screens you opened. When the connection drops, a banner explains why and shows how many
messages wait to be sent.

* **Drafts** are saved on the device as you type.
* **Replies, notes and new requests** you send offline are queued. They are sent automatically when the connection
  returns, with their attachments. A retry never creates a duplicate.
* If a colleague replied first, an agent reply is held as a **conflict**. Choose **Send anyway**, **Edit** or
  **Discard**.
* If the server rejects a queued message (for example, the ticket is closed), you can retry, edit or discard it.
* Status, assignment, approvals and ratings need a connection.

**Signing out deletes unsent messages on this device.** The app warns you first; send or discard them before you sign
out.

### Push notifications

**Not built yet in the standard app.** The **Me** screen shows "Not available in this build". Updates arrive live while
the app is open. Your organisation can enable native push by following [MOBILE.md](MOBILE.md).
