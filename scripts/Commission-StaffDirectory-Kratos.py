#!/usr/bin/env python3
"""One-time, portal-only commissioning. No generic command/path arguments or identity writes."""
import hashlib
import fcntl
import json
import os
import pathlib
import re
import shlex
import shutil
import socket
import stat
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

TENANT = "257d294c-c8a7-4b86-b791-6755edc4fe0a"
CLIENT = "45d755f5-3dd4-45b6-ae13-366b8122bb2b"
ISSUER = "https://login.microsoftonline.com/" + TENANT + "/v2.0"
SERVICE = "aegis-requestportal"
APPLICATION = pathlib.Path("/opt/aegis/requestportal")
ENVIRONMENT = pathlib.Path("/etc/aegis/requestportal.env")
ASSEMBLY = APPLICATION / "RequestPortal.Web.dll"
AUDIT_ROOT = pathlib.Path("/var/lib/aegis/requestportal-commissioning")
EXPECTED = {
    "Authentication__StaffDirectory__Enabled": "true",
    "Authentication__StaffDirectory__Provider": "Entra",
    "Authentication__StaffDirectory__TenantId": TENANT,
    "Authentication__StaffDirectory__ClientId": CLIENT,
    "Authentication__StaffDirectory__ClientSecretName": "Authentication:StaffOidc:ClientSecret",
}


class CommissioningError(RuntimeError):
    pass


def validate_desired(text):
    result = {}
    for line in text.splitlines():
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        key, separator, value = line.partition("=")
        if not separator or key in result or key not in EXPECTED or value != EXPECTED[key]:
            raise CommissioningError("Desired configuration must contain exactly the five approved non-secret settings.")
        result[key] = value
    if result != EXPECTED:
        raise CommissioningError("Desired configuration is incomplete.")
    return result


def patch_environment(original, changes):
    """Preserve all unrelated bytes, comments and credentials; normalize only changed assignments."""
    kept = []
    for line in original.splitlines(keepends=True):
        match = re.match(r"^[ \t]*([A-Za-z_][A-Za-z0-9_]*)[ \t]*=", line)
        if not match or match.group(1) not in changes:
            kept.append(line)
    result = "".join(kept)
    if result and not result.endswith("\n"):
        result += "\n"
    return result + "".join(key + "=" + value + "\n" for key, value in changes.items())


def quote_environment(value):
    if any(c in value for c in "\x00\n\r"):
        raise CommissioningError("Unsupported multiline unit environment; no changes made.")
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def private_write(path, value):
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w") as stream:
        stream.write(value)
        stream.flush()
        os.fsync(stream.fileno())


def protected_file(path):
    info = path.lstat()
    if path.is_symlink() or info.st_uid != 0 or info.st_mode & 0o022:
        raise CommissioningError("Commissioning input must be a root-owned, non-writable regular file.")
    if not stat.S_ISREG(info.st_mode):
        raise CommissioningError("Invalid commissioning input type.")


def native(arguments, timeout=30):
    process = subprocess.run(arguments, capture_output=True, text=True, timeout=timeout)
    if process.returncode:
        # Never emit raw commands/output: systemd and providers may include protected values.
        raise CommissioningError("A required privileged operation failed; protected output withheld.")
    return process.stdout.strip()


def property_value(name):
    return native(["/usr/bin/systemctl", "show", SERVICE, "--property=" + name, "--value"])


def atomic_environment(content, metadata):
    temporary = ENVIRONMENT.with_name(".requestportal.env." + uuid.uuid4().hex)
    try:
        private_write(temporary, content)
        shutil.copystat(ENVIRONMENT, temporary)
        os.chown(temporary, metadata.st_uid, metadata.st_gid)
        os.replace(temporary, ENVIRONMENT)
        descriptor = os.open(ENVIRONMENT.parent, os.O_DIRECTORY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    finally:
        temporary.unlink(missing_ok=True)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *arguments):
        return None


