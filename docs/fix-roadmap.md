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
| 6 | Secure ambassador invite redemption | Pending |
| 7 | University identity verification and onboarding eligibility | Pending |
| 8 | Account-state authorization and token lifecycle | Pending |
| 9 | Campus, block and realtime resource access controls | Pending |
| 10 | Correct vouch submission, counters and peer requests | Pending |
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

Decision before gate implementation: the supplied PDF labels itself v2.0.0 despite a v2.2 filename; confirm the requirement baseline. Record whether “each ambassador” means every active ambassador or an explicitly selected founding cohort. Until confirmed, do not silently relax the gate.

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
