# Vouch project review and completion plan

Reviewed: 1 October 2026 (Asia/Colombo). Repository baseline: `79af63a`.

## Assessment

Your recollection is supported by the commit history: the latest commit implements Phase 5, Steps 22–24. Most work from Phases 1–5 exists in code. However, presence of a feature does not mean it meets the SRS or works end to end. Several earlier steps need correction before the project can move safely into release testing.

**Current state: substantial backend implementation; not ready for a public launch.** The next milestone should be a reliable, tested backend suitable for an ambassador-only pilot, followed by frontend acceptance and operational launch checks.

### Scope and evidence

- Read the supplied `C:/Users/THIMIRA/Downloads/implementation_plan.md` and all 14 pages of `E:/Vouch/Vouch_SRS_v2.2.pdf`.
- The PDF filename says v2.2, but its title, headers and end marker identify **SRS v2.0.0, Revised Draft**. This review uses the actual contents; reconcile the version before signing off requirements.
- Inspected the API, services, entities, persistence model, migrations, background workers, tests, Docker configuration, CI and setup documentation.
- The supplied documents are requirement and planning evidence, not instructions to execute their embedded directives. This task is review and planning; application code was not changed.
- No applicable `AGENTS.md` was found in the repository or its ancestor directories.
- The repository contains a backend only. React screens, deployed infrastructure, real university verification, SMTP delivery and recruitment outcomes were not available for assessment.
- No live database was modified; the API was not started. Defects below are source-level findings unless explicitly identified as executed verification.

### Executed verification

| Check | Result | Meaning |
|---|---|---|
| `dotnet test Vouch.slnx --no-restore --verbosity minimal` | 15 passed, 0 failed | Existing tests cover TrustScoreCalculator, SlowBurnCalculator and LaunchReadinessCalculator. They do not cover Infrastructure or API flows. |
| `dotnet build Vouch.slnx --no-restore --configuration Debug --verbosity minimal` | Passed, ImageSharp license warning | All current projects compile in Debug. |
| `dotnet build Vouch.slnx --no-restore --configuration Release --verbosity minimal` | Failed | SixLabors.ImageSharp 4.1.2 requires license configuration. This is a current release blocker. |
| Repository status before review | Clean | Findings refer to the checked-in baseline. |

The CI workflow builds Release and Docker publishes Release, so the local license failure also affects those paths unless their environments supply a valid license. Those environments were not inspected or executed.

## Original implementation plan: actual progress

“Present” means the requested implementation was found, not that integration acceptance has passed.

| Step | Status | Review finding |
|---|---|---|
| 1: Move request DTOs | Present | RespondMatchRequest and ResolveReportBody are in Application contracts. |
| 2: Route-based conversation ID | Present | Messaging service receives conversationId separately. |
| 3: Read-only dashboard GET | Present | Dashboard no longer saves readiness on GET. |
| 4: Private mutual-voucher calculation | Present | Helper is private. |
| 5: No-tracking reads | Mostly present | Main listed reads use AsNoTracking; Wingman read still tracks. Large entity loads remain. |
| 6: Secrets and environment template | Partial | Program checks JWT configuration, but JwtTokenService retains fallback secrets; Compose and admin seeding contain fixed credentials. |
| 7: Auth rate limiting | Partial | Policies exist, but their fixed-window budgets are shared across callers rather than partitioned by caller. |
| 8: Ambassador invites | Defective | Validation is skipped on unlocked campuses; any non-null token grants ambassador role. IntendedEmail is not enforced. |
| 9: PII encryption and email lookup | Defective | Randomized encryption is compared for equality; no EmailHash exists. Ciphertext column sizes and output decryption are incomplete. |
| 10: Global exceptions | Partial | Middleware exists, but exceptions use a custom envelope rather than consistent Problem Details; authorization failures and message-based mappings need correction. |
| 11: Input validation | Partial | Validators exist; missing enum/nonzero checks and null-safe collection rules remain. Limits differ from the plan. |
| 12: Health endpoint | Present | PostgreSQL probe, /healthz and Docker probe exist; runtime/container behavior unverified. |
| 13: Configurable CORS | Present | Origins read from configuration. |
| 14: Pagination | Present | Conversation and message pagination exists; stable tie-break ordering and overflow protection remain. |
| 15: Inactivity nudge | Partial | SignalR nudge exists; no durable offline delivery. |
| 16: Inactivity worker | Present with defects | Worker exists; repeated inactivity cycles and stale resume timestamps need attention. |
| 17: Account deletion worker | Defective | Deletes only once the account is already 30 days overdue; photos and derived metrics remain. No all-or-nothing transaction. |
| 18: High-severity email | Partial | SMTP implementation exists, but failures are logged without retries or a delivery deadline monitor. |
| 19: Text-only guard | Partial | Text/System model exists, but Type is not validated and user-authored System messages are permitted after reveal. No media feature exists. |
| 20: Dashboard trust anomalies | Present with gaps | Real queries exist; recipient group is never populated, outputs contain encrypted identity fields, and alert durability is missing. |
| 21: Photo upload/storage/reveal | Partial | Dedicated upload and local blurred image exist. Cloud/private storage, artistic filter and intermediate clarity variants are missing. |
| 22: Structured logging | Partial | Serilog and selected service/worker logs exist; not all planned business events are logged. |
| 23: Match optimization/scheduling | Partial | Worker exists; pairing remains quadratic and uses 00:05 UTC rather than campus-local midnight. |
| 24: Match cache | Defective | Cache exists and response invalidates both users; generation, blocking, moderation and account changes do not invalidate it. |
| 25: MatchmakingScorer tests | Missing | No scorer test file. |
| 26: Moderation service tests | Missing | No service test suite. |
| 27: API integration tests | Missing | No integration test project. |
| 28: Refresh tokens | Missing | Seven-day access token only; no refresh, logout revocation or current-status validation. |
| 29: OpenTelemetry | Missing | No tracing/metrics configuration found. |
| 30: Developer onboarding docs | Partial | README and .env.example exist, but setup is inconsistent and Compose does not inject the documented required settings from .env. |

