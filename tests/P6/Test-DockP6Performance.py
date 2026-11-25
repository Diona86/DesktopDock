"""Measure a Windows Desktop Dock build using isolated profiles and the existing MCP probes."""

import argparse
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import platform
import stat
import statistics
import struct
import subprocess
import sys
import time
import uuid
import zlib


ROOT = Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("dock_p2_probe", ROOT / "tests/P2/Test-DockP2.py")
p2 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p2)

STARTUP_RUNS = 5
REVEAL_RUNS = 8
THEME_RUNS = 16
ICONS = 12
IDLE_SECONDS = 15
MIB = 2**20


def elapsed_ms(start):
    return (time.perf_counter() - start) * 1000


def describe(samples):
    ordered = sorted(samples)
    return {"count": len(samples), "median_ms": round(statistics.median(samples), 3),
            "p95_ms": round(ordered[math.ceil(len(ordered) * .95) - 1], 3),
            "min_ms": round(ordered[0], 3), "max_ms": round(ordered[-1], 3),
            "samples_ms": [round(sample, 3) for sample in samples]}


def report(metrics, checks, name, value):
    metrics[name] = value
    checks.log(f"METRIC {name} {json.dumps(value, ensure_ascii=False, separators=(',', ':'))}")


def png_fixture(path, index):
    def chunk(kind, payload):
        return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload))

    rows = (b"\0" + bytes((19 + index, 80, 120)) * 96) * 96
    header = struct.pack(">IIBBBBB", 96, 96, 8, 2, 0, 0, 0)
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header)
                     + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))


def make_profile(directory):
    profile = directory / "startup-profile"
    profile.mkdir()
    source = directory / "fixtures"
    source.mkdir()
    items = []
    for index in range(ICONS):
        target = source / f"target-{index}.txt"
        target.write_text("Desktop Dock P6 isolated performance fixture.\n", encoding="utf-8")
        icon = source / f"icon-{index}.png"
        png_fixture(icon, index)
        items.append({"id": str(uuid.uuid4()), "name": target.stem, "kind": "File", "target": str(target),
                      "customIconPath": str(icon)})
    original = {"schemaVersion": 4, "options": {"showDockOnStartup": True, "iconSize": 40},
                "items": items, "groups": [], "entries": [{"id": item["id"], "parentId": None} for item in items]}
    path = profile / "dock.json"
    path.write_text(json.dumps(original, ensure_ascii=False), encoding="utf-8")
    return profile, path.read_bytes(), items


def wait_icons(client, started, count):
    first = None
    loaded = 0
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        images = client.data("dock_state")["view"]["items"]
        loaded = sum(item["imageLoaded"] and (item["imagePixelWidth"] or 0) >= 32 for item in images)
        if loaded and first is None:
            first = elapsed_ms(started)
        if loaded == count:
            return first, elapsed_ms(started)
        time.sleep(.01)
    raise AssertionError(f"Icon readiness timed out with {loaded}/{count} decoded images")


def startups(executable, profile, checks, metrics):
    initialized, ready, first, full, reveal_flags = [], [], [], [], []
    for _ in range(STARTUP_RUNS):
        started = time.perf_counter()
        with p2.DockClient(executable, profile) as client:
            initialized.append(elapsed_ms(started))
            state = client.data("dock_state")
            ready.append(elapsed_ms(started))
            reveal_flags.append(state["view"]["nativeVisible"])
            one, all_icons = wait_icons(client, started, ICONS)
            first.append(one)
            full.append(all_icons)
    checks.check(all(reveal_flags), "fresh process MCP readiness reaches a visible native Dock on every run")
    report(metrics, checks, "startup_mcp_ready", describe(initialized))
    report(metrics, checks, "startup_native_visible_observed", describe(ready))
    report(metrics, checks, "startup_first_icon_observed", describe(first))
    report(metrics, checks, "startup_all_icons_observed", describe(full))


