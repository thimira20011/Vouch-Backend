# Vouch: step-by-step fix roadmap

Created: 1 October 2026. Detailed evidence: [project review](project-review-and-completion-plan.md).

## How we will work

Work through numbered steps in order. Each step is a reviewable change with a focused regression check and a recorded result. Add tests with the fix; do not wait until the end for testing. Never apply a migration or backfill to a real database without a dry run, backup and verified upgrade path. Infrastructure tests must use an isolated PostgreSQL database.

This roadmap covers the defects identified in the review and the remaining SRS work. Completing every step is a project-level objective; the initial implementation batch is Step 1. The original review remains the historical baseline; this file tracks subsequent progress.

## Current tracker

| Step | Deliverable | Status |
|---|---|---|
| 1 | Restore Release builds and verify photo processing | Local checks passed; Alpine execution pending |
| 2 | Secure configuration and repeatable local setup | Implemented; local checks passed |
| 3 | PostgreSQL/API integration-test harness | Implemented; local PostgreSQL checks passed; CI pending |
| 4 | Working encrypted-email lookup and readable DTOs | Implemented; local regression checks passed |
| 5 | Safe database migrations, backfill and admin bootstrap | Implemented; synthetic install/upgrade checks passed; live rehearsal required |
| 6 | Secure ambassador invite redemption | Implemented; local PostgreSQL/API regression checks passed |
| 7 | University identity verification and onboarding eligibility | Implemented; local checks passed; real SMTP/university acceptance pending |
| 8 | Account-state authorization and token lifecycle | Implemented; PostgreSQL/API checks and Supabase development upgrade passed |
| 9 | Campus, block and realtime resource access controls | Implemented; adversarial checks passed; production containment acceptance remains Step 21 |
| 10 | Correct vouch submission, counters and peer requests | Implemented; PostgreSQL/API checks and Supabase development upgrade passed |
| 11 | Correct moderation decisions and report review | Pending |
| 12 | Durable alerts and email deadlines | Pending |
| 13 | Private durable photos and staged reveal | Pending |
| 14 | Complete account deletion within its deadline | Pending |
| 15 | Idempotent, concurrent-safe match acceptance | Pending |
| 16 | Reliable daily generation and cache invalidation | Pending |
| 17 | Correct pause/resume and inactivity lifecycle | Pending |
| 18 | Reliable message counters and Architect reveal settings | Pending |
| 19 | Three grounded, unique icebreakers per match | Pending |
| 20 | Consistent API validation, errors and pagination | Pending |
| 21 | Observability, performance and deployment acceptance | Pending |
| 22 | Frontend acceptance and ambassador campus pilot | Pending |

### Step 1 — Restore Release builds

Problem: ImageSharp 4.1.2 prevents Release compilation without a license.

Actions:

- [x] Confirm your preference: use a suitable free replacement.
- [x] Replace ImageSharp with Magick.NET-Q8-AnyCPU 14.17.2, preserving JPEG/PNG/WebP input, normalized JPEG output and abstract blur.
- [x] Include upstream attribution/license notices in build/publish outputs.
- [x] Bound upload byte size and dimensions, strip metadata and support non-seekable streams.
- [x] Treat empty photo configuration as defaults, as documented.
- [x] Add photo-processing regression tests and let unit tests reference Infrastructure.
- [x] Align EF Core Relational with the existing EF Core 10.0.11 references to remove the version conflict exposed by Infrastructure tests.
- [x] Exclude local secret files, logs and uploaded photos from the Docker build context.
- [x] Verify Release build, test suite and publish.
- [ ] Verify Alpine photo-processing tests and production image build in Docker/CI.

Done when: Release build/publish/tests pass; supported formats produce valid resized originals and distinct blurred versions; malformed/unsupported/oversized uploads fail without writing files; Alpine executes the native library successfully.