**Conclusion:** finish and repair Phases 2–5 while adding regression tests, then complete Phase 6 and Phase 7. Merely implementing Steps 25–30 would leave major SRS gaps unresolved.

## Findings requiring correction

Priority: **P0** blocks release or core identity/safety; **P1** blocks SRS acceptance and pilot quality; **P2** is delivery/maintainability work. Source paths below are relative to the repository root.

### P0-01 — Registration/login and PII storage are inconsistent

Evidence: `AuthService.cs:62–75,107–110`; `AesEncryptionService.cs:30`; `ApplicationDbContext.cs:61–63`; `DbInitializer.cs:65–75`.

- Encrypt generates a new IV for every call. Re-encrypting an email cannot reproduce the stored ciphertext, so login does not find registered users and duplicate checks do not reliably reject existing emails. The unique ciphertext index does not ensure normalized email uniqueness.
- Seeding stores the Architect email/name/bio in plaintext, which the encrypted lookup also cannot find.
- Limits remain 120 characters for email/name and 280 for bio, even though encryption and Base64 expand the stored representation. Valid maximum-length inputs can fail database writes.
- Trust, match, messaging and moderation projections return stored ciphertext as names, bios and emails rather than decrypting them. The alert email also uses ciphertext identity values.
- Decrypt catches every error and returns the input, hiding wrong keys and corrupt encrypted records.
- Encryption is only applied to selected user fields. Define the PII scope explicitly for photos, invite emails, reports, messages, profile metadata, logs and backups; current code does not establish NFR-4 compliance for all PII.

Correction: add a stable normalized email lookup hash with a unique index; keep randomized encryption for the email itself; centralize encryption/materialization or explicit DTO decryption; use suitable ciphertext columns; migrate plaintext and encrypted historical data safely. Adopt authenticated, versioned encryption and a key-rotation strategy. Treat corrupt ciphertext as a controlled error rather than plaintext. Remove unnecessary identity data from tokens.

Acceptance: register → login works; duplicate normalized email is rejected under concurrency; maximum valid Unicode inputs round-trip; seeded admin can authenticate; raw database values are encrypted where required; authorized DTOs return readable values; wrong-key/corruption tests fail safely.

### P0-02 — Ambassador privilege can be obtained without a valid invite

Evidence: `AuthService.cs:45–69`; `AmbassadorInvite` and its configuration.

When the campus is unlocked, invite validation is skipped, but any non-null InviteToken sets Role=Ambassador, Status=Active and the founding badge. An arbitrary string bypasses incubation. On locked campuses, IntendedEmail is ignored. Concurrent redemption is not protected by an atomic conditional update or concurrency token.

