# UI inventory: C:\Users\sudhe\SupportDesk

Generated 2026-11-02T08:34:29.893Z. Heuristic scan; confirm everything in the running app.

## Summary

- Web routes: 233, navigation labels: 153, screens: 331
- Desktop projects: 0, forms or windows: 0

## How to run

- src/SupportDesk.Api/Properties/launchSettings.json: http://localhost:5830

## Web screens

### Navigation labels

- Start a free trial → `/signup`
- Platform console → `/platform/login`
- Channels → `/admin/channels`
- Automation & blueprints → `/automation`
- AI → `/admin/ai`
- Webhooks → `/admin/webhooks`
- Wings ERP & Books → `/products/erp`
- Engineering → `/admin/engineering`
- Teams & Slack → `/admin/collaboration`
- Departments & teams → `/teams/departments`
- Agents → `/teams/agents`
- Shifts, leave & delegation → `/teams/shifts`
- SLA policies → `/sla/policies`
- Business hours & holidays → `/sla/calendars`
- Audit log → `/admin/audit`
- Compliance → `/admin/compliance`
- Configuration transfer → `/admin/config`
- Subscription & billing → `/subscription`
- Roles & permissions → `/admin/roles`
- Ticket configuration → `/admin/ticket-config`
- Fields & form layouts → `/admin/ticket-config/fields`
- Brands & portals → `/admin/brands`
- Notifications & e-mail templates → `/admin/notifications`
- Security & sign-in → `/admin/security`
- API keys & OAuth clients → `/admin/api-credentials`
… 128 more

### Routes

| Path | Title | Component | Guards / roles |
|---|---|---|---|
| `/login` | Sign in | LoginPage | guestGuard |
| `/signup` | Start your free trial | SignupPage |  |
| `/platform` |  | ./features/platform/platform.routes |  |
| `/accept-invite` | Accept invitation | AcceptInvitePage | guestGuard |
| `/design-system` | Design system | DesignSystemPage | () => isDevMode() |
| `/` |  | ShellComponent | authGuard |
| `/` |  |  | landingGuard |
| `/command` |  | ./features/command/command.routes | perms('command') |
| `/tickets` |  | ./features/tickets/tickets.routes | perms('tickets') |
| `/inbox` |  | ./features/inbox/inbox.routes | perms('inbox') |
| `/customers` |  | ./features/customers/customers.routes | perms('customers') |
| `/products` |  | ./features/products/products.routes | perms('products') |
| `/teams` |  | ./features/teams/teams.routes | perms('teams') |
| `/sla` |  | ./features/sla/sla.routes | perms('sla') |
| `/automation` |  | ./features/automation/automation.routes | perms('automation') |
| `/kb` |  | ./features/kb/kb.routes | perms('kb') |
| `/analytics` |  | ./features/analytics/analytics.routes | perms('analytics') |
| `/ai` |  | ./features/ai/ai.routes | perms('ai') |
| `/engineering` |  | ./features/engineering/engineering.routes | perms('engineering') |
| `/admin` |  | ./features/admin/admin.routes | perms('admin') |
| `/subscription` |  | ./features/subscription/subscription.routes | perms('subscription') |
| `/approvals` | Approvals | ApprovalsPage |  |
| `/notifications` | Notifications | NotificationsPage |  |
| `/me` | My profile | ProfilePage |  |
| `/forbidden` | Access denied | ForbiddenPage |  |
| `/**` | Not found | NotFoundPage |  |
| `/` | Administration | AdminHomePage |  |
| `/channels` |  | ./channels/channels.routes | permissionGuard('admin.channels') |
| `/webhooks` |  | ./webhooks/webhooks.routes | permissionGuard('admin.integrations') |
| `/ai` |  | ./ai/admin-ai.routes | permissionGuard('admin.ai') |
| `/engineering` |  | ./engineering/engineering-admin.routes | permissionGuard('admin.integrations') |
| `/collaboration` |  | ./collaboration/collaboration.routes | permissionGuard('admin.integrations') |
| `/roles` |  | ./roles/roles.routes | permissionGuard('admin.roles') |
| `/ticket-config` |  | ./ticket-config/ticket-config.routes | permissionGuard('admin.ticket_config') |
| `/brands` |  | ./brands/brands.routes | permissionGuard('admin.organisation') |
| `/notifications` |  | ./notifications/notifications.routes | permissionGuard('admin.organisation', 'admin.channels') |
| `/security` |  | ./security/security.routes | permissionGuard('admin.security') |
| `/api-credentials` |  | ./api-credentials/api-credentials.routes | permissionGuard('admin.integrations') |
| `/audit` |  | ./audit/audit.routes | permissionGuard('admin.audit') |
| `/compliance` |  | ./compliance/compliance.routes | permissionGuard('admin.compliance') |
| `/config` |  | ./config-transfer/config-transfer.routes | permissionGuard('admin.config_transfer') |
| `/` |  | AdminAiLayoutPage |  |
| `/` |  | settings |  |
| `/settings` | AI provider & budget | AiSettingsPage |  |
| `/features` | AI features | AiFeaturesPage |  |
| `/scenarios` | Auto-reply scenarios | AiScenariosPage |  |
| `/prompts` | AI prompt templates | AiPromptsPage |  |
| `/agents` | AI agents | AiAgentsPage |  |
| `/usage` | AI usage & cost | AiUsagePage |  |
| `/executions` | AI execution log | AiExecutionsPage |  |
| `/` | API keys & OAuth clients | ApiCredentialsPage |  |
| `/` | Audit log | AuditPage |  |
| `/` | Brands & portals | BrandsListPage |  |
| `/new` | New brand | BrandEditorPage |  |
| `/:id` | Brand | BrandEditorPage |  |
| `/` | Channels | ChannelsListPage |  |
| `/new` | New channel | ChannelEditorPage |  |
| `/simulator` | Channel simulator | ChannelSimulatorPage |  |
| `/:id` | Channel | ChannelEditorPage |  |
| `/` | Teams & Slack | CollaborationPage |  |
| `/` | Compliance | CompliancePage |  |
| `/` | Configuration transfer | ConfigTransferPage |  |
| `/` | Engineering integration | EngineeringAdminPage |  |
| `/defaults` |  |  |  |
| `/templates` |  |  |  |
| `/` |  | NotificationsLayoutPage |  |
| `/` |  | defaults |  |
| `/defaults` | Notification defaults | NotificationDefaultsPage |  |
| `/templates` | E-mail templates | TemplatesPage |  |
| `/templates/:key` | E-mail template | TemplateEditorPage |  |
| `/` | Roles & permissions | RolesListPage |  |
| `/new` | New role | RoleEditorPage |  |
| `/:id` | Role | RoleEditorPage |  |
| `/policies` |  |  |  |
| `/sso` |  |  |  |
| `/sessions` |  |  |  |
| `/` |  | SecurityLayoutPage |  |
| `/` |  | policies |  |
| `/policies` | Security policies | SecurityPoliciesPage |  |
| `/sso` | Single sign-on | SsoPage |  |

