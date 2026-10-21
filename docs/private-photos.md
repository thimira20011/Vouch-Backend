# Private photos and staged reveal

Step 13 stores optional photos in the application's PostgreSQL database, including Supabase development. No Storage bucket, storage API key or local photo volume is required. Production uses its own campus database and keys. Keep Supabase's Data API disabled for this backend-owned schema, as described in [development setup](supabase-development.md).

## API and permissions

| Endpoint | Result |
|---|---|
| `GET /api/profile/photo` | Owner's `originalUrl`, `abstractUrl`, `quarterUrl`, `sixtyUrl`; all null without a current photo |
| `PUT /api/profile/photo` | Multipart field `photo`, exactly one nonempty file; atomically replaces all four variants and returns their URLs |
| `DELETE /api/profile/photo` | Idempotently clears the owner's URLs and all current photo data; 204 |
| `GET /photos/{abstract\|quarter\|sixty\|original}/{owner:N}-{generation:N}.jpg` | Authorized JPEG bytes, or 401/403/404 |

All routes require a current bearer session. Owners must have verified onboarding and an Active or InIncubation account. Other viewers must be Active, verified, onboarded, on the same current campus and unblocked in either direction. Architect role grants no photo bypass. An eligible shared conversation grants abstract access and the exact earned 25/60/100 stage plus earlier stages. Paused/archived conversations retain earned stages while both participants remain eligible. Unknown stages grant no higher image. A current UTC-day Pending/Accepted match grants only the abstract preview; rejected/expired/old matches do not grant access.

URLs identify a generation; they do not grant permission. Changing the variant in a URL rechecks the requested stage. Replacement/deletion invalidates the previous generation, and missing or legacy generations return 404 after authorization. Conversation DTOs contain the URL for the earned stage. Daily-match DTOs contain `abstractPhotoUrl`, refreshed against the current photo even on a cached match response.

Fetch with `Authorization: Bearer ...`, convert the response to a browser blob URL for an image element, and revoke that blob URL when replacing/unmounting the image. Do not put JWTs in photo query strings. The response uses `Cache-Control: no-store`, `nosniff`, same-origin resource policy and no range processing/ETag. Configure proxies to preserve authorization and no-store, and clear client image state when access fails. Previously downloaded bytes cannot be recalled.

## Processing and storage

Uploads accept JPEG, PNG or WebP with matching signatures, at most 5 MiB, at most 8192 pixels per side and 25 million pixels. The HTTP request is bounded to 6 MiB including multipart overhead. Invalid input returns 400, unsupported request media 415, and the outer request size check returns 413. Multi-frame inputs use the first frame. Orientation is applied, metadata stripped, transparency flattened on white, color converted to sRGB and the image resized to a maximum 1024-pixel side. The stored original is this normalized JPEG, not the uploaded source.

The abstract/25%/60% treatment combines reduced detail, quantized colors, oil painting and progressive smoothing. Each encoded JPEG is limited to 2 MiB. Processing is limited to two concurrent images and two native threads, with a 256 MiB native memory limit and disk pixel caches disabled. Accepted multipart files remain in memory. Inputs that exceed available processing resources fail rather than spill a private image cache to disk.

`PhotoAssets` has one row per owner and four AES-GCM protected text columns. Each plaintext envelope contains base64 JPEG plus owner ID, generation ID and stage. The encrypted column purpose authenticates its stage column; the envelope additionally rejects copied ciphertext from another owner/generation. Retrieval projects and decrypts only the authorized column. Existing protected-data inspection and key rotation include all four columns and validate their binding. Keep encryption keys separate from database backups and retain old keys for retained backups.

Campus locking serializes replacement/deletion with normal eligibility writes. Replacement deletes the previous generation and updates both user URLs in the same transaction as inserting the new generation; failures roll everything back. Deletion clears the URLs and row atomically. Physical account deletion cascades the row through its owner FK. Request-time access stops as soon as the account becomes unavailable; Step 14's [account deletion](account-deletion.md) implements the broader deadline, transactional retries and restore erasure replay.

Database backup/restore now carries photos with the remaining account data. Base64 plus encryption increases size; capacity, backup duration and deployment latency need Step 21 measurements. Restart/redeployment must preserve the database and key configuration.

## Upgrade and acceptance

Apply `20261016152507_PrivateDatabasePhotos` through the [database operations](database-operations.md) inspect, protected backup, restore rehearsal and explicit upgrade procedure. The migration creates the photo table and preserves existing user fields. It does not import old plaintext filesystem photos. Legacy URLs produce no staged DTO URL and cannot retrieve a file. Existing deployments should arrange a reviewed migration of needed legacy files or ask users to re-upload, then remove the old plaintext files under their retention policy. Never expose an old photo volume through nginx/CDN. Compose no longer mounts it; an existing volume is not deleted automatically. Legacy `PhotoStorage:BasePath` is ignored; `BaseUrl` must remain `/photos` or empty.

The user approved the synthetic portrait's left-to-right abstract → 25% → 60% → original progression on 16 October 2026. Real-photo frontend review and Linux/Alpine native-runtime execution remain acceptance checks in their intended environment. Visual stages are artistic treatments, not identity anonymization guarantees. Synthetic processing and disposable PostgreSQL/API tests cover metadata, stage permissions, blocks/campuses, current match previews, replacement rollback/concurrency, encryption/binding, rotation, restart, deletion and optional photos.
