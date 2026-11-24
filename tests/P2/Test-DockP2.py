"""Validate Desktop Dock P2 in an isolated Windows MCP session."""

import argparse
import base64
import copy
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import uuid


ROOT = Path(__file__).resolve().parents[2]
TOOLS = {
    "dock_state", "dock_settings", "dock_capture", "dock_p2_config",
    "dock_p2_monitors", "dock_p2_step", "dock_p2_event", "dock_p2_sampling",
    "dock_p2_hotkey_probe",
}
OUTSIDE = (20_000, 20_000)


class DockClient:
    def __init__(self, executable, profile):
        self.process = subprocess.Popen(
            [str(executable), "--mcp-stdio", "--config-dir", str(profile)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            encoding="utf-8", errors="replace",
        )
        self.lines = queue.Queue()
        self.diagnostics = []
        self.sequence = 0
        threading.Thread(target=self._read_lines, daemon=True).start()
        self.stderr_reader = threading.Thread(target=self._read_errors, daemon=True)
        self.stderr_reader.start()
        try:
            initialized = self.rpc("initialize", {
                "protocolVersion": "2025-11-25", "capabilities": {},
                "clientInfo": {"name": "Desktop Dock P2 validation", "version": "1"},
            })
            if initialized["protocolVersion"] != "2025-11-25":
                raise RuntimeError("Unexpected MCP protocol negotiation.")
            self._send({"jsonrpc": "2.0", "method": "notifications/initialized"})
            tools = {tool["name"] for tool in self.rpc("tools/list")["tools"]}
            if missing := TOOLS - tools:
                raise RuntimeError(f"Missing MCP tools: {sorted(missing)}")
        except BaseException:
            self.close()
            raise

    def _read_lines(self):
        for line in self.process.stdout:
            self.lines.put(line)
        self.lines.put(None)

    def _read_errors(self):
        self.diagnostics.append(self.process.stderr.read().strip())

    def _send(self, packet):
        self.process.stdin.write(json.dumps(packet, ensure_ascii=False) + "\n")
        self.process.stdin.flush()

    def rpc(self, method, params=None):
        self.sequence += 1
        self._send({"jsonrpc": "2.0", "id": self.sequence, "method": method, "params": params or {}})
        try:
            line = self.lines.get(timeout=15)
        except queue.Empty as error:
            raise RuntimeError(f"MCP request timed out: {method} {params or {}}") from error
        if line is None:
            raise RuntimeError(f"MCP process ended before {method}: {' '.join(self.diagnostics)}")
        response = json.loads(line)
        if response.get("id") != self.sequence or response.get("jsonrpc") != "2.0":
            raise RuntimeError(f"Mismatched MCP response to {method}.")
        if "error" in response:
            raise RuntimeError(f"MCP {method}: {response['error']}")
        return response["result"]

    def call(self, name, arguments=None, expect_error=False):
        result = self.rpc("tools/call", {"name": name, "arguments": arguments or {}})
        if bool(result.get("isError")) != expect_error:
            detail = " ".join(entry["text"] for entry in result.get("content", []) if entry["type"] == "text")
            raise RuntimeError(f"MCP {name} unexpected outcome: {detail[:500]}")
        return result

    def data(self, name, arguments=None):
        return self.call(name, arguments)["structuredContent"]

    def capture(self, window, output):
        images = [entry for entry in self.call("dock_capture", {"window": window})["content"]
                  if entry["type"] == "image" and entry["mimeType"] == "image/png"]
        if len(images) != 1:
            raise RuntimeError("The WPF capture did not return one PNG.")
        data = base64.b64decode(images[0]["data"], validate=True)
        if not data.startswith(b"\x89PNG\r\n\x1a\n"):
            raise RuntimeError("Invalid MCP PNG response.")
        output.write_bytes(data)
        return len(data)

    def close(self):
        self.process.stdin.close()
        try:
            code = self.process.wait(timeout=12)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=5)
            raise RuntimeError("Dock did not exit after MCP stdin EOF.")
        self.stderr_reader.join(timeout=2)
        if code != 0:
            raise RuntimeError(f"Dock process exit={code}: {' '.join(self.diagnostics)}")
        return code

    def __enter__(self):
        return self

    def __exit__(self, error_type, error, traceback):
        self.close()


