"""Validate the packaged P3 Dock through MCP without injecting desktop input."""

import argparse
import copy
import importlib.util
import json
from pathlib import Path
import struct
import sys
import time
import uuid
import zlib


ROOT = Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("dock_p2_probe", ROOT / "tests/P2/Test-DockP2.py")
p2 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p2)

TOOLS = {f"dock_p3_{action}" for action in (
    "create", "edit", "delete", "move", "drop", "layout", "open", "back", "blank",
)}


def png(path):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    pixels = (b"\x00" + b"\x22\x9a\x79\xff" * 32) * 32
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 32, 32, 8, 6, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(pixels)) + chunk(b"IEND", b""))


def make_profile(run_directory):
    profile = run_directory / "profile"
    fixtures = run_directory / "fixtures"
    profile.mkdir()
    fixtures.mkdir()
    names = ("甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸", "子", "丑", "寅", "卯")
    items = []
    for name in names:
        target = fixtures / f"P3 测试项目 {name}.txt"
        target.write_text(f"Desktop Dock test reference {name}\n", encoding="utf-8")
        items.append({"id": str(uuid.uuid4()), "name": name, "kind": "File", "target": str(target), "customIconPath": None})
    icon = fixtures / "自定义图标.png"
    png(icon)
    legacy = {"schemaVersion": 2, "options": {"placement": "Bottom", "iconSize": 38,
              "behavior": {"mode": "Resident"}}, "items": items}
    config = profile / "dock.json"
    config.write_text(json.dumps(legacy, ensure_ascii=False, indent=2), encoding="utf-8")
    return profile, fixtures, icon, items, config, config.read_bytes()


def children(state, parent=None):
    return [entry["id"] for entry in state["entries"] if entry["parentId"] == parent]


def panels(state):
    return state["groupPanels"]["panels"]


def current(client):
    return client.data("dock_state")


def wait_for(client, predicate, reason, timeout=3):
    deadline = time.monotonic() + timeout
    while True:
        latest = current(client)
        if predicate(latest):
            return latest
        if time.monotonic() >= deadline:
            raise AssertionError(reason)
        time.sleep(0.035)


def capture(client, checks, target, run_directory, filename):
    size = client.capture(target, run_directory / filename)
    checks.check(size > 256, f"real WPF PNG {filename}", f"bytes={size}")


def placed(client, state, **changes):
    return client.data("dock_p3_layout", {"layout": {**state["options"]["layout"], **changes}})


def between(first, second):
    ax, ay = first["x"], first["y"]
    bx, by = second["x"], second["y"]
    ar, ab = ax + first["width"], ay + first["height"]
    br, bb = bx + second["width"], by + second["height"]
    overlap_x = (max(ax, bx), min(ar, br))
    overlap_y = (max(ay, by), min(ab, bb))
    if overlap_x[0] < overlap_x[1] and (ab < by or bb < ay):
        return int(sum(overlap_x) / 2), int((min(ab, bb) + max(ay, by)) / 2)
    if overlap_y[0] < overlap_y[1] and (ar < bx or br < ax):
        return int((min(ar, br) + max(ax, bx)) / 2), int(sum(overlap_y) / 2)
    raise AssertionError(f"No measurable gap between owned windows: {first!r}, {second!r}")


