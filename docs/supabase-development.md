# Supabase development database

Supabase hosts PostgreSQL, so this backend continues using Npgsql, EF migrations and its own authentication/verification. Use a project dedicated to Vouch development. Later production can use a separate Supabase project or another compatible PostgreSQL server, with a different connection string and independent deployment secrets. [Supabase connection guide](https://supabase.com/docs/guides/database/connecting-to-postgres).

## Connection settings

In the project's **Connect** panel, copy the Session pooler host, port, database and username. Use session mode on port 5432 for a Windows developer network without IPv6. Direct connections also work with IPv6 (or the project's IPv4 option). Do not use the transaction pooler for the existing upgrade command: its session advisory lock requires a stable server session. [Connection modes](https://supabase.com/docs/guides/database/connecting-to-postgres).

The supplied development project's Session pooler is `aws-0-ap-northeast-2.pooler.supabase.com`, database `postgres`, user `postgres.wgeuieogoqagbwvhwbhi`, port `5432`. Its direct host resolved only to IPv6; no IPv6 default route was found here. The password is saved locally and the connection is verified with `SSL Mode=VerifyFull` and the supplied Supabase root certificate.

Keep passwords out of chat/source. Use the **database password**, not an anon/service-role API key. With your existing gitignored `src/Vouch.Api/appsettings.Local.json`, run:

```powershell
./scripts/Set-SupabaseDevelopment.ps1 -DatabaseHost 'aws-0-ap-northeast-2.pooler.supabase.com' -DatabaseUser 'postgres.wgeuieogoqagbwvhwbhi'
```

The command prompts privately on initial setup and changes only `ConnectionStrings:DefaultConnection`. Subsequent calls reuse the saved password only when host, port, database and username all match; use `-PromptForPassword` to replace an incorrect password. Certificate updates retain that saved credential. It preserves application keys/other settings and never connects, migrates, creates accounts or rotates application secrets. Keys may be in local JSON or the environment and are validated at API/database-command startup. If `Security:EmailLookupKey` is absent everywhere, configure a new independent key for a new/legacy database; protected email lookup requires its original key. Local JSON is Git-ignored and excluded from build/publish, but contains a plaintext database credential; restrict access to the development machine.

TLS uses `SSL Mode=VerifyFull` and a small client pool of ten connections. If the certificate is not trusted by your machine, obtain the project's root certificate from Supabase's Database settings and supply `-RootCertificate 'C:/path/to/project-root.crt'`. Do not disable certificate validation to fix a certificate error. [Npgsql TLS verification](https://www.npgsql.org/doc/security.html).

For this development project, download the certificate using [Database Settings → SSL Configuration](https://supabase.com/dashboard/project/wgeuieogoqagbwvhwbhi/database/settings), then:

```powershell
./scripts/Set-SupabaseDevelopment.ps1 -DatabaseHost 'aws-0-ap-northeast-2.pooler.supabase.com' -DatabaseUser 'postgres.wgeuieogoqagbwvhwbhi' -RootCertificate 'C:/Users/THIMIRA/Vouch-Backend/.test-runtime/prod-ca-2021.crt'
```

Use the actual downloaded filename. Database command errors now distinguish TLS, authentication, missing database, network and timeout failures without printing provider details or credentials.

The configured certificate must remain at this path while the local connection setting refers to it. If you move it, rerun the setup command with its new path; the existing password and application keys are preserved.

Environment variables/CLI override local JSON. An existing `ConnectionStrings__DefaultConnection` variable must be removed or deliberately updated, or the API will continue using it. `.env` is Compose syntax; local `dotnet run` does not load it. The default `docker-compose.yml` deliberately connects to its local PostgreSQL service; the Supabase instructions below use the .NET CLI.

## Inspect and initialize

First confirm whether this project has existing tables/data. The upgrade inspector examines the `public` schema and refuses unknown/shared application schemas; do not remove those tables to make inspection pass. Use an empty dedicated project, or plan a reviewed migration of existing Vouch data with its original encryption keys. Supabase-managed schemas remain managed by Supabase.

Before creating Vouch tables, disable the project's **Data API** if no other application needs it. Vouch talks to PostgreSQL through its backend; direct REST/GraphQL grants must not bypass its permissions. Existing Supabase projects may automatically grant access to new public tables. If another app relies on Data API, first review and restrict grants/default privileges specifically for Vouch instead of disabling a shared API. [Supabase API security](https://supabase.com/docs/guides/api/securing-your-api).

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/Vouch.Api --configuration Release --no-launch-profile -- --database-task inspect
# Only after confirming an empty dedicated development schema:
dotnet run --project src/Vouch.Api --configuration Release --no-build --no-launch-profile -- --database-task upgrade
dotnet run --project src/Vouch.Api --configuration Release --no-build --no-launch-profile -- --database-task seed
dotnet run --project src/Vouch.Api --configuration Release --no-build --no-launch-profile --urls http://localhost:5000
```

Existing data requires backup/restore rehearsal and explicit acknowledgement under [database operations](database-operations.md). Provision the Architect through that guide's secure command, configure SMTP for university verification, and confirm `/healthz`. Steps 6–7 migration behavior is described in [identity and onboarding](identity-and-onboarding.md).

Integration tests continue using disposable loopback PostgreSQL databases. Never point `VOUCH_TEST_POSTGRES_ADMIN` or the test runner at Supabase development/production.

## Development initialization record — 4 October 2026

Read-only inspection confirmed an empty `public` schema, with no existing Vouch records or migration history. Supabase-managed schemas were preserved. The operator confirmed that the project's Data API was disabled before Vouch tables were created; keep it disabled for this backend-only setup.

The reviewed fresh-install SQL is available locally at `.test-runtime/supabase-fresh-install.sql`. The explicit upgrade applied all four checked-in migrations through `20261004151659_SecureInvitesAndUniversityIdentity`, then initialized the protected-data verification marker. Reference campuses, reflections and prompts were seeded without creating accounts. Post-install inspection recognized all four migrations and reported no issues, collisions or rows needing rewrite.

A temporary Development API, with background jobs disabled, returned HTTP 200 `Healthy` at `/healthz` and successfully served `/api/auth/onboarding/options`. The temporary process was stopped after verification. Architect provisioning and real university verification emails still require operator credentials and SMTP configuration through the linked guides.

## Development upgrade record — 8 October 2026

Steps 8–9 applied `20261008120912_SessionsAndAccess` after a clean read-only inspection and review of `.test-runtime/step8-9-upgrade.sql`. Before the hosted upgrade, a public-schema backup was encrypted, decrypted and restored into a generated loopback database; the restored copy upgraded successfully with unchanged counts: four campuses, zero users/invites, six reflections and 50 icebreakers. Supabase-managed schemas were excluded from this application backup and preserved on the hosted project.

The retained encrypted archive is `.test-runtime/supabase-step8-backup-d66a464c6e8c4f65a131885b8b31c0ab/source.dump.dpapi`. It is Git-ignored and protected by Windows CurrentUser DPAPI; recovery requires this Windows account's DPAPI profile/key material. Temporary plaintext dump/password files were removed. This local development rehearsal does not replace portable offsite production backups or the production recovery drills in Step 21.

Post-upgrade inspection recognized all five migrations and reported no issues, collisions or rows needing rewrite. Reference records remained intact. A temporary API with workers disabled returned HTTP 200 `Healthy` and onboarding options; it was stopped after checking. No accounts were created. Keep Data API disabled. Existing clients must sign in again and implement the [refresh/logout flow](sessions-and-access.md).

## Production cutover

Create a separate production database and choose its backup/retention/connection capacity for the workload. Apply the same checked-in migrations and reference seeds through explicit deployment commands; provision the production Architect securely. Configure production connection/TLS, SMTP, CORS and secret-manager values, then verify readiness and the user journey before directing clients to it.

Use a separate database and deployment for each production campus, with independent credentials/application keys/storage/compute and `Tenancy__CampusCode`. See [campus containment](sessions-and-access.md#campus-containment-and-deployment) for the startup guard and remaining infrastructure acceptance checks.

Prefer a clean production database without development test users. If real user data must move, rehearse a database backup/restore and carry its exact encryption ring/email lookup key with it; fresh keys cannot decrypt old ciphertext. Stop writers for the cutover, verify counts/keys/readiness and retain the source backup for rollback. Do not silently switch development infrastructure or copy Supabase-managed schemas into a different provider.