Scope: restores the existing photo behavior. Public-original-photo privacy, durable storage and intermediate reveal images are handled in Step 13. A free library replacement changes native dependencies and output encoding, so Linux runtime and visual checks remain necessary.

### Step 2 — Secure configuration and repeatable setup

Actions: remove fallback secrets in JwtTokenService; reject missing/blank configuration consistently; preserve environment/command-line precedence over local files; make Compose inject documented settings; remove deployable fixed credentials; partition auth rate limits by trusted caller identity/IP; repair .env.example instructions and document SMTP/CORS/storage settings.

Checks: missing/blank secrets prevent startup/token creation; local overrides cannot replace deployment secrets; one caller does not exhaust everyone's login quota; local setup succeeds from documented instructions. Do not rotate live credentials automatically; record rotation needs for credentials previously deployed.

Depends on: Step 1 for release checks. Covers P0-03 and P2 configuration findings. Admin account bootstrap is completed with Step 5.

### Step 3 — Build the real-database integration harness

Actions: create Vouch.IntegrationTests; expose an API entry point for WebApplicationFactory; disable real background workers/email/AI in the harness; use isolated PostgreSQL via containers or an explicitly configured dedicated test server; reset test data safely; add registration, login, permissions and migration smoke tests; add the project to CI and Docker restore inputs.

Checks: tests cannot target a shared/development database; tests run repeatedly without leftovers; application startup and PostgreSQL query translation execute in tests; no real outbound email/LLM calls occur. SQLite/in-memory tests must not be used to claim PostgreSQL persistence acceptance.

Depends on: Step 1. Future correctness steps use this harness.

### Step 4 — Repair encrypted identity data

Actions: add normalized email lookup hash and uniqueness; keep email ciphertext randomized; centralize encryption/decryption boundaries; widen ciphertext columns; decrypt only authorized DTO fields; version/authenticate encryption; define all PII requiring storage encryption and key rotation; reject corrupted encrypted values safely.

Checks: register → login succeeds; normalized duplicate emails fail including concurrent registration; maximum-length Unicode names/bios round-trip; database values remain protected; authorized match/message/trust/moderation DTOs are readable; wrong-key/corruption cases are controlled errors.

Depends on: Steps 2–3. Covers P0-01. Existing-data migration ships through Step 5, not an automatic production backfill.

### Step 5 — Make installation and upgrades safe

Actions: replace EnsureCreated lifecycle with migrations; build a dry-run/collision report for plaintext and legacy ciphertext email backfill; securely provision the Architect rather than shipping a fixed password; test installations and upgrades from existing schemas, including EnsureCreated databases; separate deployment migration/seed work from readiness; record backup/rollback procedures.

Checks: empty and representative legacy databases upgrade correctly; failed migrations/backfills are recoverable; duplicates are resolved explicitly; seeded admin can log in; missing schema/config cannot be mistaken for readiness.

Depends on: Steps 3–4. No destructive reset of an existing database.

### Step 6 — Secure ambassador invitations

Actions: validate tokens whether the campus is locked or unlocked; bind to campus and intended verified email; assign role only after valid redemption; hash stored tokens; enforce expiry/single use and atomic registration/redemption; record Architect/manual approval.

Checks: invalid, blank, expired, reused, wrong-campus and wrong-email invites fail; parallel redemption yields one success; arbitrary InviteToken never grants ambassador/active status or founding badge.

Depends on: Steps 3–5. Covers P0-02.

### Step 7 — Verify identity and complete onboarding eligibility

Actions: verify university mailbox ownership or approved university authentication; map approved domains to the campus; add curated values and valid interest options; make validators null-safe; track verified/onboarded status; require eligibility before active workflows and launch readiness; recalculate the launch gate when users/vouches change.

Checks: email suffix alone cannot activate identity; campus cannot be spoofed; unknown/empty choices fail; photos remain optional; readiness includes actual onboarding and qualifying vouches.

