# API/PostgreSQL integration tests

The harness uses the real API, JWT middleware, Npgsql and PostgreSQL migrations. It never reads `ConnectionStrings:DefaultConnection` from your environment or local app settings. It requires **an explicitly configured dedicated test server on loopback**, with database `vouch_test_admin` and user `vouch_test_runner`. These names alone do not make an existing server disposable: use the separate Compose service or temporary server below.

Each test run creates an internally named `vouch_test_<random GUID>` database, upgrades it from the first migration to the current migration, and resets only that generated database between tests. Cleanup drops only databases this run successfully created, never the supplied admin database. No test calls EnsureDeleted on your connection string. Provisioning must have CREATEDB permission.

## Docker (PostgreSQL 17)

From the repository root in PowerShell:

```powershell
dotnet restore Vouch.slnx
$env:VOUCH_TEST_POSTGRES_PASSWORD = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
$env:VOUCH_TEST_POSTGRES_ADMIN = "Host=127.0.0.1;Port=55432;Database=vouch_test_admin;Username=vouch_test_runner;Password=$env:VOUCH_TEST_POSTGRES_PASSWORD"
docker compose -p vouch-tests -f docker-compose.test.yml up -d --wait
try {
    dotnet test Vouch.slnx --configuration Release --no-restore
} finally {
    docker compose -p vouch-tests -f docker-compose.test.yml down
    Remove-Item Env:VOUCH_TEST_POSTGRES_ADMIN, Env:VOUCH_TEST_POSTGRES_PASSWORD -ErrorAction SilentlyContinue
}
```

The Compose server stores its data in tmpfs, has no production volume and binds only to 127.0.0.1:55432. Never point it at an existing PostgreSQL data folder.

## Windows without Docker

Use trusted PostgreSQL binaries from your installation or the [official Windows distribution](https://www.postgresql.org/download/windows/). Run:

```powershell
dotnet restore Vouch.slnx
./scripts/Test-WithPostgres.ps1 -PostgresBin 'C:/path/to/pgsql/bin'
```

The runner creates a fresh cluster under the ignored `.test-runtime/run-<GUID>` folder, generates a password, binds to loopback on port 55432, runs the Release test suite and stops its own server in finally. It does not install a Windows service or use an existing cluster. If that port is occupied, pass `-Port 55433`; startup must succeed before tests can use that server. Runtime files/logs remain for investigation, and the plaintext password file is removed. Do not commit them. Use PowerShell 7.

Use `-Filter 'FullyQualifiedName~VouchingTests'` for a focused regression run; omitting it runs the full suite. Filtered runs can report no matching unit tests when targeting an integration class.

## Test boundaries

- The host uses the Testing environment. Development-only local JSON is ignored, startup reference-data seeding is disabled and the three business background workers are disabled.
- SMTP and AI use test implementations; most tests disable photos. Photo cases opt into the real processor and encrypted database storage using synthetic images in the generated disposable database. No real email, AI provider, Supabase or development-photo access occurs.
- A test-only startup filter simulates client socket addresses for rate-limit/proxy tests. It is defined only in the test project and is never registered in the production API.
- Configuration tests also verify local/environment/CLI precedence, deployment ignoring local JSON, database-target guards and spoofed-header behavior.
- Happy-path login after registration now runs alongside concurrent normalized duplicates, maximum Unicode fields, protected raw storage and readable authorized match/message/trust/moderation DTOs. No login test remains skipped.
- Upgrade cases create additional internally generated databases on the same disposable test server. They exercise fresh installations, both genuine legacy EnsureCreated models, mixed plaintext/CBC identities, collision and wrong-key refusal, unknown schemas, interrupted conversion recovery, key rotation, readiness and secure admin provisioning/password reset. Legacy fixture writes use raw SQL only inside those generated test databases.
- CI starts a dedicated disposable PostgreSQL 17 service and runs the test project. Missing test-server configuration fails clearly; it never silently falls back to SQLite or your app database.
- Identity/invite cases cover single-use concurrent redemption, persistence rollback, strict campus domains, verified onboarding, challenge expiry/resend/reuse/identity binding and delivery failures, manual approval, and every-active-ambassador launch recalculation. Verification mail uses the recording sender, never real SMTP.
- Session/access cases cover refresh rotation/replay/concurrency, logout, status/password/role/campus revocation, legacy/expired credentials, revocation-trigger readiness, symmetric blocks and cross-campus resources, cached-match permissions, authenticated photo retrieval, deployment containment guards and actual TestServer WebSocket invocations/outbound delivery. They never connect to Supabase.
- Vouch/request cases cover concurrent distinct and duplicate submissions, nonzero traits, exact age/bonus/clique thresholds and persisted evidence, cap/removal correctness, eligibility/block recalculation, zero-weight activation, private request lifecycle and pagination, persistent rolling/daily/pending limits, transactional rollback and bulk cleanup. Upgrade tests also reject a changed request-index predicate.

- Moderation cases cover concurrent/idempotent decisions, the inclusive resolution-time 90-day boundary, immediate third-report suspension and session revocation, final-pending visibility, complete paginated queues, current campus restrictions on reports and related audits, message evidence linkage, manual suspension/reinstatement, encrypted immutable audit reasons, relational cleanup and upgrade from the previous schema. An injected audit insert failure verifies rollback of the report, suspension, totals and database session revocation.

- Notification cases cover report/vouch/nudge transaction rollback, offline restart recovery, inbox ownership/pagination/read receipts, actual Architect subscription authorization, source campus/block changes, concurrent leases, cancellation recovery, per-recipient SMTP retry, controlled outage recovery within one hour, overdue health/recovery history, cascade cleanup and previous-schema upgrade. Email is always an injected fake; tests never send real messages. Upgrade tests reject altered pending-work index predicates as well as altered request predicates.

- Photo cases use real synthetic-image processing and encrypted PostgreSQL storage. They cover all reveal stages, direct URL attempts, current daily preview expiry, stranger/Architect/campus/block restrictions, account-state changes, cached pointers, concurrent replacement and injected-write rollback, invalid/oversized multipart uploads, ciphertext binding, selective decryption, key rotation, restart, optional photos and account cascade cleanup. Native processing tests also cover format signatures, nonseekable input, dimensions, cancellation, orientation and metadata removal.

Step 14 deletion cases cover first-request/deadline preservation, concurrent requests/workers, immediate worker startup, complete shared-conversation erasure, surviving metrics, injected rollback/retry at message/user/receipt writes, cancellation and expired-lease recovery, exact deadline health/history, identity/receipt retention, campus-scoped status, authenticated expiring restore ledgers, CLI overwrite/backup guards, trigger readiness and legacy migration. Historical moderation/notification upgrade fixtures create separate older-schema databases instead of downgrading through irreversible erasure.

To run just the unit suite without PostgreSQL:

```powershell
dotnet test tests/Vouch.UnitTests/Vouch.UnitTests.csproj --configuration Release
```
