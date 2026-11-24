"""Check P3 grid and hot-zone adjustments through the Windows application MCP."""

import argparse
import importlib.util
import json
from pathlib import Path
import sys
import time
import uuid


ROOT = Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("dock_p2_probe", ROOT / "tests/P2/Test-DockP2.py")
p2 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p2)


def profile_fixture(directory):
    profile = directory / "profile"
    profile.mkdir()
    items = []
    for name in ("A", "B", "C"):
        path = directory / f"{name}.txt"
        path.write_text(f"P3 grid reference {name}\n", encoding="utf-8")
        items.append({"id": str(uuid.uuid4()), "name": name, "kind": "File",
                      "target": str(path), "customIconPath": None})
    config = profile / "dock.json"
    legacy = {"schemaVersion": 3, "options": {"placement": "Bottom", "iconSize": 38,
              "behavior": {"mode": "Resident"}, "layout": {"mode": "SingleRow"}},
              "items": items, "groups": [], "entries": [{"id": item["id"], "parentId": None} for item in items]}
    config.write_text(json.dumps(legacy, ensure_ascii=False, indent=2), encoding="utf-8")
    return profile, config, items


def grid(client, action="read", **arguments):
    return client.data("dock_p3_grid", {"action": action, **arguments})


def tile(result, item):
    return next(value for value in result["grid"]["tiles"] if value["id"] == item)


def cell(tile_record):
    return tile_record["column"], tile_record["row"]


def span(tile_record):
    return tile_record["columnSpan"], tile_record["rowSpan"]


def dimensions(result):
    return result["grid"]["columns"], result["grid"]["rows"]


