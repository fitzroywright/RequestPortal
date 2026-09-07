# Organization Data Isolation

Organization data isolation is a hard security invariant of Request Portal.

## External representatives

An external representative may access organization-owned data only when the representative has an approved membership in that organization.

This applies to, at minimum:

- organization profile and administrative information;
- organization members and pending membership requests;
- assistance requests;
- request line items;
- request status/history;
- supporting documents;
- notes and communications;
- fulfilment information;
- reports and exports.

A representative of Organization A must not be able to retrieve operational information belonging to Organization B by changing a URL, form value, API parameter, hidden field, request id, organization id, or other client-controlled value.

Users who belong to multiple organizations may see data for each organization where they currently have approved membership. The server must authorize each organization context independently.

Organization administrators have elevated rights only inside organizations where their approved membership has the Administrator role. Administrator status in one organization grants no rights in any other organization.

The organization directory used to request membership may expose only the minimum approved directory fields required to identify an organization, such as name and country. It must not expose that organization's requests, members, documents, internal notes, or other operational information.

## FFP staff

Authorized FFP staff are the cross-organization administrative boundary. Staff access is authenticated through FFP Active Directory and authorized through Request Portal staff roles/policies.

Authorized FFP staff may view all organizations and their Request Portal information according to their assigned staff role. The staff workspace is therefore intentionally not filtered by organization membership.

## Enforcement

Organization isolation is enforced server-side in application/infrastructure queries. UI hiding is not considered authorization.

Every organization-owned read or write must either:

1. prove the current external user has the required approved membership in the owning organization; or
2. prove the current user satisfies the FFP staff policy.

Request queries for external representatives are scoped to organization ids obtained from approved memberships. Direct request lookup performs an ownership/membership authorization check before returning request details.

Denied cross-organization access should emit the Starfleet diagnostic code `SF-AUTH-ORG-DENIED` without logging sensitive record contents.
