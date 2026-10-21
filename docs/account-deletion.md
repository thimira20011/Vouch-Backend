# Account deletion and restore safety

Step 14 is implemented on `feature/step-14-account-deletion`. Apply `20261021175206_DurableAccountDeletion` before running this version. Use the [database operations](database-operations.md) backup/restore rehearsal and explicit upgrade procedure. The feature branch does not automatically upgrade shared Supabase development.

## Request and access

`POST /api/auth/delete-account` requires the caller's current session. It retains `message` and adds `requestId`, `requestedAt` and `deadlineAt`. The deadline is the first request time plus 30 days. Processing starts promptly; 30 days is the completion limit, not a waiting/cancellation period.

The request and durable queue entry commit with account-state changes, session revocation and campus metrics. Concurrent service retries return the same request ID/deadline. A PostgreSQL trigger preserves an existing request timestamp, keeps the account in DeletionRequested and revokes sessions even for direct SQL timestamp changes. A revoked session cannot retry the HTTP endpoint: retain the first successful response and clear client credentials. Missing/disabled preservation triggers fail readiness.

## Processing and failures

The worker runs immediately at startup, then every minute when `BackgroundJobs:Enabled=true`. Production requires enabled jobs. Each pass reconciles missing/imported queue entries and attempts up to 50 due requests. PostgreSQL leases and `SKIP LOCKED` claims prevent simultaneous claims. Cleanup has a 90-second cancellation timeout and a two-minute crash-recovery lease. Failures schedule exponential retries from one minute to a maximum one hour without extending deadlines or blocking all other requests.

Cleanup locks campus rows in a consistent order and commits erasure, surviving metrics and completion evidence together. It removes the account, photos, sessions/refresh tokens, verification challenges, vouches, peer requests, matches, blocks, related reports/audits, relevant notifications/receipts and invitations issued to, redeemed by or issued by the account. Scalar Architect/approver references are cleared. Ambassadors approved by a deleted account need a new recorded Architect approval to qualify for the launch gate. Unrelated accounts/conversations remain.

**Policy selected by the user on 21 October 2026:** delete shared conversations and all their messages, including messages sent by the other participant. Reports tied to erased messages are removed as well. Partner trust/report totals and launch readiness are recomputed. Losing an endorsement does not automatically return an already activated member to incubation.

## Evidence and monitoring

`AccountDeletions` records the opaque request ID, first request/deadline, attempts, next attempt, lease, fixed failure code, completion time, missed-deadline history and policy version. The live user FK is cleared at completion. Worker logs contain request IDs and fixed messages, without email, content, tokens, keys or provider exceptions.

`GET /api/architect/deletions/status` returns counts for the current active Architect's campus: pending, at risk, overdue, completed and missed deadlines. Other roles cannot use it. It is `no-store` and exposes no individual receipt or restore identifier. Counts cover retained receipts, not an all-time total.

Health check `account-deletion-deadlines` is degraded when pending work is at least one day old or intent has no queue entry. It is unhealthy at the inclusive 30-day deadline for pending erasure or an expired restore identifier awaiting purge. Recovery restores health while retaining missed-deadline history. Degraded health can still produce HTTP 200 under default framework mapping; monitor individual check status too. Production alert routing/outage drills remain Step 21.

## Retention

Profile/content/token/photo erasure is prompt. A minimal encrypted, request-bound user identifier remains solely to replay erasure into older backups. It is cleared by the **original request deadline**, not 30 extra days after completion; late completions clear it immediately. Copying ciphertext between requests fails validation. Protected-data inspection/backfill/key rotation cover this field.

Receipts without a user identifier expire 90 days after completion. Purges run even when no account is due. Application file logs default to 14 retained daily files; proxy/platform/SMTP retention needs matching operator configuration. This change does not silently delete existing external backups, log archives or provider mailboxes.

Supported production backup retention is **at most 30 days from capture**. A pre-request snapshot must age out before its restore identifier expires; a post-request snapshot contains deletion intent for reconciliation on restore. Preserve the key ring for retained snapshots. Backups predating receipt collection, older snapshots, missing authoritative ledgers and provider retention need separate recovery review. A successful database restore alone does not authorize reopening traffic.

Ledger exports are temporary encrypted recovery files: valid for at most 24 hours and never beyond the earliest included completed request's deadline. Export immediately for recovery and delete the file immediately afterward. Arbitrary operator export paths cannot be purged automatically. Do not put ledgers in Git, logs, public storage or HTTP responses. Production backup/log policy enforcement and offsite drills remain Step 21 acceptance.

## Restore procedure

Stop writers on the authoritative source and keep the restored deployment offline. Preserve a protected backup and export a **fresh** ledger with the matching current application and keys. The private directory must exist and the output file must not exist:

```powershell
# Current authoritative source database configuration.
dotnet run --project src/Vouch.Api --configuration Release --no-build --no-launch-profile -- --database-task export-deletions --deletion-ledger-path 'C:/private-recovery/deletions.ledger'
```

Restore a supported-age backup to the reviewed target, then apply the current migration/protected-data upgrade with backup acknowledgement. Switch local/environment configuration to that restored target before replay; keep passwords/connection strings off the command line.

```powershell
# Restored target configuration, retained source keys, writers still stopped.
dotnet run --project src/Vouch.Api --configuration Release --no-build --no-launch-profile -- --database-task replay-deletions --deletion-ledger-path 'C:/private-recovery/deletions.ledger' --backup-confirmed true
dotnet run --project src/Vouch.Api --configuration Release --no-build --no-launch-profile -- --database-task inspect
```

The purpose-authenticated ledger's expiry, version and entries are validated before import. Replay re-establishes original request times, erases restored targets and processes restored pending requests. It is restartable and fails if a ledger target remains; resolve failures/retry while traffic stays disabled. Wrong keys, altered content and expired ledgers are refused. Export refuses overwrite; replay requires backup acknowledgement. Offline commands share the database-operation advisory lock and start no HTTP listeners or identity-bearing output.

Verify readiness/deletion status, remove the temporary ledger, then resume traffic. Rehearse the complete workflow in production before launch.

## Legacy requests and rollback

The migration queues existing deletion intents immediately. Recorded request times are preserved; missing times use UpdatedAt/CreatedAt evidence and are flagged `RequestTimeInferred`, without granting a new 30-day window. Future legacy times are clamped to migration time. An existing request timestamp makes an account deletion-requested even if a historical status differs. The explicit protected-data upgrade builds encrypted restore identifiers from structural queue references; the migration writes no plaintext to protected columns.

A schema downgrade cannot reconstruct erased data and is refused. Reviewed rollback requires the current erasure ledger, a supported backup and erasure replay before traffic resumes. An older application that ignores deletion intent is not a safe rollback.
