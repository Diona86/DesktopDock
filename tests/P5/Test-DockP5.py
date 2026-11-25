"""Validate the real P5 Dock windows, topology and map with an isolated test profile."""

import argparse
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import time
import uuid


ROOT = Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("dock_p2_probe", ROOT / "tests/P2/Test-DockP2.py")
p2 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p2)


def monitor(key, name, left, top, right, bottom, dpi, primary):
    return {"id": key, "name": name, "left": left, "top": top, "right": right, "bottom": bottom, "scale": dpi,
            "workLeft": left, "workTop": top, "workRight": right, "workBottom": bottom - 40, "primary": primary}


LEFT = monitor("p5-left", "左屏", -1600, -150, 0, 850, 1.5, True)
RIGHT = monitor("p5-right", "右屏", 0, 0, 1600, 1000, 1.0, False)


def call(client, channel, action="state", **args):
    return client.data(f"dock_p5_{channel}", {"action": action, **args})


def select(client, key):
    return call(client, "displays", "select", monitorId=key)


def docks(state):
    return {entry["id"]: entry for entry in state["connected"]}


def screen(state, key):
    return next(entry for entry in state["screens"] if entry["monitor"]["id"] == key)


def within(entry):
    actual = entry["view"]["bounds"]
    monitor = entry["monitor"]
    return (actual and monitor["workLeft"] - 1 <= actual["x"] < actual["x"] + actual["width"] <= monitor["workRight"] + 1
            and monitor["workTop"] - 1 <= actual["y"] < actual["y"] + actual["height"] <= monitor["workBottom"] + 1)


def after_layout(client, condition):
    deadline = time.monotonic() + 3
    while time.monotonic() < deadline:
        state = docks(call(client, "displays"))
        if condition(state):
            return state
        time.sleep(0.035)
    raise AssertionError(f"Dock rectangles did not settle: {[(key, entry['view']['bounds']) for key, entry in state.items()]}")


def zone(first, last):
    return {"id": str(uuid.uuid4()), "edge": "Top", "startPercent": first, "endPercent": last, "thickness": 12,
            "offset": 3, "revealDelayMs": 0, "hideDelayMs": 320}


def test_map(client, checks, directory):
    state = call(client, "map", "open")
    checks.check(state["visible"] and len(state["screens"]) == 2
                 and screen(state, LEFT["id"])["map"]["x"] < screen(state, RIGHT["id"])["map"]["x"]
                 and all(item["dock"] and item["zones"] for item in state["screens"]),
                 "actual map shows both monitors, Dock placements and screen-local zones")
    test = zone(14, 56)
    call(client, "map", "select", monitorId=LEFT["id"], index=0)
    call(client, "map", "edit", zone=test)
    expected = test.copy()
    for handle, dx, dy, key, value in (
        ("Start", 160, 0, "startPercent", 24),
        ("End", 160, 0, "endPercent", 66),
        ("Offset", 0, 15, "offset", 13),
        ("Thickness", 0, 15, "thickness", 22),
    ):
        changed = call(client, "map", "drag", handle=handle, deltaX=dx, deltaY=dy)
        expected[key] = value
        output = screen(changed, LEFT["id"])["zones"][0]
        checks.check(all(abs(output[field] - expected[field]) < .05 for field in ("startPercent", "endPercent", "offset", "thickness")),
                     f"WPF map drag edits {key} in mixed-DPI geometry")
    conflict = call(client, "map", "add", zone=zone(32, 46))
    checks.check(screen(conflict, LEFT["id"])["conflicts"] == 2 and conflict["conflictStatus"],
                 "overlap highlights both conflicting zones")
    checks.check(client.capture("hotZoneMap", directory / "hot-zone-map.png") > 256, "WPF map screenshot with real conflict styling")
    call(client, "map", "select", monitorId=LEFT["id"], index=1)
    removed = call(client, "map", "remove")
    checks.check(len(screen(removed, LEFT["id"])["zones"]) == 1 and screen(removed, LEFT["id"])["conflicts"] == 0,
                 "removing one zone clears the overlap warning")
    saved = call(client, "map", "save")
    left = docks(call(client, "displays"))[LEFT["id"]]["options"]["behavior"]
    right = docks(call(client, "displays"))[RIGHT["id"]]["options"]["behavior"]
    checks.check(not saved["dirtyDisplayIds"] and left["monitors"][0]["zones"][0]["offset"] == 13 and not right["monitors"],
                 "map saves only the selected screen's zones")
    call(client, "map", "select", monitorId=RIGHT["id"])
    call(client, "map", "remove")
    call(client, "map", "save")
    checks.check(docks(call(client, "displays"))[RIGHT["id"]]["options"]["behavior"]["monitors"][0]["zones"] == [],
                 "deleting the last zone explicitly disables all zones on that screen")
    call(client, "map", "reset")
    call(client, "map", "save")
    checks.check(not docks(call(client, "displays"))[RIGHT["id"]]["options"]["behavior"]["monitors"],
                 "reset removes the explicit list and restores the screen default")
    call(client, "map", "cancel")
    return left