def hierarchy(client, checks, items, config, legacy_bytes, profile, expected_schema_version):
    a, b, c, d, e, f, g, h, i, j, k, l, m, n = [item["id"] for item in items]
    first = current(client)
    checks.check(first["items"] == items and first["groups"] == [] and children(first) == [item["id"] for item in items]
                 and config.read_bytes() == legacy_bytes and first["view"]["nativeVisible"],
                 "P2 profile preserves IDs, root order and bytes before migration")
    direct = client.data("dock_p3_create", {"name": "工作台"})
    parent = direct["id"]
    backups = list(profile.glob("dock.v2-*.bak"))
    checks.check(len(backups) == 1 and backups[0].read_bytes() == legacy_bytes
                 and json.loads(config.read_text(encoding="utf-8"))["schemaVersion"] == expected_schema_version,
                 f"schema 2 to {expected_schema_version} write preserves byte-exact backup")
    child = client.data("dock_p3_create", {"name": "二级工具", "parentId": parent})["id"]
    before = config.read_bytes()
    for invalid in ({"name": "三级", "parentId": child}, {"name": "不合法", "parentId": a}):
        client.call("dock_p3_create", invalid, expect_error=True)
    client.call("dock_p3_move", {"id": parent, "parentId": child}, expect_error=True)
    checks.check(config.read_bytes() == before and children(current(client), parent) == [child],
                 "two-level limit, non-group parent and cycles rejected without writes")

    drop = client.data("dock_p3_drop", {"id": a, "targetId": b, "center": True})
    state = current(client)
    created = next(group for group in state["groups"] if group["id"] not in (parent, child))
    joined = created["id"]
    checks.check(drop["feedback"] and drop["committed"] and children(state)[0] == joined
                 and children(state, joined) == [b, a] and created["expandMode"] == "Grid",
                 "root item center drop creates a grid group at the target position")
    drop = client.data("dock_p3_drop", {"id": a, "targetId": parent, "center": True})
    checks.check(drop["feedback"] and drop["committed"] and children(current(client), parent) == [child, a]
                 and children(current(client), joined) == [b], "root group center drop moves across groups")
    client.data("dock_p3_open", {"id": child})
    checks.check([panel["groupId"] for panel in panels(current(client))] == [parent, child],
                 "second-level open creates both native panels")
    drop = client.data("dock_p3_drop", {"id": c, "parentId": child, "center": False})
    checks.check(drop["feedback"] and drop["committed"] and children(current(client), child) == [c],
                 "drop into an empty visible subgroup")
    drop = client.data("dock_p3_drop", {"id": d, "parentId": parent, "targetId": a, "after": False})
    checks.check(drop["feedback"] and drop["committed"] and children(current(client), parent) == [child, d, a],
                 "edge drop into an existing group lands before the target")
    drop = client.data("dock_p3_drop", {"id": d, "parentId": parent, "targetId": a, "after": True})
    checks.check(drop["feedback"] and drop["committed"] and children(current(client), parent) == [child, a, d],
                 "within-group reorder persists the new siblings")
    drop = client.data("dock_p3_drop", {"id": c, "targetId": e, "after": True})
    roots = children(current(client))
    checks.check(drop["feedback"] and drop["committed"] and roots[roots.index(e) + 1] == c and not children(current(client), child),
                 "dragging a subgroup item out uses the Dock's anchored edge drop")

    for entry, destination in ((c, child), (e, child), (f, child), (g, parent), (h, parent), (i, parent)):
        client.data("dock_p3_move", {"id": entry, "parentId": destination})
    before = config.read_bytes()
    result = client.data("dock_p3_drop", {"id": f, "parentId": child, "targetId": c, "center": True})
    checks.check(not result["accepted"] and not result["committed"] and config.read_bytes() == before,
                 "subgroup center drop disallows creating a third level")
    client.data("dock_p3_move", {"id": joined, "anchorId": parent, "after": True})
    roots = children(current(client))
    checks.check(roots[roots.index(parent) + 1] == joined, "group reorders among root siblings")
    return parent, child, joined, (j, k, l, m, n)