def sample_process(client):
    sampled_at = time.monotonic()
    cpu, working, private = p2.native_process_sample(client.process.pid)
    return {"time": sampled_at, "cpu_ms": cpu, "working_set_mib": round(working / MIB, 3),
            "private_mib": round(private / MIB, 3)}


def summarize_process(start, end):
    elapsed = end["time"] - start["time"]
    percent = (end["cpu_ms"] - start["cpu_ms"]) / (elapsed * 10)
    return {"elapsed_s": round(elapsed, 3), "cpu_pct_one_core": round(percent, 5),
            "cpu_pct_machine": round(percent / (os.cpu_count() or 1), 5),
            "working_set_mib": end["working_set_mib"], "private_mib": end["private_mib"],
            "delta_working_set_mib": round(end["working_set_mib"] - start["working_set_mib"], 3),
            "delta_private_mib": round(end["private_mib"] - start["private_mib"], 3)}


def display_call(client, action, **fields):
    return client.data("dock_p5_displays", {"action": action, **fields})


def theme_call(client, action, **fields):
    return client.data("dock_p4_theme", {"action": action, **fields})


def auto_hide(client, monitor, dwell):
    zone = p2.zone("Top", 20, 40, 20, 0, dwell, 400)
    behavior = client.data("dock_state")["options"]["behavior"]
    behavior.update(mode="AutoHide", monitors=[{"monitorId": monitor["id"], "zones": [zone]}])
    client.data("dock_p2_config", {"behavior": behavior})


def idle(client, checks, metrics, name):
    time.sleep(4)
    start = sample_process(client)
    time.sleep(IDLE_SECONDS)
    report(metrics, checks, name, summarize_process(start, sample_process(client)))


