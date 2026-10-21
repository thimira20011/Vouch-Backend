# Vouch task progress checklist

Updated: **21 October 2026** (Asia/Colombo).

This is the working checklist for what we have completed and what remains. Step numbers follow the [fix roadmap](fix-roadmap.md), which contains the detailed change records. They refer to the remediation roadmap, not the original implementation plan's phase/step numbers.

## Current position

| Area | Status |
|---|---|
| Backend Steps 1–14 | Implemented; remaining environment/product acceptance checks are listed below |
| Backend Steps 15–20 | Remaining implementation and correctness work |
| Steps 21–22 | Remaining production readiness, frontend acceptance and pilot work |
| Next implementation step | **Step 15 — idempotent match acceptance** |
| Public launch | Not ready; remaining implementation and acceptance must be completed |

`[x]` means the stated work is implemented or supported by recorded verification. `[ ]` means work or acceptance remains. A completed backend task does not establish production or frontend acceptance.

Step 13 was recorded at `a888c48` (16 October 2026). Step 14 is implemented on **`feature/step-14-account-deletion`** (21 October 2026); its migration is prepared for deployment, while shared Supabase development remains at Step 13. Historical Step 13 results below retain their original verification date.

## Completed backend work

- [x] **Step 1 — Restore Release builds.** Replaced the licensed image dependency with Magick.NET, retained license notices, bounded image processing and verified local Release build/test/publish. Alpine execution remains an acceptance item.
- [x] **Step 2 — Secure configuration and local setup.** Removed fallback secrets/fixed admin credentials, validated required settings, preserved configuration precedence, documented setup and corrected caller-specific rate limiting/trusted proxy handling.
- [x] **Step 3 — PostgreSQL/API integration tests.** Added a real API/PostgreSQL harness, disposable database safeguards, repeatable reset/cleanup and CI configuration. Tests do not use Supabase development or real outbound providers.
- [x] **Step 4 — Encrypted identity and readable data.** Added normalized keyed email lookup, uniqueness, authenticated encryption, readable authorized DTOs, ciphertext limits, corruption handling and explicit key rotation.
- [x] **Step 5 — Safe database installation and upgrades.** Added explicit inspect/upgrade/seed/admin commands, legacy schema/backfill checks, readiness gates and backup/restore/rollback procedures. Subsequent Supabase development upgrades included actual restore rehearsals.
- [x] **Step 6 — Ambassador invitation security.** Added hashed, campus/recipient-bound, expiring, single-use invites, atomic redemption and recorded Architect/manual approval.
- [x] **Step 7 — Identity, onboarding and launch eligibility.** Added mailbox ownership challenges, campus domain checks, curated values/interests, eligibility gates and launch recalculation. The agreed gate counts **every active ambassador**, requiring verified onboarding, approval and the vouch target.
- [x] **Step 8 — Sessions and token lifecycle.** Added short-lived access JWTs, rotating hashed refresh tokens, replay rejection, logout/revocation and current account-state checks.
- [x] **Step 9 — Campus, block and realtime permissions.** Centralized resource authorization, enforced symmetric blocks/current campus, secured hub subscriptions/outbound delivery and documented campus-isolated production deployments.
- [x] **Step 10 — Consistent vouches and peer requests.** Added concurrent-safe submission, qualifying counts/scores, clique evidence, trust/readiness reconciliation and private peer requests with lifecycle and abuse limits.
- [x] **Step 11 — Moderation decisions and report review.** Added atomic/idempotent decisions, suspension on the third qualifying upheld report, evidence checks, paginated campus queues, encrypted audits and manual suspension/reinstatement.
- [x] **Step 12 — Durable notifications and email deadlines.** Added transactional encrypted notification records, offline inbox/read receipts, authorized Architect alerts, leased retries, per-recipient receipts and one-hour report-email deadline monitoring.
- [x] **Step 13 — Private photos and staged reveal.** Added encrypted PostgreSQL storage, metadata removal, abstract/25%/60%/original variants, viewer/stage authorization, atomic replacement/removal, account cascade cleanup and current DTO photo URLs. The synthetic portrait progression was approved.
- [x] **Step 14 — Account deletion within its deadline.** Added a durable queue, immutable first request/deadline, immediate startup processing, concurrent-worker leases, transactional cleanup/retries, completion receipts, deadline health/status, bounded identifier/receipt retention and encrypted restore-ledger export/replay. Shared conversations and all their messages are deleted under the user-selected policy. See [account deletion](account-deletion.md).

