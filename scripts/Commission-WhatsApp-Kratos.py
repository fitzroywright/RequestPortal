#!/usr/bin/env python3
"""Explicit operator-only, configuration-only WhatsApp commissioning on Kratos.
Never invokes sudo, a deployer, a shell, migrations, outbound messaging or secret writes.
"""
import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import socket
import stat
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

SERVICE = "aegis-requestportal"
APP = Path("/opt/aegis/requestportal")
ENV = Path("/etc/aegis/requestportal.env")
SITE = Path("/etc/nginx/sites-available/requestportal.ffpja.org")
MAIN = Path("/etc/nginx/nginx.conf")
AUDIT = Path("/var/lib/requestportal-whatsapp-commissioning")
NGINX = "/usr/sbin/nginx"
BASE_SITE_HASH = "948774fab5f720e90c298a86341e028e75dd3efe84917323c89bcf64f38a969b"
ASSEMBLY_HASH = "9fb70e1900f91e48f513c8f8897517e5e9e96206a554ee62137dcd545304b183"
DEPLOYED_COMMIT = "6b09c4452fb2c62c8daac5b91fa0aecc50dc53a3"
EXPECTED = {
    "Notifications__WhatsAppWebhookEnabled": "true",
    "Notifications__WhatsAppPhoneNumberId": "1427917097069650",
    "Notifications__WhatsAppBusinessAccountId": "1823610105620506",
    "Notifications__WhatsAppAccessTokenSecretName": "messaging/whatsapp/api-token",
}
MAP = """# BEGIN RequestPortal WhatsApp access-log protection
map $uri $requestportal_whatsapp_loggable {
    default 1;
    ~*^/api/webhooks/whatsapp/?$ 0;
}
# END RequestPortal WhatsApp access-log protection

"""
LOG = "    access_log /var/log/nginx/access.log combined if=$requestportal_whatsapp_loggable;\n"


class Stop(RuntimeError):
    pass


def digest(data):
    return hashlib.sha256(data).hexdigest()


def native(args, timeout=35):
    result = subprocess.run(args, capture_output=True, text=True, timeout=timeout)
    if result.returncode:
        raise Stop("Required operation failed; protected command output withheld.")
    return result.stdout.strip()


def protected(path, private=False):
    info = path.lstat()
    if path.is_symlink() or not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_mode & (0o077 if private else 0o022):
        raise Stop("Expected a protected root-owned regular file; no configuration changed.")
    return info


def protected_directory(path, private=False):
    info = path.lstat()
    if path.is_symlink() or not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or info.st_mode & (0o077 if private else 0o022):
        raise Stop("Expected a protected root-owned directory.")


def private_write(path, data):
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, "wb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())