def exercise_grid(client, checks, config, items, directory):
    a, b, c = [item["id"] for item in items]
    prior = config.read_bytes()
    state = client.data("dock_state")
    initial = grid(client)
    checks.check(not initial["grid"]["active"] and state["view"]["layout"]["mode"] == "SingleRow"
                 and state["items"] == items and config.read_bytes() == prior,
                 "existing P3 profile opens with its original layout, order, and bytes")
    checks.check(not initial["grid"]["settingsButtonPresent"], "settings button is absent from Dock chrome")
    checks.check(initial["grid"]["windowResizeHandles"] == 8, "Dock exposes eight WPF edge and corner resize handles")
    settings = client.data("dock_settings", {"visible": True, "page": "layout"})
    checks.check(settings["settingsVisible"], "settings window remains available")
    client.data("dock_settings", {"visible": False})

    resized = grid(client, "resize", columns=3.2, rows=3)
    checks.check(resized["accepted"] and resized["grid"]["active"] and dimensions(resized) == (3, 3)
                 and {part["id"] for part in resized["grid"]["tiles"]} == {a, b, c},
                 "3.2 by 3 snaps to 3 by 3 and preserves three items and six blank cells")
    backups = list(config.parent.glob("dock.v3-*.bak"))
    checks.check(len(backups) == 1 and backups[0].read_bytes() == prior
                 and json.loads(config.read_text(encoding="utf-8"))["schemaVersion"] == 4,
                 "first grid edit migrates schema 3 to 4 with a byte-exact backup")
    checks.check(all(value["resizeHandlePresent"] for value in resized["grid"]["tiles"]),
                 "every root tile exposes a WPF resize handle")
    half = grid(client, "resize", columns=3.5, rows=2.5)
    checks.check(half["accepted"] and dimensions(half) == (4, 3), "exact half rounds up on both axes")
    resized = grid(client, "resize", columns=3, rows=3)
    checks.check(resized["accepted"] and dimensions(resized) == (3, 3), "clean shrink leaves occupied cells intact")

    start = grid(client, "move", id=a, column=1, row=3)
    checks.check(start["accepted"] and cell(tile(start, a)) == (1, 3), "item moves to blank row 3 column 1")
    adjacent = grid(client, "move", id=a, column=2, row=3)
    checks.check(adjacent["accepted"] and cell(tile(adjacent, a)) == (2, 3)
                 and all(cell(value) != (1, 3) for value in adjacent["grid"]["tiles"])
                 and cell(tile(adjacent, b)) == cell(tile(start, b)) and cell(tile(adjacent, c)) == cell(tile(start, c)),
                 "item moves from row 3 column 1 to adjacent blank row 3 column 2 without compacting other tiles")

    initial_rect = tile(adjacent, a)["bounds"]
    wide = grid(client, "stretch", id=a, columns=1.6, rows=1)
    wide_rect = tile(wide, a)["bounds"]
    checks.check(wide["accepted"] and span(tile(wide, a)) == (2, 1)
                 and wide_rect["width"] > initial_rect["width"] and abs(wide_rect["height"] - initial_rect["height"]) <= 2,
                 "tile handle snaps to a horizontal 2 by 1 span, with real WPF geometry", str(wide_rect))
    moved = grid(client, "move", id=a, column=2, row=2)
    checks.check(moved["accepted"] and span(tile(moved, a)) == (2, 1) and cell(tile(moved, a)) == (2, 2),
                 "moving a stretched tile preserves its span")
    tall = grid(client, "stretch", id=a, columns=1, rows=1.5)
    tall_rect = tile(tall, a)["bounds"]
    checks.check(tall["accepted"] and span(tile(tall, a)) == (1, 2)
                 and tall_rect["height"] > initial_rect["height"] and abs(tall_rect["width"] - initial_rect["width"]) <= 2,
                 "tile handle snaps to a vertical 1 by 2 span, with real WPF geometry", str(tall_rect))
    both = grid(client, "stretch", id=a, columns=2, rows=2)
    both_rect = tile(both, a)["bounds"]
    checks.check(both["accepted"] and span(tile(both, a)) == (2, 2)
                 and both_rect["width"] > initial_rect["width"] and both_rect["height"] > initial_rect["height"],
                 "both grid axes can be stretched together", str(both_rect))

    bcol, brow = cell(tile(both, b))
    blocker = grid(client, "move", id=b, column=1, row=3)
    checks.check(blocker["accepted"] and cell(tile(blocker, b)) == (1, 3), "blank cell accepts a second reference")
    persisted = config.read_bytes()
    overlap = grid(client, "move", id=a, column=1, row=2)
    checks.check(not overlap["accepted"] and config.read_bytes() == persisted and cell(tile(overlap, a)) == (2, 2),
                 "a stretched tile cannot move to a blank anchor when its footprint hits another tile")
    grid(client, "move", id=b, column=bcol, row=brow)
    growth = grid(client, "stretch", id=a, columns=3, rows=2)
    checks.check(growth["accepted"] and dimensions(growth) == (4, 3) and span(tile(growth, a)) == (3, 2),
                 "stretching beyond the window grows the grid to fit the valid footprint")
    grid(client, "stretch", id=a, columns=2, rows=2)
    protected = grid(client, "resize", columns=2, rows=2)
    checks.check(set(value["id"] for value in protected["grid"]["tiles"]) == {a, b, c}
                 and dimensions(protected) == (3, 3) and span(tile(protected, a)) == (2, 2),
                 "shrinking cannot clip an occupied cell or truncate a tile span")
    size = client.capture("dock", directory / "sparse-grid.png")
    checks.check(size > 256, "real WPF PNG of sparse grid and stretched tile", f"bytes={size}")
    return grid(client)


def bounds(client):
    return client.data("dock_state")["view"]["bounds"]


def wait_for_bounds(client, predicate):
    deadline = time.monotonic() + 3
    while True:
        rectangle = bounds(client)
        if rectangle is not None and predicate(rectangle):
            return rectangle
        if time.monotonic() >= deadline:
            raise AssertionError(f"placement did not settle as expected: {rectangle!r}")
        time.sleep(0.035)


def hotzone_point(zone, edge, fraction):
    if edge in ("Top", "Bottom"):
        return round(zone["left"] + (zone["right"] - zone["left"] - 1) * fraction), (zone["top"] + zone["bottom"] - 1) // 2
    return (zone["left"] + zone["right"] - 1) // 2, round(zone["top"] + (zone["bottom"] - zone["top"] - 1) * fraction)


