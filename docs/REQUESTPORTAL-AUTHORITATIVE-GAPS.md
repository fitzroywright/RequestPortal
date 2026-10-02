# RequestPortal authoritative integration gaps

This file records integration work that must not be represented as complete until the authoritative FFP Manager side exists and is proven.

## Project request -> Draft Project

Current bridge SQL creates/links an FFP Manager ApplicationLetter using ApplicationLetters.ExternalRequestId and transfers request supporting documents. It does **not** create a linked FFP Manager Project record.

Required contract:
1. Submit the RequestPortal request to the authoritative Application Letter workflow.
2. For RequestKind=Project, create a linked Draft Project in FFP Manager.
3. Persist stable RequestPortal Request, Application Letter and Project identifiers.
4. Do not advance the Draft Project around the Application Letter approval workflow.
5. Return the authoritative identifiers/status through the bridge.

Do not fake this with a RequestPortal-only project identifier.

## Organization approval

RequestPortal currently has local organization approval behavior. The authoritative requirement is that an FFP staff approval initiated from RequestPortal executes the same FFP-side approval through the FFP Manager bridge. The bridge contract does not currently expose that operation.

Required bridge operation:
- authorize FFP staff;
- approve/reject the FFP Manager organization record;
- return authoritative organization state;
- audit organization, staff actor, action, timestamp, originating interface, and reason/comment.

The local RequestPortal organization state must not become a competing approval authority.

## Status / More Information

The Application Letter status and More Information bridge paths already exist and should remain authoritative. Common.Messaging is notification delivery only; it must not become an inbound workflow synchronization mechanism.

## Documents

Formal request uploads are Application Letter/request documents. Message attachments remain message-owned attachments and must not be silently promoted to formal request or Project documents.