Decision recorded 4 October 2026: retain the documented v2.0.0 content baseline. The user selected **every active ambassador**, including those above the minimum. Verified onboarding and recorded Architect approval are required; no selected-cohort exception applies.

Depends on: Steps 4–6. Covers P0-03 and P1-05.

### Step 8 — Enforce current account status and token lifecycle

Actions: validate current status on protected REST/hub operations; deny suspended/deletion-requested accounts according to explicit policy; implement short-lived access tokens and hashed rotating refresh tokens; add logout/revocation and refresh-reuse handling; minimize token identity claims; invalidate sessions on suspension/deletion.

Checks: an already-issued token cannot continue forbidden work after status changes; deletion-requested accounts do not get new sessions; refresh reuse is rejected; logout revokes refresh access; authorized incubation actions still work.

Depends on: Steps 3–7. Completes original Step 28.

### Step 9 — Enforce campus, block and realtime access

Actions: centralize caller/resource authorization; scope profiles, trust queries, reports, matches and storage; require conversation membership for hub groups; enforce blocks immediately across REST/hubs/photos; evict/revoke access when relationships/status change; choose infrastructure containment that satisfies NFR-9.

Checks: two-campus and three-user adversarial tests; outsiders cannot subscribe to conversation events; blocked users cannot view profiles, send messages or obtain cached/current match suggestions; previously connected clients lose forbidden access.

Depends on: Steps 3, 7–8. Covers P0-04. Schema/deployment choices needed for breach/performance containment must be recorded explicitly.

### Step 10 — Make vouching consistent and add requests

Actions: require verified active same-campus vouchers; reject self/duplicate/blocked vouches and None/invalid traits; update count/score atomically; preserve 14-day zero-weight activation behavior; flag clique evidence; implement peer vouch requests with abuse controls; recalculate affected metrics after deletions/status changes.

Checks: three distinct eligible peers activate incubation; ineligible peers do not; concurrent vouches do not lose counts/scores; base/bonus/cap/clique/age boundaries work; duplicate pair returns a controlled conflict; request permissions are enforced.

Depends on: Steps 3, 7–9. Covers P1-05 and trust consistency in P0-04/P1-03.

### Step 11 — Correct moderation and complete review flows

Actions: make report resolution atomic and idempotent; include the current decision in the suspension count; verify reported-message linkage and access; audit Architect decisions; restore visibility when appropriate; add paginated campus-scoped pending reports with accurate totals and manual suspension review/reinstatement.

Checks: third upheld report suspends immediately; two do not; repeated/concurrent decisions do not double-count; dismissed-last-pending unhides; over 20 reports are all reviewable; unrelated message reports are rejected.

Decision: document whether the 90-day window uses submission or resolution time before encoding its boundary tests.

Depends on: Steps 3, 8–9. Covers P0-05 and dashboard portion of P1-04; completes original Step 26 with real persistence checks.

### Step 12 — Deliver durable alerts within deadlines

Actions: add persistent notification/outbox records; publish after commit; allow only Architects into Architect alert subscriptions; retry failed SMTP delivery with backoff and deadline monitoring; validate production email configuration; persist offline inactivity and trust notifications.

Checks: ordinary users cannot subscribe to admin alerts; Architects receive anomalies; SMTP outages recover within the one-hour high-severity requirement in controlled tests; offline clients receive pending notices; retries do not generate duplicate decisions.

Depends on: Steps 3, 9–11. Covers P1-04 and delivery portions of P1-02.

### Step 13 — Protect photos and complete visual reveal

Actions: replace publicly served originals with durable private storage; authorize retrieval by viewer, campus, blocks and conversation stage; produce artistic abstract/25%/60%/100% variants; strip metadata; encrypt storage; clean replaced objects; preserve optional-photo behavior; agree the visual treatment with frontend acceptance.

Checks: direct original URLs cannot bypass stage authorization; outsiders/blocked users cannot retrieve images; each stage returns the approved variant; files survive replacement deployments and are deleted with the account.