def clamp(value, low, high):
    return min(max(value, low), max(low, high))


def expected_location(rectangle, point, edge, monitor):
    x, y = point
    width, height = rectangle["width"], rectangle["height"]
    pad = 10 * monitor["scale"]
    x, y = {
        "Top": (x - width / 2, y + pad), "Bottom": (x - width / 2, y - height - pad),
        "Left": (x + pad, y - height / 2), "Right": (x - width - pad, y - height / 2),
    }[edge]
    return clamp(x, monitor["workLeft"], monitor["workRight"] - width), clamp(y, monitor["workTop"], monitor["workBottom"] - height)


def exercise_hotzones(client, checks):
    state = client.data("dock_state")
    behavior = state["options"]["behavior"]
    monitors = client.data("dock_p2_monitors")["monitors"]
    monitor = next((value for value in monitors if value["primary"]), monitors[0])
    edges = ("Top", "Bottom", "Left", "Right")
    zones = [p2.zone(edge, 0, 100, 8, 0, 100, 200) for edge in edges]
    tested = {**behavior, "mode": "AutoHide", "monitors": [{"monitorId": monitor["id"], "zones": zones}]}
    timeline = p2.Timeline(client)
    configured = client.data("dock_p2_config", {"behavior": tested})
    checks.check(not configured["view"]["nativeVisible"], "AutoHide configuration begins with the HWND hidden")
    all_zones = client.data("dock_p2_monitors")["zones"]
    active = {value["zone"]["edge"]: value for value in all_zones if value["monitorId"] == monitor["id"]}

    def reveal(edge, fraction, initial_fraction=None):
        timeline.step(delta=250)
        start = hotzone_point(active[edge], edge, fraction if initial_fraction is None else initial_fraction)
        armed = timeline.step(start)
        point = hotzone_point(active[edge], edge, fraction)
        shown = timeline.step(point, delta=120)
        if armed["view"]["nativeVisible"] or not shown["view"]["nativeVisible"]:
            raise AssertionError(f"{edge} did not wait for dwell before revealing: {p2.state(armed)}, {p2.state(shown)}")
        return point

    def matches(rectangle, point, edge):
        x, y = expected_location(rectangle, point, edge, monitor)
        return abs(rectangle["x"] - x) <= 2 and abs(rectangle["y"] - y) <= 2

    for edge in edges:
        point = reveal(edge, 0.25, initial_fraction=0.55)
        rectangle = wait_for_bounds(client, lambda value: matches(value, point, edge))
        checks.check(client.data("dock_state")["options"]["placement"] == "Bottom",
                     f"{edge} zone opens near the completed-dwell pointer without rewriting placement",
                     f"pointer={point} bounds={rectangle}")
        sampled = timeline.step(hotzone_point(active[edge], edge, 0.67))
        checks.check(sampled["view"]["bounds"] == rectangle, f"{edge} reveal stays fixed while visible")
        timeline.event("OutsideClick", p2.OUTSIDE)
    near_corner = reveal("Top", 0.01)
    clamped = wait_for_bounds(client, lambda value: matches(value, near_corner, "Top"))
    checks.check(abs(clamped["x"] - monitor["workLeft"]) <= 2 and clamped["y"] >= monitor["workTop"] - 2,
                 "edge-follow reveal clamps to the work area near a corner", f"pointer={near_corner} bounds={clamped}")
    timeline.event("OutsideClick", p2.OUTSIDE)

    manual_point = hotzone_point(active["Top"], "Top", 0.25)
    timeline.step(manual_point)
    manual = timeline.event("Toggle")
    center_x = (monitor["workLeft"] + monitor["workRight"]) / 2
    rectangle = wait_for_bounds(client, lambda value: abs(value["x"] + value["width"] / 2 - center_x) <= 2
                                and value["y"] + value["height"] >= monitor["workBottom"] - 24 * monitor["scale"])
    checks.check(manual["view"]["nativeVisible"], "manual shortcut restores the configured centered bottom placement", str(rectangle))
    timeline.event("OutsideClick", p2.OUTSIDE)

    client.data("dock_p2_config", {"behavior": behavior})
    client.data("dock_options", {"placement": "Floating", "floatingLeft": 170, "floatingTop": 170})
    floating = wait_for_bounds(client, lambda value: value["x"] > monitor["workLeft"] + 50
                               and value["y"] > monitor["workTop"] + 50)
    client.data("dock_p2_config", {"behavior": tested})
    point = reveal("Top", 0.7)
    rectangle = wait_for_bounds(client, lambda value: value == floating)
    checks.check(rectangle == floating, "floating Dock retains its saved location through a hot-zone reveal",
                 f"pointer={point} bounds={rectangle}")
    client.data("dock_p2_config", {"behavior": behavior})
    client.data("dock_options", {"placement": "Bottom"})