### Screens (components with a template)

#### HelpPage (route: /help, /, /)
`clients/projects/mobile/src/app/customer/help.page.ts`

- **Fields:** Search the help center

#### HelpPage (route: /help, /, /)
`clients/projects/portal/src/app/features/help/help.page.ts`

- **Headings:** Help & contact · Guided troubleshooting · Other ways to get help · Contact us

#### LoginPage (route: /login, /login)
`clients/projects/desk/src/app/auth/login.page.ts`

- **Headings:** Every customer conversation, one calm workspace. · Sign in to SupportDesk · Two-step verification · Password expired
- **Buttons:** Sign in · Verify · Use a different account · Back to sign in

#### BrandEditorPage (route: /new, /:id)
`clients/projects/desk/src/app/features/admin/brands/brand-editor.page.ts`

- **Headings:** Identity · Logo & favicon · Custom domain · Portal · Contact & footers
- **Buttons:** Remove · Check DNS · Discard
- **Fields:** support.example.com · Add language code
- **Columns:** Type · Name · Points to
- **Messages:** The image is too large · Upload failed · Could not remove the image · DNS check failed

#### ChannelEditorPage (route: /new, /:id)
`clients/projects/desk/src/app/features/admin/channels/channel-editor.page.ts`

- **Buttons:** Test connection · Poll now · Copy URL · Rotate key · Copy embed code · Delete channel
- **Fields:** Support mailbox
- **Messages:** Test failed · Webhook key rotated · Copy failed

#### RoleEditorPage (route: /new, /:id)
`clients/projects/desk/src/app/features/admin/roles/role-editor.page.ts`

