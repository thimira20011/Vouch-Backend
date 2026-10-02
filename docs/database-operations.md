# Database installation, upgrades and protected data

Updated 2 October 2026. These operations are explicit deployment tasks. API startup never creates schemas, migrates, backfills, seeds or creates privileged accounts. `/healthz` checks the migration history, conversion marker, lookup-key verification, encryption canary and an email read; a PostgreSQL ping alone is insufficient. The root endpoint points to readiness rather than declaring the database operational.

## Configuration

Provide `ConnectionStrings__DefaultConnection`, `Security__EncryptionKey` and an **independent** `Security__EmailLookupKey` through the process environment or secret store. Local JSON loads only in Development. The lookup key is required, at least 32 characters, and stable across encryption rotations. Existing deployments must retain their exact original encryption key to decode legacy data. Generate a new independent lookup secret; do not replace the old encryption key during the first upgrade.

JWT/CORS/SMTP settings are needed by the API as documented in the README. Database tasks exit before API services, listeners, email/AI or business workers start. Never pass passwords or encryption keys on the command line. The design-time EF factory reads environment settings and does not load application local files; it is for generating/reviewing migrations, while the explicit operations below perform data preflight and backfill.

For commands below, run from the repository root with .NET 10 and PowerShell 7. Build once:

```powershell
dotnet restore Vouch.slnx
dotnet build Vouch.slnx --configuration Release --no-restore
```

Commands use the Release assembly. Its environment must contain the intended database and keys. For local JSON, set Development and use `dotnet run --project src/Vouch.Api --configuration Release --no-build --no-launch-profile -- --database-task inspect` instead; the working content root then includes that local file. Published deployments run `dotnet Vouch.Api.dll` with environment/secrets configuration.

## Fresh install

Create an empty PostgreSQL database and an operator account with schema creation/migration privileges. Do not reuse a shared database. Then:

```powershell
dotnet src/Vouch.Api/bin/Release/net10.0/Vouch.Api.dll --database-task inspect
dotnet src/Vouch.Api/bin/Release/net10.0/Vouch.Api.dll --database-task upgrade
dotnet src/Vouch.Api/bin/Release/net10.0/Vouch.Api.dll --database-task seed
```

`inspect` is read-only and reports recognized migration IDs, row counts, collisions and issues using row IDs; it prints no emails, plaintext, ciphertext, connection strings or keys. `upgrade` applies migrations to an empty database and records successful protected-data initialization. `seed` inserts campuses, reflections and curated prompts, with no accounts. Repeating these operations preserves data. Subsequent upgrades of an existing schema require the backup acknowledgement below.

For local Docker Compose, fill the four secrets in `.env` first, then:

```powershell
docker compose up -d postgres --wait
docker compose build backend
docker compose run --rm --no-deps backend --database-task inspect
docker compose run --rm --no-deps backend --database-task upgrade
docker compose run --rm --no-deps backend --database-task seed
docker compose up -d backend --wait
```

An existing volume follows the upgrade procedure, including backups. Do not remove volumes to make a failed upgrade disappear. Database tasks mount the same configured volumes but do not process photos. The application database must exist; these commands do not provision PostgreSQL servers/accounts.

## Existing databases and EnsureCreated installations

1. Stop all API replicas and writers/jobs. Preserve the exact old application build, encryption keys, database credentials and photo storage. Allocate maintenance time; conversion holds exclusive locks on affected tables.
2. Take an encrypted PostgreSQL backup using your normal secret-management connection method, and restore it to a separate recovery database. Verify row counts, login data and photo references. Do not print credentials or place them in shell history. A backup acknowledgement is an operator assertion, not an automated backup.
3. Rehearse this upgrade against that restored copy, using a distinct explicitly supplied connection string and the correct legacy encryption key. Record database identity and key IDs in the deployment record, not the secret values.
4. Run `inspect`. Resolve every email collision and unreadable field from trusted source data before continuing. Collision reports list user IDs without emails; resolving ownership is a deliberate operator decision. The tool never merges/deletes users or chooses a winner.
5. Review pending migration SQL and the dry-run report. Run the applicable command:

