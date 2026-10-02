# RequestPortal RC1 implementation

The consolidated sixteen-section specification supplied on 1 October 2026 is the RC1 scope. RC1 requires the individual, organization and FFP staff production journeys to pass; compilation is insufficient.

## First implementation batch

- Permanent public request codes use `RP-yy-number`, allocated by a PostgreSQL sequence. The GUID remains the internal identifier. Existing references remain unchanged. The unique database index remains in place. The sequence is deliberately preserved during rollback to prevent recycling issued codes.
- Applicants can filter their authorized requests by code. FFP staff can find a request by exact code. The existing bridge already maps the public reference to Application Letter Code and ReferenceNo.
- Submission confirmation includes the code and instructions to quote it when contacting FFP. Confirmation and significant status events also appear in the request conversation/inbox. External delivery continues through Common.Messaging and existing channel preferences.
- Each request has a canonical public conversation. FFP information requests and applicant responses use it. Direct, private and staff-only conversations remain separate. Replies and attachments are available inside the request view.
- External status labels and Completed / Current / Next indicators are shown in the request view. Information Required is explicitly labelled Waiting on You.
- Applicant exceptions are replaced by a safe reference message. Server logging and Common.Diagnostics receive the same HTTP correlation ID. Engineering diagnostics require FFP staff authorization.
- FFP staff have a Test SharePoint operation for the configured Common.Storage provider's authentication, target resolution and read/write round trip. Storage errors during request document actions preserve the request screen and allow text-only replies.
- Managers have a per-person/per-organization Notify me about all organization activity control. New manager memberships do not automatically opt in. Existing choices are preserved.
- Profile forms validate their own inputs independently. Logout clears representative, staff and reviewer sessions; authenticated pages use no-store caching. Visible Workspace labels are replaced with request terminology.
- CI provisions PostgreSQL rather than failing its integration tests because REQUESTPORTAL_POSTGRES is absent.

## Remaining work before RC1

| Specification | Remaining acceptance work |
| --- | --- |
| 1–2 Individual intake | Implement the individual applicant ownership path, persistent profile and centralized completeness validation. Fields: DOB, gender, National ID/TRN, marital status, address, parish, district, primary/alternative telephone, email, employment status, employer/institution, household income, household size, dependants, children under 18, housing situation and household description. Show a read-only requester summary and return to the saved draft after profile editing. |
| 3 Organization requests | Prove approved membership and organization completeness server-side at submission, with no individual demographics on organization requests. |
| 4 Structured intake | Add controlled referral source and presenting-need categories, retain assistance classification and structured item add/edit/remove; verify supporting documents for goods/services as well as projects. |
| 5 Codes | Prove concurrent allocation and applicant/staff/FFP Manager lookup in production. Codes are references and never grant access. |
| 6 Confirmation | Prove actual external receipt, rather than only durable queue acceptance. |
| 7 Conversation | Prove the same conversation across RequestPortal and FFP Manager, including attachments and Information Required responses. |
| 8–10 Notifications | Prove the single manager all-activity preference and owner/participant/subscribed-manager routing, opt-out and direct/restricted privacy. Verify delivery, retries and protected diagnostics independently for email and commissioned WhatsApp. In-app remains part of the request/inbox experience. No SMS. |
| 11 Status | Verify every external status and the staff operational detail in the production workflow. |
| 12 Staff boundaries | Keep assessment, recommendations, eligibility, availability, matching, approval, implementation/delivery, referrals and follow-up in FFP staff systems; verify Application Letter mapping. |
| 13 UI | Complete the density pass and individual Requester → Code & Status → Request → Documents → Conversation layout. |
| 14 Reliability/storage | Commission real production SharePoint credentials and library, run Test SharePoint, verify outage recovery and retrieve failures by the shown correlation ID. |
| 15 Authentication | Verify Google and Yahoo using observed provider errors/settings. Commission Apple after required credentials are available. Recheck Microsoft/Facebook and logout. |
| 16 Acceptance | Run all three personas: Registration → Profile → New Request → Code → Submission → Notification → Documents → FFP review → Messaging → Information Required → Applicant response → Status progression. |

Do not declare RC1 until these production acceptance gates pass. Further features are outside this boundary.
