# University identity, ambassadors and onboarding

Implemented for roadmap Steps 6–7 on 4 October 2026. Baseline: documented SRS v2.0.0 contents. The user selected **every active ambassador**, including those above the minimum, for launch eligibility.

## API flow

1. `POST /api/auth/register`: email's entire domain must equal the selected campus's `DomainPattern` (leading `@` optional). Other `.ac.lk` domains, lookalikes and unlisted subdomains fail. All users, including invited ambassadors, start `InIncubation`. Responses include `emailVerified` and `onboardingCompleted`, initially false.
2. With the returned bearer token, `POST /api/auth/verification/request` (no body). The token is sent to the stored university mailbox, never returned by the API. Missing SMTP/delivery failure returns 503 and rolls back the challenge/cooldown; there is no verification bypass.
3. `POST /api/auth/verification/confirm` with `{ "token": "TOKEN_FROM_EMAIL" }`. A challenge is bound to user, campus and normalized email lookup, expires after 30 minutes, and is single use under concurrency. Resend replaces the old challenge after a one-minute cooldown. Both routes use strict per-client auth rate limiting.
4. `GET /api/auth/onboarding/options` exposes the catalogue. `POST /api/auth/onboarding` requires verified email, optional/empty bio <=280 characters, 1–5 distinct exact catalogue values and 1–5 distinct valid numeric interests. Null collections and empty/unknown/duplicate choices fail. Photos remain optional and use the separate upload route after verified onboarding.
5. `GET /api/auth/identity` exposes current flags/status. Approved ambassadors activate after verification/onboarding. Ordinary users need three distinct vouches from active, verified, onboarded peers on the same campus; a legacy count alone cannot activate them.

Unverified sessions can request verification, complete onboarding when verified, read their own trust/status, request deletion and use safety reporting. Matching, conversations, icebreakers, realtime groups and vouch submission require eligible membership. Eligibility checks read current database flags, so an existing token can finish onboarding without re-login.

Tokens use 32 random bytes encoded as URL-safe Base64; only SHA-256 digests persist. Verification responses/logs contain no bearer token. Email retains existing encryption/keyed lookup protection. Challenge rows cascade-delete with the user.

Configure `Smtp__Host`, `Smtp__Port`, `Smtp__User`, `Smtp__Password` and optionally `Smtp__FromAddress` (blank uses the SMTP user). Verification uses STARTTLS and a 15-second cancellation budget; port 587 is the normal setting. Implicit TLS on 465 is unsupported by this sender. Tests use a recording sender, with no real outbound email. Validate real mailbox delivery and each seeded domain with the university before pilot acceptance; seeds do not prove university approval or enrollment.

## Ambassador approval

`POST /api/moderation/invites` requires a current Active Architect, `campusId` and `intendedEmail` matching that campus. Complete the student's manual review before issuance. The raw token is returned once and stored as a digest. Any supplied token is validated on locked and unlocked campuses; blank, invalid, expired, reused, wrong-campus and wrong-email tokens fail. Omit it for ordinary unlocked-campus registration.

Conditional redemption and registration share one PostgreSQL transaction. Failed persistence leaves the invite unused. Redemption records user/time, and the user records approving Architect/time. Founding badges require valid redemption or explicit approval; account activation still requires verified onboarding.

Existing accounts use `POST /api/architect/ambassadors/{userId}/approve`. It requires an Active Architect of the student's campus and an available, verified, onboarded student. It cannot reinstate suspended/deletion-requested accounts or convert an Architect. Repetition preserves original actor/time. Steps 8–9 replace the earlier global-Architect behavior with campus-scoped issuance and approval; see [sessions and access](sessions-and-access.md).

## Launch gate

At least `RequiredAmbassadorsForLaunch` (default 30) must be Active. Every Active ambassador needs verified onboarding, recorded Architect approval and `RequiredVouchesPerAmbassador` (default 2) outgoing vouches to distinct available, verified, onboarded peers on the same campus. Incubating recipients qualify; suspended/deletion-requested recipients do not. Young-account zero-weight vouches qualify for the count.

Step 10 additionally excludes endorsements blocked in either direction and recomputes trust/counts alongside readiness. Incubating verified/onboarded members can use [private peer requests](vouching-and-peer-requests.md) to seek their three endorsements.

Readiness assigns 50% to reaching the minimum and 50% to the qualifying fraction, using the larger of minimum and actual Active count as denominator. Tracked user/vouch writes recalculate the gate inside the same transaction with a per-campus row lock. Dashboard GET uses the same query without writing. Extra nonqualifying ambassadors, status changes and removed vouches affect the gate. Future bulk SQL paths must explicitly maintain eligibility: `ExecuteUpdate`/`ExecuteDelete` bypass the hook. Direct campus threshold changes also require recalculation.

## Upgrade and rollback

Follow [database operations](database-operations.md): stop writers, inspect, back up/rehearse, then explicitly upgrade with backup acknowledgement. No real application database was upgraded during implementation.

Migration `20261004151659_SecureInvitesAndUniversityIdentity` hashes old invites before dropping plaintext. Recipient-less legacy invites remain for audit but cannot redeem. Existing identities are not automatically verified/onboarded. Active non-Architect users return to incubation, legacy ambassador badges are removed, and campus gates/readiness reset. Architect credentials/roles remain. Students verify/onboard; legacy ambassadors then need explicit manual approval.

Hashing is irreversible. Downgrade is refused; rollback restores the reviewed pre-upgrade backup with matching old application/configuration. Synthetic tests do not replace rehearsal with the real backup.

Steps 8–9 add [sessions and access](sessions-and-access.md), including refresh/revocation, campus/block/resource checks, realtime delivery and authorized local photo retrieval. Steps 10–12 retain concurrent vouch counters/requests, moderation and durable delivery/deadlines. Durable encrypted photos and intermediate reveal variants remain Step 13; production acceptance remains Step 21.
