# Sessions and campus access

Steps 8–9 extend the identity/onboarding rules with database-backed sessions, caller/resource checks and authorized realtime/photo delivery. Existing access tokens without a session ID are rejected after deploying this change; clients must sign in again. Apply `20261008120912_SessionsAndAccess` using the explicit database upgrade procedure before starting the new API.

## Client session flow

Registration and login retain the existing `AuthResponse` fields, including `token`, and add `refreshToken`, `accessTokenExpiresAt` and `refreshTokenExpiresAt`. Access tokens expire after 15 minutes with no clock-skew grace period. Each login creates a separate session with a fixed 30-day expiration. JWTs contain user ID, campus, role, session ID and a unique token ID, without email/name or cached status/badge claims.

| Endpoint | Authentication | Result |
|---|---|---|
| `POST /api/auth/refresh` with `{ "refreshToken": "..." }` | Refresh secret, no access JWT required | New access JWT and replacement refresh secret |
| `POST /api/auth/logout` | Current bearer JWT | Revoke the current session; HTTP 204 |
| `POST /api/auth/logout-all` | Current bearer JWT | Revoke every session belonging to this user; HTTP 204 |

Auth responses use `Cache-Control: no-store`. Refresh secrets are random 256-bit values; only SHA-256 digests are persisted. Consumed digests remain as replay evidence for the session. Refresh does not extend the session's 30-day lifetime. Do not log tokens; send refresh secrets only in the JSON request body. Refresh requests use the strict per-client rate limit; logout-all uses the authenticated standard limit.

Serialize refresh attempts per session on the client, replace both credentials together, and retry pending application requests with the new access JWT. Reusing any consumed refresh token revokes the whole session, including its latest replacement and access JWTs. Two simultaneous refreshes produce one successful rotation and one replay rejection; the successful response's session is then revoked. Network uncertainty after refresh requires signing in again rather than replaying the old secret. This follows [refresh-token replay protection](https://www.rfc-editor.org/rfc/rfc9700.html#section-4.14).

Every protected HTTP request verifies the session, current account status, role and campus against PostgreSQL. Only Active and InIncubation accounts can authenticate. Incubating users retain identity, mailbox verification, onboarding, their own trust summary, safety reporting/blocking and deletion actions; active workflows require verified onboarding and Active status. Suspended/deletion-requested/missing accounts cannot use access JWTs or obtain replacement credentials.

The migration adds a PostgreSQL trigger that permanently revokes sessions when status becomes unavailable or role, campus or password changes. Readiness rejects a missing/disabled trigger. Tracked changes also abort registered local connections. Direct database updates remain covered by the trigger and fresh access checks. Restoring Active status does not revive old sessions. Used refresh evidence and sessions cascade when an account is removed; the broader deletion deadline and storage cleanup remain Step 14.

## Resource rules

`ResourceAccess` centralizes same-campus peer checks, symmetric blocks, conversation membership and campus-scoped Architect checks. Both sides of a block lose peer trust/profile-photo, vouch, match, message, conversation and Wingman access. Conversation lists and totals exclude unavailable relationships. Cached match suggestions recheck current peer permissions and the underlying match's campus/participants before returning a DTO.

Self trust summaries remain available during incubation. Peer trust summaries may include verified onboarded incubation members on the same campus so they can receive vouches; inaccessible voucher identities/notes are omitted. Safety reporting/blocking remains available during incubation. Reports may be submitted after a block, but cannot target another campus or attach unrelated messages. Message reports must refer to a message sent by the reported user in the reporter's same-campus conversation.

Architect privileges apply to the Architect's current campus. Dashboard, invite issuance, manual ambassador approval, report resolution and manual match generation cannot operate on another campus. This replaces the earlier global-Architect behavior. Provision a separate Architect for each isolated campus deployment.

## Realtime access

Hub connection and each invocation recheck current session/account eligibility. `JoinConversation` requires a GUID, actual participation, eligible same-campus participants and no block. Realtime subscriptions are held in a server connection registry rather than treated as permission grants. Every outbound event rechecks session, expiration and resource policy before addressing an individual connection. Trust anomaly alerts go only to current Active Architects of the affected campus.

Blocking removes affected conversation subscriptions on the local instance; logout and tracked revocation abort affected local sockets. Changes made through another database connection are checked before the next invocation or outbound event. Idle connections close at JWT expiration through SignalR's `CloseOnAuthenticationExpiration`. Clients reconnect with their refreshed JWT. See [SignalR authentication and authorization](https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-10.0).

Browser WebSocket/SSE transports may supply only the access JWT as `access_token` on the hub route over HTTPS. Framework request-start logging is suppressed because it includes query strings; application request logging uses the path. Configure proxies/ingress to omit or redact hub query strings as well. Refresh secrets must never be supplied there.

An already delivered event or downloaded photo cannot be recalled. The checks deny subsequent operations/delivery after the committed policy change; clients should clear inaccessible views when notified or when requests fail.

## Photo access

Public static file serving has been removed. Existing `/photos/original/{userId:N}.jpg` and `/photos/abstract/{userId:N}.jpg` URLs require a current bearer session and match the owner's stored URL. Owners can fetch their own photos after verified onboarding. Eligible, unblocked same-campus peers can fetch abstracts; original photos require a shared conversation at Full100 reveal. Missing files return 404 after authorization, and file responses use `Cache-Control: no-store` without range processing.

The API requires `PhotoStorage:BaseUrl=/photos` (or empty for that default). Fetch photos with authenticated requests and use a temporary browser blob URL where needed; a bare image URL cannot supply the Authorization header. Preserve the photo disk/volume and prevent a reverse proxy/CDN from independently serving it. Durable private storage, encryption at rest, approved artistic treatment and intermediate 25/60 variants remain Step 13.

## Campus containment and deployment

The selected NFR-9 deployment model is one campus per database and API deployment, with independent database credentials, JWT/encryption/lookup secrets, photo volumes and compute resources. Separate campus databases must not share a credential with cross-database access. Development and security tests may use a shared database to exercise adversarial campus boundaries; this does not establish breach/performance containment.

Set `Tenancy__CampusCode` for a campus deployment. Production refuses an empty code. Configured deployments refuse startup/readiness if the campus does not exist or the database contains another campus's private user accounts or invitations. Reference campus catalogue rows may coexist. Registration, login/refresh, current sessions and manual/job match generation enforce the deployment boundary. Compose forwards this variable; separate each production Compose/project deployment and its resources.

The supported realtime topology currently has one API instance per campus. A future multi-instance deployment needs a backplane that performs per-connection checks on each destination instance and distributed disconnect/subscription invalidation; do not replace authorization with shared broadcast groups. Step 21 still must verify the deployment topology, campus failure/load isolation, backup/restore, capacity and latency in the intended environment. Implemented application guards are not evidence that those infrastructure acceptance checks passed.

## Upgrade and rollback

Stop writers, inspect, take a protected backup and rehearse its restore/upgrade before applying the migration with `--backup-confirmed true`; follow [database operations](database-operations.md). The migration creates sessions, digest history and the revocation trigger without backfilling sessions for old JWTs. Existing users and encrypted fields are preserved. Deploy clients that understand refresh and re-login before resuming normal traffic.

Restore the verified pre-upgrade backup and matching application/configuration for a reviewed rollback. Do not remove token-history rows while an unexpired session depends on replay detection. No test runner may target Supabase or production; tests use disposable loopback PostgreSQL and actual TestServer WebSocket connections.
