# Vouching and peer requests

Step 10 makes trust totals authoritative and adds private peer requests. Apply `20261009213532_ConsistentVouchesAndPeerRequests` through the explicit [database upgrade procedure](database-operations.md) before running this API. No frontend or outbound request email is included; clients poll the persistent inbox. Durable notifications remain Step 12.

## Vouch submission and trust

`POST /api/vouches/` retains `{ targetUserId, traits, note }` and returns the existing vouch DTO. A voucher must be Active, university verified and onboarded. The recipient must be verified/onboarded, Active or InIncubation, on the same campus, and unblocked in both directions. Self-vouches, empty/unknown trait bits, missing IDs and notes over 500 characters fail validation. Any combination of the six supported nonzero trait flags is accepted. A voucher can endorse a recipient only once; repeated/concurrent submissions return HTTP 409 rather than a database error.

Submission holds the campus row lock in a transaction before reading eligibility or calculating weight. The record, request fulfillment, recipient metrics, incubation activation and campus readiness commit together. Parallel different vouchers preserve all records and counts. The unique voucher/recipient database index remains the final pair constraint.

| Rule | Submission-time result |
|---|---|
| Base | 1.0 |
| Voucher has at least five qualifying received vouches | Add 0.2 |
| At least three distinct qualifying peers endorsed both voucher and recipient | Multiply by 0.5 |
| Voucher is younger than exactly 14 days | Weight 0; still counts toward activation |
| Current qualifying received-weight sum | Cap the recipient score at 20 |

Weight is a historical snapshot. Aging, later bonuses or changing clique membership never reweights an existing record. `IsCliqueFlagged` and `MutualVoucherCountAtSubmission` retain the shared-peer threshold evidence without exposing a peer graph in public DTOs. Historical dampened records are flagged from their stored multiplier, with a null count because their original evidence was never recorded. This flags possible gaming for review; it does not automatically suspend an account.

Current count/score include only valid, distinct, same-campus, unblocked endorsements from Active, verified, onboarded vouchers to available verified/onboarded recipients. Tracked user/vouch/block writes recalculate these totals and launch readiness in the same transaction. Reinstatement/unblocking restores the original eligible weights; deletion or loss of eligibility removes their contribution. Recomputing from the remaining records preserves excess weight above the cap instead of subtracting from a capped score. Read-only trust summaries derive current metrics and hide inaccessible voucher names/notes.

Three qualifying peers activate an onboarded incubating member even if all three weights are zero. The first completion time is preserved. Later endorsement loss reduces metrics but does not automatically return an already activated member to incubation; suspension/deletion remain explicit account states. Approved ambassadors retain their existing verification/onboarding activation path. Blocks also disqualify ambassador launch endorsements.

Bulk account cleanup now deletes relational records and recomputes surviving trust/readiness under campus locks in a transaction; request rows cascade with either owner. Its existing deletion schedule, photos, retention and deadline guarantees still require Step 14. Operator SQL/imports bypass tracked hooks: stop writers and run the explicit upgrade/reconciliation procedure before resuming. Do not claim that arbitrary direct SQL automatically maintains cached counters.

## Private request flow

Verified/onboarded Active or InIncubation members may request a vouch from an Active, verified, onboarded same-campus peer. Both directions of a block deny the request. Asking a peer who already vouched fails with HTTP 409. No free-text message or suggested endorsement traits are supplied; the peer independently chooses whether and how to endorse.

| Endpoint | Result |
|---|---|
| `POST /api/vouches/requests` with `{ requestedVoucherId }` | HTTP 201, private request DTO |
| `GET /api/vouches/requests/incoming?page=1&pageSize=20` | Caller's inbox |
| `GET /api/vouches/requests/sent?page=1&pageSize=20` | Caller's sent history |
| `POST /api/vouches/requests/{id}/dismiss` | Requested voucher declines; HTTP 204 |
| `POST /api/vouches/requests/{id}/cancel` | Requester cancels; HTTP 204 |
| Normal vouch submission by the requested voucher | Fulfill the live request in the vouch transaction |

Lists return `{ items, totalCount, page, pageSize }`, with page 1–10000, pageSize 1–50 and stable creation-time/ID ordering. Each DTO includes participant IDs/names, status, creation/expiration/resolution times. Only the two participants can see their request; totals also exclude currently forbidden relationships. Another user cannot dismiss/cancel it, and being an Architect does not grant access to others' request inboxes. Status uses numeric values: Pending=1, Fulfilled=2, Dismissed=3, Cancelled=4, Expired=5.

The initial abuse policy allows at most five new requests in a rolling 24 hours, ten live pending requests, and one request to the same peer per seven days. Requests expire at exactly seven days. Dismissal/cancellation does not reset the daily budget or pair cooldown. Campus locking and a unique pending-pair index prevent parallel retries bypassing limits. Limits use stored history and survive API restarts. All vouch endpoints also use the authenticated standard rate limit (20 requests/minute); business limits return HTTP 409, while the HTTP rate limiter returns 429.

Expiry is represented during reads without writing. A later request after cooldown marks the previous expired pending row before inserting a new one. Repeating the same dismissal/cancellation is harmless. Fulfilled/expired requests cannot be resolved as pending. A new ordinary vouch may be submitted voluntarily even after dismissal or cancellation; a request never grants eligibility or forces an endorsement. Blocks or tracked loss of peer eligibility cancel pending requests and hide their identity data immediately; account deletion removes them.

## Upgrade and verification

The migration adds request history and clique evidence without deleting vouches or changing historical weights. The explicit upgrade then reconciles existing campus metrics/activation/readiness. The inspector compares table names independently of server collation and checks the exact pending-index predicate; altered schemas are refused. Use a verified backup/restore rehearsal and `--backup-confirmed true` for an existing database. Rollback restores the matching pre-upgrade backup/application; downgrading loses new request history and is not the recovery procedure.

Regression tests use disposable loopback PostgreSQL, synthetic accounts and the API. They cover distinct/duplicate concurrent writes, invalid traits and peers, exact age/bonus/clique boundaries, capped-score removals, status/block changes, zero-weight activation, private request ownership/lists, expiry/resend/daily/pending limits, failed-save rollback, account cleanup and schema-predicate drift. The existing match cache also received a narrow UTC-expiration correction discovered by the regression run; cycle/scheduling/cache redesign remains Step 16.