def verify_health():
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        try:
            request = urllib.request.Request("http://127.0.0.1:5401/health", headers={"Host": "requestportal.ffpja.org"})
            with urllib.request.urlopen(request, timeout=5) as response:
                if json.load(response).get("status") == "Healthy":
                    break
        except (OSError, ValueError):
            pass
        time.sleep(2)
    else:
        raise CommissioningError("Request Portal local health did not recover.")
    with urllib.request.urlopen("https://requestportal.ffpja.org/health", timeout=15) as response:
        if response.status != 200 or json.load(response).get("status") != "Healthy":
            raise CommissioningError("Public HTTPS health failed.")
    try:
        response = urllib.request.build_opener(NoRedirect).open("https://requestportal.ffpja.org/Account/StaffLogin", timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    endpoint = urllib.parse.urlsplit(response.headers.get("Location", ""))
    query = urllib.parse.parse_qs(endpoint.query)
    if response.status != 302 or endpoint.hostname != "login.microsoftonline.com" or endpoint.path != "/" + TENANT + "/oauth2/v2.0/authorize" or query.get("client_id") != [CLIENT]:
        raise CommissioningError("Existing staff authentication challenge changed unexpectedly.")


def main():
    if sys.argv[1:] != ["--apply"] or os.geteuid() != 0 or socket.gethostname().split(".")[0] != "kratos":
        raise CommissioningError("Run the installed commissioning command as root on Kratos with --apply.")
    installed = pathlib.Path(__file__).resolve()
    manifest_path = pathlib.Path(str(installed) + ".json")
    desired_path = pathlib.Path(str(installed) + ".env")
    for path in (installed, manifest_path, desired_path, ENVIRONMENT):
        protected_file(path)
    manifest = json.loads(manifest_path.read_text())
    if not re.fullmatch(r"[0-9a-f]{40}", manifest.get("sourceCommit", "")):
        raise CommissioningError("A verified merged source commit is required.")
    if hashlib.sha256(ASSEMBLY.read_bytes()).hexdigest() != manifest.get("assemblySha256"):
        raise CommissioningError("The installed portal assembly does not match the reviewed compatibility build.")
    changes = validate_desired(desired_path.read_text())
    if property_value("WorkingDirectory") != str(APPLICATION) or str(ASSEMBLY) not in property_value("ExecStart"):
        raise CommissioningError("Unexpected Request Portal service definition.")
    if property_value("EnvironmentFiles") != str(ENVIRONMENT) + " (ignore_errors=no)":
        raise CommissioningError("Unexpected environment-file composition; no changes made.")
    if any(property_value(name) for name in ("LoadCredential", "LoadCredentialEncrypted", "SetCredential")):
        raise CommissioningError("Service uses systemd credentials; faithful diagnostic credential handling needs review.")
    user = property_value("User")
    group = property_value("Group") or user
    if user in ("", "root") or not all(re.fullmatch(r"[a-z_][a-z0-9_-]*", item) for item in (user, group)):
        raise CommissioningError("A non-root Request Portal service identity is required.")
    audit_root = AUDIT_ROOT
    audit_root.mkdir(mode=0o700, exist_ok=True)
    protected_directory = audit_root.stat()
    if protected_directory.st_uid != 0 or protected_directory.st_mode & 0o077 or audit_root.is_symlink():
        raise CommissioningError("Commissioning audit directory is not private and root-owned.")
    work = audit_root / (time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) + "-" + uuid.uuid4().hex[:8])
    work.mkdir(mode=0o700)
    original = ENVIRONMENT.read_bytes().decode("utf-8")
    metadata = ENVIRONMENT.stat()
    private_write(work / "requestportal.env.before", original)
    unit_environment = {}
    for assignment in shlex.split(property_value("Environment")):
        key, separator, value = assignment.partition("=")
        if not separator or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", key):
            raise CommissioningError("Unexpected unit environment format.")
        unit_environment[key] = value
    prefix = "".join(key + "=" + quote_environment(value) + "\n" for key, value in unit_environment.items())
    audit = {"sourceCommit": manifest["sourceCommit"], "applicationId": CLIENT, "secretReference": EXPECTED["Authentication__StaffDirectory__ClientSecretName"], "backup": str(work / "requestportal.env.before"), "identityRecordsModifiedByProcedure": False}

    def probe(content, mode):
        environment_path = work / (mode + "-" + uuid.uuid4().hex + ".env")
        private_write(environment_path, prefix + content)
        arguments = ["/usr/bin/systemd-run", "--quiet", "--wait", "--pipe", "--collect", "--unit=aegis-requestportal-directory-check-" + uuid.uuid4().hex,
                     "--property=User=" + user, "--property=Group=" + group,
                     "--property=WorkingDirectory=" + str(APPLICATION), "--property=EnvironmentFile=" + str(environment_path),
                     "--property=RuntimeMaxSec=75", "/usr/bin/dotnet", str(ASSEMBLY), mode]
        try:
            output = native(arguments, timeout=85)
        finally:
            environment_path.unlink(missing_ok=True)
        try:
            result = json.loads(output.splitlines()[-1])
        except (ValueError, IndexError):
            raise CommissioningError("The protected diagnostic returned no valid evidence.")
        if result.get("status") != "ok" or result.get("compatibility") != "issuer-sub-and-tenant-object-read-only-v1":
            raise CommissioningError("The reviewed identity compatibility diagnostic did not pass.")
        return result

    applied = False
    try:
        before = probe(original, "inspect-staff-directory")
        if not before.get("staffEnabled") or before.get("staffClientId") != CLIENT or before.get("staffProviderId") != "requestportal-staff":
            raise CommissioningError("Existing staff authentication identity differs from the approved application.")
        if before.get("issuer", "").rstrip("/") != ISSUER or before.get("authority", "").rstrip("/") not in (ISSUER, ISSUER.removesuffix("/v2.0")):
            raise CommissioningError("Staff authority/issuer mismatch requires review before changing working sign-in.")
        if before.get("staffBindingCount") != 4:
            raise CommissioningError("Existing staff binding count differs from the four audited bindings.")
        tenants = before.get("allowedTenantIds", [])
        if TENANT not in [str(value).lower() for value in tenants]:
            indices = [int(value) for value in re.findall(r"Authentication__StaffOidc__AllowedTenantIds__(\d+)", prefix + original)]
            index = max(indices + [len(tenants) - 1]) + 1
            changes["Authentication__StaffOidc__AllowedTenantIds__" + str(index)] = TENANT
        candidate = patch_environment(original, changes)
        checked = probe(candidate, "verify-staff-directory")
        if checked.get("staffBindingFingerprint") != before.get("staffBindingFingerprint"):
            raise CommissioningError("Staff identity bindings changed during preflight; no configuration applied.")
        audit["before"] = before
        audit["candidate"] = checked
        if ENVIRONMENT.read_bytes().decode("utf-8") != original:
            raise CommissioningError("Production configuration changed during preflight; no settings applied.")
        # The protected backup and successful Common.Secrets/Graph preflight exist before promotion.
        applied = True
        atomic_environment(candidate, metadata)
        native(["/usr/bin/systemctl", "restart", SERVICE], timeout=75)
        verify_health()
        after = probe(candidate, "verify-staff-directory")
        if after.get("staffBindingFingerprint") != before.get("staffBindingFingerprint") or after.get("staffBindingCount") != 4:
            raise CommissioningError("Staff binding preservation could not be verified after restart.")
        audit["after"] = after
        audit["status"] = "commissioned"
        private_write(work / "result.json", json.dumps(audit, indent=2) + "\n")
        print(json.dumps({"status": "commissioned", "service": SERVICE, "health": "Healthy", "applicationId": CLIENT,
                          "secretReference": EXPECTED["Authentication__StaffDirectory__ClientSecretName"],
                          "staffBindingsPreserved": 4, "directorySearchMatches": after["searchMatches"],
                          "individualLookupVerified": after["individualLookupVerified"], "audit": str(work / "result.json")}))
    except Exception:
        if applied:
            try:
                if ENVIRONMENT.read_bytes().decode("utf-8") not in (candidate, original):
                    raise CommissioningError("Configuration changed concurrently; automatic restore needs operator review.")
                atomic_environment(original, metadata)
                native(["/usr/bin/systemctl", "restart", SERVICE], timeout=75)
                verify_health()
                audit["status"] = "rolled-back"
            except Exception:
                audit["status"] = "rollback-needs-operator-attention"
        else:
            audit["status"] = "preflight-failed-no-production-config-change"
        private_write(work / "result.json", json.dumps(audit, indent=2) + "\n")
        print(json.dumps({"status": audit["status"], "audit": str(work / "result.json")}))
        raise


if __name__ == "__main__":
    try:
        if os.geteuid() != 0:
            raise CommissioningError("An authorized administrator must run the installed command through sudo.")
        descriptor = os.open("/run/lock/aegis-requestportal-directory-commission.lock", os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
        with os.fdopen(descriptor, "w") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            main()
    except Exception as exception:
        message = str(exception) if type(exception) is CommissioningError else type(exception).__name__
        print("STOPPED: " + message)
        sys.exit(1)
