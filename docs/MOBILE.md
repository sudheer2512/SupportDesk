# SupportDesk mobile app

One Ionic 8 + Capacitor 8 app (`clients/projects/mobile`) with two modes, chosen on the sign-in screen:

| Mode | Sign-in | Screens |
| --- | --- | --- |
| **Agent** | workspace + e-mail + password (+ MFA code) | Home (availability, queue counts, next SLA deadlines, pending approvals) · Tickets (Mine / Unassigned / Overdue / At risk / Escalated / Waiting / Watching / All open, search, pull-to-refresh, keyset infinite scroll, swipe *Assign to me* / *Close*) · Ticket (live SLA countdowns, status / priority / assignee quick edit, timeline, reply or internal note with camera/file attachments and templates, customer card) · Alerts (notifications, acknowledge, deep links) · Approvals (approve / reject with a note) · Me |
| **Customer** (portal contact) | help-center name + e-mail + password, or a one-time code e-mailed to them | My tickets (own / location / organisation scope as the portal allows) · Request (conversation, replies with attachments, confirm the fix, reopen, CSAT rating) · New request (portal form API: departments, categories, products, installations, priorities, custom fields + KB suggestions) · Help (search, categories, article reader) · Me |

Every screen calls the real API (no mock data) and has loading, empty, error and offline states.

## Run it in a browser

```bash
# API (any free port; the dev server proxies /api and /hubs to it)
dotnet run --project src/SupportDesk.Api --no-launch-profile --urls http://localhost:5830

# Mobile dev server (clients/)
npm run start:mobile            # http://localhost:4832, proxy → http://localhost:5830
# or with your own proxy file / host:
npx ng serve mobile --host 127.0.0.1 --port 4850 --proxy-config /path/to/proxy.json
```

Open the URL, use the browser's device toolbar (phone size). Demo logins (`docs/DEVELOPMENT.md`): agent workspace
`wings`, `priya@wings.demo` / `Desk@2026` (`admin@` uses an e-mailed MFA code shown on screen in demo mode);
customer help center `wings`, `ramesh@abcretail.demo` / `Portal@2026` (or *E-mail me a code*).

Light/dark follows the system; *Me → Appearance* overrides it (System / Light / Dark).

## Build for Android / iOS

The native projects are not committed. Once per machine/repo:

```bash
cd clients
npm run build:mobile            # → dist/mobile/browser (Capacitor webDir)
npx cap add android             # needs Android Studio + JDK 21
npx cap add ios                 # macOS + Xcode + CocoaPods
npx cap sync                    # after every web build (npm run cap:sync does both)
npx cap open android            # or: npx cap open ios
```

Point the app at your server before building: `projects/mobile/src/environments/environment.ts` → `apiOrigin`
(e.g. `https://desk.example.com`; Android emulator → host: `http://10.0.2.2:5830`). Then allow the app's WebView
origin in the API configuration (`Cors:Origins`): `capacitor://localhost` (iOS) and `https://localhost` (Android).
The refresh token is returned in the response body for `X-Client: mobile` and kept in Capacitor Preferences (one slot
per mode); the access token stays in memory.

Attachments use `<input type=file accept capture>` (camera or files) — no camera plugin is needed. Downloads use a
blob link; on devices, add `@capacitor/filesystem` + `@capacitor/share` if users need "open in…" for downloaded files.

## Offline behaviour

