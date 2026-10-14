# Ticket view extension points

Other desk modules (automation blueprints, AI assistant, engineering) add UI to the ticket view **without editing
the tickets module's components**: append an entry to one of the two arrays below and keep the implementation in
your own feature folder.

## `ticket-panels.ts` → `TICKET_PANELS`

Side-panel tabs next to Properties / Tasks / Time / Watchers / Follow-ups / Related.

| Field | Meaning |
| --- | --- |
| `key` | Unique id (`<module>.<panel>`); also remembers the selected tab. |
| `label`, `icon` | Tab text and `@sd/ui` icon name. |
| `permission?` | Permission string or list (any of). Hidden otherwise — the API still enforces access. |
| `feature?` | Plan feature flag (`MeStore.hasFeature`). |
| `order?` | Position after the built-in tabs (default 100). |
| `loadComponent` | `() => import('…').then((m) => m.MyPanel)` — loaded the first time the tab opens. |

The component receives two inputs: `ticketId: Guid` and `ticket: TicketDetailDto`. `ticket` is replaced with a new
object whenever the ticket changes (edits, realtime updates), so derive state with `computed()` and refetch your own
data in an `effect()` keyed on `ticketId()`. Use the `@sd/core` API clients only; show loading/empty/error states.

## `composer-actions.ts` → `COMPOSER_ACTIONS`

Buttons in the composer toolbar (Reply / Internal note / Handover tabs).

| Field | Meaning |
| --- | --- |
| `key`, `label`, `icon` | Id, button text (also the accessible name) and icon. |
| `permission?`, `feature?` | As above. |
| `modes?` | `'reply' \| 'note' \| 'handover'` tabs the button shows on (default: all). |
| `run(ctx)` | Runs in the composer's injection context (`inject()` works before the first `await`). |

`ctx` gives `ticket`, `mode`, `html()`, `insertHtml(html)`, `replaceHtml(html)` and `notify(message, kind)`.
Inserted HTML is sanitised by the editor and again by the server. A thrown/rejected error is shown to the agent.

Keep entries small and lazy: import heavy code inside `loadComponent` / `run`, not at the top of these files.
