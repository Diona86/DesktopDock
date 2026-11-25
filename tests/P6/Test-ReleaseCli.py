"""Verify the published application's release commands with isolated data."""

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import uuid


ROOT = Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("dock_client", ROOT / "tests/P2/Test-DockP2.py")
dock = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dock)


def run(exe, *arguments, success=True):
    result = subprocess.run([str(exe), *(str(a) for a in arguments)], capture_output=True,
                            encoding="utf-8", timeout=20)
    if (result.returncode == 0) != success:
        raise AssertionError(f"{arguments[0]}: exit={result.returncode} stdout={result.stdout!r} stderr={result.stderr!r}")
    return (result.stdout if success else result.stderr).strip()


def verify(exe, directory, report):
    profile = directory / "profile"
    profile.mkdir()
    backups = directory / "backups"
    target = directory / "USER-PRIVATE-target.txt"
    target.write_text("USER-PRIVATE-content", encoding="utf-8")
    target_hash = hashlib.sha256(target.read_bytes()).hexdigest()
    config = profile / "dock.json"
    legacy = {"schemaVersion": 1, "options": {}, "items": [
        {"id": str(uuid.uuid4()), "name": "USER-PRIVATE-label", "kind": "File", "target": str(target)}
    ]}
    original = json.dumps(legacy, ensure_ascii=False).encode("utf-8")
    config.write_bytes(original)

    report.check(run(exe, "--version") == "1.0.0", "real release WinExe prints version 1.0.0 when redirected")
    run(exe, "--verify-profile", profile)
    report.check(config.read_bytes() == original, "verify-profile accepts old Dock schema with exact source bytes")
    snapshot = Path(run(exe, "--prepare-upgrade", profile, backups, "0.9.0", "1.0.0"))
    report.check(snapshot.is_dir() and config.read_bytes() == original,
                 "prepare-upgrade returns an existing isolated snapshot and leaves live bytes untouched")

    with dock.DockClient(exe, profile) as client:
        state = client.data("dock_state")
        report.check(state["releaseVersion"] == "1.0.0" and state["items"][0]["target"] == str(target),
                     "actual WPF startup opens the legacy profile and reports package version")
        contents = Path(client.data("dock_p6_diagnostics")["path"]).read_text(encoding="utf-8")
        exported = json.loads(contents)
        report.check(exported["version"] == "1.0.0" and exported["schemaVersion"] == 1 and "started" in contents
                     and "USER-PRIVATE" not in contents,
                     "real application diagnostics are written with startup event and omit private item details")
        run(exe, "--prepare-upgrade", profile, backups, "0.9.0", "1.0.0", success=False)
        report.check(config.read_bytes() == original, "exclusive running profile rejects upgrade without writing")
        client.data("dock_options", {"iconSize": 44})

    report.check(json.loads(config.read_bytes())["schemaVersion"] == 4
                 and any(path.read_bytes() == original for path in profile.glob("dock.v1-*.bak")),
                 "first WPF settings save migrates schema and retains original migration backup")
    checkpoint = Path(run(exe, "--restore-upgrade", profile, snapshot))
    report.check(checkpoint.is_dir() and config.read_bytes() == original, "explicit restore returns a safety checkpoint and original bytes")

    broken = profile / "themes.json"
    broken.write_bytes(b'{"schemaVersion":99,"state":{}}')
    before = broken.read_bytes()
    snapshots = set(backups.iterdir())
    run(exe, "--prepare-upgrade", profile, backups, "0.9.0", "1.0.0", success=False)
    report.check(broken.read_bytes() == before and config.read_bytes() == original and set(backups.iterdir()) == snapshots,
                 "incompatible theme stops upgrade without creating a usable snapshot or changing the profile")

    unrelated = profile / "keep.txt"
    unrelated.write_text("unrelated", encoding="utf-8")
    journal = profile / "desktop-icons.json"
    journal.write_text("invalid recovery data", encoding="utf-8")
    run(exe, "--remove-profile-data", profile, success=False)
    report.check(config.read_bytes() == original and journal.is_file(), "unsafe recovery journal prevents profile deletion")
    journal.unlink()
    broken.unlink()
    run(exe, "--remove-profile-data", profile)
    report.check(not config.exists() and unrelated.read_text(encoding="utf-8") == "unrelated"
                 and hashlib.sha256(target.read_bytes()).hexdigest() == target_hash,
                 "explicit cleanup removes owned config and preserves unrelated data and the original target")


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, default=ROOT / "artifacts/releases/1.0.0/DesktopDock-win-x64/DesktopDock.exe")
    parser.add_argument("--run-directory", type=Path, default=ROOT / "artifacts" / f"p6-cli-{uuid.uuid4().hex}")
    options = parser.parse_args()
    exe = options.executable.resolve(strict=True)
    run_directory = options.run_directory.resolve()
    run_directory.mkdir()
    report = dock.Checks(run_directory / "release-cli.log")
    try:
        verify(exe, run_directory, report)
    except (AssertionError, RuntimeError, ValueError, KeyError, OSError, subprocess.TimeoutExpired) as error:
        report.log(f"FAIL {type(error).__name__}: {error}")
        report.log(f"END passed={report.passed} failed=1")
        return 1
    report.log(f"END passed={report.passed} failed=0")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