def exercise(executable, directory, checks):
    profile, config, items = profile_fixture(directory)
    with p2.DockClient(executable, profile) as client:
        names = {tool["name"] for tool in client.rpc("tools/list")["tools"]}
        checks.check("dock_p3_grid" in names, "P3 grid tool is advertised over MCP")
        before = exercise_grid(client, checks, config, items, directory)
        exercise_hotzones(client, checks)
    with p2.DockClient(executable, profile) as client:
        restored = grid(client)
        state = client.data("dock_state")
        checks.check(dimensions(restored) == dimensions(before)
                     and {value["id"]: (cell(value), span(value)) for value in restored["grid"]["tiles"]}
                     == {value["id"]: (cell(value), span(value)) for value in before["grid"]["tiles"]}
                     and {item["id"]: item for item in state["items"]} == {item["id"]: item for item in items}
                     and [entry["id"] for entry in state["entries"]] == [value["id"] for value in
                         sorted(restored["grid"]["tiles"], key=lambda value: (value["row"], value["column"]))],
                     "restart restores exact cell holes, tile spans, and original references")

        a, b, c = [item["id"] for item in items]
        col, row = cell(tile(restored, c))
        merged = grid(client, "move", id=b, column=col, row=row)
        state = client.data("dock_state")
        group_id = state["groups"][0]["id"] if state["groups"] else None
        checks.check(merged["accepted"] and group_id is not None and cell(tile(merged, group_id)) == (col, row)
                     and {entry["id"] for entry in state["entries"] if entry["parentId"] == group_id} == {b, c}
                     and span(tile(merged, a)) == (2, 2),
                     "center-drop grouping still uses the group target cell in the sparse grid")
        client.data("dock_p3_delete", {"id": group_id, "choice": "MoveContentsToParent"})
        promoted = grid(client)
        checks.check(cell(tile(promoted, c)) == (col, row) and span(tile(promoted, a)) == (2, 2)
                     and {value["id"] for value in promoted["grid"]["tiles"]} == {a, b, c},
                     "group promotion preserves other sparse placements and restores both references")


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, default=ROOT / "artifacts/DesktopDock-P3-Grid-R2-win-x64/DesktopDock.exe")
    parser.add_argument("--run-directory", type=Path, default=ROOT / "artifacts" / f"p3-grid-{uuid.uuid4().hex}")
    args = parser.parse_args()
    executable = args.executable.resolve(strict=True)
    directory = args.run_directory.resolve()
    directory.mkdir()
    checks = p2.Checks(directory / "mcp-results.log")
    checks.log(f"START exe={executable} directory={directory}")
    try:
        exercise(executable, directory, checks)
    except (AssertionError, KeyError, RuntimeError, OSError, ValueError, TypeError, StopIteration) as error:
        checks.log(f"FAIL {type(error).__name__}: {error}")
        checks.log(f"END passed={checks.passed} failed=1")
        return 1
    checks.log(f"END passed={checks.passed} failed=0")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
