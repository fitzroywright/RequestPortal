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
