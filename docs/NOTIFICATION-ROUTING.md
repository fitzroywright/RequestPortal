# Notification routing

System messages use explicit recipient IDs. Internal staff status and the Administrator role do not subscribe a person to notifications. SignalR continues to send `messageReceived` only to the selected users' private groups; the browser and visual design are unchanged.

## Administration

Use **Admin → Notification Routing** to designate:

- **Client Relations / intake**: responsible for unassigned request notifications and incoming applicant correspondence.
- **Operational escalation**: responsible for request-creation failure alerts and unaddressed internal operational correspondence.

There are no default recipients and no all-staff fallback. Only active staff authorized to review requests can be designated. Saving requires the current Administrator role, staff administration authorization, antiforgery validation, and a matching configuration version. Changes are audited. Responsibilities are application-owned `AspNetUserClaims` entries of type `requestportal.notification-responsibility`, with values `intake` or `operations`; they grant no authentication permissions. No schema migration or historical data rewrite is required.

When an assignment exists, request notification delivery follows the assignment instead of the intake queue. Inactive or unauthorized assignees are excluded; they are not replaced by unrelated staff. Assignment notifications are private internal messages, separate from applicant conversations, committed with the assignment. Repeating the same assignment does not create another notification.

If no eligible recipient exists, correspondence/failure messages remain stored. `NotificationRoutingMissing` warnings identify the missing responsibility and configuration page in operational logs and the existing audit trail, without applicant data. The administration page also shows the missing-configuration warning. Selecting recipients affects future delivery; historical alerts are available to the designated escalation staff through authorized thread/archive access, without manufacturing recipient rows or replaying notifications.

## Affected paths

- `OutboxDispatcher`: submission/client confirmation and status-change portal messages explicitly combine existing applicant/subscribed organization recipients with assigned/intake staff. External applicant notifications retain their original recipient list, preferences, delivery IDs, and reference handling.
- `FfpManagerIntegrationService`: incoming information requests explicitly address existing applicant/subscribed organization recipients and assigned/intake staff. Downstream submissions, reference handling, and the Bridge are unchanged.
- `SendStaffAlertAsync`: request-creation failures address only designated operational escalation staff.
- Shared applicant correspondence and request replies select assigned/intake staff rather than every internal user. Request replies revalidate current organization membership. Internal messages use selected eligible staff, or explicitly designated escalation staff when no recipients were supplied.
- `SendSystemMessageAsync` deduplicates its caller's explicit list and validates current public membership/staff eligibility. It never appends staff. Existing stable message IDs still prevent repeated persistence and SignalR notification.

## Historical access

Inbox/unread/thread/archive/attachment and mutation checks no longer treat `StaffVisible` or a historical recipient row as sufficient permission to system/request conversations. Current responsibility or assignment is required. Administrators may explicitly review request conversations but do not receive inbox entries or popups by virtue of their role. Manual direct participants retain access to their conversations. Public request access requires current approved organization membership; subscriptions remain delivery preferences rather than an access gate.

Mailbox reads no longer insert recipients. Sent mail shows the current sender's messages rather than every staff user's sent messages. Historical messages, recipient records, assignments, notification history, and audit trails are not deleted or rewritten. Legacy system rows cannot reliably establish intended staff responsibility, so current explicit responsibilities and assignments are the authorization boundary.

## Validation and deployment

Use isolated PostgreSQL fixtures (`REQUESTPORTAL_POSTGRES`) for the full infrastructure and domain suites. Live FFP SQL contract tests require a separately approved rehearsal target; they must not use production databases. Focused tests cover anonymous/authenticated Quick Intake, applicant and subscribed representative delivery, assignments, unconfigured routing, operational warnings, historical access, administrator configuration, lifecycle exclusion, idempotency, and per-user SignalR groups. Existing Client Relations email and FFP integration tests exercise the unchanged queue/reference workflows.

Deploy only `requestportal` through the existing guarded Kratos deployer, preserving environment, OpenBao, application data, and rollback backups. No uncontrolled live intake submission or real applicant notification is needed for deployment validation. After deployment, an administrator must select the actual responsible staff; do not infer them from internal/administrator status.
