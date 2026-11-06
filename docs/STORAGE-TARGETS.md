# Request Portal Storage Targets

RequestPortal document persistence is provider-neutral through `Common.Storage.IFileStorage`. Existing document, proposal and storage-backed request services do not contain SharePoint-specific code.

Set `Storage:Provider` to `LocalFolder`, `NetworkFolder` or `SharePoint`.

## Network folder

```json
"Storage": {
  "Provider": "NetworkFolder",
  "NetworkFolder": {
    "RootPath": "\\\\fileserver\\RequestPortal"
  }
}
```

A mounted SMB/NFS path can be used on Linux.

## SharePoint

```json
"Storage": {
  "Provider": "SharePoint",
  "SharePoint": {
    "TenantId": "<tenant-id>",
    "ClientId": "<app-registration-client-id>",
    "ClientSecretName": "storage/sharepoint/client-secret",
    "HostName": "tenant.sharepoint.com",
    "SitePath": "sites/Requests",
    "DriveName": "Documents",
    "RootFolder": "Aegis/RequestPortal"
  }
}
```

The client secret is resolved through Common.Secrets. `DriveId` may be configured instead of `DriveName`.

RequestPortal continues to own authorization, malware scanning, retention, document verification, request ownership and audit metadata. Common.Storage owns provider-neutral persistence and integrity.

## Production switching

Changing providers does not copy existing files. Migrate the stored keys first, verify the destination, then change `Storage:Provider` and restart. RequestPortal does not silently fall back to the old target because that would create two competing authoritative stores.

For Kratos, follow the [TrueNAS cutover procedure](deployment/TRUENAS-STORAGE-CUTOVER.md).
The existing FFP Manager share is authoritative only after verified migration and
explicit administrator cutover authorization. Request locations and dashboard
aggregates are PostgreSQL data; dashboard requests and rebuilds do not read the
file provider for location information.


## Request Portal logical storage layout

New request-owned documents use request-centric storage keys. The Common.Storage provider remains unaware of Request Portal domain concepts; Request Portal constructs the logical key before calling `IFileStorage`.

```text
RequestPortal/
  Requests/
    {public-request-reference}/
      Supporting Documents/
        {document-guid}-{sanitized-original-filename}
      Message Attachments/
        {document-guid}-{sanitized-original-filename}
      Delivery/
        {document-guid}-{sanitized-original-filename}
```

The sender type does not determine the storage location. If an attachment belongs to a request, the request owns it whether the sender is an applicant, organization representative, or FFP staff member.

Standalone conversations are intended to use:

```text
RequestPortal/
  Conversations/
    {thread-guid}/
      Attachments/

  FFP/
    Conversations/
      {thread-guid}/
        Attachments/
```

The current standalone messaging UI does not upload new binary attachments; it only attaches documents already associated with a request. Therefore those standalone branches are reserved by policy but are not created until a standalone upload workflow is implemented.

Existing documents are not renamed or moved automatically. Their database `StorageKey` remains authoritative and they continue to resolve from their original location. Migration is a separate, explicit operation.