| What | How |
| --- | --- |
| **Connectivity** | `NetworkService` combines the OS flag with server reachability (status 0 / 502–504 → unreachable; any API answer → reachable). The *offline banner* under each header says why, how many messages wait to be sent, and when a screen shows a saved copy. |
| **Cache** | `OfflineCache` (IndexedDB `sd-mobile`, store `cache`, small wrapper in `core/idb.ts` — no package): recently opened tickets (detail, timeline, customer context), first pages of ticket lists per filter, queue counts, notifications, approvals, the portal form, KB home / categories / articles. Keys are scoped to the signed-in identity (`a:{tenant}:{user}` / `c:{brand}:{contact}`), the most recent 20–40 rows per kind are kept, and sign-out clears them. Screens show the cached copy immediately and refresh from the network (`cachedResource`). |
| **Read-only** | While offline (or in an offline session) server actions are disabled: status/priority/assignee changes, swipe actions, approvals, ratings. Composers stay usable and *queue* instead. |
| **Offline start-up** | If the app starts without a network, the stored refresh token is kept (it is only deleted when the server rejects it) and an *offline session* opens from the saved identity snapshot; the session is restored automatically when the server answers again (probe every 15 s, on `online` and on resume). |
| **Drafts** | Composer text (per ticket and mode) and the new-request form are saved on the device while typing (`DraftStore`, IndexedDB `drafts`). |
| **Outbox** | Every reply, note, customer reply and new request goes through `OutboxService` (IndexedDB `outbox`): online it is sent at once, otherwise it waits and is retried on reconnect, resume, sign-in and with backoff (5 s, 15 s, 1 min, 5 min, then every 15 min). Attachments are stored as blobs and uploaded at send time (uploaded file ids are remembered so a retry never re-uploads). Each entry's id is its **idempotency key** — sent as `clientRequestId`; the API returns the already-stored message/ticket for a repeated key (agent tickets, replies and notes per tenant; portal tickets and replies namespaced per contact), so a retry after a lost response never duplicates. An entry left *sending* by a killed app is re-queued on the next start. |
| **Conflicts** | Agent replies carry `basedOnSeq`. If customers/agents replied meanwhile, the API answers `409 reply_collision` with the new entries: the outbox parks the entry as *conflict*, the ticket screen shows the new messages with **Send anyway** (same key, `force`), **Edit** (back into the composer) or **Discard**. Server rejections (validation, permissions) park it as *failed* with the reason. |

## Real time

Agents use `/hubs/desk` (`RealtimeService`): the open ticket's group (changes, new entries, presence — "Arjun is
replying"), queue counts (debounced refresh on `ticket.changed`) and notifications (live prepend + tab badge).
Customers use `/hubs/portal` (`PortalRealtimeService`) for their tickets. On Capacitor `resume` the
`SessionCoordinator` restarts a dropped hub connection, re-syncs push, flushes the outbox and asks screens to refetch.

## Push notifications

### Backend

| Endpoint | Who |
| --- | --- |
| `GET/POST /api/v1/me/devices`, `POST /api/v1/me/devices/unregister` | signed-in agent |
| `GET/POST /api/v1/portal/{brand}/devices`, `POST /api/v1/portal/{brand}/devices/unregister` | signed-in portal contact |

Body: `{ "platform": "Android" | "iOS" | "Web", "token": "…", "appVersion": "1.0.0" }` / `{ "token": "…" }`.
Rows are tenant-owned (`PushDevice`); a token moves to whoever registered it last in that tenant; unregister only
touches the caller's own rows; the token is never echoed back (`tokenHint`).

Dispatch:

* **Agents** — `NotificationService` creates a `Push` delivery when the user's preference for the event type has push
  on (defaults: assignment, mention, approval request, SLA warning/breach, escalation, customer reply, follow-up,
  waiting chat). `NotificationDeliveryProcessor` sends it to **every** active device of the user via `PushFanout`
  (quiet hours respected, critical alerts immediate, acknowledged alerts skipped).
* **Customers** — `ContactPushHandler` (outbox) pushes public agent replies and the move to *Resolved* to the ticket's
  contact (never internal notes).
* Payload `data`: `type`, `link` (`/tickets/{id}` for agents, `/customer/tickets/{id}` for customers),
  `notificationId`, `entityId` / `ticketId`. Tokens the provider reports as dead are deactivated.

Providers (`IPushSender` → `PushRouter`, no SDK packages): iOS tokens → **APNs** when configured, everything else →
**FCM HTTP v1**; with neither configured pushes are only logged.

```jsonc
"Push": {
  "Fcm":  { "ServiceAccountFile": "/secrets/firebase-sa.json" },      // or ServiceAccountJson (inline)
  "Apns": { "KeyId": "ABC123DEFG", "TeamId": "TEAM123456", "BundleId": "com.supportdesk.app",
            "PrivateKeyFile": "/secrets/AuthKey_ABC123DEFG.p8",          // or PrivateKey (PEM inline)
            "Environment": "production" }                               // "sandbox" for development builds
}
```

FCM: service-account JWT (RS256) → OAuth token (cached) → `projects/{id}/messages:send`. APNs: provider token = ES256
JWT signed with the `.p8` key (cached 50 min) → HTTP/2 `POST /3/device/{token}` with `apns-topic` = bundle id. If the
iOS app bundles the Firebase SDK (FCM tokens on iOS), leave APNs unconfigured and FCM delivers to both platforms.

### Client