Correction: validate supplied tokens on every campus state; assign role only after successful redemption; enforce intended identity, campus, expiry and single use; store token digests; commit redemption and registration together. Define manually verified ambassador approval explicitly.

Acceptance: invalid, expired, wrong-campus, wrong-email and reused tokens fail; two simultaneous redemptions yield only one success; ordinary unlocked-campus registration remains in incubation.

### P0-03 — Identity verification and current-account authorization are missing

Evidence: AuthService registration checks only email suffixes; Program has no OnTokenValidated/current-account guard; JwtTokenService expires access tokens after seven days.

- Matching a suffix proves neither mailbox ownership nor current student identity. Any `.ac.lk` email is accepted for any campus, bypassing campus-specific membership checks.
- Login rejects suspension only. DeletionRequested accounts can receive tokens, and existing tokens remain usable after suspension or deletion request.
- The seeded privileged account has a fixed source-controlled password. Compose includes fixed JWT/encryption/database secrets; JwtTokenService retains fallback values.
- Rate-limiter policies use a shared permit pool: five login/register attempts across the application can throttle everyone.

Correction: implement expiring, single-use email verification or approved university authentication; map verified domains to campuses; gate each operation by current account state and resource policy. Shorten access-token lifetime and add hashed rotating refresh tokens, logout/revocation and reuse handling. Remove fixed credentials from deployable code, bootstrap the admin securely, externalize secrets and partition rate limits by trusted caller identity/IP with correct proxy handling. Rotate any fixed credentials that were used in a deployment.

Acceptance: unverified users cannot enter active workflows; campus membership cannot be spoofed; suspended/deletion-requested accounts lose access immediately according to policy; refresh reuse is rejected; one caller cannot exhaust the global login allowance.

### P0-04 — Blocking, campus boundaries and realtime access are not enforced consistently

Evidence: `VouchHub.cs:19–21`; SendMessageAsync; VouchEndpoints `/user/{userId}`; TrustService SubmitVouchAsync; MatchService GetTodayConnectionAsync/RespondToMatchAsync.

- Any authenticated user can join an arbitrary conversation group without checking participation, campus, blocks or account state. Today this exposes clarity events, not the message bodies delivered through personal groups; it is still unauthorized conversation metadata access.
- SendMessageAsync does not check blocks or either participant's current account state.
- Character-card/trust retrieval accepts any user ID without caller-aware block or campus checks.
- Vouch submission does not require an active voucher or same-campus target, so incubation/suspended accounts can contribute activation vouches.
- Existing/cached matches and acceptance are not rechecked after a block, report, suspension or deletion request.
- A shared CampusId field is not sufficient for the breach/performance containment promised by NFR-9.

Correction: centralize caller/resource authorization and campus scoping across REST, hubs, jobs and storage. Guard hub subscriptions and revoke existing access when policy changes. Make blocked profile/messaging/match access impossible immediately. Define infrastructure containment separately: choose per-campus databases/deployments or explicitly amend the stronger containment requirement.

Acceptance: automated two-campus and three-user tests demonstrate that outsiders cannot subscribe, retrieve profiles/messages, vouch or accept inaccessible matches; blocking takes effect immediately, including cached and realtime paths.

### P0-05 — Moderation suspension can occur one report late

Evidence: `ModerationService.cs:130–172`.

ResolveReportAsync sets report.Status in memory, then executes a database CountAsync before saving. On the third upheld report, the database still counts only two. The method also permits re-resolving reports and incrementing metrics repeatedly. It uses CreatedAt for the 90-day window rather than the decision timestamp; the intended interpretation needs an explicit decision.

Correction: make resolution an atomic, idempotent state transition; include the current decision correctly; use the agreed window timestamp; validate message/report subject linkage and reporter access; audit the acting Architect. Recompute visibility from remaining pending cases. Add a suspension review/reinstatement workflow.

Acceptance: the third qualifying upheld report suspends immediately; two do not; repeated decisions do not double-count; 90-day boundaries, concurrent decisions and dismiss/unhide behavior pass PostgreSQL-backed tests.

### P0-06 — Original photos can bypass reveal controls

Evidence: LocalPhotoStorageService uses predictable `{userId:N}.jpg` under `wwwroot/photos/original`; Program invokes UseStaticFiles before authentication; conversation DTOs expose only one abstract version until full reveal.