```powershell
# A database with valid existing migration history:
dotnet src/Vouch.Api/bin/Release/net10.0/Vouch.Api.dll --database-task upgrade --backup-confirmed true

# ONLY a recognized, reviewed EnsureCreated schema without migration history:
dotnet src/Vouch.Api/bin/Release/net10.0/Vouch.Api.dll --database-task upgrade --backup-confirmed true --adopt-ensure-created true
```

6. Run `inspect` again; expect no issues/collisions and zero rows needing rewrite. Seed reference data only if required, then provision/verify the admin as below. Start the API with the same lookup key and retained encryption key ring. Verify `/healthz`, registration/login and an authorized profile/message read before ending maintenance.

The two supported untracked baselines are the schema represented by `20260917123615_AddAmbassadorInvites` and `20260927150805_Step19_MessageType`. Identification compares the complete public application table set, column types/nullability, known defaults, index definitions, primary keys and foreign-key constraints. Unrecognized tables/schema drift/history are rejected; the tool never guesses or drops tables. For a recognized EnsureCreated database, it records only verified already-present migrations transactionally, then applies subsequent migrations. Unknown variants, non-public search paths and custom server connection Options require a separately reviewed migration path before adoption.

Legacy registration encrypted email/name/bio with unversioned AES-CBC; the old admin seed stored those fields as plaintext. The converter identifies the known identity format by the email, decodes CBC using the original key, validates limits/UTF-8/email/JSON, normalizes email and reports duplicates. Other protected fields were previously plaintext. Legacy CBC was unauthenticated: format validation cannot prove historical integrity; compare against a trusted restored backup. Completed conversions fail on unversioned nonempty plaintext instead of silently accepting it.

## Failure and rollback

Preflight errors stop before baseline/schema changes. Each schema migration is transactional; the protected-data rewrite, lookup hashes and completion marker commit in one serializable transaction while application tables are locked. Deployment commands share an advisory lock. If conversion fails after schema expansion, the expanded schema/history may remain, but the failed rewrite rolls back and readiness stays unhealthy. Correct the reported key/data issue and retry; internally generated `pending:<row ID>` values cannot pass readiness. Recovery tests cover this interrupted state and successful retry.

Do not run the old application against converted data. The new migration deliberately refuses `Down`: shrinking ciphertext columns or reverting only the schema would destroy data/readability. To roll back, keep writers stopped, restore the verified pre-upgrade database and matching old application/keys, reconnect the unchanged photo storage, verify recovery, then resume. Keep the failed upgraded database isolated for investigation. Test the restore procedure on a separate server before applying to live data.

## Architect provisioning

Provide `Bootstrap__Email`, `Bootstrap__FullName`, `Bootstrap__CampusCode` and `VOUCH_BOOTSTRAP_PASSWORD` only to the provisioning process. Choose a strong unique password, at least 12 characters and no more than 72 UTF-8 bytes (BCrypt limit). The email can be an operator-controlled address; ordinary university registration rules do not grant administrator privileges.

```powershell
# Set Bootstrap variables using your secret store/environment configuration.
# Read the password privately; do not embed it in a command or committed file.
$env:VOUCH_BOOTSTRAP_PASSWORD = Read-Host 'Architect password' -MaskInput
try {
    dotnet src/Vouch.Api/bin/Release/net10.0/Vouch.Api.dll --database-task provision-admin
} finally {
    Remove-Item Env:VOUCH_BOOTSTRAP_PASSWORD -ErrorAction SilentlyContinue
}
```

Compose operators can pass environment variable names without exposing their values in arguments:

```powershell
docker compose run --rm --no-deps -e Bootstrap__Email -e Bootstrap__FullName -e Bootstrap__CampusCode -e VOUCH_BOOTSTRAP_PASSWORD backend --database-task provision-admin
```

