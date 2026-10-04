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

## Test boundaries

- The host uses the Testing environment. Development-only local JSON is ignored, startup reference-data seeding is disabled and the three business background workers are disabled.
- SMTP, AI and photo-storage services are replaced by test implementations. No real email, provider calls or photo writes occur.
- A test-only startup filter simulates client socket addresses for rate-limit/proxy tests. It is defined only in the test project and is never registered in the production API.
- Configuration tests also verify local/environment/CLI precedence, deployment ignoring local JSON, database-target guards and spoofed-header behavior.
- Happy-path login after registration now runs alongside concurrent normalized duplicates, maximum Unicode fields, protected raw storage and readable authorized match/message/trust/moderation DTOs. No login test remains skipped.
- Upgrade cases create additional internally generated databases on the same disposable test server. They exercise fresh installations, both genuine legacy EnsureCreated models, mixed plaintext/CBC identities, collision and wrong-key refusal, unknown schemas, interrupted conversion recovery, key rotation, readiness and secure admin provisioning/password reset. Legacy fixture writes use raw SQL only inside those generated test databases.
- CI starts a dedicated disposable PostgreSQL 17 service and runs the test project. Missing test-server configuration fails clearly; it never silently falls back to SQLite or your app database.
- Identity/invite cases cover single-use concurrent redemption, persistence rollback, strict campus domains, verified onboarding, challenge expiry/resend/reuse/identity binding and delivery failures, manual approval, and every-active-ambassador launch recalculation. Verification mail uses the recording sender, never real SMTP.

To run just the unit suite without PostgreSQL:

```powershell
dotnet test tests/Vouch.UnitTests/Vouch.UnitTests.csproj --configuration Release
```
