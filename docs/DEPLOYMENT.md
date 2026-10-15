# SupportDesk — Deployment Guide

## 1. Components

| Component | Artifact | Notes |
|---|---|---|
| API + web apps | `src/SupportDesk.Api` (publishes the Angular desk + portal from `wwwroot`) | ASP.NET Core 10; in-process in IIS (site or virtual directory) or Kestrel behind a reverse proxy |
| Worker service (optional) | `src/SupportDesk.WorkerHost` | Windows Service or systemd; runs jobs and recurring tasks |
| Database | SQL Server 2019+ / Azure SQL | One shared database; optional dedicated databases per enterprise tenant |
| Cache / backplane (recommended for >1 node) | Redis 6+ | Distributed cache, presence, SignalR backplane |
| File storage | Local/shared path (`Storage:DataPath`) | Use an SMB/NFS share or mounted volume shared by all nodes |
| Malware scanning (optional) | ClamAV `clamd` | `Security:ClamAv:Host/Port`; uploads fail closed if configured and unreachable |
| Mobile app | `clients/projects/mobile` (Ionic/Capacitor) | Build with Android Studio / Xcode |

## 2. Build

```bash
cd clients && npm ci && npm run build          # desk → src/SupportDesk.Api/wwwroot, portal → wwwroot/portal
cd .. && dotnet publish src/SupportDesk.Api -c Release -o publish/api
dotnet publish src/SupportDesk.WorkerHost -c Release -o publish/worker
```

On Windows `deploy\iis\Publish-SupportDesk.ps1` runs the same steps into `publish\iis\api` and `publish\iis\worker`.

## 3. Configuration (environment variables or appsettings.Production.json)

| Key | Purpose |
|---|---|
| `ConnectionStrings__SupportDesk` | Shared database connection string |
| `ConnectionStrings__<name>` | Dedicated tenant databases (referenced by `Tenant.DedicatedConnectionName`) |
| `ConnectionStrings__DedicatedTemplate` | Optional template with `{name}` (e.g. `Server=…;Database=SupportDesk_{name};…`) used for dedicated connection names that are not configured explicitly — lets operators provision dedicated databases without a configuration change |
| `Billing__GraceDays` / `Billing__PaymentTermDays` / `Billing__TaxRate` | Grace period after trial expiry or an overdue invoice (default 7), invoice payment terms (14), tax rate as a fraction (0) |
| `Billing__InvoicePrefix`, `Billing__IssuerName`, `Billing__IssuerAddress`, `Billing__Manual__Instructions` | Invoice numbering and the issuer / bank-transfer text printed on invoices (offline "manual" payment provider) |
| `Signup__ShowVerificationCode` | Shows the sign-up e-mail code on screen. Defaults to `true` only in Development/Testing; never enable in production |
| `Compliance__DownloadLinkHours` | Lifetime of one-time data-export download links (default 24) |
| `Security__MasterKey` | **Required in production.** 32-byte base64 key for AES-GCM secret encryption (`openssl rand -base64 32`). Keep it in a secret store; losing it makes stored channel/API secrets unreadable. |
| `Security__JwtSigningKey` | Optional: protected ES256 PKCS#8 key (otherwise generated once under `Storage:DataPath/keys`, which must then be shared by all nodes) |
| `Storage__DataPath` | Root for files, keys, mail drop folders (shared by all nodes) |
| `Redis__Configuration` | e.g. `redis01:6379,password=…,ssl=True` — enables Redis cache + SignalR backplane |
| `Workers__Enabled/Outbox/Jobs/Recurring` | Which background engines run in this process |
| `Mail__Smtp__Host/Port/Username/Password`, `Mail__From` | System e-mail (OTP codes, digests, reports) |
| `OpenTelemetry__OtlpEndpoint` | OTLP collector for traces |
| `Cors__Origins` | Only if the SPAs are hosted on another origin |
| `RateLimits__*` | Per-minute limits for auth, API and public endpoints |
| `Automation__Webhooks__AllowPrivateNetworks` | Default `false` (SSRF protection). Set `true` only when webhooks/rules must reach on-premises systems such as a local Wings ERP server |
| `Seed__Demo` | Must be `false` in production |
| `ForwardedHeaders__KnownProxies__0…`, `ForwardedHeaders__KnownNetworks__0…` | IPs / CIDR ranges of remote reverse proxies / load balancers. By default only loopback proxies (Nginx or IIS on the same host) are trusted, so a forged `X-Forwarded-For` cannot bypass the agent IP allow-list or per-IP rate limits. `ForwardedHeaders__TrustAll=true` trusts any sender — use it only when the platform edge always overwrites the header and Kestrel is not reachable directly |
| `Portal__CustomDomainTarget` | Host name customers point their brand's custom domain at (CNAME); defaults to the host of `App__PublicUrl` |
| `App__PublicUrl` | Public address of the app **including any virtual directory** (e.g. `https://server.example.com/supportdesk`), used for links in e-mails, invitations and the portal |
| `Hosting__PathBase` | Only behind a reverse proxy that forwards a sub-path unchanged (e.g. Nginx `location /supportdesk/`): set `/supportdesk`. Not needed for an IIS virtual directory — IIS supplies the path base itself |

