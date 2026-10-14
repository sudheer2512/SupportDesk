# SupportDesk — Security & Compliance

| Threat / requirement | Control | Where |
|---|---|---|
| Cross-tenant data exposure | Named EF query filter on every tenant-owned entity; write guard throwing `CrossTenantWriteException`; tenant only from signed tokens / key records / server-side brand mapping; tenant-prefixed cache keys, file keys, search index, AI chunks, SignalR groups | `Persistence/DeskDbContext.cs`, `Api/Infrastructure/RequestContextMiddleware.cs`; tests `TenantIsolationTests` |
| Broken access control | Permission checks inside services (`ctx.Require`), ticket data scope (`VisibleTo`), 404 for out-of-scope records, light-agent restrictions, portal scope (Own/Location/Organisation), API-key/OAuth scopes intersected with permissions | `Application/**`; tests `PermissionBoundaryTests` |
| Credential attacks | PBKDF2 hashing, password policy + history + expiry, lockout, MFA (TOTP / e-mail OTP), rate-limited auth endpoints, timing-equalised unknown-user checks, generic error messages | `Identity/AuthService.cs`, `Platform/Security.cs` |
| Session theft | Short-lived ES256 access tokens, rotating refresh tokens with family revocation on reuse, idle/absolute lifetime, concurrent-session limit, server-side revocation checked per request | `AuthService.RotateAsync`, `RequestContextMiddleware` |
| CSRF | Bearer tokens for APIs; refresh cookie is httpOnly + SameSite=Strict + path-scoped + requires `X-Requested-With: SupportDesk` | `AuthController`, `DeskControllerBase` |
| XSS | Server-side HTML allow-list sanitiser for all rich text (scripts, handlers, `javascript:` URLs, unknown iframes removed; links get `rel=noopener`), strict CSP on SPAs, Angular escaping | `Platform/Security.cs` `HtmlSanitization`, `HostingSetup.UseDeskSecurityHeaders` |
| SQL injection | EF Core parameterisation; raw SQL only with parameters; AI analytics uses a whitelisted query builder, never model-generated SQL | everywhere |
| Malicious uploads | Size limits per plan, blocked extensions and executable magic bytes, MIME sniffing (declared types not trusted), SHA-256, malware scan (EICAR built in, ClamAV optional, fail-closed), downloads as attachments with `nosniff` and sandbox CSP | `Platform/Files.cs`, `FilesController` |
| Secrets at rest | AES-256-GCM with master key; API keys/refresh tokens/OTP codes stored as hashes only | `AesGcmSecretProtector`, `TokenHasher` |
| Secrets in transit | HTTPS + HSTS in production; webhooks signed with HMAC-SHA256; inbound provider webhooks signature-verified | `HostingSetup`, communication/automation modules |
| Tampering with records | Ticket history is append-only; audit log rows sealed into per-tenant SHA-256 hash chains; DB trigger blocks updates/deletes of sealed rows; verifier detects edits | `Platform/Audit.cs`, `DatabaseInitializer`; tests `Audit_chain_is_sealed_verifiable_and_tamper_evident` |
| Abuse / DoS | Per-principal token-bucket API limits, per-IP auth/public limits, request size limits, regex timeouts in conditions | `HostingSetup.AddDeskRateLimiting`, `Conditions.cs` |
| AI risks | Feature toggles and roles, budgets, PII redaction before external providers, prompt-injection containment (untrusted content delimited, detector flags, schema-validated outputs, no direct tool execution), human approval for consequential actions, full audit + revert | AI module |
| Privacy / GDPR-style rights | Contact data export, erasure (anonymisation preserving statistics), retention policies (archive/anonymise/delete), legal holds blocking deletion/erasure, consent records per channel and purpose | administration/compliance module, `ContactChannelConsent` |
| Audit of sensitive actions | `AuditActions` catalog (logins, MFA, permission/role changes, settings, exports, deletions, merges, entitlement overrides, API keys, AI actions, subscription changes) | `IAuditWriter` |

**Responsible disclosure:** report vulnerabilities privately to the security contact configured for the deployment.
