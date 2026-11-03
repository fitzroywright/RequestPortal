# Kratos staff directory commissioning

This procedure enables the already approved Entra directory connection without creating accounts, rewriting identity bindings, rotating credentials, changing FFPJM, or deploying the Bridge or Studio.

The approved application is `45d755f5-3dd4-45b6-ae13-366b8122bb2b` in tenant `257d294c-c8a7-4b86-b791-6755edc4fe0a`. Use the existing Common.Secrets reference `Authentication:StaffOidc:ClientSecret`; do not copy the secret value into configuration. The reviewed directory file contains exactly Enabled, Provider, TenantId, ClientId and ClientSecretName under `Authentication__StaffDirectory`.

## Compatibility prerequisite

Deploy the reviewed `StaffSignInIdentityCompatibility` change from merged GitHub main with the existing Aegis.Operations `Deploy-Kratos.ps1 -Applications requestportal` mechanism. The top-level Request Portal wrapper also deploys the Bridge and must not be used for this operation.

The validated OIDC callback proves both Entra `tid`/`oid` and historical issuer/`sub`. Existing issuer-scoped bindings are resolved by their exact immutable key; newly pre-provisioned users resolve by tenant/object key. A conflicting binding to another account fails closed. Email, username and display name never authorize matching. This selection does not link, migrate or rewrite any identity, role, approval or account status. Normal successful sign-in retains its existing audit behavior.

## Stage the narrowly scoped privileged command

After CI passes, merge, fetch main, deploy the verified main build, and compare its deployed assembly hash with the published artifact. Stage these three reviewed files using the existing noninteractive SSH upload and passwordless `/usr/bin/install` mechanism:

| Installed root-owned file | Contents / mode |
| --- | --- |
| `/usr/local/sbin/aegis-requestportal-commission-directory` | `scripts/Commission-StaffDirectory-Kratos.py`, executable, `0755` |
| `/usr/local/sbin/aegis-requestportal-commission-directory.env` | Approved five-setting directory file, `0600` |
| `/usr/local/sbin/aegis-requestportal-commission-directory.json` | `{"sourceCommit":"<verified merged main SHA>","assemblySha256":"<published portal DLL SHA256>"}`, `0600` |

No unrestricted root SSH access or broad sudoers change is required. Installing the reviewed files does not run commissioning or alter application configuration.

An authorized Kratos administrator then runs exactly this one command:

```sh
sudo /usr/local/sbin/aegis-requestportal-commission-directory --apply
```

The helper accepts no arbitrary command, path, application ID or secret argument. It requires Kratos, the expected systemd service/environment file and the exact reviewed portal assembly. It fails before configuration promotion if these checks or protected preflight fail.

## What the command does

1. Creates a root-only audit directory under `/var/lib/aegis/requestportal-commissioning/` and backs up `/etc/aegis/requestportal.env` with mode `0600` before changes.
2. Runs the deployed `inspect-staff-directory` diagnostic in a transient systemd unit as the actual non-root service user. Systemd parses the existing environment; no shell sources the protected file and no secret is passed on a command line.
3. Verifies the existing staff application, tenant authority/issuer, compatibility marker and four staff bindings. The diagnostic reads the Request Portal database in an explicitly read-only transaction and reports only a count and fingerprint for identity bindings.
4. Prepares the five approved directory settings. If the approved tenant is absent from AllowedTenantIds, appends it without removing existing allowed entries. It preserves Authority, Issuer, application ID, callback, claim mappings, unrelated configuration and credentials. An unexpected provider or authority/issuer requires review rather than a blind rewrite.
5. Runs `verify-staff-directory` with the candidate settings and the existing workload Common.Secrets/OpenBao configuration. This does not start the website, run migrations or provision accounts. It validates configuration and protected secret resolution, performs a read-only Graph prefix search for `adm`, and verifies an individual immutable identity if that search returns a match. Zero results are reported accurately and do not mean authentication failed.
6. Atomically promotes only the necessary environment assignments, restarts only `aegis-requestportal`, and checks local/public health and the unchanged Entra sign-in challenge.
7. Repeats the protected directory validation and verifies the four binding records have the identical fingerprint. It writes a non-secret `result.json` with source SHA, application ID, reference, pre/post evidence and backup location. Transient environment copies are deleted. Bootstrap SecretId files are not deleted by diagnostics.
8. If promotion or verification fails, restores the original environment, restarts the same service and checks recovery. The result explicitly reports rollback success or that operator attention is required. Identity data is never restored or modified by this helper.

## Authenticated acceptance

An existing authorized Administrator must use a fresh staff Entra sign-in, confirm the same account and permissions, open **Administration → Staff & Roles → Add Staff**, search at least three characters of a known directory user's name or UPN, and open its review/details view. Confirm the expected enabled/disabled state and canonical Administrator/Reviewer choices. Stop before **Create**, **Pending Access**, or **Approve & Activate**; no real staff record is needed for acceptance. Verify existing administration/sign-in history through Staff & Roles. A Reviewer can confirm staff administration remains forbidden using their normal existing account.

Automated disposable-database tests cover pending/active/suspended/disabled accounts, role preservation, conflicting bindings, profile-match rejection, public/staff isolation and first-sign-in resolution. Production diagnostics do not manufacture authenticated sessions or grant roles.