Database: the API applies EF Core migrations at start-up (`Database:InitializeMode=Migrate`). For controlled
releases set it to `None` and apply migrations with `dotnet ef database update` or an idempotent script
(`dotnet ef migrations script --idempotent`).

## 4. Windows / IIS

SupportDesk runs **in-process** in IIS (ASP.NET Core Module V2) — as its own site (`https://support.example.com`) or as an
application/virtual directory of an existing site (`https://server.example.com/supportdesk`). The desk, the customer portal
(`/portal/`), the chat widget, Swagger and the realtime hubs all follow the virtual directory: the server rewrites the SPAs'
`<base href>` per request, and the web clients map the API's root-relative links (`/api/v1/…` in KB articles, attachments,
chat payloads) under the application path. Stored content stays root-relative, so a tenant can be moved between hosts.

**Server prerequisites**

| Item | How |
|---|---|
| IIS + management scripts | `Install-WindowsFeature Web-Server, Web-Scripting-Tools` |
| WebSocket Protocol (realtime; without it SignalR falls back to long polling) | `Install-WindowsFeature Web-WebSockets` |
| Application Initialization (starts the app and its background workers without waiting for a request) | `Install-WindowsFeature Web-AppInit` |
| **ASP.NET Core 10 Hosting Bundle** — install *after* IIS, then `iisreset` | <https://dotnet.microsoft.com/download/dotnet/10.0> |
| SQL Server reachable from the server | LocalDB does **not** work under IIS (it runs per interactive user) |

On Windows 10/11 use *Turn Windows features on or off* → Internet Information Services instead of `Install-WindowsFeature`.

**Publish and install** (scripts in `deploy/iis/`):

```powershell
# Build machine: npm ci + Angular build + dotnet publish → publish\iis\api and publish\iis\worker
.\deploy\iis\Publish-SupportDesk.ps1

# Server, elevated PowerShell — own site:
.\deploy\iis\Install-SupportDesk.ps1 -PublishPath D:\drops\supportdesk\api -HostName support.example.com `
    -ConnectionString 'Server=sql01;Database=SupportDesk;Integrated Security=True;TrustServerCertificate=True' `
    -PublicUrl 'https://support.example.com'

# …or as an application of an existing site, plus the worker Windows service:
.\deploy\iis\Install-SupportDesk.ps1 -PublishPath D:\drops\supportdesk\api -ParentSite 'Default Web Site' `
    -ApplicationPath /supportdesk -ConnectionString '…' -PublicUrl 'https://server.example.com/supportdesk' `
    -WorkerPublishPath D:\drops\supportdesk\worker
```

`Install-SupportDesk.ps1` is idempotent (run it again with a new drop to upgrade) and does the following:

1. Checks the prerequisites above (module, runtime, WebSockets, Application Initialization).
2. Places `app_offline.htm` (the module stops the app and serves a maintenance page), mirrors the drop to `-InstallPath`
   (default `C:\inetpub\supportdesk`) and keeps `appsettings.Production.json` and `logs`.