Depends on: Steps 1, 8–9. Covers P0-06 and REQ-17/20. Provider selection can follow the intended hosting environment; avoid committing to a paid service without your choice.

### Step 14 — Meet the account-deletion deadline

Actions: preserve first request time and process promptly rather than waiting 30 days; track completion deadline; transact relational cleanup; idempotently remove photos/tokens/invite PII; retry failures; recompute surviving trust/launch metrics; document backup/log retention and deletion evidence.

Checks: controllable-clock tests prove completion before day 30; injected failures recover; repeated requests do not extend the deadline; no user-owned objects/tokens remain; partner data handling follows the agreed retention policy.

Decision: agree whether conversations retain anonymized partner messages; do not delete them incidentally through the FK cleanup order.

Depends on: Steps 5, 8, 10, 12–13. Covers P1-03.

### Step 15 — Make match acceptance idempotent

Actions: replace ambiguous boolean response with pending/mutual/rejected state and conversation ID; enforce participant authorization, current eligibility and cycle validity; make rejection terminal for the cycle; ensure one conversation per accepted match under retries/concurrency.

Checks: first accept reports pending; second accept creates one conversation; repeated/parallel accepts do not create more; rejected/expired/inaccessible matches cannot be accepted.

Depends on: Steps 3, 8–9. Covers acceptance portion of P1-01.

### Step 16 — Make generation, scheduling and caching correct

Actions: enforce one participant reservation per cycle in the database; implement cycle/timezone service and campus-aware eligibility; durable job ledger/catch-up/leases; scope candidate/block data; invalidate/version match cache on generation, response, block, report, suspension and deletion; optimize only after scorer equivalence tests.

Checks: concurrent manual/scheduled runs yield at most one match per user; newly generated matches replace cached no-match; safety changes take effect immediately; restart catches up missed cycles; valid values-only quality matches survive optimizations; scorer scenarios from original Step 25 pass.

Decision: use an explicit campus-calendar cycle (Asia/Colombo for the pilot) or rolling 24 hours; record the selected interpretation before changing scheduler timing. Do not automatically exclude pairs with zero shared interests.

Depends on: Steps 3, 7–10, 15. Covers remaining P1-01.

### Step 17 — Repair conversation lifecycle

Actions: explicit pause/resume/archive transitions and pause ownership; prevent overwriting the pause owner or reviving archived chats; define inactivity episode after resume; reset nudge eligibility on meaningful activity; select archive/nudge sets exclusively; durable offline nudge delivery.

Checks: only the pausing user resumes; paused chats never auto-archive; each inactivity episode receives one nudge; the 30-day pass does not both nudge and archive; long-pause resume does not immediately rearchive.

Depends on: Steps 3, 9, 12, 15. Covers lifecycle portion of P1-02.

### Step 18 — Repair message/reveal counters and administration

Actions: validate user message types and keep system messages server-owned; protect concurrent reveal counter updates; emit stage changes after commit; add audited Architect threshold settings within 20–80; connect stages to private photo retrieval.

Checks: short messages do not advance reveal; concurrent sends preserve counts; thresholds and 25/60/100 stage boundaries work; rolled-back sends do not emit transitions; clients cannot spoof system messages.

Depends on: Steps 3, 12–13, 17. Covers counter/type portion of P1-02 and REQ-18.

### Step 19 — Complete grounded icebreakers

Actions: persist/cache exactly three unique suggestions per new match; use only shared values/interests in provider prompts; add values-only fallback; validate provider output; configure model identifier; distinguish timeout from request cancellation.

Checks: one/duplicate/malformed model responses trigger valid fallback; failures stay within the measured three-second budget; all suggestions are grounded; no identities, message bodies or vouch graph leave the service.

Depends on: Steps 3, 7, 15–16. Covers P1-06.

### Step 20 — Finish API quality