- **Headings:** General · Data scope · Permissions · Members ( )
- **Buttons:** Clone to customise · Add · Remove · Discard
- **Fields:** Tickets · Customers · Filter permissions · Add a user to this role…
- **Messages:** Role cloned · Could not clone the role · Member added · Could not add the member · Member removed · Could not remove the member

#### CategoriesPage (route: /categories, /categories)
`clients/projects/desk/src/app/features/admin/ticket-config/categories.page.ts`

- **Headings:** Categories · Issue types
- **Buttons:** Try again · Category · Sub · Issue type · Delete · Cancel · Save
- **Messages:** Category saved · Category deleted · Could not reorder · Issue type saved · Issue type deleted

#### SearchPage (route: /search, /)
`clients/projects/desk/src/app/features/ai/search/search.page.ts`

- **Buttons:** Search
- **Fields:** Search by meaning · Describe the problem in your own words…

#### AnalyticsDashboardsPage (route: /dashboards, /dashboards/:id)
`clients/projects/desk/src/app/features/analytics/dashboards.page.ts`

- **Buttons:** Retry · New dashboard · Add widget · Cancel · Save layout · Edit layout · W− · = 12">W+ · H− · = 8">H+ · Refresh widgets
- **Fields:** Dashboard
- **Messages:** Dashboard saved · Personal copy created · Dashboard deleted · Could not delete the dashboard

#### BlueprintDesignerPage (route: /blueprints/new, /blueprints/:id)
`clients/projects/desk/src/app/features/automation/blueprints/blueprint-designer.page.ts`

- **Headings:** State · Transition
- **Buttons:** Check · Save draft · Publish · Add state · Delete selected · Add transition from here · Delete state · 0" (click)="remove(d)">Delete blueprint
- **Fields:** All departments · Any category · Keep current status
- **Messages:** From any state: · Key already used · Published v$ d.blueprint.publishedVersionNumber · Version $ v.versionNumber loaded · Blueprint is valid · Could not delete

#### RuleDesignerPage (route: /rules/new, /rules/:id)
`clients/projects/desk/src/app/features/automation/rules/rule-designer.page.ts`

- **Buttons:** Check · Save draft · Publish · New secret · Copy · Add payload field · Delete rule
- **Fields:** Change note (optional) · Any field · Payload field, e.g. event or order.total
- **Messages:** Version $ v.versionNumber loaded · Published v$ d.rule.publishedVersionNumber · Rolled back — now live as v$ d.rule.publishedVersionNumber · Looks good · Copied · Rule deleted

#### ArticleEditorPage (route: /articles/new, /articles/:id)
`clients/projects/desk/src/app/features/kb/articles/article-editor.page.ts`

- **Buttons:** Try again · Retry · Submit for review · Approve · Reject · Publish · Reopen as draft
- **Fields:** e.g. How to reset your password · Write the article — paste or drop images to upload them. · What changed and why?
- **Messages:** Image upload failed · Draft created from the ticket · Check the highlighted fields · Draft created · Article saved · Could not $ ACTION_VERB[action] · Article deleted · Could not delete the article

#### CategoriesPage (route: /categories, /categories)
`clients/projects/desk/src/app/features/kb/categories/categories.page.ts`

- **Headings:** Categories
- **Buttons:** Refresh · New category · Retry · Clear filter
- **Fields:** Filter · Category name or slug
- **Columns:** Category · Visibility · Brands · Articles · Order · Actions
- **Messages:** Category deleted · Could not delete “$ c.name ”

#### FlowDesignerPage (route: /flows/new, /flows/:id)
`clients/projects/desk/src/app/features/kb/flows/flow-designer.page.ts`

- **Buttons:** Retry · Design · Preview · Unpublish · Publish
- **Fields:** Go to step · e.g. Printer will not print · Any product · Uncategorised
- **Messages:** New $ type step added · Check the highlighted fields · Flow created · Flow saved · Could not save the flow · Could not $ action the flow · Flow deleted · Could not delete the flow

#### PolicyEditorPage (route: /policies/new, /policies/:id)
`clients/projects/desk/src/app/features/sla/policy-editor.page.ts`

- **Buttons:** Delete · Save policy · Try again · Add level · Remove · Simulate
- **Fields:** None · Also notify… · e.g. 1042
- **Columns:** Priority · First response · Next response · Resolution · Business hours · Warn at · Milestone · Target · Starts · Due · Current
- **Messages:** No escalations — breaches are recorded and shown on the risk boards only. · SLA policy saved · Policy deleted · Could not delete