def layouts(client, checks, run_directory, parent, icon):
    client.data("dock_p3_blank")
    client.data("dock_p3_edit", {"id": parent, "name": "工作台 · 自定义图标", "iconPath": str(icon), "direction": "Up"})
    updated = wait_for(client, lambda s: any(entry["id"] == parent and entry["imageLoaded"] and entry["imagePixelWidth"] == 32
                                             for entry in s["view"]["items"]), "custom group PNG was not rendered")
    checks.check(any(group["id"] == parent and group["name"] == "工作台 · 自定义图标" and group["customIconPath"] == str(icon)
                     for group in updated["groups"]), "renamed group loads the 32px custom PNG in its real Dock button")

    state = placed(client, current(client), mode="SingleRow", gridTracks=3, iconSpacing=4, panelSpacing=14)
    animations = {}
    for mode in ("Grid", "Fan", "Horizontal", "Vertical"):
        client.data("dock_p3_edit", {"id": parent, "name": "工作台 · 自定义图标", "expandMode": mode, "direction": "Up"})
        opened = client.data("dock_p3_open", {"id": parent})
        samples = animations[mode] = []
        deadline = time.monotonic() + 3
        snapshot = opened
        while panels(snapshot)[0]["animationActive"]:
            samples.append(round(panels(snapshot)[0]["opacity"], 3))
            if time.monotonic() >= deadline:
                raise AssertionError("group animation never settled")
            time.sleep(0.025)
            snapshot = current(client)
        panel = panels(snapshot)[0]
        boxes = [entry["bounds"] for entry in panel["entries"]]
        xs = {round(box["x"], 1) for box in boxes}
        ys = {round(box["y"], 1) for box in boxes}
        pattern = {"Grid": len(xs) == 3 and len(ys) >= 2, "Fan": len(xs) >= 3 and len(ys) >= 2,
                   "Horizontal": len(xs) == len(boxes) and len(ys) == 1,
                   "Vertical": len(xs) == 1 and len(ys) == len(boxes)}[mode]
        checks.check(panel["nativeVisible"] and not panel["activatesOnShow"] and panel["expandMode"] == mode and pattern
                     and panel["actualDirection"] == "Up" and panel["opacity"] >= 0.99 and panel["animationMilliseconds"] == 170,
                     f"{mode} panel has the expected live WPF geometry and direction",
                     f"entries={len(boxes)} x={sorted(xs)} y={sorted(ys)}")
        if mode == "Grid":
            dock = snapshot["view"]["bounds"]
            gap = dock["y"] - (panel["bounds"]["y"] + panel["bounds"]["height"])
            checks.check(gap > 0, "panel spacing leaves an actual gap between windows", f"gapPx={gap}")
        capture(client, checks, "group", run_directory, f"p3-group-{mode.lower()}.png")
        client.data("dock_p3_back")
    checks.check(any(0 < opacity < 0.99 for samples in animations.values() for opacity in samples),
                 "sampled intermediate opacity during the 170ms WPF opening animation", f"samples={animations}")

    for mode, rows, columns in (("SingleRow", 1, None), ("SingleColumn", None, 1),
                                ("MultiRowGrid", 2, None), ("MultiColumnGrid", None, 2)):
        state = placed(client, state, mode=mode, gridTracks=2, maxVisibleIcons=0)
        mixed = state["view"]["layout"]
        checks.check(mixed["mode"] == mode and (rows is None or mixed["rows"] == rows)
                     and (columns is None or mixed["columns"] == columns), f"Dock layout {mode} renders requested tracks", str(mixed))
        capture(client, checks, "dock", run_directory, f"p3-dock-{mode.lower()}.png")

    uncapped = placed(client, state, mode="SingleRow", maxVisibleIcons=0)["view"]["layout"]["visibleLength"]
    capped = placed(client, state, mode="SingleRow", maxVisibleIcons=2)["view"]["layout"]
    checks.check(capped["scrolling"] and capped["visibleLength"] < uncapped, "maximum icon count limits the viewport while retaining scrolling")
    state = placed(client, state, mode="Auto", maxVisibleIcons=0)
    state = client.data("dock_options", {"placement": "Left"})
    checks.check(state["view"]["vertical"] and state["view"]["layout"]["columns"] == 1, "automatic layout follows vertical Dock placement")
    state = client.data("dock_options", {"placement": "Bottom"})
    state = placed(client, state, mode="GroupExpand")
    state = wait_for(client, lambda s: len(panels(s)) == 1, "group-expansion layout did not open the first root group")
    first = next(entry for entry in children(state) if any(group["id"] == entry for group in state["groups"]))
    checks.check(panels(state)[0]["groupId"] == first, "group-expansion layout opens first root group")
    capture(client, checks, "dock", run_directory, "p3-dock-groupexpand.png")
    capture(client, checks, "group", run_directory, "p3-group-groupexpand.png")
    client.data("dock_p3_blank")
    placed(client, state, mode="SingleRow")
    client.data("dock_settings", {"visible": True, "page": "layout"})
    capture(client, checks, "settings", run_directory, "p3-settings-layout.png")
    client.data("dock_settings", {"visible": False})