class Checks:
    def __init__(self, path):
        self.path = path
        self.passed = 0

    def log(self, message):
        line = f"{datetime.now(timezone.utc).isoformat(timespec='milliseconds')} {message}"
        with self.path.open("a", encoding="utf-8", newline="\n") as stream:
            stream.write(line + "\n")
        print(line, flush=True)

    def check(self, condition, name, detail=""):
        outcome = f"{name} {detail}".strip()
        if not condition:
            raise AssertionError(outcome)
        self.passed += 1
        self.log(f"PASS {outcome}")


class Timeline:
    def __init__(self, client):
        self.client = client
        self.now = 0

    def step(self, point=OUTSIDE, delta=40, window_state="Ordinary", **extra):
        self.now += delta
        return self.client.data("dock_p2_step", {
            "now": self.now, "x": point[0], "y": point[1],
            "windowState": window_state, **extra,
        })

    def event(self, action, point=None):
        arguments = {"action": action}
        if point is not None:
            arguments.update(x=point[0], y=point[1])
        return self.client.data("dock_p2_event", arguments)


def state(data):
    return data["interaction"]["state"]


def center(bounds):
    return (int(bounds["x"] + bounds["width"] / 2), int(bounds["y"] + bounds["height"] / 2))


def monitor(monitor_id, left, top, right, bottom, scale, primary):
    return {
        "id": monitor_id, "name": f"P2 {monitor_id}", "left": left, "top": top,
        "right": right, "bottom": bottom, "workLeft": left, "workTop": top,
        "workRight": right, "workBottom": bottom - 40, "scale": scale, "primary": primary,
    }


def zone(edge, start, end, thickness, offset, reveal, hide):
    return {
        "id": str(uuid.uuid4()), "edge": edge, "startPercent": start, "endPercent": end,
        "thickness": thickness, "offset": offset, "revealDelayMs": reveal, "hideDelayMs": hide,
    }


def digest(blob):
    return hashlib.sha256(blob).hexdigest()


def native_process_sample(process_id):
    class FileTime(ctypes.Structure):
        _fields_ = [("low", wintypes.DWORD), ("high", wintypes.DWORD)]

        def value(self):
            return (self.high << 32) | self.low

    class MemoryCounters(ctypes.Structure):
        _fields_ = [
            ("size", wintypes.DWORD), ("page_faults", wintypes.DWORD),
            *( (name, ctypes.c_size_t) for name in (
                "peak_working_set", "working_set", "peak_paged_pool", "paged_pool",
                "peak_nonpaged_pool", "nonpaged_pool", "pagefile", "peak_pagefile", "private_bytes",
            )),
        ]

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(FileTime)] * 4
    kernel.K32GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(MemoryCounters), wintypes.DWORD]
    handle = kernel.OpenProcess(0x0410, False, process_id)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        creation, exit_time, cpu_kernel, cpu_user = FileTime(), FileTime(), FileTime(), FileTime()
        memory = MemoryCounters(size=ctypes.sizeof(MemoryCounters))
        if not kernel.GetProcessTimes(handle, creation, exit_time, cpu_kernel, cpu_user):
            raise ctypes.WinError(ctypes.get_last_error())
        if not kernel.K32GetProcessMemoryInfo(handle, memory, memory.size):
            raise ctypes.WinError(ctypes.get_last_error())
        return (cpu_kernel.value() + cpu_user.value()) / 10_000, memory.working_set, memory.private_bytes
    finally:
        kernel.CloseHandle(handle)