#### NewTicketPage (route: /new, /new)
`clients/projects/desk/src/app/features/tickets/new/new-ticket.page.ts`

- **Headings:** created · Support eligibility
- **Buttons:** Create another · Open ticket · Attach files · Create ticket
- **Fields:** Search customers… · Search contacts… · What does the customer need? Steps, error messages, impact… · Add tag
- **Messages:** $ t.displayNumber created

#### NewTicketPage (route: /new, /new)
`clients/projects/mobile/src/app/customer/new-ticket.page.ts`

- **Fields:** What happened? What did you expect? Steps, error messages…
- **Messages:** Request $ sent.displayNumber ?? · Saved on this device — it will be submitted when you are back online.

#### SearchPage (route: /search, /)
`clients/projects/portal/src/app/features/search/search.page.ts`

- **Headings:** Articles · Community

#### DesignSystemPage (route: /design-system)
`clients/projects/desk/src/app/dev/design-system.page.ts`

- **Buttons:** Toggle theme · Command palette · Primary · Secondary · Subtle · Ghost · Danger · Link · Small · Saving · Disabled · Success toast · Error toast · Confirm dialog · Drawer · Clear filters · Settings
- **Fields:** Unassigned · Write a reply… type @ to mention
- **Messages:** Menu: · Drill into open tickets · Saved · Could not send · Open · Mod+Enter submit · Drill:

#### AdminHomePage (route: /)
`clients/projects/desk/src/app/features/admin/admin-home.page.ts`

- **Fields:** Find a setting — e.g. roles, SSO, statuses, templates
- **Messages:** No administration area matches “ ”. · Your role has no administration areas.

#### AiAgentsPage (route: /agents)
`clients/projects/desk/src/app/features/admin/ai/agents.page.ts`

- **Buttons:** New agent · Edit
- **Columns:** Agent · Kind · Mode · Scope · Runs · Enabled · Actions
- **Messages:** Saved agent “$ saved.name ” · $ saved.name $ saved.isEnabled ? · The agent was not updated · Deleted $ a.name

#### AiExecutionsPage (route: /executions)
`clients/projects/desk/src/app/features/admin/ai/executions.page.ts`

- **Headings:** Prompt-injection checks · Error · Citations · Output
- **Fields:** All features · Any status · Any outcome

#### AiFeaturesPage (route: /features)
`clients/projects/desk/src/app/features/admin/ai/features.page.ts`

- **Buttons:** Save
- **Fields:** All roles
- **Columns:** Feature · Enabled · Limit to roles · Min. confidence · Auto-apply
- **Messages:** Some features need attention

#### AiPromptsPage (route: /prompts)
`clients/projects/desk/src/app/features/admin/ai/prompts.page.ts`

- **Buttons:** Reset to default · Start from default · Save as version · Activate · Load
- **Messages:** Saved $ humanize(t.feature) as version $ updated.activeVersion · The template was not saved · Built-in default activated

#### AiScenariosPage (route: /scenarios)
`clients/projects/desk/src/app/features/admin/ai/scenarios.page.ts`

- **Buttons:** Add scenario · Save scenarios
- **Fields:** Password reset how-to · password_reset_howto · Add a phrase… · Any channel · Any category · Article id…
- **Messages:** Every scenario needs a name · Some scenarios need attention

#### AiSettingsPage (route: /settings)
`clients/projects/desk/src/app/features/admin/ai/settings.page.ts`

- **Buttons:** Test saved provider · Reindex knowledge now · Discard · Save settings
- **Fields:** e.g. gpt-4o-mini · https://… · e.g. text-embedding-3-small
- **Messages:** Check the highlighted fields · Knowledge index updated

#### AiUsagePage (route: /usage)
`clients/projects/desk/src/app/features/admin/ai/usage.page.ts`

- **Buttons:** Refresh
- **Fields:** History
- **Columns:** Feature · Calls · Accepted · Applied · Rejected · Reverted · Failed · Use rate

#### ApiCredentialsPage (route: /)
`clients/projects/desk/src/app/features/admin/api-credentials/api-credentials.page.ts`

- **Buttons:** Try again · Create an API key · Revoke · Create an OAuth client · Rotate secret · Cancel · Dismiss
- **Fields:** Scopes · No user (scopes only)
- **Messages:** API key created · OAuth client created · API key revoked · Could not revoke the key · Could not rotate the secret · OAuth client revoked · Could not revoke the client · Copied