Actions: consistent Problem Details and correct 401/403/409 mapping without inspecting exception words; null-safe enum/flag validation; stable bounded pagination; agreed message/note/year limits; cancellation and started-response handling; complete profile/Character Card contracts.

Checks: invalid/null/undefined-enum inputs produce controlled errors; page arithmetic cannot overflow; duplicate-timestamp ordering is stable; clients receive documented response shapes and limits.

Depends on: Steps 3–19 as applicable. Covers P2 and remaining original Steps 10–14.

### Step 21 — Measure and prove production readiness

Actions: OpenTelemetry, privacy-safe structured logs, worker/outbox/deletion alerts; load tests including HTTP and SignalR; baseline match/profile p95 and memory/cold start; verify 5,000 concurrent-user scenario and campus containment; CI migration/container smoke checks; TLS 1.3 ingress; backup restore and rollback drills; complete setup/operations documentation.

Checks: p95 <200 ms under agreed normal load; capacity/containment evidence; reproducible deploy/restore/rollback; missed jobs and deadline risks alert operators; no secrets/private content in logs or final image.

Decision: time-box Native AOT compatibility for exact EF/Npgsql/SignalR packages early, but accept production deployment only after testing. If unsuitable, record an approved SRS amendment; do not label normal publishing Native AOT.

Depends on: Steps 1–20. Completes original Steps 29–30 and P1-07.

### Step 22 — Accept the full product and run the pilot

Actions: separately review/create frontend; verify mobile onboarding/incubation/vouch/match/reflection/letter/reveal/pause/report/block/delete/admin screens; design/accessibility review and Lighthouse >=95; recruit 30 verified diverse-faculty ambassadors and secure Student Union partnership; run the two-week closed beta; verify readiness and operational response.

Checks: whole-product user journeys accepted against every SRS requirement; no unresolved P0 defects; pilot feedback and safety outcomes reviewed; 100% launch readiness before general registration.

Depends on: stable backend contracts for frontend work; Steps 1–21 for public launch. Future Trust Graph and community features remain outside the MVP unless explicitly added.

## Step 1 change record

Implementation completed locally in this chat. Database/user data was not modified. Docker is unavailable locally; Alpine compatibility is documented by the upstream package and its installed linux-musl-x64 assets, but runtime acceptance still requires CI/Docker execution. The Dockerfile now has a `photo-tests` target and CI builds it before the production image.

Verification:

- Release solution build: passed, zero warnings/errors.
- Release test suite: 23 passed (15 existing + 8 new photo-processing cases), zero failed/skipped.
- Release API publish: passed; upstream license notices confirmed in published output.
- Alpine photo-processing tests/production Docker image: configured for CI, not executed locally. No CI run was triggered by this chat.
- Photo-storage access controls and encryption/login defects remain open in their later steps; Step 1 does not certify production readiness.