Once stored and served under the configured web root, an original photo can be fetched directly by user ID without proving conversation participation or reveal stage. Concealing its URL in a DTO does not protect it. Local files also have no configured container volume and are not encrypted by this service.

Correction: private durable object storage; authorized stage-aware retrieval or short-lived access grants; abstract/25%/60%/100% variants with agreed artistic treatment; upload decoder/pixel limits and metadata stripping; encrypted storage matching the PII requirement; cleanup on replacement/deletion. Ensure path and storage access cannot bypass blocks or campus isolation.

Acceptance: direct unauthenticated and pre-reveal requests for originals fail; each stage returns the correct approved variant; files survive deployment replacement and disappear after account deletion.

### P0-07 — Production build is currently blocked

Evidence: executed Release build; ImageSharp 4.1.2 PackageReference; CI and Dockerfile.

Correction: configure a valid eligible ImageSharp license in local release/CI/container build environments, or replace the image-processing dependency with an appropriately licensed implementation. Do not suppress the license check as a release fix. Keep license material out of source and final images as appropriate.

Acceptance: clean Release build, tests, publish and Docker build succeed in CI without relying on undeclared local files.

### P1-01 — Match state transitions, scheduling and cache can violate product rules

Evidence: MatchService; DailyMatchGenerationWorker; MatchEndpoints.

- The first participant's acceptance returns true, and the endpoint labels that boolean isMutualMatch, falsely reporting a mutual connection.
- Repeated acceptance after both flags are set creates additional conversations. A rejected match can later be accepted; no current-cycle guard exists.
- Concurrent generation/acceptance has no database-enforced participant-per-cycle or conversation-per-match invariant. Existing indexes are not unique and cannot enforce participation across the A/B columns.
- Generation still scores nested candidate pairs, loads full user entities, and loads blocks across all campuses.
- Cached “no match” results can remain until midnight even when generation later creates a match. Safety/status changes also leave stale cached access.
- The worker runs at 00:05 UTC (05:35 Sri Lanka time), with no missed-run catch-up or distributed lease. The cycle is UTC-calendar based; the plan specifies campus midnight. Resolve calendar-cycle versus rolling-24-hour semantics.
- Generation includes all active roles and does not require completed onboarding or an explicit pilot/launch matching policy.

Correction: typed response containing acceptance state and conversation ID; guarded, idempotent state transitions; transactional unique participant/cycle reservations and one conversation per match; campus timezone/cycle service; durable job run ledger and multi-instance coordination; complete cache invalidation/versioning. Benchmark candidate indexing/pre-filtering before adopting it: shared values can make a quality match even with zero shared interests, so the original plan's mandatory-interest prefilter would exclude valid pairs.

Acceptance: concurrent/manual/scheduled runs produce at most one match per user per cycle; one accepted match yields one conversation; first acceptance reports pending; rejection/expiry cannot be bypassed; generated matches appear immediately; blocks remove cached suggestions immediately; restart catches up an unprocessed cycle.

### P1-02 — Conversation lifecycle and delivery need correction

Evidence: MessagingService PauseConversationAsync, ResumeConversationAsync and ProcessInactivityChecksAsync.

- Either participant can resume a pause, although REQ-22 gives this control to the pausing user. A second pause can overwrite its owner; pause/resume can also reactivate an archived conversation.
- Sending a new message does not reset InactivityNudgeSentAt, so later inactivity periods receive no nudge.
- Resume retains LastMessageAt; a conversation resumed after a long pause can be archived immediately by the next worker pass.
- Archive and nudge queries run before saving archived state; a 30-day inactive conversation can be selected for both in the same pass.
- SignalR nudges disappear when participants are offline. Clarity notification is sent before persistence, and counters have no concurrency guard.
- Type accepts undefined enum values; System is client-selectable after reveal. Text-only should describe supported user content types, not grant clients system-message authorship.
- Paging has no unique secondary sort and integer page arithmetic can overflow.

Correction: define an explicit conversation state machine, pause ownership, resume timing and inactivity-episode semantics; archive before selecting eligible nudges or use exclusive predicates; publish durable events after commit; optimistic concurrency for counters; server-owned system messages and validated types; bounded/stable pagination.

Acceptance: only the pause owner resumes; paused chats are preserved; each inactivity episode gets exactly one delivered nudge; 30-day archiving does not also nudge; concurrent messages produce correct clarity counts and stages; notifications never claim a rolled-back transition.