#### AuditPage (route: /)
`clients/projects/desk/src/app/features/admin/audit/audit.page.ts`

- **Headings:** Changes · Raw record
- **Buttons:** Export CSV · Verify chain · Open entry # · Apply · Clear · Try again · Load more
- **Fields:** Name or id · GUID · e.g. 10.0.
- **Columns:** When · Actor · Action · Entity · Summary · IP · Field · Before · After
- **Messages:** Entry could not be loaded · Export failed

#### BrandsListPage (route: /)
`clients/projects/desk/src/app/features/admin/brands/brands-list.page.ts`

- **Buttons:** Try again
- **Messages:** Default brand changed · Could not change the default brand · Brand deleted · Could not delete the brand

#### ChannelSimulatorPage (route: /simulator)
`clients/projects/desk/src/app/features/admin/channels/channel-simulator.page.ts`

- **Headings:** Inbound e-mail to · Call event on · Inbound message to
- **Buttons:** Deliver e-mail · New call · Send call event · Deliver message · Refresh
- **Fields:** customer@example.com · Invoice total is wrong · abc@mail.example.com · (generated) · https://…/recording.mp3
- **Messages:** File too large · From and subject are required · Sender and text (or media) are required · The caller number is required

#### ChannelsListPage (route: /)
`clients/projects/desk/src/app/features/admin/channels/channels-list.page.ts`

- **Buttons:** Retry · Test · Poll
- **Messages:** $ c.name : connection OK · $ c.name : connection failed · Test failed · $ c.name : polling failed · $ c.name : $ r.fetched fetched

#### CollaborationPage (route: /)
`clients/projects/desk/src/app/features/admin/collaboration/collaboration.page.ts`

- **Buttons:** Add channel · Retry · Add a channel · Send test · Cancel
- **Fields:** Post these events · Departments (none selected = all)
- **Messages:** Could not change the channel · Test message posted · Test failed · Channel deleted · Could not delete

#### ConfigTransferPage (route: /)
`clients/projects/desk/src/app/features/admin/config-transfer/config-transfer.page.ts`

- **Headings:** Drafts · Snapshots
- **Buttons:** All sections · Export · Review · Capture snapshot · Roll back · Discard draft · Save choices · Publish
- **Fields:** Hide unchanged
- **Columns:** Version · Label · Kind · Captured · Items · Actions · Field · This workspace · File
- **Messages:** Export failed · Published as v$ r.snapshot.version · Some references were not found · Snapshot v$ s.version captured · Rolled back — now v$ r.snapshot.version · Rollback failed

#### EngineeringAdminPage (route: /)
`clients/projects/desk/src/app/features/admin/engineering/engineering-admin.page.ts`

- **Headings:** Trackers · Sync-back rules
- **Buttons:** Copy · Retry · Enable the built-in board · Test · Sync now · Edit · Save rules · Dismiss the secret
- **Messages:** Tracker saved · Tracker deleted · Could not delete · Connection OK · Connection problem · Test failed · Sync stopped · Sync finished · Sync failed · Sync-back rules saved · Could not save · Copied

#### NotificationDefaultsPage (route: /defaults)
`clients/projects/desk/src/app/features/admin/notifications/notification-defaults.page.ts`

- **Headings:** Default channels per event · Quiet hours & digests
- **Buttons:** Try again · Discard · Save defaults
- **Columns:** Event
- **Messages:** Notification defaults saved

#### TemplateEditorPage (route: /templates/:key)
`clients/projects/desk/src/app/features/admin/notifications/template-editor.page.ts`

- **Headings:** Preview
- **Buttons:** Reset to default · Discard · Save template
- **Messages:** Template saved · Template reset to the default · Could not reset the template

#### TemplatesPage (route: /templates)
`clients/projects/desk/src/app/features/admin/notifications/templates.page.ts`

- **Buttons:** Try again

#### RolesListPage (route: /)
`clients/projects/desk/src/app/features/admin/roles/roles-list.page.ts`

- **Buttons:** Try again · Cancel · Clone role · Delete role
- **Fields:** Filter roles
- **Messages:** Role cloned · Role deleted · Could not delete the role

#### SecurityPoliciesPage (route: /policies)
`clients/projects/desk/src/app/features/admin/security/security-policies.page.ts`