def test_topology(client, checks, directory):
    folder = directory / "Pinned"
    folder.mkdir()
    client.data("dock_add", {"targets": [str(folder)]})
    initial = call(client, "displays", "topology", monitors=[LEFT, RIGHT])
    bound = after_layout(client, lambda values: len(values) == 2 and all(item["view"]["nativeVisible"] and within(item) for item in values.values()))
    checks.check(initial["legacyDisplayId"] == LEFT["id"] and bound[LEFT["id"]]["entries"] and not bound[RIGHT["id"]]["entries"]
                 and bound[LEFT["id"]]["themeId"] == "primary" and bound[RIGHT["id"]]["themeId"] != "primary",
                 "two native Dock HWNDs are assigned to separate screens and legacy items follow their original identity",
                 str({key: item["view"]["bounds"] for key, item in bound.items()}))
    select(client, LEFT["id"])
    client.data("dock_options", {"placement": "Top", "dockPositionLocked": True})
    select(client, RIGHT["id"])
    client.data("dock_options", {"placement": "Right", "dockPositionLocked": False, "iconSize": 36})
    layout = copy.deepcopy(client.data("dock_state")["options"]["layout"])
    layout["mode"] = "SingleColumn"
    client.data("dock_p3_layout", {"layout": layout})
    client.data("dock_p4_theme", {"action": "open"})
    client.data("dock_p4_theme", {"action": "scope", "scope": "Current"})
    client.data("dock_p4_theme", {"action": "theme", "id": "glass"})
    client.data("dock_p4_theme", {"action": "save"})
    client.data("dock_p4_theme", {"action": "cancel"})
    changed = after_layout(client, lambda values: all(within(item) for item in values.values()))
    checks.check(changed[LEFT["id"]]["options"]["placement"] == "Top" and changed[LEFT["id"]]["view"]["positionLocked"]
                 and changed[LEFT["id"]]["rendering"]["material"] == "Solid"
                 and changed[RIGHT["id"]]["options"]["placement"] == "Right" and not changed[RIGHT["id"]]["view"]["positionLocked"]
                 and changed[RIGHT["id"]]["view"]["vertical"] and changed[RIGHT["id"]]["rendering"]["material"] == "Frosted",
                 "position, layout, lock and theme are independently applied and rendered")
    behavior = test_map(client, checks, directory)

    select(client, LEFT["id"])
    behavior["mode"] = "AutoHide"
    client.data("dock_p2_config", {"behavior": behavior})
    checks.check(not docks(call(client, "displays"))[LEFT["id"]]["visible"]
                 and docks(call(client, "displays"))[RIGHT["id"]]["visible"], "automatic hiding applies independently")
    step = {"now": 100, "x": 650, "y": 0, "windowState": "Ordinary"}
    client.data("dock_p2_step", step)
    outside = docks(call(client, "displays"))
    step.update(now=230, x=-1040, y=-125)
    client.data("dock_p2_step", step)
    revealed = docks(call(client, "displays"))
    checks.check(not outside[LEFT["id"]]["visible"] and revealed[LEFT["id"]]["visible"] and revealed[RIGHT["id"]]["visible"],
                 "target Dock ignores the other monitor and responds to its own live controller")

    for command, field, flag in (("hide", "visible", False), ("show", "visible", True),
                                 ("pause", "paused", True), ("resume", "paused", False)):
        result = call(client, "displays", "tray", command=command)
        checks.check(all(item[field] is flag for item in result["connected"]), f"tray command {command} reaches both Docks")
    checks.check(call(client, "displays", "tray", command="settings")["settingsVisible"], "tray settings path opens the real window")
    checks.check(client.capture("settings", directory / "display-settings.png") > 256, "settings includes an actual display selector")
    client.data("dock_settings", {"visible": False})

    swapped_left = LEFT | {"primary": False}
    swapped_right = monitor(RIGHT["id"], "右屏", 0, -280, 1900, 900, 1.25, True)
    swapped = call(client, "displays", "topology", monitors=[swapped_right, swapped_left])
    final = after_layout(client, lambda values: all(within(item) for item in values.values()))
    checks.check(swapped["legacyDisplayId"] == LEFT["id"] and final[LEFT["id"]]["entries"]
                 and final[RIGHT["id"]]["options"]["placement"] == "Right" and final[RIGHT["id"]]["monitor"]["scale"] == 1.25,
                 "primary swap, new resolution, DPI and layout keep each Dock bound to its saved identity",
                 str({key: item["view"]["bounds"] for key, item in final.items()}))

    local_files = list((directory / "profile" / "display-docks").glob("*/dock.json"))
    checks.check(len(local_files) == 1, "secondary Dock has its own on-disk configuration")
    before_bytes = local_files[0].read_bytes()
    select(client, RIGHT["id"])
    unplugged = call(client, "displays", "topology", monitors=[LEFT])
    checks.check(list(docks(unplugged)) == [LEFT["id"]] and unplugged["selectedDisplayId"] == LEFT["id"]
                 and local_files[0].read_bytes() == before_bytes, "disconnect closes secondary Dock and keeps its exact profile bytes")
    attached = call(client, "displays", "topology", monitors=[swapped_right, swapped_left])
    checks.check(docks(attached)[RIGHT["id"]]["options"]["placement"] == "Right"
                 and docks(attached)[RIGHT["id"]]["rendering"]["material"] == "Frosted", "reconnect restores the same secondary configuration and theme")
    only_right = call(client, "displays", "topology", monitors=[swapped_right])
    checks.check(only_right["legacyDisplayId"] == LEFT["id"] and list(docks(only_right)) == [RIGHT["id"]]
                 and only_right["selectedDisplayId"] == RIGHT["id"], "legacy screen can disconnect without transferring its profile to the surviving display")
    return swapped_left, swapped_right


