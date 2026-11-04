#!/usr/bin/env python3
"""Fixed-scope administrative launcher. No deployment, migration, queue processing or repair."""
import datetime
import hashlib
import json
import os
import pathlib
import pwd
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent


def protected(path):
    stat = path.lstat()
    if path.is_symlink() or stat.st_uid != 0 or stat.st_mode & 0o022:
        raise RuntimeError("Audit code must be installed root-owned and not writable by other users.")


def service_environment(service):
    pid = subprocess.check_output(["/usr/bin/systemctl", "show", service, "--property=MainPID", "--value"], text=True).strip()
    if not pid.isdecimal() or int(pid) < 2:
        raise RuntimeError("The production service must be running for a configuration-consistent audit.")
    entries = pathlib.Path("/proc", pid, "environ").read_bytes().split(b"\0")
    initial = dict(entry.decode().split("=",1) for entry in entries if b"=" in entry)
    allowed = ("CommonSecrets__", "Bridge__", "FfpManagerIntegration__", "KRB5", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT")
    env = {"PATH": "/usr/bin:/bin", "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1"}
    for key,value in initial.items():
        if key.startswith(allowed):
            env[key] = value
    # A named bootstrap variable is configuration indirection, not a CommonSecrets__ key.
    # The portal's production JSON declares this default; environment overrides may name another.
    names = {"REQUESTPORTAL_OPENBAO_SECRET_ID"} if service == "aegis-requestportal" else set()
    names.update(value for key,value in initial.items() if key.startswith("CommonSecrets__") and key.endswith("__SecretIdEnvironmentVariable"))
    for name in names:
        if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*",name) and name in initial:
            env[name] = initial[name]
    env["DOTNET_ENVIRONMENT"] = "Production"
    env["ASPNETCORE_ENVIRONMENT"] = "Production"
    return env


def run_audit(mode, account, service, cwd):
    user = pwd.getpwnam(account)
    env = service_environment(service)
    env["HOME"] = user.pw_dir

    def drop_privileges():
        os.initgroups(account, user.pw_gid)
        os.setgid(user.pw_gid)
        os.setuid(user.pw_uid)
        os.umask(0o077)

    result = subprocess.run(["/usr/bin/dotnet", str(ROOT / "RequestPortal.IntegrationAudit.dll"), mode], cwd=cwd,
                            env=env, preexec_fn=drop_privileges, capture_output=True, text=True, timeout=100)
    if result.returncode:
        # Provider/driver errors may contain protected data. Do not print stderr or stdout on failure.
        failure = "Unavailable"
        for line in result.stderr.splitlines():
            try:
                detail = json.loads(line)
                candidate = detail.get("errorType","")
                if detail.get("status") == "failed" and isinstance(candidate,str) and re.fullmatch(r"[A-Za-z][A-Za-z0-9_.+]{0,99}",candidate):
                    failure = candidate
            except (ValueError,AttributeError):
                continue
        raise RuntimeError(mode + " audit unavailable (" + failure + "); no database writes were requested.")
    lines = [line for line in result.stdout.splitlines() if line.startswith("{")]
    value = json.loads(lines[-1])
    if value.get("readOnly") is not True:
        raise RuntimeError("The audit did not return its read-only guard.")
    return value


def journal_evidence(service):
    result = subprocess.run(["/usr/bin/journalctl", "--unit=" + service, "--since=7 days ago", "--lines=20000",
                             "--no-pager", "--quiet", "--output=json"], capture_output=True, text=True, timeout=30)
    if result.returncode:
        return {"available": False}
    evidence = []
    for line in result.stdout.splitlines():
        try:
            row = json.loads(line)
        except ValueError:
            continue
        message = row.get("MESSAGE", "")
        if not isinstance(message, str):
            continue
        if not any(term in message.lower() for term in ("exception", "outbox", "integration", "ffp manager", "sqlclient", "dead-letter", "error number", "sqlstate", "cannot insert", "constraint", "truncated")):
            continue
        types = re.findall(r"\b(?:SqlException|HttpRequestException|SocketException|AuthenticationException|InvalidOperationException|ExternalApplicationValidationException|IntegrationTransferException|TimeoutException|IOException|PostgresException|NpgsqlException|TaskCanceledException|OperationCanceledException|UnauthorizedAccessException|SQLSTATE\s+[A-Z0-9]{5}|Error Number:\s*\d{1,6})\b", message)
        # A credential may itself be GUID-shaped. Export only explicitly labelled business correlations.
        ids = re.findall(r"\b(?:RequestPortal request|RequestId|IntegrationLinkId|MessageId|CorrelationId|Outbox message|integration link)\s*[=:]?\s*([0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\b", message, re.IGNORECASE)
        fields = [field for field in ("Parish", "ReferenceNo", "ExternalRequestId", "Occupation", "FirstName", "LastName", "Quantity", "Street", "Town", "MainContactNo", "DateOfBirth", "ApplicationStatus", "Programme", "Requirements")
                  if field in message]
        evidence.append({"utcMicroseconds": row.get("__REALTIME_TIMESTAMP"), "types": types, "correlations": ids,
                         "mappingFields": fields, "fingerprint": hashlib.sha256(message.encode()).hexdigest()})
    return {"available": True, "boundedEntries": 20000, "sanitizedEvents": evidence}


def reconcile(portal, bridge):
    records = []
    for source in portal["records"]:
        received = datetime.datetime.fromisoformat(source["submittedUtc"].replace("Z", "+00:00")) if source.get("submittedUtc") else None
        recent = received is not None and datetime.datetime.now(datetime.timezone.utc) - received < datetime.timedelta(minutes=5)
        matches = [r for r in bridge["records"] if r["requestId"] == source["requestId"] and not r["archived"]]
        aliases = [r for r in bridge["records"] if r["portalReference"] == source["reference"] and not r["archived"] and r not in matches]
        archived = [r for r in bridge["records"] if r["archived"] and (r["requestId"] == source["requestId"] or r["portalReference"] == source["reference"])]
        if not source.get("sourceRequestExists", True):
            outcome, stage, action = "MissingSourceRequest", "SourceIdentity", "Preserve the intake and destination evidence. Investigate source history; do not create a replacement request automatically."
        elif len(matches) > 1 or aliases:
            outcome, stage, action = "AmbiguousDestination", "DestinationIdentity", "Do not retry. Investigate conflicting destination identities with the FFP Manager administrator."
        elif len(matches) == 1:
            target = matches[0]
            if source["status"] == "Synchronized" and source["applicationId"] == target["applicationId"] and source["applicationNumber"] == target["applicationNumber"] and target["applicationNumber"] and target["applicationNumber"].casefold() != source["reference"].casefold():
                outcome, stage, action = "VerifiedSynchronized", "Complete", "No recovery write recommended."
            elif recent and source["status"] in (None,"Pending","NotSent") and not source["errorClassification"]:
                outcome, stage, action = "CommittedAwaitingAcknowledgement", "AcknowledgementOrArtifacts", "Within the expected transfer interval. Allow the current worker to finish and check again before recovery."
            else:
                outcome, stage, action = "CommittedNotAcknowledged", "AcknowledgementOrArtifacts", "Reconcile immutable identity, preserve the existing application and resume acknowledgement/documents only."
        elif archived:
            outcome, stage, action = "ArchivedDestination", "DestinationLifecycle", "An archived destination exists. Do not recreate it automatically; review the archival and ownership history."
        elif source["status"] == "Synchronized":
            outcome, stage, action = "SynchronizedWithoutTarget", "DestinationVerification", "Do not trust source synchronized status. Investigate deletion, archival and target identity before recovery."
        elif source["submissionEvents"] == 0 or (source["integrationEvents"] == 0 and not recent):
            outcome, stage, action = "NeverQueuedForTransfer", "SubmissionQueue", "Verify target absence, approve one audited deterministic queue recovery."
        elif recent and not source["errorClassification"]:
            outcome, stage, action = "PendingWithinExpectedInterval", "SubmissionQueue", "Allow the normal worker interval; do not initiate recovery solely because this newly submitted record is not yet present."
        else:
            outcome, stage, action = "NoVerifiedDestinationRecord", source["errorClassification"] or "PendingOrWorkerInterrupted", "Inspect the correlated sanitized failure evidence; correct permanent errors before a verified, audited retry."
        records.append({"reference": source["reference"], "requestId": source["requestId"], "outcome": outcome, "failureStage": stage,
                        "activeDestinationMatches": len(matches), "archivedDestinationMatches": len(archived),
                        "allDestinationMatches": len(matches) + len(aliases) + len(archived), "recommendation": action})
    return {"inspected": len(records), "verifiedSynchronized": sum(r["outcome"] == "VerifiedSynchronized" for r in records),
            "missingVerifiedTarget": sum(r["activeDestinationMatches"] == 0 and r["outcome"] != "AmbiguousDestination" for r in records),
            "missingFromDestination": sum(r["allDestinationMatches"] == 0 for r in records),
            "missingBeyondExpectedInterval": sum(r["allDestinationMatches"] == 0 and r["outcome"] != "PendingWithinExpectedInterval" for r in records),
            "recordsRequiringRecoveryReview": sum(r["outcome"] not in ("VerifiedSynchronized","PendingWithinExpectedInterval","CommittedAwaitingAcknowledgement") for r in records), "records": records}


def main():
    if len(sys.argv) != 1 or os.geteuid() != 0:
        raise RuntimeError("Run the reviewed fixed audit launcher once using sudo, with no arguments.")
    protected(ROOT)
    for file in ROOT.rglob("*"):
        protected(file)
    evidence = {}
    limitations = []
    for mode,account,service,cwd in (("portal","aegis","aegis-requestportal","/opt/aegis/requestportal"),
                                      ("bridge","aegis-ffpmanager-bridge","aegis-ffpmanager-bridge","/opt/aegis/ffpmanager-bridge")):
        try:
            evidence[mode] = run_audit(mode,account,service,cwd)
        except Exception as exception:
            evidence[mode] = None
            limitations.append({"stage": mode,"errorType":type(exception).__name__,
                                "detail":str(exception) if isinstance(exception,RuntimeError) else "Read-only service-identity inspection was unavailable; do not interpret this as missing data."})
    portal,bridge = evidence["portal"],evidence["bridge"]
    if bridge is not None and (bridge.get("server") != "JMDB01" or bridge.get("database") != "FFPJM"):
        raise RuntimeError("Destination guard failed.")
    report = {"observedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "readOnly": True,
              "status": "partial" if limitations else "complete", "accessLimitations": limitations,
              "auditLauncherSha256": hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
              "portal": portal, "bridge": bridge, "reconciliation": reconcile(portal, bridge) if portal is not None and bridge is not None else None,
              "journals": {service: journal_evidence(service) for service in ("aegis-requestportal", "aegis-ffpmanager-bridge")}}
    # stdout contains selected references and sanitized evidence only; no credentials or raw exception bodies.
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    try:
        main()
    except Exception as exception:
        print(json.dumps({"status": "blocked", "errorType": type(exception).__name__, "detail": str(exception) if isinstance(exception, RuntimeError) else "Read-only audit could not complete."}))
        sys.exit(1)
