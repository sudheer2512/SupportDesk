# SupportDesk user manual — plan

One manual for everyone who uses SupportDesk. Each chapter says who it is for.
Formats: HTML (online help with search), PDF and Word. Brand: Wings. Language: English, A4.

## Sources

- The running app (the screen wins when it disagrees with anything else).
- Angular templates: `clients/projects/desk/src/app` (desk, admin, platform console), `clients/projects/portal/src/app`
  (customer portal), `clients/projects/mobile/src/app` (mobile app), shared UI in `clients/projects/core/src/lib`.
- Existing guides with verified behaviour (long sentences — rewrite them short): `docs/USER-GUIDE.md`,
  `docs/ADMIN-GUIDE.md`, `docs/MOBILE.md`, `docs/FUNCTIONAL-SPEC.md`.

## Folder layout

| Path | What |
|---|---|
| `manual.base.json` | Title, audience, support, brand, version history. |
| `parts/NN-slug.json` | One chapter per file: `{ "chapters": [ … ], "troubleshooting": [ … ], "faq": [ … ], "glossary": [ … ] }`. |
| `shotlists/NN-slug.json` | Web shot lists (`outDir` is `../shots`). One or more per chapter. |
| `shots/NN-slug.png` | Screenshots. `NN` is the chapter number. |
| `assemble.mjs` | Joins `manual.base.json` and `parts/*.json` into `manual.json`. |
| `manual.json` | Generated. Do not edit by hand. |
| `dist/` | Built HTML, PDF and DOCX. |

## Chapters

| NN | Chapter | Readers | Owner |
|---|---|---|---|
| 01 | Introduction | Everyone | lead |
| 02 | Getting started | Desk users | A |
| 03 | Command Center | Agents, supervisors | A |
| 04 | Ticket views and the ticket list | Agents | B |
| 05 | Working on a ticket | Agents | B |
| 06 | Replies, notes and templates | Agents | B |
| 07 | Inbox and live chat | Agents | C |
| 08 | Customers | Agents | C |
| 09 | Products, assets and contracts | Agents | C |
| 10 | Knowledge base | Agents, authors, reviewers | D |
| 11 | AI Assistant | Agents | D |
| 12 | Analytics | Supervisors, managers | D |
| 13 | Engineering board | Agents, engineers | D |
| 14 | Notifications | Desk users | A |
| 15 | Teams, agents and routing | Supervisors, administrators | E1 |
| 16 | SLA policies and business hours | Supervisors, administrators | E1 |
| 17 | Administration, roles and permissions | Administrators | E1 |
| 18 | Ticket configuration | Administrators | E1 |
| 19 | Channels | Administrators | E2 |
| 20 | Automation, blueprints and webhooks | Administrators | E2 |
| 21 | AI and knowledge settings | Administrators | E2 |
| 22 | Brands and portals | Administrators | E2 |
| 23 | Notification settings and e-mail templates | Administrators | E2 |
| 24 | Security, sign-in and API access | Administrators | F |
| 25 | Integrations | Administrators | F |
| 26 | Audit log and compliance | Administrators | F |
| 27 | Configuration transfer | Administrators | F |
| 28 | Subscription, billing and free trial | Administrators | F |
| 29 | Customer portal | Customers | G |
| 30 | Mobile app | Agents, customers | G |
| 31 | Platform console | Platform operators | H |

Generated at the end by the build: About this manual, Troubleshooting, Frequently asked questions, Glossary, Document history.

## Running the app for screenshots

- Desk, portal and platform console: launch configuration `supportdesk-manual` (Development, port 5830, own LocalDB database
  `SupportDesk_Manual` seeded with demo data dated "today", raised sign-in rate limits). Desk `http://localhost:5830/`,
  portal `http://localhost:5830/portal/wings/`, platform console `http://localhost:5830/platform/login`.
- Mobile app: launch configuration `supportdesk-mobile` (`ng serve mobile`, port 4832, talks to 5830).

## Demo personas (passwords only through environment variables)

| Persona | Sign-in | Password variable |
|---|---|---|
| Administrator Asha Menon (two-step code shown on screen in the demo) | workspace `wings`, `admin@wings.demo` | `SD_DESK_PASSWORD` |
| Supervisor Priya Raman (no two-step code) | workspace `wings`, `priya@wings.demo` | `SD_DESK_PASSWORD` |
| Agent Arjun Nair (no two-step code) | workspace `wings`, `arjun@wings.demo` | `SD_DESK_PASSWORD` |
| Other agents | `meera`, `karthik`, `rahul`, `divya`, `sana`, `vikram`, `neha`, `dev`, `lakshmi`, `anil` `@wings.demo` | `SD_DESK_PASSWORD` |
| Customer Ramesh Kumar (portal, brand `wings`) | `ramesh@abcretail.demo` | `SD_PORTAL_PASSWORD` |
| Platform operator | `platform@supportdesk.demo` | `SD_PLATFORM_PASSWORD` |

The values are the demo passwords in `docs/DEVELOPMENT.md`. Never write them into the manual, a shot list or a part file.
The administrator sign-in with the two-step code is in `shotlists/01-intro.json` (`setup`); copy it.

## Capture rules

- Viewport `1280 × 800`, `scale: 1.5` (desk, portal, platform). Mobile: `390 × 844`, `scale: 2`, `"mobile": true`.
- `hide: ["sd-toast-outlet"]` on desk shot lists.
- Shot ids and files start with the chapter number: id `05-ticket-layout`, file `05-ticket-layout.png`.
- Helpers capture in parallel against one demo database. **Never save, submit, send, assign, merge, delete or change
  anything.** Open forms, menus and dialogs, fill sample values if that helps, capture, then cancel or press Escape.
- Prefer `clip` for dialogs, panels and forms; keep full-page shots for chapter tours with numbered highlights.
- Read each PNG after capture and fix wrong, half-loaded or cut-off shots (`--only <id>`).

## Writing rules (summary — read the skill's `references/writing-style.md` and `references/manual-format.md`)

- Sentences of 20 words or fewer; never more than 35. One idea per sentence. Present tense, active voice, "you".
- Task titles are imperative ("Reply to a customer"). One action per step. UI labels in **bold**, exactly as on screen.
- No code words: no API, JSON, GUID, database, endpoint, server, query string. Say "the app" or "SupportDesk".
- Every chapter `intro` starts by saying who the chapter is for.
- Add `troubleshooting` rows from the area's error messages, a few `faq` entries, and `glossary` terms.

## Checking a chapter while writing

```bash
node assemble.mjs --only 04,05,06 --out _check-b.json
node <skill>/scripts/check-prose.mjs _check-b.json
```

## Refreshing the whole manual after UI changes

Start `supportdesk-manual` (and `supportdesk-mobile`), set the three password variables, then:

```bash
node refresh.mjs            # re-captures every shot list, assembles, checks and builds
```
