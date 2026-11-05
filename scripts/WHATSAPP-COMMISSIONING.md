# Operator-only Kratos WhatsApp commissioning

This procedure changes configuration only. It targets the portal deployed from
`6b09c4452fb2c62c8daac5b91fa0aecc50dc53a3` (Web DLL SHA-256
`9fb70e1900f91e48f513c8f8897517e5e9e96206a554ee62137dcd545304b183`).
It never deploys/rebuilds the application, edits systemd/sudoers, changes OpenBao
policies or secret versions, sends WhatsApp messages, or runs migrations.
The operator explicitly runs it interactively with sudo; there is no privileged
entrypoint added to Aegis.Operations. The existing Aegis portal-only deployer and
staff-directory commissioning helper were inspected for host/service, backup,
health and environment conventions; neither is changed or invoked here.

## Preparation and artifact provenance

Build the standalone probe against the existing validated Linux publish output:

```sh
python3 scripts/Build-WhatsAppCommissioningBundle.py \
  --publish-root /path/to/validated/6b09c445/publish \
  --output /tmp/requestportal-whatsapp-commissioning-bundle

tar -czf /tmp/requestportal-whatsapp-commissioning.tgz \
  -C /tmp/requestportal-whatsapp-commissioning-bundle .
sha256sum /tmp/requestportal-whatsapp-commissioning.tgz
```

Use the checksum supplied with the reviewed handoff. The manifest binds every
bundle file to its SHA-256 and records the tooling commit and deployed commit.
The operator script also compares the deployed Common.Secrets, Common.Diagnostics
and Npgsql binaries with the probe copies. Changed deployment or nginx content
requires a new review/build, not removal of these safeguards.

A secret-free archive may be uploaded using the existing SSH account. Uploading
is not commissioning. No service/environment/configuration is modified until an
authorized operator installs and explicitly executes the script below.

## Operator installation and preflight on Kratos

After comparing the archive checksum with the reviewed handoff, run:

```sh
sudo install -d -o root -g root -m 0755 \
  /usr/local/lib/aegis-requestportal-whatsapp-commissioning
sudo tar --no-same-owner -xzf /tmp/requestportal-whatsapp-commissioning.tgz \
  -C /usr/local/lib/aegis-requestportal-whatsapp-commissioning

sudo /usr/bin/python3 /usr/local/lib/aegis-requestportal-whatsapp-commissioning/Commission-WhatsApp-Kratos.py --preflight
sudo /usr/bin/python3 /usr/local/lib/aegis-requestportal-whatsapp-commissioning/Commission-WhatsApp-Kratos.py --dry-run
```

Both modes validate without changing production configuration or restarting
services. They create private audit/backups/candidates and temporary systemd probe
units. Do not run during a deployment or another configuration change. Required:
Debian Kratos, root-owned non-writable tooling and configuration, a private
root-owned `/etc/aegis/requestportal.env`, .NET 10 ASP.NET runtime, systemd-run,
busctl, nginx, journal access, HTTPS connectivity and healthy portal/nginx services.
The existing fixed service user/group, executable, working directory and single
environment-file composition must match. Unsupported multiline environment
assignments, extra credential mounts or environment inheritance fail closed.

The probe uses the deployed Common.Secrets bootstrap/configuration, not a new
OpenBao client. Under the existing `aegis` UID it reads these KV v2 `value` fields:

- `secret/requestportal/messaging/whatsapp/verification-token`
- `secret/requestportal/messaging/whatsapp/app-secret`
- `secret/requestportal/messaging/whatsapp/api-token`

It also reads the existing Request Portal database reference and verifies the
inbox/outbox schema in a read-only transaction. The API-token reference remains
`messaging/whatsapp/api-token`. Verification-token and App-Secret references must
already have the expected values; they are never overwritten. No credentials are
printed, passed as arguments, exported for an operator, or newly stored. Existing
service environment backups may contain bootstrap credentials and remain root-only.
Bootstrap secret-ID files are not deleted. Preflight performs fresh AppRole logins;
the existing bootstrap must permit repeated logins, as required for service restarts.
No permissions are granted. Missing read permission requires an OpenBao operator
reviewing exact KV data-path read grants while preserving other application grants.