Provisioning verifies database readiness, serializes operators, encrypts identity data, hashes the password and creates one active Architect. Repeating with the same identity/password is idempotent. It refuses to promote an ordinary account, create another Architect, reactivate a suspended/deletion-requested account or silently replace credentials. Output contains only the user ID.

If the earlier public fixed admin password was deployed, explicitly rotate that account after reviewing its identity and recovery plan. Use its existing email in Bootstrap settings, provide the new password privately and add `--reset-admin-password true --backup-confirmed true` to `provision-admin`. This changes only that existing active Architect's password. No credentials have been rotated automatically. Already-issued access tokens remain a Step 8 revocation concern; keep the exposed account/session controlled until that work is complete.

## Protection inventory and key rotation

Runtime entities/services use readable values. EF converters encrypt on storage and authenticate/decrypt on materialization, including projections. DTOs retain their current endpoint/service authorization; account/campus/block access-control gaps remain Steps 8–9. Randomized ciphertext must never be compared in SQL; email/invite equality uses dedicated normalized HMAC-SHA256 lookup columns with unique user-email enforcement. The keyed lookup is pseudonymous and still sensitive metadata.

Protected text includes user email/name/bio/faculty/department, user values/interests JSON, message bodies, endorsement notes, report details/admin notes and invite recipient emails. Columns use PostgreSQL text so maximum Unicode plaintext plus encryption overhead is preserved; plaintext limits remain in validators and persistence checks. Passwords remain BCrypt hashes. JWTs carry identifiers/role/state without email/name claims. Structural identifiers, eligibility/status/counters/timestamps, academic year and enum flags remain queryable metadata; infrastructure access, encrypted disks/backups and retention controls still apply. Photos/storage and durable notification privacy remain later roadmap work.

The format is `vouch:v1:<key ID>:<payload>` with AES-256-GCM, random 96-bit nonce, 128-bit tag and field-specific authenticated data. Empty optional text/null remains empty/null. Unknown versions/keys, tampering and wrong purposes fail with a controlled error; ciphertext/plaintext is never returned as a decryption fallback. Field binding prevents moving a payload between columns; it does not bind ciphertext to a particular row. EF models are cached per encryption-service instance, so a model cannot reuse another key ring. References: [EF value converters](https://learn.microsoft.com/en-us/ef/core/modeling/value-conversions), [AES-GCM authentication](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.decrypt?view=net-10.0).

To rotate encryption: retain `Security__EncryptionKey` (legacy and v1), add a new independent >=32-character secret under `Security__EncryptionKeys__v2`, set `Security__ActiveEncryptionKeyId=v2`, stop writers, back up/rehearse, inspect, then run the normal explicit upgrade. It authenticates retained-key payloads and rewrites them to the active key in one data transaction. Repeat inspect, verify readiness/login and resume with that same ring. Additional IDs follow the same pattern. Do not overwrite a key ID with a different secret. Keep old keys until no live data or retained backup needs them.

To rotate the independent lookup key: stop writers, back up/rehearse, supply the new lookup key and inspect with `--rotate-lookup-key true`. Then upgrade with both `--rotate-lookup-key true --backup-confirmed true`. The tool decrypts all emails, recomputes user/invite lookup values, checks collisions and replaces the verification marker atomically. Ordinary upgrade/readiness refuses a changed lookup key. Resume every API replica with the same new key; rollback uses the matching pre-rotation backup/key pair.

## Verification scope

The integration suite creates its own disposable PostgreSQL databases. It covers fresh migrations, both genuine EnsureCreated baselines, mixed plaintext/CBC data, private collision reports, wrong keys, unknown schemas, interrupted conversion/retry, readiness, explicit key rotation, bootstrap refusal/reset, login, concurrent duplicate registration, maximum Unicode fields and readable authorized match/message/trust/moderation DTOs. These are synthetic upgrade fixtures; rehearse with a restored copy of your actual database before live deployment. No existing project database was migrated by this implementation work.