def atomic_replace(path, data, metadata):
    temporary = path.with_name("." + path.name + ".whatsapp-" + uuid.uuid4().hex)
    try:
        private_write(temporary, data)
        os.chown(temporary, metadata.st_uid, metadata.st_gid)
        os.chmod(temporary, stat.S_IMODE(metadata.st_mode))
        os.replace(temporary, path)
        descriptor = os.open(path.parent, os.O_DIRECTORY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    finally:
        temporary.unlink(missing_ok=True)


def patch_environment(data):
    # A deliberately conservative parser: never source an environment file or interpret shell code.
    # Refuse multiline assignments instead of accidentally changing a line inside a credential.
    kept, actual = [], {}
    for line in data.decode("utf-8").splitlines(keepends=True):
        if not line.strip() or line.lstrip().startswith(("#", ";")):
            kept.append(line)
            continue
        match = re.fullmatch(r"[ \t]*([A-Za-z_][A-Za-z0-9_]*)=(.*?)(?:\r?\n)?", line)
        if not match or line.rstrip("\r\n").endswith("\\"):
            raise Stop("Multiline or unsupported environment assignment requires operator review.")
        try:
            values = shlex.split(match[2], comments=False)
        except ValueError:
            raise Stop("Multiline or unsupported environment quoting requires operator review.") from None
        if len(values) > 1:
            raise Stop("Unquoted environment whitespace requires operator review.")
        value = values[0] if values else ""
        key = match[1].casefold()
        actual[key] = value
        if key not in {key.casefold() for key in EXPECTED}:
            kept.append(line)
    # Preserve outbound configuration exactly; never turn on/off a previously commissioned channel.
    if actual.get("notifications__whatsappenabled", "false").lower() != "false":
        raise Stop("Outbound WhatsApp is already enabled; stop for operator review.")
    text = "".join(kept)
    if text and not text.endswith("\n"):
        text += "\n"
    text += "# Meta development test number; future production number requires separate commissioning.\n"
    text += "".join(key + "=" + value + "\n" for key, value in EXPECTED.items())
    # Avoid accumulating generated comments on rerun.
    marker = "# Meta development test number; future production number requires separate commissioning.\n"
    while text.count(marker) > 1:
        text = text.replace(marker, "", 1)
    return text.encode("utf-8")


def patch_site(data):
    text = data.decode("utf-8")
    if text.startswith(MAP):
        original = text[len(MAP):].replace(LOG, "")
        if digest(original.encode()) != BASE_SITE_HASH or text.count(LOG) != 2:
            raise Stop("Managed nginx site has drifted; no changes made.")
        return data
    if digest(data) != BASE_SITE_HASH or text.count("server {\n") != 2:
        raise Stop("Nginx site differs from the reviewed Kratos site; no changes made.")
    return (MAP + text.replace("server {\n", "server {\n" + LOG)).encode()


def property_value(name):
    return native(["/usr/bin/systemctl", "show", SERVICE, "--property=" + name, "--value"])


def service_context():
    expected = {"User": "aegis", "Group": "aegis", "WorkingDirectory": str(APP),
                "EnvironmentFiles": str(ENV) + " (ignore_errors=no)"}
    for name, value in expected.items():
        if property_value(name) != value:
            raise Stop("Service definition differs from the reviewed Request Portal service.")
    if not re.fullmatch(r".*path=/usr/bin/dotnet ; argv\[\]=/usr/bin/dotnet /opt/aegis/requestportal/RequestPortal.Web.dll ;.*", property_value("ExecStart")):
        raise Stop("Unexpected Request Portal ExecStart.")
    for name in ("PassEnvironment", "UnsetEnvironment", "RootDirectory", "RootImage"):
        if property_value(name):
            raise Stop("Unsupported service environment or filesystem context.")
    bus = ["/usr/bin/busctl", "--json=short"]
    unit = json.loads(native(bus + ["call", "org.freedesktop.systemd1", "/org/freedesktop/systemd1", "org.freedesktop.systemd1.Manager", "GetUnit", "s", SERVICE + ".service"]))
    path = unit["data"][0]
    for name in ("LoadCredential", "LoadCredentialEncrypted", "SetCredential", "SetCredentialEncrypted"):
        result = json.loads(native(bus + ["get-property", "org.freedesktop.systemd1", path, "org.freedesktop.systemd1.Service", name]))
        if result.get("type") not in ("a(ss)", "a(say)") or result.get("data") != []:
            raise Stop("Systemd credentials require a separately reviewed probe context.")
    prefix = []
    for assignment in shlex.split(property_value("Environment")):
        key, sep, value = assignment.partition("=")
        if not sep or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", key) or any(c in value for c in "\n\r\0"):
            raise Stop("Unsupported unit environment.")
        prefix.append(key + '="' + value.replace("\\", "\\\\").replace('"', '\\"') + '"\n')
    return "".join(prefix).encode()


def staff_login_redirect_is_safe(location):
    # urlsplit tolerates whitespace/control characters; reject them before parsing.
    # Allow only URL characters and well-formed escapes, never browser backslashes.
    if not re.fullmatch(r"[A-Za-z0-9._~:/?\[\]@!$&'()*+,;=%-]+", location):
        return False
    if re.search(r"%(?![0-9A-Fa-f]{2})", location):
        return False
    if re.search(r"%(?:0[0-9a-f]|1[0-9a-f]|7f|5c)", location, re.IGNORECASE):
        return False
    try:
        target = urllib.parse.urlsplit(location)
    except ValueError:
        return False
    if target.path != "/Account/StaffLogin" or target.fragment:
        return False
    if location.startswith("/Account/StaffLogin"):
        return not target.scheme and not target.netloc
    # An explicit default TLS port is the same origin. No userinfo or other ports.
    return target.scheme == "https" and target.netloc.lower() in (
        "requestportal.ffpja.org", "requestportal.ffpja.org:443")


def health():
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args):
            return None
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        try:
            request = urllib.request.Request("http://127.0.0.1:5401/health", headers={"Host": "requestportal.ffpja.org"})
            with urllib.request.urlopen(request, timeout=5) as response:
                if response.status == 200 and json.load(response).get("status") == "Healthy":
                    break
        except (OSError, ValueError):
            pass
        time.sleep(2)
    else:
        raise Stop("Local health check failed.")
    for url in ("https://requestportal.ffpja.org/health", "https://requestportal.ffpja.org/"):
        with urllib.request.urlopen(url, timeout=15) as response:
            if response.status != 200:
                raise Stop("Public HTTPS health/home check failed.")
    try:
        response = urllib.request.build_opener(NoRedirect).open("https://requestportal.ffpja.org/Staff", timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    if response.status != 302 or not staff_login_redirect_is_safe(response.headers.get("Location", "")):
        raise Stop("Staff authentication enforcement changed.")
    for service in (SERVICE, "nginx"):
        native(["/usr/bin/systemctl", "is-active", "--quiet", service])


def validate_nginx(work, candidate):
    # Validate the complete live graph with only this site's include substituted.
    native([NGINX, "-t"])
    main = MAIN.read_text()
    include = "include /etc/nginx/sites-enabled/*;"
    if main.count(include) != 1:
        raise Stop("Unexpected nginx include graph.")
    enabled = sorted(Path("/etc/nginx/sites-enabled").iterdir())
    if sum(path.resolve() == SITE for path in enabled) != 1:
        raise Stop("Portal site is not enabled exactly once.")
    candidate_path = work / "nginx-site.candidate"
    private_write(candidate_path, candidate)
    paths = [candidate_path if path.resolve() == SITE else path for path in enabled]
    if any(not re.fullmatch(r"/[A-Za-z0-9_./-]+", str(path)) for path in paths):
        raise Stop("Unexpected nginx include path.")
    private_write(work / "nginx-main.candidate", main.replace(include, "\n".join("include " + str(path) + ";" for path in paths)).encode())
    native([NGINX, "-t", "-p", "/etc/nginx/", "-c", str(work / "nginx-main.candidate")])
    return digest(native([NGINX, "-T"]).encode())


def run_probe(bundle, work, prefix, environment, mode, since):
    snapshot = work / ("probe-" + uuid.uuid4().hex + ".env")
    private_write(snapshot, prefix + environment)
    try:
        # systemd reads the 0600 snapshot as root; the secret-read and HTTPS checks run
        # as the unchanged workload UID, with no shell or secret-valued arguments.
        arguments = ["/usr/bin/systemd-run", "--quiet", "--wait", "--pipe", "--collect",
                         "--unit=requestportal-whatsapp-probe-" + uuid.uuid4().hex,
                         "--property=User=" + ("root" if mode == "scan-logs" else "aegis"),
                         "--property=Group=" + ("root" if mode == "scan-logs" else "aegis"),
                         "--property=WorkingDirectory=" + str(APP), "--property=PrivateTmp=true",
                         "--property=NoNewPrivileges=true", "--property=EnvironmentFile=" + str(snapshot),
                         "--property=RuntimeMaxSec=100", "/usr/bin/dotnet", str(bundle / "probe/WhatsAppCommissioningProbe.dll"), mode, str(since)]
        process = subprocess.run(arguments, capture_output=True, text=True, timeout=110)
        try:
            results = [json.loads(line) for line in process.stdout.splitlines()]
        except ValueError:
            raise Stop("Protected validation probe returned no valid evidence.") from None
        if process.returncode:
            phase = results[-1].get("phase") if results else None
            allowed = {"arguments", "configuration", "approle-secret-read", "approle-verification-token-read",
                       "approle-app-secret-read", "approle-api-token-read", "database-read", "https-verification", "log-privacy"}
            raise Stop("Protected validation probe failed at " + (phase if phase in allowed else "unclassified-stage") + "; raw output withheld.")
        if not results or results[-1].get("status") != "ok" or results[-1].get("mode") != mode:
            raise Stop("Protected validation probe failed.")
        return results
    finally:
        snapshot.unlink(missing_ok=True)


def check_bundle(bundle):
    protected_directory(bundle / "probe")
    current = bundle
    while current != current.parent:
        protected_directory(current)
        current = current.parent
    protected(bundle / "Commission-WhatsApp-Kratos.py")
    protected(bundle / "manifest.json")
    manifest = json.loads((bundle / "manifest.json").read_text())
    if manifest.get("deployedCommit") != DEPLOYED_COMMIT:
        raise Stop("Bundle does not target the approved deployed commit.")
    required = {"Commission-WhatsApp-Kratos.py", "probe/WhatsAppCommissioningProbe.dll",
                "probe/WhatsAppCommissioningProbe.runtimeconfig.json", "probe/WhatsAppCommissioningProbe.deps.json"}
    if not required.issubset(manifest["files"]):
        raise Stop("Incomplete operator bundle.")
    for name, expected in manifest["files"].items():
        if not re.fullmatch(r"(?:probe/)?[A-Za-z0-9_.-]+", name):
            raise Stop("Invalid bundle path.")
        protected(bundle / name)
        if digest((bundle / name).read_bytes()) != expected:
            raise Stop("Bundle checksum mismatch.")
    if digest((APP / "RequestPortal.Web.dll").read_bytes()) != ASSEMBLY_HASH:
        raise Stop("Deployed application differs from 6b09c445; rebuild/review the probe bundle.")
    for name in ("Common.Secrets.dll", "Common.Diagnostics.dll", "Npgsql.dll"):
        if digest((APP / name).read_bytes()) != manifest["files"]["probe/" + name]:
            raise Stop("Probe dependencies differ from the deployed libraries.")
    return manifest


def restore(work):
    evidence = json.loads((work / "result.json").read_text())
    # Refuse to overwrite any operator/deployment changes made after this attempt.
    for name, path in (("env", ENV), ("site", SITE)):
        current = digest(path.read_bytes())
        if current not in (evidence[name + "Before"], evidence[name + "After"]):
            raise Stop("Configuration drift prevents automatic rollback; inspect protected backups.")
    backups = []
    for name, path in (("env", ENV), ("site", SITE)):
        backup = work / (name + ".before")
        protected(backup, private=True)
        data = backup.read_bytes()
        if digest(data) != evidence[name + "Before"]:
            raise Stop("Backup checksum mismatch.")
        backups.append((path, data, path.stat()))
    for path, data, metadata in backups:
        atomic_replace(path, data, metadata)
    native([NGINX, "-t"])
    native(["/usr/bin/systemctl", "reload", "nginx"])
    native(["/usr/bin/systemctl", "restart", SERVICE], timeout=80)
    health()


def commission(mode, bundle):
    check_bundle(bundle)
    env_metadata = protected(ENV, private=True)
    site_metadata = protected(SITE)
    protected(MAIN)
    prefix = service_context()
    original_env, original_site = ENV.read_bytes(), SITE.read_bytes()
    candidate_env, candidate_site = patch_environment(original_env), patch_site(original_site)
    AUDIT.mkdir(mode=0o700, exist_ok=True)
    protected_directory(AUDIT, private=True)
    work = AUDIT / (time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) + "-" + uuid.uuid4().hex[:8])
    work.mkdir(mode=0o700)
    since = int(time.time())
    evidence = {"status": "preflight", "mode": mode, "deployedCommit": DEPLOYED_COMMIT,
                "envBefore": digest(original_env), "siteBefore": digest(original_site),
                "envAfter": digest(candidate_env), "siteAfter": digest(candidate_site), "since": since,
                "desired": EXPECTED, "outboundEnabled": False}
    def save():
        temporary = work / ("result-" + uuid.uuid4().hex)
        private_write(temporary, (json.dumps(evidence, indent=2) + "\n").encode())
        os.replace(temporary, work / "result.json")
    save()
    changed = False
    try:
        private_write(work / "env.before", original_env)
        private_write(work / "site.before", original_site)
        private_write(work / "env.candidate", candidate_env)
        fingerprint = validate_nginx(work, candidate_site)
        health()
        evidence["secretPreflight"] = run_probe(bundle, work, prefix, candidate_env, "preflight", since)
        # Check existing logs before the first real verification token is put into any HTTP URL.
        evidence["logPreflight"] = run_probe(bundle, work, prefix, candidate_env, "scan-logs", since)
        if mode != "apply":
            evidence["status"] = "preflight-passed-no-production-change"
            save()
            print(json.dumps({"status": evidence["status"], "audit": str(work / "result.json"), "desired": EXPECTED,
                              "nginxChange": "Suppress access logging only for normalized webhook path; proxy/TLS unchanged."}))
            return
        if ENV.read_bytes() != original_env or SITE.read_bytes() != original_site or digest(native([NGINX, "-T"]).encode()) != fingerprint:
            raise Stop("Configuration changed during preflight; nothing applied.")
        check_bundle(bundle)
        if service_context() != prefix:
            raise Stop("Service context changed during preflight.")
        # Secret reads and candidate nginx -t have passed before enabling or restarting anything.
        print(json.dumps({"status": "applying", "audit": str(work / "result.json")}), flush=True)
        changed = True
        atomic_replace(SITE, candidate_site, site_metadata)
        native([NGINX, "-t"])
        native(["/usr/bin/systemctl", "reload", "nginx"])
        # Harmless canary proves the running nginx workers suppress query-bearing access logs.
        canary = "rp-whatsapp-log-canary-" + uuid.uuid4().hex
        try:
            urllib.request.urlopen("https://requestportal.ffpja.org/api/webhooks/whatsapp?hub.verify_token=" + canary, timeout=15).close()
        except urllib.error.HTTPError:
            pass
        for path in (Path("/var/log/nginx/access.log"), Path("/var/log/nginx/access.log.1")):
            if path.exists():
                with path.open() as stream:
                    if any(canary in line for line in stream):
                        raise Stop("Running nginx still logs webhook query strings; no real token request sent.")
        if not Path("/var/log/nginx/access.log").is_file():
            raise Stop("Nginx access log cannot be inspected.")
        if ENV.read_bytes() != original_env or SITE.read_bytes() != candidate_site:
            raise Stop("Configuration changed before environment promotion.")
        atomic_replace(ENV, candidate_env, env_metadata)
        native(["/usr/bin/systemctl", "restart", SERVICE], timeout=80)
        health()
        evidence["verification"] = run_probe(bundle, work, prefix, candidate_env, "verify", since)
        evidence["logVerification"] = run_probe(bundle, work, prefix, candidate_env, "scan-logs", since)
        evidence["status"] = "commissioned"
        save()
        print(json.dumps({"status": "commissioned", "audit": str(work / "result.json"), "callback": "https://requestportal.ffpja.org/api/webhooks/whatsapp",
                          "outboundEnabled": False}))
    except BaseException as error:
        evidence["failure"] = str(error) if type(error) is Stop else type(error).__name__
        evidence["status"] = "failed-before-production-change"
        save()
        if changed:
            try:
                restore(work)
                evidence["status"] = "rolled-back"
            except BaseException:
                evidence["status"] = "rollback-needs-operator-attention"
        save()
        print(json.dumps({"status": evidence["status"], "audit": str(work / "result.json")}))
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group(required=True)
    modes.add_argument("--preflight", action="store_true")
    modes.add_argument("--dry-run", action="store_true")
    modes.add_argument("--apply", action="store_true")
    modes.add_argument("--rollback", metavar="AUDIT_ID")
    args = parser.parse_args()
    if os.geteuid() != 0 or socket.gethostname().split(".")[0] != "kratos":
        raise Stop("An authorized operator must explicitly run this command with sudo on Kratos.")
    os.umask(0o077)
    for path in ("/usr/bin/systemctl", "/usr/bin/systemd-run", "/usr/bin/dotnet", "/usr/bin/busctl", NGINX, "/usr/bin/journalctl"):
        if not Path(path).is_file():
            raise Stop("A required host prerequisite is unavailable.")
    descriptor = os.open("/run/lock/requestportal-whatsapp-commission.lock", os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, "w") as lock:
        info = os.fstat(lock.fileno())
        if info.st_uid != 0 or not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_mode & 0o077:
            raise Stop("Commissioning lock is not private and root-owned.")
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        bundle = Path(__file__).resolve().parent
        if args.rollback:
            check_bundle(bundle)
            service_context()
            if not re.fullmatch(r"[0-9]{8}T[0-9]{6}Z-[a-f0-9]{8}", args.rollback):
                raise Stop("Rollback requires the printed audit directory ID, not an arbitrary path.")
            work = AUDIT / args.rollback
            protected_directory(AUDIT, private=True)
            protected_directory(work, private=True)
            protected(work / "result.json", private=True)
            restore(work)
            evidence = json.loads((work / "result.json").read_text())
            evidence["status"] = "rolled-back"
            evidence["rollbackUtc"] = int(time.time())
            atomic_replace(work / "result.json", (json.dumps(evidence, indent=2) + "\n").encode(), (work / "result.json").stat())
            print(json.dumps({"status": "rolled-back", "audit": str(work / "result.json")}))
        else:
            commission("apply" if args.apply else "dry-run" if args.dry_run else "preflight", bundle)


if __name__ == "__main__":
    try:
        main()
    except BaseException as exception:
        # Only our fixed, secret-free messages are printable; never emit provider/native errors.
        print("STOPPED: " + (str(exception) if type(exception) is Stop else type(exception).__name__))
        sys.exit(1)
