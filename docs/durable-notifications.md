# Durable notifications (Step 12)

High-severity reports (severity 4–5), trust anomalies and inactivity nudges now persist encrypted inbox/outbox events. Report and vouch transactions save their alert before committing. Inactivity checks save both participant events with the nudge marker; a new letter clears the marker and invalidates old-cycle notices. Failure to save an event rolls back its source change. Clarity events publish only after the message commits. Existing messages and conversation state remain the durable source for message/clarity/pause/resume clients.

## Inbox and subscriptions

- `GET /api/notifications/?page=1&pageSize=20&unreadOnly=false`: current user's visible inbox, full filtered total and unread count. Page is 1–10000 and page size 1–50. Reading does not acknowledge events.
- `POST /api/notifications/{id}/read`: idempotently records that user's first acknowledgement; 204 on success. Another recipient's acknowledgement does not mark yours read.
- `GET /api/notifications/delivery`: current campus Architect only; pending/overdue email totals, late recoveries and oldest pending deadline.
- SignalR `JoinArchitectAlerts(campusId)` requires a current active Architect of that campus. `LeaveArchitectAlerts(campusId)` stops that subscription. Every outbound event rechecks current session, role, campus and source access.

Fetch the inbox on startup/reconnect and acknowledge after displaying an event. Offline events remain available even after an online publication attempt completes. Architect events use `HighSeverityReportSubmitted` and `TrustScoreAnomalyDetected`; participant events use `InactivityNudge`. Payload fields retain the existing camelCase wire format, with a stable `notificationId` for client deduplication. Message subscriptions continue using `JoinConversation`.

Architects see campus alerts only while their current role/status/campus permits it. Both report participants must still belong to the event's campus. Inactivity events require eligible, unblocked conversation participants, an active conversation and the unchanged last-message timestamp. Obsolete sources cancel delivery. Payloads contain references and short fixed messages; report details remain in authorized moderation review, and no details or email addresses are placed in notification payloads. Private inbox responses use `Cache-Control: no-store` and the standard authenticated rate limit.

## Email and retries

The worker runs every five seconds while `BackgroundJobs:Enabled` is true. PostgreSQL leases with `FOR UPDATE SKIP LOCKED` serialize claims; leases expire after two minutes and renew between recipients. No transport publishes uncommitted domain events. SMTP attempts time out after 15 seconds. Failures remain pending with bounded backoff (30 seconds, one minute, two minutes, four minutes, then five minutes); the final ten minutes before a report's deadline use one-minute retries. Work is never discarded merely because its deadline passed.

High-severity report emails target **current active Architects in the affected campus**, using their stored account emails. The old global `Smtp:ArchitectEmail` setting is unused. No active Architect leaves delivery pending so later provisioning can recover it. Individual receipts prevent already accepted recipients from being resent during ordinary retries. Reading an inbox event does not skip its required email.

The one-hour deadline starts at the report's creation timestamp. SMTP acceptance records delivery; it cannot prove final mailbox receipt. Delivery after the deadline retains `DeadlineMissedAt`, even after recovery. `/healthz` returns unhealthy while required emails remain overdue; the Architect delivery status exposes backlog and recovered misses. Monitor health and the structured deadline logs, and investigate SMTP failures or missing Architects.

External transport is **at least once**: if SMTP accepts a message and the process dies before recording acceptance, a retry can send a duplicate. The email carries the stable notification reference. Exactly-once mailbox delivery requires a provider with supported idempotency; no such guarantee is claimed. Retrying notifications never reruns report decisions or vouch submission. Realtime retries can also repeat a notification, so clients must deduplicate by ID.

## Configuration and upgrade

Production startup requires valid `Smtp:Host`, `Smtp:Port`, `Smtp:User`, `Smtp:Password` and a plain sender mailbox (`Smtp:FromAddress`, or an email-shaped SMTP username). SMTP uses STARTTLS; normally port 587. Implicit TLS port 465 is unsupported. Production also requires enabled background jobs. Never put passwords in Git or command arguments.

Development can omit SMTP. Verification then returns 503 and alert email attempts remain pending; missing configuration no longer pretends to send successfully. Configure real SMTP in private local settings or deployment secrets before live delivery testing. Automated tests use an injected recording/failing sender and never send real emails. A live provider/mailbox acceptance check remains an operator validation task.

Apply `20261014041109_DurableNotificationDelivery` through the explicit reviewed upgrade before starting this API. It adds notification events, per-user read/email receipts and the pending-work index, with payload encryption included in key rotation/backfill tooling. Existing historical reports are preserved without fabricated delivery history or retroactive emails; review them through the report queue. Deleting a source report/conversation/subject or recipient cascades its dependent events/receipts. General retention limits and deletion deadlines remain Step 14; this step does not define a new retention schedule.

The worker lease protects concurrent delivery claims, including multiple processes. Realtime connections remain local to one API process; deployment across replicas still requires the infrastructure/backplane checks in the later production-readiness steps. The persistent REST inbox works across replicas.
