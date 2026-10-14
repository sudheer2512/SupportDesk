# SupportDesk clients — conventions

Angular 21.2 workspace with two source libraries and three apps. Read this before adding a screen.

| Project | Import as | What lives there | Selector prefix |
| --- | --- | --- | --- |
| `projects/core` | `@sd/core` | API clients, DTO models, auth/session, realtime, stores, utils — **no UI** | — |
| `projects/ui` | `@sd/ui` | Design system: tokens (`styles/sd-tokens.css`), global CSS (`styles/sd-ui.css`), components, pipes | `sd-` |
| `projects/desk` | — | Agent workbench + administration console (served at `/`) | `desk-` |
| `projects/portal` | — | Customer portal (served at `/portal/`, routes `/:brand/...`) | `portal-` |
| `projects/mobile` | — | Ionic + Capacitor app (agent and customer modes) | `m-` |

Libraries are consumed **as source** through `tsconfig.json` `paths`; they are not built separately.
`projects/core/package.json` and `projects/ui/package.json` declare `sideEffects: false` so the bundler can drop
anything an app does not use (this keeps TipTap and ECharts out of the initial bundle) — keep library modules free of
top-level side effects.

## Scripts (run in `clients/`)

| Script | What it does |
| --- | --- |
| `npm run start:desk` | Desk dev server on http://localhost:4830 (proxies `/api` + `/hubs` → http://localhost:5830) |
| `npm run start:portal` | Portal dev server on http://localhost:4831/portal/ (same proxy) |
| `npm run start:mobile` | Mobile (Ionic) dev server on http://localhost:4832 (same proxy) |
| `npm run build:desk` / `build:portal` | Production builds into `../src/SupportDesk.Api/wwwroot` and `wwwroot/portal` |
| `npm run build` | Desk then portal (order matters: the desk build cleans `wwwroot`) |
| `npm run build:mobile` | Production build into `dist/mobile` (Capacitor `webDir` = `dist/mobile/browser`) |
| `npm run check` | `tsc` over the libs + strict template type-check (`ngc --noEmit`) of desk, portal and mobile |

`ngc` skips template checks while the program has plain TypeScript errors — fix those first, then re-run `check`.

## Components

- Standalone components only (no NgModules), `changeDetection: ChangeDetectionStrategy.OnPush` everywhere.
- Signals for state: `signal`, `computed`, `effect`, `linkedSignal`; component APIs use `input()`, `input.required()`,
  `output()` and `model()` (two-way). No `@Input()`/`@Output()` decorators, no `EventEmitter`.
- `inject()` instead of constructor parameters.
- Built-in control flow only: `@if`, `@for` (always with `track`), `@switch`, `@defer`. The one exception is
  `*cdkVirtualFor` inside `<sd-data-table>` (CDK virtual scrolling has no control-flow equivalent).
- Apps run zoneless (`provideZonelessChangeDetection()`): never rely on zone.js to notice changes — update a signal
  (or use forms/`async` bindings that mark for check).
- Every public `@sd/ui` component starts with a short doc comment showing its usage.
- Prefer `protected` for template-only members; keep components small and move logic to `computed()` or services.
- Accessibility is part of done: semantic elements, labels for every control (`<sd-form-field>` wires id /
  `aria-describedby` / `aria-invalid`), visible focus (tokens provide `--sd-focus-ring`), keyboard paths for every
  pointer interaction, `prefers-reduced-motion` respected (global rule in `sd-ui.css`), AA contrast with the tokens.

## Styling

- Use the tokens (`var(--sd-…)`) — never hard-code colours, radii, shadows or font sizes. Light/dark switch through
  `data-theme` on `<html>` (`ThemeService`), defaulting to `prefers-color-scheme`.
- Component styles are inline `styles:` blocks; global classes are limited to what `sd-ui.css` defines
  (`.sd-input`, `.sd-menu*`, `.sd-popover`, `.sd-prose`, utilities like `.sd-truncate`, `.sd-muted`).
- The portal applies brand colours at runtime (`--brand-primary` / `--brand-accent` → `--sd-primary` / `--sd-accent`);
  derived hover/soft tokens follow automatically via `color-mix()`.
- Icons: `<sd-icon name="ticket" />` from the offline registry (Lucide, bundled). Add icons with
  `registerIcons({ 'name': LucideIcon })`; never load icons or fonts from a CDN (the apps must work offline).

## Routing & features

- One lazy route per module: `loadChildren: () => import('./features/<module>/<module>.routes').then(m => m.ROUTES)`.
  Each `features/<module>/` folder owns its routes file, pages (`*.page.ts`) and module-local components.
- Desk module permissions live in `projects/desk/src/app/nav.ts` (`DESK_MODULES`): the same list drives the nav rail
  (items hidden without permission) and the route guard (`permissionGuard(...)` → friendly `/forbidden` page).
- Guards: `authGuard` (desk/mobile agent), `guestGuard` (login), `permissionGuard(...perms)`,
  `portalAuthGuard` / `portalSessionResolver` (portal). Route `title`s feed the title strategies.
- Route params, query params and route `data` arrive as component inputs (`withComponentInputBinding()`).

## Data access

- **Feature components never call `HttpClient`.** All HTTP goes through `ApiClient` (base `/api/v1`) via the
  per-module clients in `@sd/core` — one file per module, e.g. `tickets.api.ts`, `meta.api.ts`, `views.api.ts`,
  `files.api.ts`, `notifications.api.ts`. Add a new module client as `projects/core/src/lib/api/<module>.api.ts` and
  export it from `public-api.ts`.
- DTOs are TypeScript interfaces in `projects/core/src/lib/models/*` that mirror `SupportDesk.Contracts` exactly
  (camelCase names, enums as string unions, dates as ISO strings, GUIDs as strings).
- Errors are `ApiError` (RFC 7807 problem details + `code`): use `err.is('concurrency_conflict')`,
  `err.currentTicket`, `err.replyCollision`, `err.fieldError('subject')`. Network/5xx/403/429 failures are toasted
  globally by `errorToastInterceptor`; pass `{ silent: true }` when a screen shows its own feedback.
- Lists use `CursorList` / `cursorList(fetcher)` (signals; `load()`, `loadMore()`, `upsert()` for realtime patches).
- Realtime: inject `RealtimeService` (`ticketChanged$`, `ticketThread$`, `presenceFor(id)`, `joinTicket()` …); it
  connects automatically while an agent is signed in.
- **No mock data services.** Every screen talks to the real API. The only locally generated sample values live in the
  dev-only `/design-system` reference page.
- Tokens: the access token is memory-only; only the tenant slug and e-mail (and UI preferences) go to `localStorage`,
  always through `safeStorage` (never throws).

## Naming

- Files: `kebab-case.component.ts` / `.page.ts` / `.service.ts` / `.api.ts` / `.routes.ts`.
- Classes: `SdButton` (ui), `TicketsApi` (core), `LoginPage`, `ShellComponent` (apps).
- Keyboard shortcuts register through `ShortcutRegistry` (they appear in the `?` overlay automatically); palette
  commands through `CommandPaletteService.register()` / `registerProvider()`.
