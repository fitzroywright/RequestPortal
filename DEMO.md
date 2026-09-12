# Request Portal Management Demo

This file applies to the `demo` branch only. Do not merge demo-only changes into `main`.

## Demo architecture

The demonstration runs completely from one Windows laptop:

- Request Portal: real Request Portal web application
- Dashboard: real `ManagementDashboardService` calculations, filtering and drill-downs
- Database: local PostgreSQL database `requestportal_demo`
- Documents/revision data/location metadata: local `App_Data/demo-storage` through Common.Storage
- FFP Manager: disabled for the offline demo
- Notifications: disabled for the offline demo
- OpenBao: not required
- Active Directory: not required
- OIDC/Keycloak: not required
- Azure/public DNS/Internet: not required

The demo-specific staff login is enabled only when `Demo:Enabled=true`. It issues the same staff cookie and permission claims used by the real management pages. With demo mode disabled, the existing production OIDC architecture remains in effect.

## Start the demo

From PowerShell:

```powershell
cd D:\Projects\RequestPortal
git switch demo
git pull
.\Start-Demo.ps1
```

The script:

1. refuses to run unless the current Git branch is `demo`;
2. checks local PostgreSQL;
3. prompts once for the local PostgreSQL `postgres` password unless `REQUESTPORTAL_DEMO_PG_PASSWORD` is already set;
4. creates `requestportal_demo` if it does not exist;
5. uses only local environment/configuration;
6. builds with `--no-restore` so the demonstration does not reach the Internet for NuGet;
7. applies existing EF migrations;
8. seeds synthetic Request Portal data;
9. validates required demonstration scenarios;
10. binds Request Portal to the laptop on HTTPS port 5443.

If local NuGet assets have never been restored on this laptop, restore/build the repository once before going offline. The actual management demonstration does not require Internet access.

## URLs

On the demonstration laptop:

`https://localhost:5443`

FFP Staff Sign-In:

`https://localhost:5443/Account/StaffLogin`

Management Dashboard after sign-in:

`https://localhost:5443/Management`

The launcher also prints each active LAN address, for example:

`https://10.90.0.50:5443`

LAN clients may receive a browser warning because the certificate is the laptop development certificate. If Windows Firewall prompts, allow access on Private networks. If Wi-Fi/LAN disappears completely, `https://localhost:5443` continues to work because the database, storage, authentication and application all remain local.

## Credentials

### FFP management staff

- Username: `FWright`
- Password: `FFPDemo2026!`

This is a synthetic demo-only credential. It is not an AD credential and is never used when demo mode is off.

### Approved organization representative

- Username/email: `representative@demo.local`
- Password: `DemoUser2026!`

This account uses the normal public Request Portal Identity login and can be used to demonstrate the real requester workflow.

### Organization administrator

- Username/email: `orgadmin@demo.local`
- Password: `DemoUser2026!`

This also uses the normal public Request Portal Identity login and includes the seeded project proposal/budget/milestone scenario.

## Synthetic management scenarios

The demo seeds requests across all 14 Jamaican parishes and uses real `AssistanceRequest` state transitions. The dataset contains:

- new and older submitted requests;
- more-information-required requests;
- approved requests;
- pending-fulfilment requests;
- declined requests;
- fulfilled and closed requests;
- multiple categories/programmes;
- current-week/current-month and older records;
- old outstanding records for overdue/stalled views;
- requests without supporting documents for needs-attention scenarios;
- parish/community location strings for the real Jamaica reach/drill-down implementation;
- a real project proposal with narrative, budget and milestones.

No dashboard totals, status calculations, filters, trends, parish summaries, request drill-downs or proposal views are mocked.

## Differences from `main`

Only the `demo` branch contains these changes:

1. `appsettings.Demo.json` enables the isolated local demo environment and disables external production integrations.
2. `/Account/StaffLogin` offers demo-only local staff credential validation when `Demo:Enabled=true`; otherwise the existing OIDC challenge remains unchanged.
3. Demo staff authentication issues the existing FFP staff cookie with `ManagementDashboard.View` and the existing Request Portal staff permission claims.
4. `DemoManagementDataSeeder.cs` adds synthetic management scenarios across Jamaica using real domain state transitions.
5. `DemoReadinessValidator` prepares/validates the expanded management scenarios and assigns passwords to synthetic public demo users through the normal ASP.NET Identity password mechanism.
6. `Start-Demo.ps1` provides the offline laptop launcher, local PostgreSQL bootstrap, HTTPS binding and printed LAN URLs/credentials.
7. The `demo` branch CI workflow also listens to pushes to `demo` so branch-specific work can be compiled/tested without altering `main`.

There is no merge from `demo` to `main` as part of the demo preparation.
