# Request Portal to FFP Manager reference and notifications

## Submission sequence

1. Submit commits the Portal request and RequestSubmitted outbox event.
2. When FFP Manager integration is enabled, dispatch queues the downstream transfer without sending the old Portal-code confirmation.
3. The SQL bridge resolves the submitted parish, increments FFP Manager's existing ApplicationLetter XPO counter, and commits the Application Letter and requested items. Code is the Portal code. ReferenceNo uses the parish code, DateCreated year, GR marker, and shared sequence padded to at least four digits. DocumentSource identifies Request Portal or Request Portal - Quick Intake.
4. The bridge returns ApplicationLetters.ID separately from ReferenceNo (ApplicationNumber). Portal persists both. It queues separate durable client-confirmation and Customer Service email events after supporting document transfers succeed.
5. The customer service email and applicant WhatsApp contain the generated FFP reference. The Portal code is explicitly labeled separately. The existing applicant email/in-app confirmation is retained with the FFP reference.

A retry matches ExternalRequestId and reuses the saved reference. A legacy Portal-imported row whose ReferenceNo is blank or equal to its Portal code receives a generated reference on retry. Genuine existing references are preserved. Already-processed historical transfers are not automatically replayed.

## Shared counter commissioning

The bridge uses the existing dbo.IDGeneratorTable row for FFP.Module.BusinessObjects.ApplicationLetter and the empty server prefix used by FFP Manager. It does not introduce or initialize another sequence. It validates the mutable Oid counter, existing type discriminator, and OptimisticLockField, locks allocation within the application transaction, and increments the optimistic lock version so concurrent legacy XPO saves detect stale data.

No separate annual or parish reset is introduced. Invalid parish, missing/ambiguous sequence, identity-based Oid schema, or an already-used generated reference rolls back the transfer and leaves it retryable. The runtime schema must be verified against the actual test database before deployment: this source environment cannot resolve JMDB01 or authenticate with your Windows integrated credentials.

## Configuration

Set Notifications:FfpEmailAddress (environment variable Notifications__FfpEmailAddress) to the Customer Service mailbox. Notifications:Enabled and the email provider must be enabled. A missing mailbox leaves the staff email pending for the existing retry/dead-letter policy; it does not rerun the bridge.

WhatsApp requires Notifications:WhatsAppEnabled, the Meta Phone Number ID, an access token in the configured secret provider, and the approved request_portal_request_update template in en_US with two body parameters (subject, body). Quick Intake client confirmation remains pending when WhatsApp is disabled. Meta commissioning and recipient notification preferences still apply. Existing queued messages retain their original payloads.

Portal NotificationHistory Sent currently records successful admission to Common.Messaging's durable queue. Actual provider delivery, retries, and dead letters must also be checked in Common.Messaging; admission is not proof of receipt.

## Test database proof on Windows

Use the existing FFPMANAGER_TEST targeting JMDB01, database FFP. FFPJM is prohibited for these tests. The real SQL tests enforce both the server and database before opening a connection. Counter-only tests roll back; the gateway test creates a uniquely identified test application and deletes it afterwards, without rewinding the shared counter.

With FFPMANAGER_TEST and REQUESTPORTAL_POSTGRES configured:

```powershell
cd D:\Projects\RequestPortal
dotnet restore RequestPortal.slnx
dotnet build RequestPortal.slnx -c Release --no-restore
dotnet test RequestPortal.Tests\RequestPortal.Infrastructure.Tests\RequestPortal.Infrastructure.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~FfpManagerReferenceNumberTests|FullyQualifiedName~NotificationWorkflowTests"
```

Do not treat skipped SQL tests as a database proof. Inspect the generated reference, ApplicationLetters.Code/ReferenceNo/Parish/DocumentSource, repeat-submission identity, Portal integration link, outbox records, and actual email/WhatsApp provider outcomes.

Update both Request Portal and the separately hosted FfpManager.Bridge; the Kratos requestportal deployment does not deploy the bridge. Keep the bridge pointed at the test database throughout acceptance.