The app talks to the `PushService` seam (`core/push.service.ts`); `PushRegistrar` registers the token with the server
after sign-in (if permission was granted earlier), on token rotation and from *Me → Push notifications*, and
unregisters before sign-out. The web build provides `WebPushService` (unsupported → in-app realtime only).

To enable native push:

1. `cd clients && npm install @capacitor/push-notifications@^8` then `npx cap sync`.
2. Android: add `google-services.json` (Firebase project) to `android/app/`. iOS: enable the *Push Notifications*
   capability and *Background Modes → Remote notifications* in Xcode; create an APNs key for the server.
3. Add `projects/mobile/src/app/core/capacitor-push.service.ts`:

```ts
import { Injectable, NgZone, inject, signal } from '@angular/core';
import { PushNotifications } from '@capacitor/push-notifications';
import { Subject } from 'rxjs';
import { PushPermission, PushService, PushTap } from './push.service';

@Injectable()
export class CapacitorPushService extends PushService {
  readonly supported = true;
  private readonly _permission = signal<PushPermission>('prompt');
  private readonly _token = signal<string | null>(null);
  private readonly tapSubject = new Subject<PushTap>();
  readonly permission = this._permission.asReadonly();
  readonly token = this._token.asReadonly();
  readonly taps$ = this.tapSubject.asObservable();
  private listening = false;

  async refresh(): Promise<void> {
    const { receive } = await PushNotifications.checkPermissions();
    this._permission.set(receive === 'granted' ? 'granted' : receive === 'denied' ? 'denied' : 'prompt');
    this.listen();
  }

  async enable(): Promise<string | null> {
    this.listen();
    let { receive } = await PushNotifications.checkPermissions();
    if (receive === 'prompt' || receive === 'prompt-with-rationale') receive = (await PushNotifications.requestPermissions()).receive;
    this._permission.set(receive === 'granted' ? 'granted' : 'denied');
    if (receive !== 'granted') return null;
    const token = new Promise<string | null>((resolve) => {
      void PushNotifications.addListener('registration', (t) => resolve(t.value));
      void PushNotifications.addListener('registrationError', () => resolve(null));
    });
    await PushNotifications.register();
    return token;
  }

  async disable(): Promise<void> {
    await PushNotifications.unregister();
    this._token.set(null);
  }

  private listen(): void {
    if (this.listening) return;
    this.listening = true;
    void PushNotifications.addListener('registration', (t) => this._token.set(t.value));
    void PushNotifications.addListener('pushNotificationActionPerformed', (action) => {
      const data = (action.notification.data ?? {}) as Record<string, string>;
      this.tapSubject.next({ link: data['link'] ?? null, data });
    });
  }
}
```

4. In `app.config.ts` provide it on devices:
   `{ provide: PushService, useFactory: () => (Capacitor.isNativePlatform() ? new CapacitorPushService() : new WebPushService()) }`.

Foreground notifications are already shown in-app over SignalR; `@capacitor/local-notifications` is not required.

## Code map (`clients/projects/mobile/src/app`)

| Folder | Contents |
| --- | --- |
| `core/` | `mobile-session` (mode, refresh-token store, auth routing), `session-snapshot`, `session-coordinator` (guards, offline session, resume, sign-out), `network.service` (+ connectivity interceptor), `idb` / `offline-cache.service` / `cached-resource`, `drafts.service`, `outbox.service`, `push.service` / `push-registrar`, `links` (server link → app route), `ui-services` (theme, toasts) |
| `agent/` | stores (queue counts, notifications, availability), home, tickets, ticket detail + composer, notifications, approvals |
| `customer/` | my tickets, ticket, new request, help, article |
| `shared/` | state view, offline banner, SLA chip, attachment picker, outbox item, formatting helpers |

Shared API clients live in `@sd/core` (`DevicesApi`, `models/mobile.ts`).

## Tests

* `tests/SupportDesk.IntegrationTests/Mobile/MobileTests.cs` — device registration isolation (users, tenants,
  agent vs contact endpoints, validation), idempotent ticket creation / replies / notes by `clientRequestId`
  (including a reply collision followed by *Send anyway*), per-contact portal idempotency, push selection by
  preference with every-device fan-out and dead-token deactivation (fake sender), customer push on public reply and
  resolution only.
* `tests/SupportDesk.UnitTests/Mobile/PushAdapterTests.cs` — APNs ES256 provider token, request shape and error
  mapping; per-platform routing.
* `cd clients && npm run check && npm run build:mobile`.
