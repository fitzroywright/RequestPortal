# Quick Intake FFP email

Quick Intake FFP notifications use the durable Portal outbox and Common.Messaging email queue. The applicant confirmation remains independent. After the downstream bridge returns a genuine FFP Manager application number, the dispatcher queues a separate FFP email containing that number, the Portal reference, applicant contact information, address, and requested assistance.

Configure `Notifications:FfpEmailAddress` (environment variable `Notifications__FfpEmailAddress`) to the FFP receiving mailbox. `Notifications:Enabled` and the existing email provider must be enabled. The FFP recipient is independent of applicant notification preferences. Missing configuration or provider failure leaves the notification eligible for the existing retry/dead-letter workflow.

## Outstanding reference generation dependency

The current SQL bridge writes the Portal reference into both ApplicationLetters.Code and ReferenceNo and returns it as ApplicationNumber. This cannot satisfy the requested FFP-generated reference. The new dispatcher explicitly rejects that value and leaves downstream processing retryable rather than sending a mislabeled number.

The FFP Manager source generates references in `Letters.GenerateReferenceNo` using parish code, DateCreated year, and `DistributedIdGeneratorHelper.Generate` for `FFP.Module.BusinessObjects.ApplicationLetter`. Its controller only generates a reference if ReferenceNo is empty and Parish is set. Completing automatic generation requires using that authoritative numbering mechanism and verified sequence schema/API; it must not introduce a separate counter or use the SQL identity as a substitute.

This change is a draft until that integration dependency is completed and the FFP receiving mailbox is configured. No live emails have been sent during implementation.