Upstream sources: [package and license](https://www.nuget.org/packages/Magick.NET-Q8-AnyCPU/14.17.2), [platform support](https://github.com/dlemstra/Magick.NET). License notices are retained in `third-party-notices/Magick.NET.txt` and copied to published output.

## Steps 2–3 change record — 2 October 2026

Step 2 now validates required connection/JWT/encryption settings at startup, removes fallback secrets and the fixed privileged seed account, and keeps local Development settings below deployment overrides. The local secret file is excluded from publish output. Compose requires supplied secrets and injects documented settings; setup instructions now distinguish key=value `.env` from local JSON. Strict auth limits are partitioned by client IP and standard limits by authenticated user, with immediate JSON 429 and Retry-After. Forwarded headers require explicitly configured trusted proxy addresses.

Step 3 adds the real API/PostgreSQL harness, safe generated-database reset/cleanup, first-to-current migration checks, and isolated test settings with workers/SMTP/AI/photo writes disabled. Both a disposable Compose server and a temporary Windows PostgreSQL runner are documented. The integration project is included in the solution, CI and Docker restore inputs.

Verification:

- Release unit tests: 40 passed, zero failed/skipped.
- Release integration tests on a fresh temporary PostgreSQL 17.11 server: 15 passed, zero failed, one explicitly skipped.
- Verified registration → onboarding → daily response, encrypted email persistence, JSON persistence, database health, no default Architect, invalid login, actual signed JWT 401/403, per-caller rate limits and trusted/untrusted forwarded-header behavior.
- The successful-login test remains skipped for the known encrypted-email lookup defect; Step 4 must implement it and enable that test.
- Release solution build: passed, zero warnings/errors. Release API publish: passed; local secret configuration excluded from output.
- Docker/Alpine/CI execution remains pending until GitHub runs the workflow.

No existing application database was accessed or migrated. The runner initializes its own temporary cluster, creates and drops only its generated test database, and stops its server. Production migration/admin provisioning remains Step 5; no automatic credential rotation or live data backfill was performed.

## Steps 4–5 change record — 2 October 2026

Step 4 replaces randomized-ciphertext equality with invariant normalized email HMAC lookup and a unique database index, including controlled conflicts under concurrent registration. Identity/profile/private-text fields and values/interests JSON are encrypted by persistence converters using versioned AES-256-GCM; services and authorized DTO projections receive readable values. Ciphertext columns are widened to text while plaintext limits remain enforced. Unknown keys/formats, corruption and field swaps fail closed; encryption model caching cannot cross key-ring instances. JWTs no longer include name/email PII. Encryption and independent lookup-key rotation are explicit offline operations.

Step 5 removes EnsureCreated and all startup migration/seeding behavior. Explicit inspect/upgrade/seed/provision-admin commands perform private dry-run reports, collision/key/schema checks, reviewed EnsureCreated adoption, migrations and an atomic protected-data backfill. Existing databases require a backup acknowledgement; recognized baselines are verified against complete application tables, columns, indexes and constraints. Unknown schemas are refused without guessing. A completed marker and key checks gate readiness. Admin provisioning creates no fixed credentials, refuses ordinary-account promotion/automatic password replacement, and supports an explicit reviewed password reset. Unsafe schema downgrade is refused; restore/rollback procedures are documented in [database operations](database-operations.md).

Verification:

- Release unit tests: 51 passed, zero failures/skips.
- Release PostgreSQL/API tests: 36 passed, zero failures/skips. The former successful-login skip is enabled and passes.
- Tested fresh install/reference seed/admin login, both genuine historical EnsureCreated schemas with mixed plaintext/CBC identities, normalized legacy collisions, wrong legacy keys, schema drift, interrupted expansion/backfill rollback and retry, changed lookup keys, retained-key rotation, admin refusal/idempotency/password reset and non-public-schema refusal.
- Tested registration → login, concurrent normalized duplicates, 120-character Unicode names/280-character Unicode bios, protected raw storage, readable match/message/trust/moderation DTOs, signed JWT permissions and controlled corruption responses.
- Exercised inspect/upgrade/seed/admin commands as separate processes without HTTP listeners or secret output.
- Release build: zero warnings/errors. Release publish: passed, local secret settings excluded. EF confirms no pending model changes. Idempotent migration SQL was executed twice successfully against a legacy fixture; backfill/readiness remained explicitly required. Recovery from a recognized EnsureCreated installation with an empty history table also passed. Docker/Alpine/GitHub CI remains separately pending.

No real application database, credentials or uploaded photos were changed. Existing deployments require the new independent `Security:EmailLookupKey` and the reviewed explicit upgrade before resuming service. Synthetic fixtures establish the implementation's upgrade paths; restore a backup of the actual deployment and rehearse before live use. Legacy CBC cannot retrospectively prove integrity, and field authentication does not bind payloads to row IDs. Later authorization, session revocation, photo privacy and operational acceptance remain their numbered steps.

## Steps 6–7 change record — 4 October 2026

Invites now require a campus-bound recipient, store only a token digest, and validate on every campus state. Conditional redemption and registration share a transaction. Issuance/redemption/manual approval retain actor and time evidence. Verification uses a mailbox-delivered, hashed, expiring, single-use challenge; SMTP failure rolls it back. Curated distinct values/interests and null-safe validation establish explicit verified/onboarded status. Invited ambassadors remain incubating until both are complete. Membership workflows and matching candidates require current eligibility; ordinary activation counts qualifying peers rather than trusting old counters.

The user selected every Active ambassador for launch. Shared database queries require verified onboarding, approval and qualifying outgoing vouches; tracked user/vouch writes recalculate readiness inside the transaction with campus locking. Dashboard GET remains read-only. The new migration hashes legacy invites, resets unproven non-Architect eligibility and gates, and requires existing ambassador reapproval. Its downgrade is deliberately unavailable because digests cannot restore bearer tokens; rollback restores the pre-upgrade backup. See [identity and onboarding](identity-and-onboarding.md).

Verification:

- Release solution build: passed, zero warnings/errors.
- Full Release tests using temporary PostgreSQL 17.11: **119 passed** (58 unit, 61 PostgreSQL/API integration), zero failed/skipped.
- Covered locked/unlocked invalid invites, blank/expired/used/wrong recipient/campus, parallel registration, injected database failure and rollback/retry, legacy token hashing, strict domain mapping, ownership/onboarding gating, challenge expiry/resend/reuse/identity changes/concurrency, delivery outage, manual approval and strict launch recalculation.
- Verified setup-script parsing, quoted/semicolon password handling and preservation of unrelated JWT/encryption/configuration values with a disposable local settings fixture.
- Real SMTP/university verification, actual backup rehearsal, Docker/Alpine and CI acceptance remain external checks. Complete session/campus/block controls and vouch-counter concurrency remain Steps 8–10.

The user additionally selected an existing Supabase project for development. [Setup and production cutover](supabase-development.md) use its Session pooler and the existing Npgsql connection setting. The supplied root certificate enabled verified TLS and successful login. Read-only inspection confirmed an empty public schema; the operator confirmed Data API was disabled. All four migrations and reference seeds were applied, preserving Supabase-managed schemas and creating no accounts. Post-install inspection was clean, and the temporary API returned HTTP 200 Healthy plus a successful onboarding-options response. Added safe connectivity error categories: seven focused privacy/diagnostic tests passed. Disposable integration tests remain isolated from the hosted project.

## Steps 8–9 change record — 8 October 2026

Step 8 adds persisted sessions, 15-minute access JWTs and hashed rotating refresh tokens with a fixed 30-day session lifetime. Replay revokes the whole session; logout supports the current session or all of the caller's sessions. Protected requests and hub operations use current database status, role, campus and session validity. A database trigger revokes sessions on unavailable account status or changed password/role/campus, including direct SQL updates; readiness verifies the trigger. Old JWTs without a persisted session require signing in again.

Step 9 centralizes peer/conversation/Architect authorization, symmetric blocks and campus scope across matches, trust, messages, reports, invitations and Wingman. Cached suggestions recheck current permissions and the underlying match. Hub subscriptions require membership; every outbound event checks the current session and resource before delivery. Local blocked subscriptions are removed and revoked sessions disconnected. Photo URLs now require authenticated authorization, and originals require Full100 reveal. See [sessions and access](sessions-and-access.md) for the client flow and policy.

The chosen production containment model uses an independent database, credentials, application keys, photo volume and API/compute deployment for each campus. Production requires `Tenancy__CampusCode`; startup/readiness reject foreign-campus private users or invites. Development remains shared for adversarial checks. The current supported realtime deployment has one API instance per campus. Actual infrastructure containment/load acceptance remains Step 21; durable encrypted photo storage and intermediate variants remain Step 13.

Verification:

- Full Release suite on disposable loopback PostgreSQL 17.11: **148 passed** (58 unit, 90 integration), zero failed/skipped. Release build passed with zero warnings/errors; EF reported no pending model changes.
- Covered refresh rotation/replay/concurrency, logout isolation, expired/legacy credentials, incubation permissions, suspension/deletion/role/campus/password revocation, disabled-trigger readiness, outsiders and cross-campus resource requests, symmetric blocks, cached match invalidation, legacy foreign reports/senders, real TestServer WebSocket invocations/delivery and authenticated photo retrieval.
- Inspected the actual Supabase development schema, reviewed the migration SQL, encrypted a public-schema backup with Windows CurrentUser DPAPI, verified decryption, restored it into a generated local database and successfully rehearsed the upgrade while preserving reference counts.
- Applied `20261008120912_SessionsAndAccess` to Supabase development. Post-upgrade inspection recognized all five migrations with no issues/collisions/rewrites. The temporary API returned HTTP 200 `Healthy` and onboarding options, then was stopped. Supabase-managed schemas and existing reference records were preserved; no accounts were created and no production database was changed.

Next implementation step: Step 10, vouch submission/counter correctness and peer requests. Real SMTP/university, Docker/Alpine and production acceptance remain the previously recorded external checks.

## Step 10 change record — 10 October 2026

Vouch submission now validates nonzero known traits and eligible same-campus peers before persisting, with a campus lock held from the current-state check through commit. Stored qualifying records determine counts and capped scores instead of incrementing stale entities. Concurrent different vouchers preserve totals; duplicate pairs return a controlled conflict. Three distinct eligible vouchers activate incubation even with zero age weight. Original age/bonus/clique weights remain snapshots, while current status/onboarding/campus/blocks determine whether they contribute. Losing endorsements does not automatically relock an activated member.

Persisted clique flags/shared-voucher counts record threshold evidence. Historical dampened records receive a flag with an unknown count instead of invented evidence. Tracked user/vouch/block changes and transactional bulk deletion reconcile trust totals and launch readiness; the explicit upgrade reconciles historical/imported records before writers resume. Peer requests add private inbox/sent history, independent voluntary endorsement, dismissal/cancellation/fulfillment, exact seven-day expiry/pair cooldown, five new requests per rolling day and ten live pending requests. Pending uniqueness, campus locking and the authenticated HTTP rate limit prevent concurrent retries from bypassing controls. See [vouching and peer requests](vouching-and-peer-requests.md).

Verification:

- Full Release suite on disposable PostgreSQL 17.11: **176 passed** (58 unit, 118 integration), zero failed/skipped. Release build passed with zero warnings/errors; publish passed and excluded local secrets; EF confirmed no pending model changes.
- Covered concurrent different/duplicate submissions, self/foreign/unverified/blocked peers, invalid traits, exact 14-day/bonus/clique boundaries, frozen zero weights, cap/removal correctness, lifecycle metrics, private request ownership/pagination, persistent rolling and pending limits, cooldown/expiry, fulfillment rollback under an injected database failure, and relational account cleanup.
- Upgrade tests cover strict partial-index predicates and schema drift. Corrected inspector table ordering to compare names independently of server collation. A timezone bug discovered by existing match tests received a narrow UTC cache-expiration correction; Step 16's scheduler/cycle/cache redesign remains pending.
- Reviewed migration SQL, took a fresh encrypted Supabase public-schema backup, verified decryption, restored it to a generated loopback database and rehearsed the upgrade. Applied the sixth migration to Supabase development; post-inspection had no issues/collisions/rewrites. Existing counts remained four campuses, six reflections and 50 icebreakers; users, invites, vouches and requests remained zero.
- The temporary API returned HTTP 200 `Healthy`, onboarding options 200 and anonymous request inbox 401, then was stopped. No production database or application keys were changed; credentials/backups remain excluded from Git.

Next implementation step: Step 11, atomic moderation decisions and complete campus report review. Durable notifications, deletion deadlines/photo cleanup, deployment performance and frontend acceptance remain their later numbered steps.