### P1-03 — Deletion misses the deadline and leaves data behind

Evidence: AccountDeletionWorker and RequestAccountDeletionAsync.

The worker waits until DeletionRequestedAt <= now−30 days, then runs daily. Completion is after day 30, potentially almost day 31 even without failures. Database bulk deletes commit separately, photos are untouched, IntendedEmail is compared with encrypted user.Email, and other users' trust counts/scores and campus readiness are not repaired after vouches/users are removed. Repeating deletion requests resets the request timestamp.

Correction: preserve the original request time; disable account access immediately; process promptly with a completion deadline before day 30; track deletion jobs and retry failures; transact related database changes; idempotently clean storage and refresh tokens; recompute affected derived metrics; define backup/log retention and verification evidence. Agree whether partner messages should survive in anonymized conversations rather than being removed wholesale.

Acceptance: deadline tests use a controllable clock; injected failures recover without incomplete relational state; photo objects/invite PII/tokens are removed; affected metrics are consistent; completion evidence is available.

### P1-04 — Alerts and dashboard are not operationally complete

Evidence: SlowBurnNotificationService sends to architect_alerts, but VouchHub never adds Architects to that group; SmtpEmailService catches delivery failures; dashboard takes 20 reports before computing PendingReportsCount and returns only critical report details.

Correction: role-checked Architect subscriptions plus persistent alert records; outbox/retry/backoff and deadline monitoring for high-severity email; fail production configuration checks when required delivery is disabled; separate full campus-scoped pending count from paginated report listing; include noncritical cases; audit decisions and manual suspension review.

Acceptance: an Architect receives a trust alert; no ordinary user can subscribe; SMTP outage recovers and notifies within one hour in failure-injection testing; more than 20 reports have an accurate total and all categories are reviewable; campus selection scopes report data.

### P1-05 — Verification/onboarding, vouch requests and reveal administration are unfinished

Evidence: AuthValidators, User entity, current endpoints and contracts.

- DeepValues are arbitrary strings rather than a curated catalogue. Interests lack enum validation; collection Must rules can dereference null. CharacterTraits.None remains accepted by IsInEnum.
- No explicit onboarding-completed state exists; readiness does not require it. Calculator gate passes when 30 ambassadors meet the vouch target even if extra active ambassadors do not, whereas REQ-A5 says each ambassador must do so. Decide the qualifying cohort/all-active interpretation.
- Submitting a vouch is supported, but requesting one from peers is not (REQ-5).
- AdaptiveRevealThreshold exists, but there is no Architect endpoint/policy to set it within 20–80 (REQ-18).
- No complete caller-aware profile/Character Card endpoint exists; today's match and trust summary cover only portions of the UI data.

Correction: curated choices and enum/flag validation; null-safe validators; explicit verified/onboarded state; gate recalculation on every relevant lifecycle event; vouch-request workflow with abuse controls; bounded/audited reveal settings; authorized profile contract.

Acceptance: onboarding is required for launch eligibility; unknown/empty values fail validation; vouch requests work without exposing forbidden relationships; threshold bounds are enforced; founding badge and profile data are available only to permitted viewers.

### P1-06 — AI icebreakers do not fully meet REQ-24/25

Evidence: AnthropicAiWingmanService and WingmanEndpoints.

Icebreakers are requested on demand for a conversation rather than generated on a new match. The prompt sends both users' full values/interests, not just shared themes. A one-item or duplicate model response is accepted. Fallback becomes generic when no interest overlaps; valid matches can have shared values without shared interests. The model identifier is hardcoded. A three-second cancellation budget is present, but total observed latency has not been tested.

Correction: cache/persist exactly three unique validated suggestions per match; send shared themes only; support values-grounded fallback for no-interest-overlap cases; make model configurable and verify availability at implementation time; distinguish request cancellation from provider timeout; test malformed/error/timeout responses.

Acceptance: three distinct grounded suggestions on every eligible new match; provider failure falls back within the measured latency budget; no personal identities, messages or vouch graph are transmitted.

### P1-07 — Database lifecycle, deployment and SRS performance claims are unverified

Evidence: DbInitializer calls EnsureCreatedAsync; checked-in migrations exist but startup does not apply them; Program swallows seeding errors; .dockerignore omits local secret JSON files; no PublishAot/telemetry/load tests are present.

