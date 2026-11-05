#!/usr/bin/env python3
"""Build operator tools against an existing validated publish; never rebuild/deploy the portal."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
DEPLOYED = "6b09c4452fb2c62c8daac5b91fa0aecc50dc53a3"
WEB_HASH = "9fb70e1900f91e48f513c8f8897517e5e9e96206a554ee62137dcd545304b183"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if hashlib.sha256((args.publish_root / "RequestPortal.Web.dll").read_bytes()).hexdigest() != WEB_HASH:
        parser.error("Publish output does not match the validated deployed 6b09c445 assembly.")
    branch = subprocess.check_output(["git", "-C", str(ROOT), "branch", "--show-current"], text=True).strip()
    dirty = subprocess.check_output(["git", "-C", str(ROOT), "status", "--porcelain", "--",
        "scripts/Commission-WhatsApp-Kratos.py", "scripts/Build-WhatsAppCommissioningBundle.py", "tools/WhatsAppCommissioningProbe"], text=True)
    if branch != "main" or dirty:
        parser.error("Build the operator bundle from clean, reviewed main sources.")
    if args.output.exists():
        parser.error("Output must be a new directory.")
    args.output.mkdir(parents=True)
    subprocess.run(["dotnet", "publish", str(ROOT / "tools/WhatsAppCommissioningProbe/WhatsAppCommissioningProbe.csproj"),
                    "-c", "Release", "-r", "linux-x64", "--self-contained", "false", "-m:1",
                    "-p:UseSharedCompilation=false", "-p:NuGetAudit=false", "-p:PortalPublishRoot=" + str(args.publish_root.resolve()),
                    "-o", str(args.output.resolve() / "probe")], check=True)
    shutil.copyfile(ROOT / "scripts/Commission-WhatsApp-Kratos.py", args.output / "Commission-WhatsApp-Kratos.py")
    source = subprocess.check_output(["git", "-C", str(ROOT), "rev-parse", "HEAD"], text=True).strip()
    files = {str(path.relative_to(args.output)): hashlib.sha256(path.read_bytes()).hexdigest()
             for path in sorted(args.output.rglob("*")) if path.is_file()}
    manifest = {"deployedCommit": DEPLOYED, "toolingCommit": source, "files": files}
    (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print("Prepared operator bundle; no production changes made.")


if __name__ == "__main__":
    main()