3. On the first install writes `appsettings.Production.json` — connection string, a generated `Security:MasterKey`
   (**back it up**), `Storage:DataPath` (default `%ProgramData%\SupportDesk`, outside the site so a redeploy never deletes
   uploads or keys), `App:PublicUrl`, `Seed:Demo=false` — readable only by Administrators, SYSTEM and the app pool.
4. Creates the app pool: *No Managed Code*, Integrated, `ApplicationPoolIdentity`, **AlwaysRunning** with idle time-out 0
   (the outbox dispatcher, jobs and recurring tasks run inside the app), recycled daily at 03:00 instead of every 29 hours.
5. Creates the site (`-HostName`, `-Port`) or the application (`-ParentSite`, `-ApplicationPath`) with **preload** enabled,
   and grants the pool read on the app folder and modify on `logs` and the data folder.
6. Excludes `<site>/hubs` from IIS request logging: SignalR WebSocket/long-polling URLs carry the short-lived access token
   in the query string.
7. With `-WorkerPublishPath`: installs the **SupportDeskWorkers** service (virtual account `NT SERVICE\SupportDeskWorkers`,
   restart on failure, same settings without the web app's switches) and sets `Workers:Jobs=false`,
   `Workers:Recurring=false` for the web app (the outbox dispatcher stays in the web app).
8. Restarts the pool, removes `app_offline.htm` and calls `/health/ready`.

Then:

* **Database login** for Windows authentication — `CREATE LOGIN [IIS APPPOOL\SupportDesk] FROM WINDOWS` and a `db_owner`
  user (migrations run at start-up; with `Database:InitializeMode=None` a `db_datareader`/`db_datawriter` user is enough).
  For a remote SQL Server grant the computer account (`DOMAIN\SERVER$`) instead, or use a SQL login in the connection string.
  Do the same for `NT SERVICE\SupportDeskWorkers` when the worker service is installed.
* **HTTPS**: add an HTTPS binding with a certificate in IIS Manager (HSTS is sent automatically outside Development) and,
  for an own site, redirect or remove the HTTP binding.
* **Custom portal domains**: add host bindings (or a wildcard certificate) for each brand's `CustomDomain`.

**What `web.config` sets** (`src/SupportDesk.Api/web.config`, completed by `dotnet publish`): in-process hosting, WebDAV
removed (it would answer PUT/DELETE with 405), request limits of 110 MB / 8 KB URL and query string, `httpErrors
existingResponse="PassThrough"` (keeps the API's problem+json bodies), no `X-Powered-By`. It does not set the `<webSocket>`
section — it is locked at server level, and WebSockets are on by default once the feature is installed.

**Troubleshooting**

| Symptom | Cause / fix |
|---|---|
| HTTP 500.19 | Hosting Bundle missing (unknown `aspNetCore` section) or a locked section added to `web.config` |
| HTTP 500.30 / 500.37 | The app failed to start / timed out — see the Application event log (source *IIS AspNetCore Module V2*) or set `stdoutLogEnabled="true"` in `web.config` (logs in `logs\stdout_*.log`) |
| HTTP 500.31 | ASP.NET Core 10 runtime missing — reinstall the Hosting Bundle |
| Health check 503 / log shows `Login failed for user 'IIS APPPOOL\…'` | Database login missing (see above) |
| Realtime updates arrive late | WebSocket Protocol feature missing (long polling) or a proxy in front of IIS strips `Upgrade` |
| Start-up warning *Storage:DataPath is not set* | Files would be written under the app folder; set `Storage:DataPath` |
| Links in e-mails point to the site root | `App:PublicUrl` must include the virtual directory |

Manual alternative to the script: publish, point a site/application at the `api` folder with a *No Managed Code* pool, put
the settings above into `appsettings.Production.json` or `<environmentVariables>` in `web.config` (never commit secrets), and
install the worker with `sc.exe create SupportDeskWorkers binPath= "…\SupportDesk.WorkerHost.exe" start= auto`.

## 5. Linux (systemd + Nginx)

```ini
# /etc/systemd/system/supportdesk-api.service
[Unit]
Description=SupportDesk API
After=network.target
[Service]
WorkingDirectory=/opt/supportdesk/api
ExecStart=/usr/bin/dotnet /opt/supportdesk/api/SupportDesk.Api.dll --urls http://127.0.0.1:5830
Restart=always
User=supportdesk
Environment=ASPNETCORE_ENVIRONMENT=Production
EnvironmentFile=/etc/supportdesk/api.env
[Install]
WantedBy=multi-user.target
```
The worker host is installed the same way (`SupportDesk.WorkerHost.dll`, `Type=notify` is supported via systemd
integration). Nginx must forward WebSockets for `/hubs/`:

```nginx
location / {
    proxy_pass http://127.0.0.1:5830;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    client_max_body_size 110m;
}
```

To serve the app under a sub-path, use `location /supportdesk/ { proxy_pass http://127.0.0.1:5830; … }` (no URI after the
port, so the prefix is forwarded) and set `Hosting__PathBase=/supportdesk` and `App__PublicUrl=https://host/supportdesk`.

## 6. Scaling and high availability

* Run 2+ API nodes behind a load balancer (no sticky sessions needed when Redis is configured).
* Outbox dispatcher, job runner and recurring tasks are lease-based: running them on several nodes is safe.
* SQL Server: enable Always On / Azure SQL zone redundancy; schedule index maintenance; consider partitioning
  `TicketEvents`, `Messages`, `AuditLogs` by month for very large tenants (retention policies archive old data).
* Health checks: `/health/live` (process) and `/health/ready` (database) for load balancers/orchestrators.

### Moving a tenant to a dedicated database

Platform console → Tenants → *tenant* → Actions → **Move to dedicated database** (`POST /api/v1/platform/tenants/{id}/dedicated-database`,
plan feature `dedicated_database`). The provisioner:

1. resolves the connection by name — `ConnectionStrings:<name>` or `ConnectionStrings:DedicatedTemplate` (names are `[a-z0-9_]{3,40}`;
   a raw connection string is never accepted from the UI);
2. creates the database (`CREATE DATABASE`, the SQL login needs `dbcreator` on that instance);
3. applies the model (migrations, or `EnsureCreated` in development) and the audit-protection trigger;
4. copies the tenant's rows of every tenant-owned table, the tenant record and the global plans/permissions with a cross-database
   `INSERT … SELECT` (constraints disabled during the copy) — this requires both databases on the **same SQL Server instance**;
5. switches `Tenant.DatabaseMode = Dedicated` + `DedicatedConnectionName`, refreshes the routing directory and mirrors the tenant,
   subscription and plan rows into the new database (kept in sync on every later billing/status change).

Runbook: suspend the tenant first (no writes during the copy), move it, verify sign-in and data, reactivate. Users sign in again
(sessions are not copied). The shared-database rows stay as a frozen copy for rollback; purge them after verification. For another
server: back up/restore a copy of the tenant's rows there, create the connection name, then switch the tenant (step 5).
Background work follows the tenant: the outbox dispatcher, job runner, audit sealer and cross-tenant scans (SLA monitor,
contract lifecycle, ERP sync) process the shared database and every dedicated database (the shared frozen copy of a moved
tenant is skipped). Events and jobs still pending in the shared database at the move are copied and retired there, so each
runs once. An unreachable dedicated database is logged and skipped without stopping the other tenants.

On LocalDB (development) set `ConnectionStrings__DedicatedTemplate=Server=(localdb)\MSSQLLocalDB;Database=SupportDesk_{name};Trusted_Connection=True;TrustServerCertificate=True`.

## 7. Backup and recovery

* Database: full nightly + differential hourly + log backups every 15 min (RPO ≤ 15 min). Test restores monthly.
* Files: snapshot `Storage:DataPath` with the same schedule; keep the master key and JWT key in a vault, separately.
* Restore order: database → files → keys → start API (migrations are idempotent) → start workers. Leases expire on
  their own, so interrupted jobs resume automatically.

## 8. Upgrades

1. Back up. 2. Deploy new binaries to one node with `Database:InitializeMode=Migrate` (or run the migration script).
3. Roll the remaining nodes. 4. Workers last. Breaking API changes are versioned under `/api/v2`.

On IIS, run `Install-SupportDesk.ps1` again with the new drop: it takes the app offline, replaces the files (settings,
logs and the data folder are kept), restarts the pool and the worker service and checks `/health/ready`.