def test_system(client, checks, directory, native):
    state = call(client, "system", "desktop")
    checks.check(state["managerVisible"] and not state["desktop"]["canHide"] and not state["desktop"]["canRestore"],
                 "real desktop manager opens in read-only validation", str(state["desktop"]))
    checks.check(client.capture("desktopManager", directory / "desktop-management.png") > 256, "WPF desktop manager and manual recovery instructions")
    before = state["desktop"]
    call(client, "displays", "tray", command="restore")
    checked = call(client, "system")["desktop"]
    checks.check(checked["visible"] == before["visible"] and checked["windowsPreferenceHidden"] == before["windowsPreferenceHidden"],
                 "read-only tray restore path leaves the actual desktop unchanged")
    if native:
        registration = call(client, "system", "startupProbe")
        checks.check(registration["initial"] == "Disabled" and registration["enabled"] == "Enabled"
                     and registration["repeated"] == "Enabled" and registration["ownedValues"] == 1
                     and registration["other"] == "OtherInstallation" and registration["otherPreserved"]
                     and registration["disabled"] == "Disabled" and registration["removed"] == "Disabled"
                     and registration["conflict"] == "Conflict" and registration["conflictPreserved"]
                     and registration["unrelatedPreserved"], "native Registry enable, detect, repeat, disable and uninstall ownership", str(registration))
        probe = call(client, "system", "restoreHotkeyProbe")
        checks.check(probe["registered"] and probe["conflictError"] == 1409 and probe["registeredAfterRelease"]
                     and probe["releasedAgain"], "actual Windows restore hotkey registration, conflict and release", str(probe))


def exercise(executable, directory, checks, native):
    profile = directory / "profile"
    with p2.DockClient(executable, profile) as client:
        left, right = test_topology(client, checks, directory)
        test_system(client, checks, directory, native)
    with p2.DockClient(executable, profile) as client:
        restored = call(client, "displays", "topology", monitors=[right, left])
        values = docks(restored)
        index = json.loads((profile / "display-profiles.json").read_text(encoding="utf-8"))
        checks.check(restored["legacyDisplayId"] == index["legacyMonitorId"] == LEFT["id"]
                     and values[LEFT["id"]]["options"]["dockPositionLocked"] and values[LEFT["id"]]["entries"]
                     and values[RIGHT["id"]]["rendering"]["material"] == "Frosted"
                     and values[LEFT["id"]]["options"]["behavior"]["monitors"][0]["zones"][0]["offset"] == 13,
                     "restart preserves identity, independent settings, items, zones and theme")
        try:
            call(client, "displays", "tray", command="exit")
        except RuntimeError as error:
            if not str(error).startswith("MCP process ended before "):
                raise
        checks.check(client.process.wait(timeout=5) == 0, "tray exit shuts down the running Dock process cleanly")


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, default=ROOT / "artifacts/DesktopDock-P5-win-x64/DesktopDock.exe")
    parser.add_argument("--run-directory", type=Path, default=ROOT / "artifacts" / f"p5-run-{uuid.uuid4().hex}")
    parser.add_argument("--native-system", action="store_true", help="Also run native shortcut and isolated registry probes.")
    args = parser.parse_args()
    executable = args.executable.resolve(strict=True)
    directory = args.run_directory.resolve()
    directory.mkdir()
    checks = p2.Checks(directory / "mcp-results.log")
    checks.log(f"START exe={executable} directory={directory} native-system={args.native_system}")
    try:
        exercise(executable, directory, checks, args.native_system)
    except (AssertionError, KeyError, RuntimeError, OSError, ValueError, StopIteration, subprocess.TimeoutExpired) as error:
        checks.log(f"FAIL {type(error).__name__}: {error}")
        checks.log(f"END passed={checks.passed} failed=1")
        return 1
    checks.log(f"END passed={checks.passed} failed=0")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