def interaction(client, checks, parent, child):
    timeline = p2.Timeline(client)
    client.data("dock_p3_open", {"id": child})
    opened = timeline.event("OpenMenu")
    first = timeline.event("Escape")
    checks.check(opened["view"]["interactionMenus"] and len(panels(opened)) == 2 and not first["view"]["interactionMenus"]
                 and len(panels(first)) == 2, "Escape dismisses a real context menu before the open group panels")
    checks.check(len(panels(timeline.event("Escape"))) == 1 and not panels(timeline.event("Escape"))
                 and current(client)["view"]["nativeVisible"], "Escape closes subgroup then parent while Resident stays visible")
    client.data("dock_p3_open", {"id": child})
    back = client.data("dock_p3_back")
    checks.check([panel["groupId"] for panel in panels(back)] == [parent]
                 and not panels(client.data("dock_p3_blank")), "Back returns one group and Dock blank clears the rest")
    client.data("dock_p3_open", {"id": child})
    dismissed = timeline.event("OutsideClick", p2.OUTSIDE)
    checks.check(not panels(dismissed) and dismissed["view"]["nativeVisible"], "outside click clears both group panels in Resident mode")

    resident = copy.deepcopy(current(client)["options"]["behavior"])
    hidden = client.data("dock_p2_config", {"behavior": {**resident, "mode": "AutoHide"}})
    checks.check(not hidden["view"]["nativeVisible"] and not panels(hidden), "AutoHide starts fully hidden")
    timeline.step()
    timeline.event("Toggle")
    client.data("dock_p3_open", {"id": child})
    state = wait_for(client, lambda s: len(panels(s)) == 2 and all(panel["nativeVisible"] for panel in panels(s)), "panels failed to open")
    dock_point = p2.center(state["view"]["bounds"])
    owner, inner = [panel["bounds"] for panel in panels(state)]
    bridge, inner_bridge = between(state["view"]["bounds"], owner), between(owner, inner)
    owner_point, inner_point = p2.center(owner), p2.center(inner)
    for point in (dock_point, bridge, owner_point, inner_bridge, inner_point):
        for _ in range(7):
            held = timeline.step(point, delta=100)
        checks.check(held["view"]["nativeVisible"] and len(panels(held)) == 2,
                     "native Dock and both panels stay visible while sampling their union", f"point={point}")
    checks.check(len(panels(timeline.event("OutsideClick", inner_point))) == 2,
                 "a click inside the child panel belongs to the interaction area")
    timeline.step(p2.OUTSIDE, delta=100)
    for _ in range(7):
        out = timeline.step(p2.OUTSIDE, delta=100)
    checks.check(not out["view"]["nativeVisible"] and not panels(out), "continuous leave from the whole interaction area hides all three HWNDs")
    timeline.event("Toggle")
    client.data("dock_p3_open", {"id": child})
    out = timeline.event("OutsideClick", p2.OUTSIDE)
    checks.check(not out["view"]["nativeVisible"] and not panels(out), "outside click closes Dock and both panels immediately in AutoHide")
    client.data("dock_p2_config", {"behavior": resident})