def measure_hidden(client, checks, metrics, monitor):
    auto_hide(client, monitor, dwell=250)
    baseline = client.data("dock_p2_sampling", {"enabled": True})["interaction"]
    idle(client, checks, metrics, "single_auto_hide_idle")
    after = client.data("dock_p2_sampling", {"enabled": False})["interaction"]
    readings = {field: after[field] - baseline[field] for field in ("pointerSamples", "foregroundSamples", "automaticOpens")}
    report(metrics, checks, "single_auto_hide_poll_counts", readings)
    checks.check(readings["pointerSamples"] > 0 and readings["foregroundSamples"] == readings["automaticOpens"] == 0,
                 "native timer sampled while idle outside the synthetic offscreen zone")

    timeline = p2.Timeline(client)
    contact = (monitor["left"] + (monitor["right"] - monitor["left"]) // 3, monitor["top"] + 4)
    dwell_total, threshold_call = [], []
    for _ in range(REVEAL_RUNS):
        timeline.event("OutsideClick", p2.OUTSIDE)
        timeline.step(p2.OUTSIDE)
        entered = time.perf_counter()
        timeline.step(contact)
        before = timeline.step(contact, delta=249)
        time.sleep(max(0, .25 - (time.perf_counter() - entered)))
        threshold = time.perf_counter()
        after = timeline.step(contact, delta=1)
        if before["view"]["nativeVisible"] or not after["view"]["nativeVisible"]:
            raise AssertionError("The real WPF window did not transition at the configured 250 ms dwell threshold")
        dwell_total.append(elapsed_ms(entered))
        threshold_call.append(elapsed_ms(threshold))
    checks.check(len(dwell_total) == REVEAL_RUNS, "all controlled hot-zone trials remain hidden at 249 ms and reveal at 250 ms")
    report(metrics, checks, "hot_zone_250ms_input_to_visible_observed", describe(dwell_total))
    report(metrics, checks, "hot_zone_threshold_request_to_visible_observed", describe(threshold_call))
    config = client.data("dock_state")["options"]["behavior"]
    config.update(mode="Resident", monitors=[])
    client.data("dock_p2_config", {"behavior": config})


def measure_themes(client, checks, metrics):
    theme_call(client, "open")
    samples = []
    palette = ("tech", "glass", "minimal")
    expected = ("Technical", "Frosted", "Solid")
    for index in range(THEME_RUNS):
        trend = index % len(palette)
        start = time.perf_counter()
        reply = theme_call(client, "theme", id=palette[trend])
        samples.append(elapsed_ms(start))
        if reply["rendering"]["material"] != expected[trend]:
            raise AssertionError(f"Theme did not affect the actual view: {reply['rendering']}")
    theme_call(client, "cancel")
    checks.check(len(samples) == THEME_RUNS, "every measured theme switch updates the live WPF rendering")
    report(metrics, checks, "theme_switch_wpf_roundtrip", describe(samples))


def measure_live_icon(client, checks, metrics, item, directory):
    samples = []
    for index in range(6):
        icon = directory / f"replacement-{index}.png"
        png_fixture(icon, index)
        started = time.perf_counter()
        state = client.data("dock_edit", {"id": item["id"], "name": item["name"], "target": item["target"],
                                          "iconPath": str(icon)})
        while elapsed_ms(started) < 5000:
            view = next(entry for entry in state["view"]["items"] if entry["id"] == item["id"])
            if view["imageLoaded"] and (view["imagePixelWidth"] or 0) >= 32:
                samples.append(elapsed_ms(started))
                break
            time.sleep(.008)
            state = client.data("dock_state")
        else:
            raise AssertionError("The live replacement icon was not decoded in the expected interval")
    checks.check(len(samples) == 6, "six different local PNG replacements update the live icon")
    report(metrics, checks, "live_png_edit_to_bitmap_observed", describe(samples))


def measure_soak(client, checks, metrics, duration, monitor_ids):
    start = sample_process(client)
    samples = []
    deadline = start["time"] + duration
    while time.monotonic() < deadline:
        time.sleep(max(0, min(20, deadline - time.monotonic())))
        count = len(samples)
        pulse_started = time.perf_counter()
        state = display_call(client, "state")
        visible = {display["id"] for display in state["connected"] if display["view"]["nativeVisible"]}
        if visible != monitor_ids:
            raise AssertionError(f"Unexpected visible windows during continuous run: {visible}")
        end = sample_process(client)
        sample = {"uptime_s": round(end["time"] - start["time"], 3), "visible_docks": len(visible),
                  "state_roundtrip_ms": round(elapsed_ms(pulse_started), 3),
                  "working_set_mib": end["working_set_mib"], "private_mib": end["private_mib"]}
        samples.append(sample)
        checks.log(f"SOAK sample={count + 1} {json.dumps(sample, separators=(',', ':'))}")
    checks.check(bool(samples) and end["time"] >= deadline, "requested continuous interval completed and every live-state sample responded")
    report(metrics, checks, "dual_dock_continuous", {"process": summarize_process(start, end), "samples": samples})


def live_metrics(executable, profile, items, checks, metrics, duration):
    first = p2.monitor("p6-offscreen-a", 12000, 12000, 13920, 13080, 1, True)
    second = p2.monitor("p6-offscreen-b", 14000, 12000, 15920, 13080, 1.25, False)
    with p2.DockClient(executable, profile) as client:
        wait_icons(client, time.perf_counter(), ICONS)
        display_call(client, "topology", monitors=[first])
        first_state = display_call(client, "state")["connected"]
        checks.check(len(first_state) == 1 and first_state[0]["view"]["nativeVisible"],
                     "one visible WPF window before one-display resident measurement")
        idle(client, checks, metrics, "single_resident_idle")
        display_call(client, "topology", monitors=[first, second])
        display_call(client, "select", monitorId=second["id"])
        start = time.perf_counter()
        client.data("dock_add", {"targets": [item["target"] for item in items]})
        for item, fixture in zip(client.data("dock_state")["items"], items, strict=True):
            client.data("dock_edit", {"id": item["id"], "name": item["name"], "target": item["target"],
                                      "iconPath": fixture["customIconPath"]})
        first_new, all_new = wait_icons(client, start, ICONS)
        report(metrics, checks, "secondary_live_add_and_icon_decode_observed",
               {"first_icon_ms": round(first_new, 3), "all_icons_ms": round(all_new, 3), "includes_config_edits": ICONS})
        dual = display_call(client, "state")["connected"]
        checks.check(len(dual) == 2 and all(display["view"]["nativeVisible"]
                    and len(display["view"]["items"]) == ICONS for display in dual),
                     "two visible WPF windows with the same fixture count before two-display resident measurement")
        idle(client, checks, metrics, "dual_resident_idle")
        one, two = (metrics[key] for key in ("single_resident_idle", "dual_resident_idle"))
        report(metrics, checks, "dual_minus_single_resident_idle",
               {key: round(two[key] - one[key], 5) for key in ("cpu_pct_one_core", "working_set_mib", "private_mib")})
        display_call(client, "select", monitorId=first["id"])
        measure_live_icon(client, checks, metrics, items[0], profile.parent / "fixtures")
        measure_soak(client, checks, metrics, duration, {first["id"], second["id"]})
        display_call(client, "topology", monitors=[first])
        measure_hidden(client, checks, metrics, first)
        measure_themes(client, checks, metrics)


def error_text(result):
    return " ".join(content["text"] for content in result.get("content", []) if content["type"] == "text")


def abort_and_restart(executable, directory, checks):
    profile = directory / "abort-profile"
    target = directory / "abort-fixture.txt"
    target.write_text("test-only target", encoding="utf-8")
    client = p2.DockClient(executable, profile)
    path = profile / "dock.json"
    try:
        item = client.data("dock_add", {"targets": [str(target)]})["items"][0]
        client.data("dock_options", {"iconSize": 48, "dockThickness": 60})
        persisted = path.read_bytes()
        client.process.kill()
        code = client.process.wait(timeout=10)
        checks.check(code != 0 and path.read_bytes() == persisted, "forced termination leaves the committed profile bytes intact",
                     f"exit={code} sha256={hashlib.sha256(persisted).hexdigest().upper()}")
    finally:
        if client.process.poll() is None:
            client.close()
        else:
            client.process.stdin.close()
            client.stderr_reader.join(timeout=2)
    with p2.DockClient(executable, profile) as reopened:
        state = reopened.data("dock_state")
        checks.check(state["view"]["nativeVisible"] and state["items"][0]["id"] == item["id"]
                     and state["options"]["iconSize"] == 48 and path.read_bytes() == persisted,
                     "the killed process releases the profile lease and a fresh Dock recovers committed state")


def moved_and_protected(executable, directory, checks):
    profile = directory / "target-profile"
    path = profile / "dock.json"
    target = directory / "move-original.txt"
    moved = directory / "moved-fixture.txt"
    body = b"isolated fixture content\n"
    target.write_bytes(body)
    with p2.DockClient(executable, profile) as client:
        item = client.data("dock_add", {"targets": [str(target)]})["items"][0]
        original = path.read_bytes()
        target.rename(moved)
        response = client.call("dock_open", {"id": item["id"]}, expect_error=True)
        checks.check("已失效" in error_text(response) and moved.read_bytes() == body and path.read_bytes() == original
                     and client.data("dock_state")["items"][0]["target"] == str(target),
                     "moved target yields a useful error, retains the historical reference and preserves the file")
        path.chmod(stat.S_IREAD)
        try:
            failure = client.call("dock_options", {"iconSize": 50, "dockThickness": 62}, expect_error=True)
            checks.check(bool(error_text(failure)) and path.read_bytes() == original
                         and client.data("dock_state")["options"]["iconSize"] == 40,
                         "Windows read-only profile rejects a write without replacing the file or in-process state",
                         error_text(failure))
        finally:
            path.chmod(stat.S_IREAD | stat.S_IWRITE)
        fixed = client.data("dock_edit", {"id": item["id"], "name": item["name"], "target": str(moved)})
        checks.check(fixed["items"][0]["id"] == item["id"] and fixed["items"][0]["target"] == str(moved)
                     and moved.read_bytes() == body, "editing rebinds the moved target without changing the fixture")


def corrupt_inputs(executable, directory, checks):
    profile = directory / "corrupt-config-profile"
    profile.mkdir()
    dock_config = profile / "dock.json"
    broken = b'{"schemaVersion":4,"options":'
    dock_config.write_bytes(broken)
    process = subprocess.run([str(executable), "--mcp-stdio", "--config-dir", str(profile)],
                             capture_output=True, input=b"", timeout=12)
    message = process.stderr.decode("utf-8", errors="replace").strip()
    checks.check(process.returncode != 0 and bool(message) and dock_config.read_bytes() == broken,
                 "corrupt Dock configuration reports failure and preserves exact bytes",
                 f"exit={process.returncode} error={message}")

    theme_profile = directory / "corrupt-theme-profile"
    theme_profile.mkdir()
    path = theme_profile / "themes.json"
    broken = b'{"schemaVersion":1,"state":'
    path.write_bytes(broken)
    with p2.DockClient(executable, theme_profile) as client:
        state = theme_call(client, "open")
        theme_call(client, "theme", id="tech")
        client.call("dock_p4_theme", {"action": "save"}, expect_error=True)
        checks.check(state["rendering"]["nativeVisible"] and bool(state["saved"]["loadError"]) and path.read_bytes() == broken,
                     "corrupt theme storage reports built-in fallback and preserves exact bytes")


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, default=ROOT / "artifacts/releases/1.0.0/DesktopDock-win-x64/DesktopDock.exe")
    parser.add_argument("--run-directory", type=Path, default=ROOT / "artifacts" / f"p6-perf-{uuid.uuid4().hex}")
    parser.add_argument("--soak-seconds", type=int, default=180)
    args = parser.parse_args()
    if args.soak_seconds <= 0:
        parser.error("--soak-seconds must be positive")
    executable = args.executable.resolve(strict=True)
    directory = args.run_directory.resolve()
    directory.mkdir()
    checks = p2.Checks(directory / "performance.log")
    dll = executable.with_suffix(".dll")
    info = {"executable": str(executable), "app_sha256": hashlib.sha256(executable.read_bytes()).hexdigest().upper(),
            "assembly_sha256": hashlib.sha256(dll.read_bytes()).hexdigest().upper() if dll.exists() else None,
            "platform": platform.platform(), "logical_processors": os.cpu_count(), "python": platform.python_version(),
            "soak_seconds_requested": args.soak_seconds, "fresh_start_runs": STARTUP_RUNS,
            "resident_idle_seconds": IDLE_SECONDS, "icon_items_per_dock": ICONS}
    metrics = {"environment": info}
    checks.log(f"START {json.dumps(info, ensure_ascii=False)} output={directory}")
    try:
        profile, clean, items = make_profile(directory)
        startups(executable, profile, checks, metrics)
        checks.check((profile / "dock.json").read_bytes() == clean, "reading startup fixture leaves the profile bytes unchanged")
        abort_and_restart(executable, directory, checks)
        moved_and_protected(executable, directory, checks)
        corrupt_inputs(executable, directory, checks)
        live_metrics(executable, profile, items, checks, metrics, args.soak_seconds)
    except (AssertionError, KeyError, OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
        metrics["failure"] = f"{type(error).__name__}: {error}"
        checks.log(f"FAIL {metrics['failure']}")
    finally:
        metrics["passed"] = checks.passed
        metrics["failed"] = int("failure" in metrics)
        (directory / "performance.json").write_text(json.dumps(metrics, ensure_ascii=False, indent=2), encoding="utf-8")
        checks.log(f"END passed={metrics['passed']} failed={metrics['failed']}")
    return metrics["failed"]


if __name__ == "__main__":
    raise SystemExit(main())