Feature guides: [identity/onboarding](identity-and-onboarding.md), [sessions/access](sessions-and-access.md), [vouches/requests](vouching-and-peer-requests.md), [moderation](moderation-review.md), [notifications](durable-notifications.md), [private photos](private-photos.md), [account deletion](account-deletion.md).

## Completed development database and verification work

- [x] Connected the existing Supabase development PostgreSQL project through its Session pooler using verified TLS.
- [x] Recorded the operator's confirmation that the Supabase Data API was disabled for this backend-owned schema.
- [x] Applied nine migrations through `20261016152507_PrivateDatabasePhotos` and preserved Supabase-managed schemas.
- [x] Took encrypted development backups, verified decryption, restored local copies and rehearsed upgrades before hosted changes.
- [x] Recorded clean post-upgrade schema/protected-data inspection and API health checks on 16 October.
- [x] Verified the final Step 13 Release build with zero warnings/errors and publish output without local secrets or legacy photo files.
- [x] Historical Step 13 verification: **261 full-suite tests** (77 unit, 184 integration), followed by **37 focused photo tests** after the last adjustment/four new cases. That version contained **265 cases**; no single full 265-case run was recorded at Step 13.
- [x] Pushed the implementation through Step 13 to GitHub `main` at `a888c48`.
- [x] Step 14 verification on 21 October: **287 full Release tests passed** (77 unit, 210 PostgreSQL/API integration), zero failed/skipped; final build had zero warnings/errors, publish excluded local secrets/legacy photos, and EF reported no pending model changes.
- [x] Reviewed the Step 14 migration SQL and rehearsed nine-to-ten-migration upgrade/inspection on a restored Supabase backup, preserving reference counts and leaving the hosted source unchanged.

The last recorded development counts were four campuses, six reflections and 50 icebreakers, with zero users, invites, vouches, requests, reports, audits, notifications, receipts and photos. These are historical verification results, not a live database inventory. See [Supabase development](supabase-development.md) and [database operations](database-operations.md).

## Step 14 completion details

- [x] Existing work provides transactional relational cleanup/trust reconciliation and a photo owner FK that cascades on physical deletion.
- [x] Preserve the first deletion-request timestamp; repeated/concurrent requests cannot extend the deadline or restore active status.
- [x] Process on startup and every minute; track the original 30-day completion deadline.
- [x] Clean account/content/token/photo/invite data atomically, retry failures and repair surviving trust/report/readiness totals.
- [x] Record the selected policy: delete shared conversations and both participants' messages.
- [x] Add controllable-clock deadline, cancellation/restart, concurrent-worker and injected-failure rollback/retry tests.
- [x] Record completion/deadline evidence and document backup/log retention, encrypted restore replay and identifier expiry by the original deadline.
- [ ] Apply `20261021175206_DurableAccountDeletion` to shared development when deploying the branch, following the reviewed upgrade procedure. This is deployment work; the shared database was not changed by implementation tests.
- [ ] Complete production backup/log retention enforcement, alert routing and restore/outage drills in Step 21.

## Remaining implementation tasks

### Step 15 — Make match acceptance idempotent and safe under concurrency

- [ ] Return explicit pending/mutual/rejected state and a conversation ID instead of an ambiguous boolean.
- [ ] Enforce participant permission, current eligibility and cycle validity on every response.
- [ ] Make rejection terminal for the cycle.
- [ ] Guarantee one conversation per accepted match under retries and simultaneous accepts.
- [ ] Test first/second/repeated/parallel accepts and rejected/expired/inaccessible matches.

### Step 16 — Correct daily generation, scheduling and caching

- [ ] Decide the cycle interpretation: campus calendar day (Asia/Colombo for the pilot) or rolling 24 hours.
- [ ] Enforce at most one participant reservation/match per cycle in the database.
- [ ] Add a campus-aware cycle service, durable job ledger, leases and restart catch-up.
- [ ] Scope generation candidates and block data to the appropriate campus/current eligibility.
- [ ] Complete cache invalidation/versioning across generation, responses and safety/account changes, including cached no-match results.
- [ ] Verify concurrent manual/scheduled runs and scorer equivalence; preserve valid values-only matches.

### Step 17 — Correct pause/resume and inactivity lifecycle

