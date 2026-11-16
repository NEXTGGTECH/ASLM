# Copyright NEXTGGTECH. Apache License 2.0.

import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tarfile


# Run the installed launcher and wait for the inherited output pipes of its actual GUI process to close.
def verify_window(launcher):
    process = subprocess.Popen(
        [str(launcher), "--verify-linux"],
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        start_new_session=True,
    )
    try:
        output, _ = process.communicate(timeout=90)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        output, _ = process.communicate()
        print(output, flush=True)
        raise RuntimeError("ASLM did not complete the window/WebKit check within 90 seconds.") from None
    print(output, flush=True)
    if process.returncode != 0 or "ASLM_LINUX_COMPATIBILITY_OK" not in output.splitlines():
        raise RuntimeError("The installed launcher did not complete the GTK/WebKit check.")


# Verify first-run bootstrapping and a per-user update using the exact release archive on a noexec /tmp host.
def main():
    archive = Path(sys.argv[1]).resolve()
    root = Path(os.environ["ASLM_ROOT"]).resolve()
    launcher = Path("/opt/ASLM/ASLM")
    manifest = Path("/opt/ASLM/install-manifest.json")
    shared_version = manifest.read_bytes()
    if not os.statvfs("/tmp").f_flag & os.ST_NOEXEC:
        raise RuntimeError("This compatibility check requires /tmp to be mounted with noexec.")

    # The installed launcher must create App, Patcher, seeds and its reference before the GUI check succeeds.
    verify_window(launcher)
    for relative in ("App/ASLM", "Patcher/aslm-patcher", "Data/App/ASLM_Data.json", "launcher-ref.json"):
        if not (root / relative).is_file():
            raise RuntimeError(f"The first launch omitted {relative}.")

    # A prerelease descriptor exercises user-file replacement without requesting a shared-folder update.
    staging = root / ".aslm-update" / "staging" / "compatibility"
    staging.mkdir(parents=True)
    staged_archive = staging / "update.tar.gz"
    shutil.copyfile(archive, staged_archive)
    with tarfile.open(staged_archive, "r:gz") as payload:
        payload.extractall(staging, filter="data")
    payload_root = staging / "ASLM-linux-amd64"
    marker = "compatibility-update.txt"
    (payload_root / marker).write_text("updated\n", encoding="utf-8")
    version = json.loads(shared_version)["version"]
    pending = root / ".aslm-update" / "pending.json"
    pending.write_text(json.dumps({
        "kind": "app",
        "version": f"v{version}-rc1",
        "stagingPath": str(payload_root),
        "targetRoot": str(root),
        "archivePath": str(staged_archive),
        "backupPath": str(root / ".aslm-update" / "backup" / "compatibility"),
        "preserve": ["Data/**", "Engines/**", "Models/**", "Modules/**", "launcher-ref.json"],
    }), encoding="utf-8")

    verify_window(launcher)
    if pending.exists() or (root / marker).read_text(encoding="utf-8") != "updated\n":
        raise RuntimeError("The patcher did not apply the staged update.")
    helpers = root / ".aslm-update" / "helpers"
    if not helpers.is_dir() or any(helpers.iterdir()):
        raise RuntimeError("The patcher did not remove its profile-local shadow copy.")
    if manifest.read_bytes() != shared_version:
        raise RuntimeError("The prerelease unexpectedly modified the shared installation manifest.")
    print("ASLM_LINUX_DISTRIBUTION_OK", flush=True)


if __name__ == "__main__":
    main()