def exercise(executable, run_directory, checks, idle_seconds, expected_schema_version=2):
    profile = run_directory / "profile"
    profile.mkdir()
    config_file = profile / "dock.json"
    legacy_item = {"id": str(uuid.uuid4()), "name": "P1 retained reference",
                   "kind": "Url", "target": "https://example.test/", "customIconPath": None}
    legacy = {"schemaVersion": 1, "options": {"placement": "Bottom", "iconSize": 40}, "items": [legacy_item]}
    initial_bytes = json.dumps(legacy, ensure_ascii=False, indent=2).encode("utf-8")
    config_file.write_bytes(initial_bytes)

    left = monitor("sim-left", -1920, -200, 0, 880, 1.5, False)
    right = monitor("sim-right", 0, 0, 1600, 900, 1, True)
    top = zone("Top", 25, 75, 8, 2, 160, 320)
    overlap = zone("Top", 20, 80, 12, 2, 0, 80)
    bottom = zone("Bottom", 25, 75, 10, 4, 90, 150)
    west = zone("Left", 25, 75, 12, 2, 90, 150)
    east = zone("Right", 25, 75, 8, 3, 90, 150)
    profile_zones = [{"monitorId": left["id"], "zones": [top, overlap]},
                     {"monitorId": right["id"], "zones": [bottom, west, east]}]
    top_point = (-1000, -195)
    overlap_only = (-1500, -195)
    right_points = {"Bottom": (800, 890), "Left": (3, 400), "Right": (1594, 400)}

    with DockClient(executable, profile) as client:
        checks.log(f"MCP attached pid={client.process.pid} protocol=2025-11-25")
        physical = client.data("dock_p2_monitors")["monitors"]
        checks.log("Physical monitor enumeration: " + json.dumps(physical, ensure_ascii=False))
        checks.check(len(physical) > 0, "native monitor enumeration")
        current = client.data("dock_state")
        checks.check(state(current) == "Resident" and current["view"]["visible"] and current["items"] == [legacy_item]
                     and config_file.read_bytes() == initial_bytes, "P1 current profile startup and unchanged v1 bytes")
        behavior = copy.deepcopy(current["options"]["behavior"])
        defaults = copy.deepcopy(behavior["hotkeys"])
        client.data("dock_p2_monitors", {"monitors": [left, right]})
        behavior.update(mode="AutoHide", monitors=profile_zones)
        hidden = client.data("dock_p2_config", {"behavior": behavior})
        checks.check(state(hidden) == "Hidden" and not hidden["view"]["visible"] and not hidden["view"]["nativeVisible"],
                     "AutoHide hides both the WPF view and its HWND")
        backups = list(profile.glob("dock.v1-*.bak"))
        config = json.loads(config_file.read_text(encoding="utf-8"))
        checks.check(config["schemaVersion"] == expected_schema_version and len(backups) == 1 and backups[0].read_bytes() == initial_bytes,
                     f"atomic v1 to v{expected_schema_version} backup byte equality", f"sha256={digest(initial_bytes)}")

        resolved = client.data("dock_p2_monitors")["zones"]
        which_zone = {entry["zone"]["id"]: entry for entry in resolved}
        coords = lambda entry: tuple(entry[field] for field in ("left", "top", "right", "bottom"))
        checks.check(len(resolved) == 5 and coords(which_zone[top["id"]]) == (-1440, -197, -480, -185),
                     "negative origin and 150% DPI top zone extent")
        for label, item, expected in [
            ("wider overlapping zone", overlap, (-1536, -197, -384, -179)),
            ("bottom", bottom, (400, 886, 1200, 896)),
            ("left", west, (2, 225, 14, 675)),
            ("right", east, (1589, 225, 1597, 675)),
        ]:
            checks.check(coords(which_zone[item["id"]]) == expected, f"physical rect {label}", str(expected))

        timeline = Timeline(client)
        probes = [(top_point, "Maximized"), (top_point, "BorderlessFullScreen"),
                  (top_point, "ExclusiveFullScreen"), (top_point, "Unknown")]
        opens = hidden["interaction"]["automaticOpens"]
        for point, window_state in probes:
            timeline.step()
            timeline.step(point, window_state=window_state)
            blocked = timeline.step(point, delta=200, window_state=window_state)
            checks.check(state(blocked) == "Hidden" and blocked["interaction"]["automaticOpens"] == opens,
                         f"{window_state} blocks automatic reveal")

        timeline.step()
        holding = timeline.step(top_point)
        almost = timeline.step(top_point, delta=159)
        checks.check(state(almost) == "Hidden" and holding["interaction"]["pendingZoneId"] == top["id"],
                     "first configured overlap owns the full dwell")
        revealed = timeline.step(top_point, delta=1)
        checks.check(state(revealed) == "Revealed" and revealed["interaction"]["monitorId"] == left["id"]
                     and revealed["interaction"]["hideDelayMs"] == 320, "exact dwell reveals on the matching display")
        timeline.step(inside=False)
        inside_again = timeline.step(delta=250, inside=True)
        timeline.step(inside=False)
        early = timeline.step(delta=250, inside=False)
        late = timeline.step(delta=70, inside=False)
        checks.check(state(inside_again) == "Revealed" and state(early) == "Revealed" and state(late) == "Hidden",
                     "re-entry cancels prior delay, continuous leave closes at configured delay")

        held = timeline.step(overlap_only)
        checks.check(state(held) == "Revealed" and held["interaction"]["activeZoneId"] == overlap["id"],
                     "non-overlapping portion may reveal immediately")
        timeline.event("OutsideClick", OUTSIDE)
        checks.check(state(timeline.step(overlap_only, delta=100)) == "Hidden",
                     "explicit dismissal suppresses the held trigger")
        timeline.step()

        crossing = timeline.step(top_point)
        switched = timeline.step(right_points["Left"], delta=100)
        reached = timeline.step(right_points["Left"], delta=90)
        checks.check(crossing["interaction"]["pendingZoneId"] == top["id"] and state(switched) == "Hidden"
                     and state(reached) == "Revealed" and reached["interaction"]["monitorId"] == right["id"],
                     "cross-display change starts a fresh dwell with separate configuration")
        timeline.event("OutsideClick", OUTSIDE)
        for label in ("Bottom", "Right"):
            timeline.step()
            timeline.step(right_points[label])
            edge_hit = timeline.step(right_points[label], delta=90)
            checks.check(state(edge_hit) == "Revealed" and edge_hit["interaction"]["monitorId"] == right["id"],
                         f"{label} edge triggers on the second display")
            timeline.event("OutsideClick", OUTSIDE)
        corner = timeline.step((-1900, -199))
        checks.check(state(corner) == "Hidden" and corner["interaction"]["pendingZoneId"] is None,
                     "outside start/end and offset stays hidden")
        timeline.step(top_point)
        gap = timeline.step(top_point, delta=400)
        checks.check(state(gap) == "Hidden", "sampling gap clears previous dwell")
        timeline.step()

        allow_path = str(executable)
        block_path = str(executable.with_name("other-fixture.exe"))
        behavior["applicationRules"] = [
            {"executablePath": allow_path, "action": "AllowAutomatic"},
            {"executablePath": block_path, "action": "BlockAutomatic"},
        ]
        client.data("dock_p2_config", {"behavior": behavior})
        blocked = timeline.step(overlap_only, executablePath=block_path)
        checks.check(state(blocked) == "Hidden", "application block overrides ordinary window")
        absent = timeline.step(overlap_only, window_state="Maximized", executablePath=allow_path, shellState=1)
        checks.check(state(absent) == "Hidden", "locked Windows session blocks even an application allow rule")
        allowed = timeline.step(overlap_only, window_state="Maximized", executablePath=allow_path.swapcase())
        checks.check(state(allowed) == "Revealed", "case-insensitive EXE allow rule overrides maximized policy")
        timeline.event("OutsideClick", OUTSIDE)
        timeline.step()

        paused = timeline.event("Pause")
        held = timeline.step(overlap_only)
        checks.check(paused["interaction"]["paused"] and state(held) == "Hidden", "pause suppresses an instant zone")
        temporary = timeline.event("Peek")
        checks.check(state(temporary) == "Temporary" and temporary["view"]["visible"],
                     "temporary keyboard action bypasses paused zones")
        for _ in range(7):
            temporary = timeline.step(delta=250, inside=False)
        checks.check(state(temporary) == "Temporary", "manual reveal grants time to reach the Dock")
        for _ in range(2):
            temporary = timeline.step(delta=250, inside=False)
        checks.check(state(temporary) == "Hidden", "temporary view expires after grace and leave delay")
        locked = timeline.event("Lock")
        for _ in range(8):
            locked = timeline.step(delta=250, inside=False)
        checks.check(state(locked) == "Locked", "locked view ignores prolonged leave")
        unlocked = timeline.event("Lock")
        checks.check(state(unlocked) == "Revealed", "unlock returns to ordinary auto-hide behavior")
        timeline.event("OutsideClick", OUTSIDE)
        timeline.event("Toggle")
        closed_explicitly = timeline.event("Toggle")
        checks.check(state(closed_explicitly) == "Hidden", "manual show and hide toggle")

        visible = timeline.event("Peek")
        inside_point = center(visible["view"]["bounds"])
        checks.check(state(timeline.event("OutsideClick", inside_point)) == "Temporary",
                     "click within actual Dock bounds does not dismiss")
        opened_menu = timeline.event("OpenMenu")
        menu_bounds = opened_menu["view"]["interactionMenus"]
        checks.check(len(menu_bounds) == 1, "real WPF menu has an interaction rectangle", str(menu_bounds))
        timeline.step(inside_point)
        for _ in range(4):
            over_menu = timeline.step(center(menu_bounds[0]), delta=200)
        checks.check(state(over_menu) == "Temporary", "staying over the actual menu prevents hide after leaving the Dock")
        first_escape = timeline.event("Escape")
        checks.check(state(first_escape) == "Temporary" and not first_escape["view"]["interactionMenus"],
                     "first Escape closes the open WPF menu")
        second_escape = timeline.event("Escape")
        checks.check(state(second_escape) == "Hidden", "second Escape dismisses the Dock", str(second_escape["view"]["interactionMenus"]))
        timeline.event("Lock")
        checks.check(state(timeline.event("OutsideClick", OUTSIDE)) == "Hidden", "explicit outside click dismisses locked AutoHide")
        resumed = timeline.event("Pause")
        checks.check(not resumed["interaction"]["paused"], "resume re-enables hot zones")

        invalid = copy.deepcopy(behavior)
        invalid["hotkeys"]["peek"] = copy.deepcopy(invalid["hotkeys"]["toggle"])
        saved = config_file.read_bytes()
        duplicate = client.call("dock_p2_config", {"behavior": invalid}, expect_error=True)
        message = " ".join(entry["text"] for entry in duplicate["content"] if entry["type"] == "text")
        checks.check("快捷键" in message and config_file.read_bytes() == saved, "duplicate chord rejected without writing", message)
        new_chord = {"modifiers": 7, "virtualKey": 0x79}
        behavior["hotkeys"]["toggle"] = new_chord
        changed = client.data("dock_p2_config", {"behavior": behavior})
        checks.check(changed["options"]["behavior"]["hotkeys"]["toggle"] == new_chord, "custom shortcut values persist")
        behavior["hotkeys"] = defaults
        restored = client.data("dock_p2_config", {"behavior": behavior})
        checks.check(restored["options"]["behavior"]["hotkeys"] == defaults, "default chord values restore")
        probe = client.data("dock_p2_hotkey_probe", {"chord": new_chord})
        checks.check(probe["registered"] and not probe["secondRegistered"] and probe["conflictError"] == 1409
                     and probe["unregistered"] and probe["registeredAfterRelease"] and probe["releasedAgain"],
                     "native RegisterHotKey conflict and registration cleanup", json.dumps(probe, ensure_ascii=False))

        for page in ("hotZones", "shortcuts"):
            client.data("dock_settings", {"visible": True, "page": page})
            output = run_directory / f"p2-settings-{page}.png"
            size = client.capture("settings", output)
            checks.check(size > 1000, f"real settings rendering for {page}", f"path={output.name} bytes={size}")
        client.data("dock_settings", {"visible": False})

        persisted = copy.deepcopy(behavior)
        far = monitor("offscreen-perf-fixture", 100_000, 100_000, 101_920, 101_080, 1, True)
        idle_zone = zone("Top", 0, 0.05, 1, 0, 250, 400)
        behavior["monitors"] = [{"monitorId": far["id"], "zones": [idle_zone]}]
        client.data("dock_p2_config", {"behavior": behavior})
        client.data("dock_p2_monitors", {"monitors": [far]})
        baseline = client.data("dock_p2_sampling", {"enabled": True})["interaction"]
        begin = time.monotonic()
        cpu_start, _, _ = native_process_sample(client.process.pid)
        time.sleep(idle_seconds)
        cpu_end, working_set, private_bytes = native_process_sample(client.process.pid)
        elapsed = time.monotonic() - begin
        after = client.data("dock_p2_sampling", {"enabled": False})["interaction"]
        samples = after["pointerSamples"] - baseline["pointerSamples"]
        foreground_reads = after["foregroundSamples"] - baseline["foregroundSamples"]
        unintended = after["automaticOpens"] - baseline["automaticOpens"]
        cpu_pct = 100 * (cpu_end - cpu_start) / (elapsed * 1000 * (os.cpu_count() or 1))
        checks.log(f"PERF elapsed={elapsed:.2f}s pointerSamples={samples} foregroundReads={foreground_reads} "
                   f"unintendedOpens={unintended} normalizedCpu={cpu_pct:.4f}% "
                   f"workingSet={working_set / 2**20:.2f}MiB private={private_bytes / 2**20:.2f}MiB")
        checks.check(samples > 0 and foreground_reads == 0 and unintended == 0,
                     "real cursor polling idle outside a small offscreen synthetic zone")
        client.data("dock_p2_monitors", {"monitors": None})
        client.data("dock_p2_config", {"behavior": persisted})
        timeline.event("Pause")
        timeline.event("Lock")

    with DockClient(executable, profile) as restarted:
        after_restart = restarted.data("dock_state")
        checks.check(state(after_restart) == "Hidden" and not after_restart["view"]["visible"]
                     and not after_restart["interaction"]["paused"], "mode persists while temporary lock and pause clear after restart")
        checks.check(after_restart["options"]["behavior"] == persisted and after_restart["items"] == [legacy_item]
                     and len(list(profile.glob("dock.v1-*.bak"))) == 1, "multi-zone, application rules, chords and legacy item are durable")


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, default=ROOT / "artifacts/DesktopDock-P2-win-x64/DesktopDock.exe")
    parser.add_argument("--run-directory", type=Path, default=ROOT / "artifacts" / f"p2-mcp-{uuid.uuid4().hex}")
    parser.add_argument("--idle-seconds", type=float, default=22)
    parser.add_argument("--expected-schema-version", type=int, default=2)
    args = parser.parse_args()
    if not 20 <= args.idle_seconds <= 30:
        parser.error("--idle-seconds must be between 20 and 30 for the idle sample")
    executable = args.executable.resolve(strict=True)
    run_directory = args.run_directory.resolve()
    run_directory.mkdir()
    checks = Checks(run_directory / "mcp-results.log")
    checks.log(f"START exe={executable} directory={run_directory}")
    try:
        exercise(executable, run_directory, checks, args.idle_seconds, args.expected_schema_version)
    except (AssertionError, RuntimeError, OSError, ValueError, KeyError, TypeError) as error:
        checks.log(f"FAIL {error}")
        checks.log(f"END passed={checks.passed} failed=1")
        return 1
    checks.log(f"END passed={checks.passed} failed=0")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