- **Headings:** Multi-factor authentication · Passwords · Sessions · Agent IP allow-list
- **Buttons:** Try again · Add my address · Discard · Save policies
- **Messages:** Security policies saved

#### SessionsPage (route: /sessions)
`clients/projects/desk/src/app/features/admin/security/sessions.page.ts`

- **Buttons:** Sign this user out everywhere · Refresh · Try again · Revoke · Load more
- **Fields:** All users
- **Messages:** Session revoked · Could not revoke the session · Signed out everywhere · Could not sign the user out

#### SsoPage (route: /sso)
`clients/projects/desk/src/app/features/admin/security/sso.page.ts`

- **Headings:** Identity provider · Claims & provisioning
- **Buttons:** Try again · Test connection · Discard · Save SSO settings · Copy the redirect URI · Copy the sign-in URL
- **Messages:** The test could not run · SSO settings saved · Copied

#### FieldsPage (route: /fields)
`clients/projects/desk/src/app/features/admin/ticket-config/fields.page.ts`

- **Buttons:** fields · New field · Try again · Add a field · Edit · Add option · Delete · Cancel
- **Fields:** Options · Visibility · Required · Value · Label · All departments
- **Tabs:** fields
- **Messages:** Field deleted · Could not reorder

#### LayoutBuilderPage (route: /layouts/:id)
`clients/projects/desk/src/app/features/admin/ticket-config/layout-builder.page.ts`

- **Headings:** Available fields
- **Buttons:** Design · Preview · Add section · Discard
- **Fields:** Required · @if (f.required || alwaysRequired(f.key)) * @switch (controlKind(f.key)) · Filter fields · Section title
- **Messages:** Drag fields here, or use + in the field list.

#### LayoutsPage (route: /layouts)
`clients/projects/desk/src/app/features/admin/ticket-config/layouts.page.ts`

- **Buttons:** Try again
- **Messages:** Layout deleted · Could not delete the layout

#### StatusesPage (route: /statuses)
`clients/projects/desk/src/app/features/admin/ticket-config/statuses.page.ts`

- **Buttons:** New status · Try again · Edit · Delete · Cancel
- **Fields:** Allowed next statuses · Add substatus
- **Messages:** Status deleted · Could not reorder statuses

#### TicketSettingsPage (route: /settings)
`clients/projects/desk/src/app/features/admin/ticket-config/ticket-settings.page.ts`

- **Headings:** Ticket numbering · Lifecycle · Merge & split · Satisfaction surveys · Priorities & severities
- **Buttons:** Try again · Discard · Save settings
- **Messages:** Ticket settings saved

#### WebhooksPage (route: /)
`clients/projects/desk/src/app/features/admin/webhooks/webhooks.page.ts`

- **Buttons:** New webhook · Copy · Add a webhook · Deliveries · Cancel · Refresh · Send test ping · Redeliver · Previous · = page.total" (click)="loadDeliveries(page.page + 1)">Next · Dismiss
- **Fields:** Events · https://erp.example.com/hooks/supportdesk
- **Messages:** Webhook deleted · Ping delivered · Ping failed · Queued for redelivery · Could not redeliver · Copied

#### AnomaliesPage (route: /anomalies)
`clients/projects/desk/src/app/features/ai/anomalies/anomalies.page.ts`

- **Buttons:** Refresh · Tickets · Acknowledge · Dismiss
- **Fields:** Any status
- **Columns:** Day · What · Tickets · Typical · z-score · Status · Actions
- **Messages:** $ status : $ updated.dimensionLabel

#### AskPage (route: /ask)
`clients/projects/desk/src/app/features/ai/ask/ask.page.ts`

- **Buttons:** Ask · structured intent
- **Fields:** Question
- **Messages:** Click a bar or row to open the matching tickets.

#### ProposalsPage (route: /proposals)
`clients/projects/desk/src/app/features/ai/proposals/proposals.page.ts`

- **Buttons:** Refresh
- **Fields:** Any status · Any risk · Any action · Ticket, subject, agent or rationale

#### TicketAiPage (route: /tickets/:id)
`clients/projects/desk/src/app/features/ai/ticket/ticket-ai.page.ts`

- **Buttons:** Open · Send reply
- **Fields:** Open ticket number · Ticket # · Write a reply, or insert an AI draft…
- **Messages:** Ticket $ n was not found · Reply sent · The reply was not sent

