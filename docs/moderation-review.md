# Moderation review (Step 11)

The rolling 90-day suspension window uses **resolution time**, including both the current instant and the instant exactly 90 days earlier. Future-dated resolutions are excluded. Three upheld decisions in this window suspend immediately; lifetime upheld totals are separate. Submission time does not affect the window. For legacy completed reports without a resolution timestamp, the migration uses their creation timestamp as the only available historical time; it leaves the unknown deciding Architect null and does not fabricate historical audit entries.

Report decisions and account actions serialize on the campus row inside a database transaction. A decision saves the report, recalculates totals/visibility, applies any suspension, revokes sessions, and appends audit records in the same transaction. Repeating the same decision and notes succeeds without changing the original timestamp, actor or audit. Changing a completed decision returns a conflict.

Architects can review only reports whose reporter and reported account currently belong to their campus. Related report audit entries follow the same restriction when either participant moves campus. Message evidence must be a text message sent by the reported account in a conversation between that account and the reporter. Archived, paused or blocked conversations remain reportable. Invalid historical attachments expose no message body and cannot be upheld; they can be dismissed. Report details, decision notes and audit reasons are encrypted at rest.

Resolving the last pending report restores matchmaking visibility for available accounts, including upheld reports below the suspension threshold. Suspended and deletion-requested accounts remain hidden. Counts and visibility are derived from current campus reports whenever eligibility changes and during explicit upgrades.

Manual suspension requires a reason. Reinstatement restores the previous available state when verified onboarding permits it; historical suspensions without a stored previous state return to incubation unless onboarding and completed incubation are recorded. Normal trust eligibility recalculation still applies. Reinstatement is an explicit override of prior decisions; a new qualifying upheld decision can suspend again. Reports do not expire a suspension automatically. Existing sessions remain revoked after reinstatement: the account must sign in again. Architects cannot manually suspend themselves or other Architects, and deletion requests cannot be reversed here.

Review routes:

- `GET /api/architect/campuses/{campusId}/reports?status=Pending&page=1&pageSize=20`: all severities, stable priority/time/id order, full total count. Page size is 1–50; page is 1–10000. `Upheld` and `Dismissed` are also supported.
- `GET /api/architect/reports/{reportId}`: decision metadata and authorized message evidence.
- `POST /api/architect/reports/{reportId}/resolve`: `{ "uphold": true, "notes": "..." }`; notes are optional, maximum 1000 characters.
- `GET /api/architect/users/{userId}/moderation?page=1&pageSize=20`: current account state, lifetime/window/pending counts and paginated audit history.
- `POST /api/architect/users/{userId}/suspend` and `/reinstate`: `{ "reason": "..." }`, 10–1000 characters; successful actions return 204. An identical retry of the latest account action is a no-op; other invalid state transitions return a conflict.

The dashboard pending total covers the whole queue. Its critical-report preview remains capped at 20; the reports endpoint provides complete review access. Audit entries cannot be changed through tracked application writes. Deleting a report clears its audit reference; deleting the subject removes their audit data. The broader deletion and retention policy remains Step 14.

Durable alert delivery remains Step 12. Current high-severity email delivery happens after commit. Production acceptance and deployment checks remain later steps.