The probe must resolve only the configured production OpenBao provider and confirm
outbound `Notifications:WhatsAppEnabled=false`. API-token validity, Meta grants,
recipient opt-in and outbound delivery are separate commissioning checks; this
procedure does not enable or claim to verify outbound sending.

## Explicit application

Only after both checks return `preflight-passed-no-production-change`, run:

```sh
sudo /usr/bin/python3 /usr/local/lib/aegis-requestportal-whatsapp-commissioning/Commission-WhatsApp-Kratos.py --apply
```

The four approved host settings are:

```ini
# Meta development test account; not the future production WhatsApp number.
Notifications__WhatsAppWebhookEnabled=true
Notifications__WhatsAppPhoneNumberId=1427917097069650
Notifications__WhatsAppBusinessAccountId=1823610105620506
Notifications__WhatsAppAccessTokenSecretName=messaging/whatsapp/api-token
```

All unrelated environment bytes, credentials, service definitions and TLS/proxy
settings are preserved. Existing outbound notification configuration stays disabled.
The nginx change adds one HTTP-context map and one conditional access-log directive
to each of the portal's existing HTTP/HTTPS server blocks. It suppresses access
logging only for the normalized, case-insensitive webhook path (including a trailing
slash), so its query cannot enter access logs. Other application sites are untouched.
An nginx error log is not an access log; both current/previous error and access logs
are scanned for secret values before/after validation. Existing leaked credentials
cause a stop requiring operator handling; this procedure never edits/deletes logs.

The script takes root-only, checksummed backups under
`/var/lib/requestportal-whatsapp-commissioning/<AUDIT_ID>/`, validates the
complete candidate include graph with `nginx -t`, checks for configuration drift,
then promotes and reloads nginx. A dummy-token canary checks the running access
logs before a real verification token is ever sent. It then atomically promotes
the environment, restarts only `aegis-requestportal`, and checks local/public health,
homepage and protected staff authentication. It does not restart other applications.

## Success evidence

Expect final JSON `status: commissioned` and a protected `result.json` audit path.
The audit records:

- AppRole reads of all three existing secrets as `aegis`, with no values;
- public valid GET: 200, exact plain-text challenge; wrong token: 403;
- unsigned, invalid-HMAC and tampered POSTs: 403;
- valid signed POST and duplicate retry: 200;
- exactly two processed receipts (one inbound, one delivered) in both existing
  inbox/outbox tables, including a payload-based count that detects extra copies;
- synthetic Meta message ID and receipt IDs, configuration hashes, and log scans.

The probe writes no SQL: it only SELECTs under read-only transactions. The existing
webhook inserts the clearly synthetic receipts. They remain in the journal for
audit and never link an applicant or enter outbound dispatch. A repeat run keeps
the same final configuration and creates a new audit and new synthetic test receipts.
Do not paste secret-valued curl commands or run real-token tests before logging is
safe. Raw provider/native failure output is withheld by design.

After `commissioned`, the operator may enter
`https://requestportal.ffpja.org/api/webhooks/whatsapp` in Meta, manually supply the
existing verification token and select **Verify and save**, then subscribe to
`messages`. The script does not create a Meta dashboard subscription.

## Failure and rollback

Preflight failure makes no production configuration changes. Apply failures attempt
automatic rollback of both files, `nginx -t`, nginx reload, portal restart and health.
`rolled-back` means this succeeded. `rollback-needs-operator-attention` requires
immediate review of the protected audit/backups. No database rollback/deletion is
performed: synthetic receipts are retained. Secret values are never rolled back.

For an interrupted or completed run, use its printed audit-directory basename:

```sh
sudo /usr/bin/python3 /usr/local/lib/aegis-requestportal-whatsapp-commissioning/Commission-WhatsApp-Kratos.py \
  --rollback YYYYMMDDTHHMMSSZ-xxxxxxxx
```

Rollback validates all backup checksums before changing either file and refuses
to overwrite configuration changed by another operator/deployment. It leaves the
original file owner/mode intact. After SIGKILL/power loss, inspect the most recent
protected audit directory and execute this command before retrying. Stop and review
configuration drift rather than bypassing a failed hash or permission check.