Correction: choose migrations as the database lifecycle; test clean installation and upgrade from real existing schema, including databases created by EnsureCreated. Separate migration/seed jobs from API readiness. Add safe configuration validation, secret exclusions from Docker context, persistent private storage, container hardening, TLS termination and forwarded-header configuration, backup/restore drills and deployment rollback. Add observability and defined performance scenarios before optimizing.

Native AOT is a requirement decision, not a flag-only task: [Microsoft's ASP.NET Core .NET 10 compatibility guide](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0) lists SignalR as partially supported. [Microsoft's EF Core guidance](https://learn.microsoft.com/en-us/ef/core/performance/nativeaot-and-precompiled-queries) describes EF NativeAOT as experimental and recommends against production deployment. Run a time-boxed compatibility prototype for the exact provider/packages; if unsuitable, document an approved SRS amendment for the MVP rather than claiming NFR-1 is complete.

Acceptance: clean and upgrade database tests pass; deployment detects missing schema/config; Release/container publish succeeds; TLS 1.3 policy is verified at the ingress; match/profile p95 stays below 200 ms under an agreed normal-load workload; a representative 5,000-concurrent-user campus test measures HTTP and SignalR behavior; isolation/failure tests prove the selected campus containment design.

### P2 — API consistency and developer experience

Use Problem Details consistently; distinguish 401 from 403; remove status mapping based on exception-message words; handle cancellation and started responses correctly. Document chosen limits (current messages 2,000 vs planned 5,000; vouch notes 500 vs planned 200; academic year 1–7 vs planned 1–6). Fix .env.example's instruction to copy key=value content into a JSON file; .NET does not automatically load .env and Compose does not forward undeclared settings. Document all SMTP, CORS, storage, secrets, verification, migration, test and worker configuration. Ensure local JSON does not override deployment environment secrets. Add structured logs for missing business transitions without logging private content.

## SRS traceability

Statuses describe this repository's evidence, not whole-product certification. Backend code marked present still needs acceptance testing. SRS future-product Phases 2/3 are distinct from the seven phases of the supplied backend implementation plan.

| SRS ID | Current assessment | Required completion/evidence |
|---|---|---|
| A1 | Partial/unsafe | Validated identity-bound invites and manual approval. |
| A2 | Present rule, operationally unverified | 30 genuine active ambassadors on pilot campus. |
| A3 | Outside code scope | Student Union/faculty recruitment evidence. |
| A4 | Backend flag present | Secure assignment and frontend Character Card badge. |
| A5 | Partial | Correct eligibility cohort, onboarding and current gate state. |
| A6 | Present calculator/dashboard | Accurate scoped metrics and frontend display. |
| 1 | Missing ownership verification | University-approved verified identity/campus flow. |
| 2 | Defective enforcement | Three distinct active verified peers; ambassador exemption only after approval. |
| 3 | Partial | Curated values, valid interests and onboarding completion. |
| 4 | Backend present | Photo remains optional in UI and API flows. |
| 5 | Partial | Peer vouch requests and predefined nonzero traits. |
| 6 | Unique pair index present | Concurrent duplicate submission returns a controlled conflict. |
| 7 | Calculator present/tested | Atomic score/count updates, consistency after deletion. |
| 8 | Weight dampening present | Explicit clique flag/evidence and service-level scenarios. |
| 9 | Calculator present/tested | Verified active-voucher checks, 14-day boundary tests. |
| 10 | Partial/broken delivery | Persistent alerts and authorized recipient delivery. |
| 11 | Unsafe under concurrency | Enforced participant/cycle invariant and guarded acceptance. |
| 12 | Backend reflection present | Stable daily fallback and frontend considered state. |
| 13 | Scorer present/untested | Scorer scenarios; resolve faculty proximity/diversity wording. |
| 14 | Faculty diversity present | Clarify whether department diversity also affects scoring. |
| 15 | Backend transport present | Letter UI typography/animation and reliable delivery. |
| 16 | Partial | Validated client content types and frontend restrictions. |
| 17 | Partial/privacy gap | Private staged artistic photo variants; visual acceptance. |
| 18 | Default present, administration missing | Architect-controlled 20–80 setting with audit. |
| 19 | Calculator present/tested | Concurrent persisted counters; short/whitespace input behavior. |
| 20 | Stage logic present/tested | Actual authorized 25/60/100 image progression. |
| 21 | Backend present | Guarded pause state and durable recipient notification. |
| 22 | Defective | Only pause owner resumes; no auto-archive of paused chats. |
| 23 | Partial | Exactly-once-per-episode nudge, offline delivery and correct archive timing. |
| 24 | Partial | Three unique suggestions tied to each new match. |
| 25 | Partial | Shared-theme-only generation and values-only grounding. |
| 26 | 50-item library present | Grounded fallback tests for all supported combinations. |
| 27 | Timeout present, unverified | Measured total budget and failure-injection tests. |
| NFR-1 | Missing | Native AOT prototype and decision, or approved MVP amendment. |
| NFR-2 | Frontend unavailable | Mobile UI, default dark mode, Lighthouse >=95. |
| NFR-3 | Unverified | Match/profile p95 <200 ms under defined normal load. |
| NFR-4 | Partial/defective | PII inventory, encrypted storage, migrations, key lifecycle. |
| NFR-5 | Unverified deployment | TLS 1.3 minimum at actual client-facing ingress. |
| NFR-6 | Defective | All associated data removed within 30 days, verified recovery. |
| NFR-7 | No advertising integration found | Access/egress/log controls proving graph confidentiality. |
| NFR-8 | Unverified | 5,000 concurrent users per campus under specified workload. |
| NFR-9 | Inadequate controls | Tenant authorization plus chosen breach/performance containment. |
| NFR-10 | Partial | Report profile/message with correct ownership/linkage and UI. |
| NFR-11 | Future matching hides user | Current/cache visibility policy and reporter access. |
| NFR-12 | Partial | All reports accessible; retryable severity emails within one hour. |
| NFR-13 | Defective | Blocks enforced across profiles, messages, matches, hubs and photos. |
| NFR-14 | Defective | Third upheld report suspends atomically; manual review workflow. |

## Revised completion sequence

These work packages supersede the unchecked phase tracker as the practical execution order. Add regression tests with every correctness fix rather than postponing all tests to a final phase. Use PostgreSQL integration tests, not an in-memory substitute, for encryption columns, migrations, concurrency, query translation and transactional rules.

### Work package 0 — Agree the acceptance baseline and recover release builds

- [ ] Reconcile SRS filename/content version; record MVP scope and explicitly deferred future Trust Graph/community features.
- [ ] Decide campus cycle timezone/semantics, faculty proximity/diversity priority, launch eligibility cohort, deletion retention semantics and reveal settings policy.
- [ ] Resolve ImageSharp licensing/dependency; exclude local secrets from Docker context and remove fixed deployable credentials.
- [ ] Establish an isolated PostgreSQL test environment and integration test project.
- [ ] Time-box Native AOT compatibility investigation and record the production deployment decision.

Exit: clean Release build/container publish; agreed requirement decisions recorded; test database never targets development/production data. Depends on no other work package.

### Work package 1 — Repair identity, encrypted persistence and database lifecycle

- [ ] Fix normalized email lookup/hash uniqueness, ciphertext sizing and authorized DTO decryption.
- [ ] Build migration/backfill with dry-run reporting, collision handling, backup and rollback; repair secure admin bootstrap.
- [ ] Implement email ownership/campus verification and robust invite redemption.
- [ ] Enforce current account state; add rotating refresh-token/logout/revocation lifecycle.
- [ ] Partition rate limiting; validate startup configuration and environment precedence.
- [ ] Replace EnsureCreated lifecycle with tested migration/seed deployment steps.

Exit: verified register → onboard → login → refresh → logout works; duplicate/corrupt data, invite races, suspended/deletion access and old-schema upgrades pass. Depends on package 0.

### Work package 2 — Make safety and privacy reliable

- [ ] Apply campus/block/resource checks to all endpoints, hubs, storage and services.
- [ ] Require active verified vouchers; protect vouch counters/scores against lost updates.
- [ ] Fix atomic/idempotent report decisions and suspension/manual-review workflow.
- [ ] Replace public original-photo storage with authorized durable staged imagery.
- [ ] Repair deletion deadlines, storage cleanup, transactions and derived metrics.
- [ ] Add durable moderation/trust alerts with retry/deadline tracking; complete scoped report listings.

Exit: P0 findings resolved; adversarial cross-campus/block/nonparticipant tests pass; exactly-third-report suspension, deletion failure recovery and SMTP outage tests pass. Depends on package 1.

### Work package 3 — Complete matching, conversation and onboarding behavior

- [ ] Enforce one participant reservation per cycle and one conversation per match; correct response semantics and state transitions.
- [ ] Implement cycle-aware durable scheduling, catch-up and multi-instance leases; fix cache invalidation/versioning.
- [ ] Add curated onboarding completion, vouch requests, caller-aware profile contract and gate recalculation.
- [ ] Enforce pause owner/state transitions, inactivity episodes, post-commit events and concurrent reveal counters.
- [ ] Add Architect reveal settings and authorized intermediate photo variants.
- [ ] Complete three unique shared-theme icebreakers and bounded grounded fallback.
- [ ] Finish scorer/service/API regression tests from original Steps 25–27, including the failures found in this review.

Exit: full two-user journey and 30-ambassador bootstrap run end to end; repeat/concurrent requests remain correct; restart/offline and boundary-clock cases pass. Depends on packages 1–2.

### Work package 4 — Prove production operation and finish developer experience

- [ ] Add OpenTelemetry traces/metrics, structured event logs and privacy-safe dashboards.
- [ ] Alert on worker failures/missed runs, SMTP deadline risk, deletion backlog, p95 regressions and abnormal trust activity.
- [ ] Benchmark matching; introduce safe candidate indexing only after quality-equivalence tests.
- [ ] Load-test agreed normal and 5,000-concurrent-user scenarios, including SignalR; verify tenant resource containment.
- [ ] Run migration/backup restoration, container smoke, TLS ingress, health/readiness and rollback checks.
- [ ] Repair README/.env.example/Compose; document required settings, local startup, verification, admin bootstrapping, migrations and tests.
- [ ] Expand CI to enforce release build, unit/integration tests, image publish and isolated smoke checks.

Exit: reproducible documented deployment with measured SRS targets, restore/rollback evidence and actionable monitoring. Depends on packages 0–3.

### Work package 5 — Full-product acceptance and campus pilot

- [ ] Review the frontend repository separately, or create it if none exists; do not infer frontend completion from backend contracts.
- [ ] Implement/verify mobile onboarding, incubation, vouch requests/cards, daily match/reflection, letter conversations, reveal, pause, report/block/delete and Architect screens.
- [ ] Allocate explicit Monastic Minimalism design review; verify typography, spacing, motion, dark mode and accessibility; run Lighthouse >=95.
- [ ] Secure the Student Union partnership and recruit 30 ambassadors across diverse faculties; record manual verification.
- [ ] Run the SRS's two-week ambassador-only closed beta; assess match quality and safety response times.
- [ ] Confirm onboarded ambassadors meet the agreed vouch target and readiness is 100%; obtain launch acceptance against the traceability matrix.

Exit: pilot acceptance evidence and all critical requirements satisfied; only then open general registration. Frontend work can overlap stable backend contracts, but public launch depends on packages 0–4 and completed pilot checks.

## Definition of done

The MVP is complete only when all of the following are true:

1. Every SRS requirement has passing acceptance evidence, an explicitly external owner, or a documented approved amendment. No critical requirement is silently deferred.
2. Release build, unit/integration tests, migrations, publish, container startup and CI pass from a clean checkout.
3. Identity verification, ambassador redemption, login, revocation and campus boundaries work under normal and adversarial tests.
4. The complete user journey is accepted: verify/register → onboard/incubate → three active-peer vouches → daily match → mutual acceptance → letters/reveal → pause/resume → report/block/delete.
5. Repeated/concurrent requests, worker restarts and offline notifications preserve database and product invariants.
6. Encryption, deletion, private photo access and report response deadlines have verification evidence.
7. Latency, concurrent-user capacity, TLS and campus containment are measured in the intended deployment design.
8. Frontend design/mobile/Lighthouse requirements and the two-week pilot are accepted.

## Recommended first implementation batch

Start with **release build recovery + PostgreSQL integration harness + encrypted email lookup/migration + invite validation + block/hub authorization + exactly-third-report suspension**. These give the greatest immediate reduction in broken core flows and safety risk. Complete their regression tests before considering them closed.

This review does not assign a percentage complete: the backend is substantial, but integration behavior, frontend scope and production evidence are too incomplete for a defensible whole-project percentage or delivery date. Estimate each work package after the SRS decisions and frontend inventory are available.