- [x] Step 12 provides durable offline inactivity notifications to reuse.
- [ ] Enforce explicit pause/resume/archive transitions and pause ownership.
- [ ] Prevent another user from overwriting the pause owner or reviving an archived conversation.
- [ ] Define/reset inactivity episodes after resume and meaningful activity.
- [ ] Keep paused conversations out of automatic archival and make nudge/archive selection exclusive.
- [ ] Test one nudge per episode, the 30-day boundary and long-pause resume behavior.

### Step 18 — Correct message/reveal counters and Architect settings

- [x] Step 12 moves clarity broadcasts after commit; Step 13 provides authorized staged photo retrieval.
- [ ] Validate allowed user message types and keep system messages server-owned.
- [ ] Preserve qualifying-message counts and reveal stages under concurrent sends.
- [ ] Add audited Architect reveal-threshold settings within 20–80.
- [ ] Test short-message exclusion, threshold/stage boundaries, concurrent sends, rollback and spoofed system messages.

### Step 19 — Provide three grounded, unique icebreakers

- [ ] Persist/cache exactly three unique suggestions per new match.
- [ ] Ground provider prompts only in shared values/interests and support values-only fallback.
- [ ] Validate duplicate, incomplete and malformed provider responses and fall back reliably.
- [ ] Make the model identifier configurable and distinguish provider timeout from caller cancellation.
- [ ] Verify the measured three-second budget and that identities, messages and vouch graphs are not sent to the provider.

### Step 20 — Finish API validation, errors and pagination

- [ ] Standardize Problem Details and correct 401/403/409 mapping without matching exception-message words.
- [ ] Complete null-safe enum/flag validation and agree consistent message/note/year limits.
- [ ] Make pagination bounded, stable and safe from arithmetic overflow and duplicate-timestamp ordering issues.
- [ ] Handle cancellation and already-started responses consistently.
- [ ] Complete profile/Character Card contracts and document response shapes/limits.

### Step 21 — Prove production readiness

- [ ] Add OpenTelemetry, privacy-safe structured logs and worker/outbox/deletion deadline alerts.
- [ ] Run HTTP/SignalR load tests and measure profile/match p95 latency, memory and cold starts.
- [ ] Verify the agreed 5,000-concurrent-user scenario, campus capacity and failure/breach containment.
- [ ] Verify CI migration smoke tests, Linux/Alpine native photo processing and the production container image.
- [ ] Configure and verify TLS 1.3 ingress and production deployment topology.
- [ ] Rehearse portable/offsite backup restore, rollback and production cutover, including encrypted photos and retained keys.
- [ ] Evaluate Native AOT with the actual packages; record an approved SRS amendment if it is unsuitable.
- [ ] Complete operational documentation and verify the p95 <200 ms target under the agreed normal load.

### Step 22 — Accept the frontend and run the campus pilot

- [ ] Review/build the frontend against stable backend contracts.
- [ ] Accept mobile onboarding, incubation, vouch/request, match/reflection, letter/reveal, pause, report/block, deletion and Architect journeys.
- [ ] Verify the approved reveal treatment on real photos in the frontend.
- [ ] Complete design/accessibility review and Lighthouse >=95 acceptance.
- [ ] Recruit 30 verified ambassadors across faculties and secure the Student Union partnership.
- [ ] Run the two-week closed beta and review feedback, safety outcomes and operational response.
- [ ] Accept all MVP SRS journeys, resolve outstanding P0 defects and reach 100% launch readiness before general registration.

## Outstanding acceptance for already implemented work

These remain open even though the relevant backend implementation is complete. Some are also listed under Steps 21–22 so they are included in release acceptance.

- [ ] Confirm actual GitHub CI results, including the complete current test suite and migration checks; workflow configuration alone is not execution evidence.
- [ ] Execute/verify Alpine native photo tests and the production image in Docker/CI.
- [ ] Validate real university mailbox verification and SMTP delivery with the intended provider.
- [ ] Validate real alert-email provider acceptance, outage recovery and deployed deadline monitoring.
- [ ] Complete real-photo frontend acceptance; approval so far covers the synthetic portrait progression.
- [ ] Verify deployed campus isolation, load targets, operational alerts and production restore/rollback drills.
- [ ] Provision a separate production database/deployment with appropriate credentials, keys, SMTP, CORS, TLS and backup retention before cutover. Development Supabase is not the production deployment.

## How to keep this checklist current

When a step is implemented, update its checkboxes here and the tracker/change record in [fix-roadmap.md](fix-roadmap.md). Record the verification date, tests actually run, any migration/backup rehearsal and remaining acceptance. Keep historical test counts separate from new runs, and never mark a provider/deployment check complete solely because the code or workflow exists.