def reference_safety(client, checks, fixtures, config, parent, child, joined, remaining):
    j, k, l, _, _ = remaining
    original_hashes = {path: p2.digest(path.read_bytes()) for path in fixtures.iterdir()}
    promotion = client.data("dock_p3_create", {"name": "将提升的分组", "parentId": joined})["id"]
    beta = children(current(client), joined)[0]
    client.data("dock_p3_move", {"id": beta, "parentId": promotion})
    roots = children(current(client))
    slot = roots.index(joined)
    promoted = client.data("dock_p3_delete", {"id": joined, "choice": "MoveContentsToParent"})
    checks.check(joined not in children(promoted) and children(promoted)[slot] == promotion
                 and children(promoted, promotion) == [beta], "explicit keep choice promotes subgroup intact in the same root slot")

    removed = client.data("dock_p3_create", {"name": "删除引用试验"})["id"]
    inner = client.data("dock_p3_create", {"name": "子组", "parentId": removed})["id"]
    for item, destination in ((j, removed), (k, inner), (l, inner)):
        client.data("dock_p3_move", {"id": item, "parentId": destination})
    before = config.read_bytes()
    client.call("dock_p3_delete", {"id": removed}, expect_error=True)
    checks.check(config.read_bytes() == before, "deletion without an explicit choice cannot mutate config")
    result = client.data("dock_p3_delete", {"id": removed, "choice": "RemoveReferences"})
    remaining_ids = {entry["id"] for entry in result["entries"]}
    checks.check(not remaining_ids.intersection((removed, inner, j, k, l)) and {parent, child, promotion, beta} <= remaining_ids,
                 "explicit remove choice removes only that subtree's references")
    checks.check(all(p2.digest(path.read_bytes()) == digest for path, digest in original_hashes.items()),
                 "removing group references leaves all original files and the icon SHA-256 unchanged")


def exercise(executable, run_directory, checks, expected_schema_version):
    profile, fixtures, icon, items, config, legacy = make_profile(run_directory)
    with p2.DockClient(executable, profile) as client:
        names = {tool["name"] for tool in client.rpc("tools/list")["tools"]}
        checks.check(TOOLS <= names, "P3 tools advertised on the existing stdio MCP interface", f"total={len(names)}")
        parent, child, joined, remaining = hierarchy(client, checks, items, config, legacy, profile, expected_schema_version)
        layouts(client, checks, run_directory, parent, icon)
        interaction(client, checks, parent, child)
        reference_safety(client, checks, fixtures, config, parent, child, joined, remaining)
        previous = placed(client, current(client), mode="MultiRowGrid", maxVisibleIcons=4, iconSpacing=7, panelSpacing=12, gridTracks=2)

    with p2.DockClient(executable, profile) as client:
        restarted = current(client)
        checks.check(all(restarted[key] == previous[key] for key in ("items", "groups", "entries", "options"))
                     and restarted["view"]["nativeVisible"], "normal restart restores ordered hierarchy, custom icon and layout settings")


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, default=ROOT / "artifacts/DesktopDock-P3-Grid-R2-win-x64/DesktopDock.exe")
    parser.add_argument("--run-directory", type=Path, default=ROOT / "artifacts" / f"p3-mcp-{uuid.uuid4().hex}")
    parser.add_argument("--expected-schema-version", type=int, default=4)
    args = parser.parse_args()
    executable = args.executable.resolve(strict=True)
    run_directory = args.run_directory.resolve()
    run_directory.mkdir()
    checks = p2.Checks(run_directory / "mcp-results.log")
    checks.log(f"START exe={executable} directory={run_directory}")
    try:
        exercise(executable, run_directory, checks, args.expected_schema_version)
    except (AssertionError, KeyError, RuntimeError, OSError, ValueError, TypeError, IndexError) as error:
        checks.log(f"FAIL {error}")
        checks.log(f"END passed={checks.passed} failed=1")
        return 1
    checks.log(f"END passed={checks.passed} failed=0")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