#### AnalyticsExportsPage (route: /exports)
`clients/projects/desk/src/app/features/analytics/exports.page.ts`

- **Buttons:** Refresh · Retry · Download
- **Columns:** Report · Format · Status · Requested · Rows · Size · Download
- **Messages:** Download failed

#### AnalyticsReportLibraryPage (route: /reports)
`clients/projects/desk/src/app/features/analytics/report-library.page.ts`

- **Buttons:** Retry

#### AnalyticsReportPage (route: /reports/:key)
`clients/projects/desk/src/app/features/analytics/report.page.ts`

- **Buttons:** Schedule · Retry · Refresh report
- **Messages:** Report scheduled

#### AnalyticsSchedulesPage (route: /schedules)
`clients/projects/desk/src/app/features/analytics/schedules.page.ts`

- **Buttons:** New schedule · Retry · History
- **Messages:** Report queued · Could not run the report · Could not update the schedule · Schedule deleted

#### AutomationLayoutComponent (route: /)
`clients/projects/desk/src/app/features/automation/automation-layout.component.ts`

- **Buttons:** Retry

#### BlueprintsListPage (route: /blueprints)
`clients/projects/desk/src/app/features/automation/blueprints/blueprints-list.page.ts`

- **Buttons:** Open ticket blueprint
- **Fields:** Ticket #
- **Messages:** Could not change the blueprint

#### ExecutionsPage (route: /executions)
`clients/projects/desk/src/app/features/automation/executions/executions.page.ts`

- **Headings:** Conditions · Actions
- **Buttons:** Apply · Clear · Details · Previous · = p.total" (click)="search(p.page + 1)">Next · Retry failed actions · Refresh
- **Fields:** Ticket #
- **Columns:** When · Rule · Trigger · Ticket · Status · Actions · Depth · Details · Field · Check · Value · Result
- **Messages:** Ticket not found · Retried · Retry failed

#### RulesListPage (route: /rules)
`clients/projects/desk/src/app/features/automation/rules/rules-list.page.ts`

- **Fields:** Search rules
- **Messages:** Could not change the rule

## Sample data and demo logins (read these for screenshot data)

- `src/SupportDesk.Application/Administration/AdministrationDemoSeed.cs`
- `src/SupportDesk.Application/Ai/AiDemoSeedContributor.cs`
- `src/SupportDesk.Application/Analytics/AnalyticsDemoSeed.cs`
- `src/SupportDesk.Application/Automation/AutomationDemoSeed.cs`
- `src/SupportDesk.Application/Communication/CommunicationDemoSeed.cs`
- `src/SupportDesk.Application/Customers/CustomersDemoSeed.cs`
- `src/SupportDesk.Application/Integrations/Engineering/EngineeringDemoSeed.cs`
- `src/SupportDesk.Application/Knowledge/KnowledgeDemoSeed.cs`
- `src/SupportDesk.Application/Organisation/OrganisationDemoSeed.cs`
- `src/SupportDesk.Application/Seed/DemoSeeder.cs`
- `src/SupportDesk.Application/Subscriptions/SubscriptionsDemoSeed.cs`
- `tests/SupportDesk.E2ETests/Infrastructure/DeskServerFixture.cs`

## Existing documentation

- `clients/CONVENTIONS.md` SupportDesk clients — conventions
- `clients/projects/desk/src/app/features/tickets/extensions/README.md` Ticket view extension points
- `docs/ADMIN-GUIDE.md` SupportDesk administration guide
- `docs/API.md` SupportDesk API reference for integrators
- `docs/ARCHITECTURE.md` SupportDesk — Solution Architecture
- `docs/DEPLOYMENT.md` SupportDesk — Deployment Guide
- `docs/DEVELOPMENT.md` SupportDesk — Development Guide & Conventions
- `docs/FUNCTIONAL-SPEC.md` SupportDesk — Functional Specification
- `docs/integrations/ENGINEERING.md` Engineering escalation (Azure DevOps, GitHub, built-in tracker) and Teams / Slack
- `docs/integrations/WINGS-ERP.md` Wings ERP / Wings Books integration
- `docs/METRICS.md` SupportDesk — Metric definitions
- `docs/MOBILE.md` SupportDesk mobile app
- `docs/PERFORMANCE.md` SupportDesk — Performance
- `docs/SECURITY.md` SupportDesk — Security & Compliance
- `docs/USER-GUIDE.md` SupportDesk user guide
- `README.md` SupportDesk
