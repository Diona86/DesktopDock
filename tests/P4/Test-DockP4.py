"""Exercise the real P4 WPF editor and Dock in isolated MCP profiles."""

import argparse
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import statistics
import struct
import sys
import time
import uuid


ROOT = Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("dock_p2_probe", ROOT / "tests/P2/Test-DockP2.py")
p2 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p2)


def editor(client, action="state", **arguments):
    return client.data("dock_p4_theme", {"action": action, **arguments})


def contents(path):
    return path.read_bytes() if path.is_file() else None


def add_items(client, directory):
    fixtures = []
    for name in ("Draft", "Library", "Artwork"):
        folder = directory / name
        folder.mkdir()
        fixtures.append(str(folder))
    return client.data("dock_add", {"targets": fixtures})["items"]


def capture(client, checks, window, path):
    size = client.capture(window, path)
    checks.check(size > 256, f"real WPF PNG for {path.stem}", f"bytes={size}")
    return hashlib.sha256(path.read_bytes()).hexdigest()


def expect_rejected(client, checks, path, store):
    before = contents(store)
    before_selection = editor(client)["editor"]
    failure = client.call("dock_p4_theme", {"action": "import", "path": str(path)}, expect_error=True)
    detail = failure["content"][0]["text"]
    after = editor(client)
    checks.check(bool(detail) and contents(store) == before and after["editor"] == before_selection,
                 f"reject {path.name} without changing saved themes or active draft", detail)


def test_files(client, checks, package, profile, directory):
    exported = json.loads(package.read_text(encoding="utf-8-sig"))
    checks.check(exported["format"] == "desktop-dock-theme" and exported["schemaVersion"] == 1,
                 "export uses the versioned standalone theme format")

    def emit(name, document):
        path = directory / f"{name}.docktheme"
        path.write_text(json.dumps(document, ensure_ascii=False), encoding="utf-8")
        return path

    wrong_version = copy.deepcopy(exported)
    wrong_version["schemaVersion"] = 99
    unknown_resource = copy.deepcopy(exported)
    unknown_resource["theme"]["assets"] = {"../outside.ps1": "bad test resource"}
    unknown_code = copy.deepcopy(exported)
    unknown_code["theme"]["tokens"]["script"] = "fixture.dll"
    out_of_range = copy.deepcopy(exported)
    out_of_range["theme"]["tokens"]["opacity"] = 2.5
    external_color = copy.deepcopy(exported)
    external_color["theme"]["tokens"]["backgroundColor"] = r"..outside.exe"
    duplicate = directory / "duplicate-key.docktheme"
    duplicate.write_text('{"format":"desktop-dock-theme","schemaVersion":1,"schemaVersion":1,"theme":{}}',
                         encoding="utf-8")
    oversized = directory / "oversized.docktheme"
    oversized.write_bytes(b" " * 32769)
    for path in (
        emit("wrong-version", wrong_version), emit("asset-traversal", unknown_resource),
        emit("executable-field", unknown_code), emit("out-of-range", out_of_range),
        emit("external-path", external_color), duplicate, oversized, package,
    ):
        expect_rejected(client, checks, path, profile / "themes.json")
    return exported["theme"]


def test_styles(client, checks, directory):
    store = directory / "profile" / "themes.json"
    initial = contents(store)
    opened = editor(client, "open")
    checks.check(opened["editorVisible"], "real theme editor window opens")
    catalog = {theme["id"]: theme for theme in opened["saved"]["themes"]}
    checks.check({"minimal", "glass", "tech"} <= catalog.keys() and opened["saved"]["globalThemeId"] == "minimal",
                 "all three built-in themes load with Minimal as default")
    visuals = {}
    applied = {}
    for theme_id in ("minimal", "glass", "tech"):
        result = editor(client, "theme", id=theme_id)
        applied[theme_id] = result["rendering"]
        screenshot = directory / f"dock-{theme_id}.png"
        visuals[theme_id] = capture(client, checks, "dock", screenshot)
        if theme_id == "glass":
            width, height = struct.unpack(">II", screenshot.read_bytes()[16:24])
            checks.check(width > applied[theme_id]["width"] and height > applied[theme_id]["height"],
                         "the glass capture includes its reserved shadow margins", f"size={width}x{height}")
            capture(client, checks, "themeEditor", directory / "editor-glass.png")
        checks.check(result["saved"]["globalThemeId"] == "minimal" and contents(store) == initial,
                     f"{theme_id} preview leaves the formal theme and configuration untouched")
    checks.check(len(set(visuals.values())) == 3, "three applied Dock themes have distinct WPF renderings")
    checks.check({value["material"] for value in applied.values()} == {"Solid", "Frosted", "Technical"}
                 and len({value["cornerRadius"] for value in applied.values()}) == 3
                 and {value["grip"] for value in applied.values()} == {"Dots", "Bar", "Lines"}
                 and applied["tech"]["textureBrush"] == "DrawingBrush" and applied["tech"]["layout"] == "MultiRowGrid",
                 "built-ins differ in material, edge shape, grip, pattern and layout")

    base = editor(client, "theme", id="glass")["rendering"]
    property_changed = editor(client, "set", key="cornerRadius", value=1)["rendering"]
    checks.check(property_changed["cornerRadius"] == 1, "corner radius edit changes actual WPF border immediately")
    undo = editor(client, "undo")["rendering"]
    checks.check(undo["cornerRadius"] == base["cornerRadius"], "undo restores the prior rendered radius")
    editor(client, "set", key="opacity", value=0.47)
    size = editor(client, "set", key="iconSize", value=52)["rendering"]
    checks.check(size["background"] != base["background"] and size["iconSize"] == 52,
                 "opacity and icon size edit update the rendered brush and geometry")
    reset = editor(client, "reset")["rendering"]
    checks.check(all(reset[key] == base[key] for key in ("cornerRadius", "background", "iconSize")),
                 "reset restores the built-in theme defaults")
    cancelled = editor(client, "cancel")
    checks.check(not cancelled["editorVisible"] and contents(store) == initial,
                 "cancel discards the draft and leaves theme storage unchanged")
    checks.check(capture(client, checks, "dock", directory / "dock-cancelled.png") == visuals["minimal"],
                 "cancel restores the actual Dock to its original look")


def test_preferences(client, checks, directory):
    editor(client, "open")
    editor(client, "theme", id="glass")
    editor(client, "set", key="cornerRadius", value=9)
    saved = editor(client, "saveAs", name="P4 Glass Lab")["saved"]
    custom_id = saved["globalThemeId"]
    checks.check(custom_id not in ("minimal", "glass", "tech") and saved["tokens"]["cornerRadius"] == 9,
                 "save as creates and applies a separate custom theme")
    editor(client, "set", key="cornerRadius", value=11)
    saved = editor(client, "save")["saved"]
    checks.check(saved["globalThemeId"] == custom_id and saved["tokens"]["cornerRadius"] == 11,
                 "save updates the selected custom theme in place")
    package = directory / "saved.docktheme"
    editor(client, "export", path=str(package))
    checks.check(package.is_file() and 256 < package.stat().st_size <= 32768, "export writes the complete bounded theme file")
    original = test_files(client, checks, package, directory / "profile", directory)
    checks.check(original["id"] == custom_id and original["tokens"] == saved["tokens"], "export preserves the entire editable token set")

    editor(client, "scope", scope="Current")
    editor(client, "override", key="cornerRadius", enabled=True)
    editor(client, "set", key="cornerRadius", value=3)
    local = editor(client, "save")["saved"]
    checks.check(local["globalThemeId"] == custom_id and local["tokens"]["cornerRadius"] == 3,
                 "saved Dock-only parameter overrides the inherited value")
    editor(client, "scope", scope="Global")
    editor(client, "theme", id="tech")
    global_changed = editor(client, "save")["saved"]
    checks.check(global_changed["globalThemeId"] == "tech" and global_changed["tokens"]["cornerRadius"] == 3,
                 "changing the global theme preserves independent Dock overrides")

    editor(client, "scope", scope="Current")
    editor(client, "theme", id="glass")
    selected_local = editor(client, "save")["saved"]
    checks.check(selected_local["globalThemeId"] == "tech" and selected_local["tokens"]["material"] == "Frosted",
                 "this Dock can select a material different from the global theme")
    editor(client, "theme", id=None)
    inherited = editor(client, "save")["saved"]
    checks.check(inherited["globalThemeId"] == "tech" and inherited["tokens"]["material"] != "Frosted"
                 and inherited["tokens"]["cornerRadius"] == 3, "restoring theme inheritance keeps the explicit local field")
    return package, original


def test_effects(client, checks, group_id, directory):
    editor(client, "scope", scope="Global")
    editor(client, "theme", id="glass")
    editor(client, "save")
    normal = editor(client)["rendering"]
    editor(client, "set", key="hideMilliseconds", value=390)
    editor(client, "set", key="revealMilliseconds", value=340)
    client.data("dock_visibility", {"visible": False})
    exit_frame = editor(client)["rendering"]
    checks.check(not exit_frame["logicalVisible"] and not exit_frame["pointerInput"],
                 "hide drops logical visibility and pointer input immediately")
    if exit_frame["animationMilliseconds"] > 0:
        checks.check(exit_frame["animationMilliseconds"] == 390 and exit_frame["animationActive"] and exit_frame["nativeVisible"],
                     "hide applies its configured fade to the real native window")
    else:
        checks.log("System window animation preference disabled the configured fade")
    final = settled_window(client)
    checks.check(not final["nativeVisible"], "hide completes with the native window completely hidden")
    client.data("dock_visibility", {"visible": True})
    opening = editor(client)["rendering"]
    if opening["animationMilliseconds"] > 0:
        checks.check(opening["animationMilliseconds"] == 340 and opening["animationActive"] and opening["nativeVisible"]
                     and opening["opacity"] < 1, "reveal applies its configured slide with a visible intermediate frame")
    else:
        checks.log("System window animation preference disabled the configured reveal")
    client.data("dock_visibility", {"visible": False})
    client.data("dock_visibility", {"visible": True})
    final = settled_window(client)
    checks.check(final["logicalVisible"] and final["nativeVisible"] and final["pointerInput"],
                 "reopening during the exit cancels the pending native hide")
    reduced = editor(client, "performance", enabled=True)["rendering"]
    checks.check(normal["shadow"] and normal["blur"] > 0 and not reduced["shadow"] and reduced["blur"] == 0,
                 "performance mode removes the native WPF shadow and material blur")
    editor(client, "animations", enabled=False)
    client.data("dock_visibility", {"visible": False})
    hidden = client.data("dock_state")
    checks.check(not hidden["view"]["nativeVisible"], "reduced animations hide the native window immediately")
    client.data("dock_visibility", {"visible": True})
    client.data("dock_p3_open", {"id": group_id})
    panel = client.data("dock_state")["groupPanels"]["panels"][0]
    checks.check(not panel["animationActive"] and panel["opacity"] == 1, "group opens without animation when the global switch is off")
    capture(client, checks, "group", directory / "group-effects-reduced.png")
    client.data("dock_p3_back")
    saved = editor(client, "save")["saved"]
    checks.check(saved["performanceMode"] and not saved["animationsEnabled"], "both global toggles persist through editor save")
    editor(client, "cancel")

    editor(client, "open")
    samples = []
    for theme_id in ("minimal", "tech", "glass") * 6:
        start = time.perf_counter()
        editor(client, "theme", id=theme_id)
        samples.append((time.perf_counter() - start) * 1000)
    ordered = sorted(samples)
    checks.check(max(samples) < 1500, "eighteen live WPF switches completed without a long stall",
                 f"median={statistics.median(samples):.1f} ms p95={ordered[-1]:.1f} ms max={max(samples):.1f} ms")
    editor(client, "cancel")
    return saved


def settled_window(client):
    deadline = time.monotonic() + 2
    while time.monotonic() < deadline:
        rendering = editor(client)["rendering"]
        if not rendering["animationActive"]:
            return rendering
        time.sleep(0.025)
    raise RuntimeError("WPF visibility transition did not settle.")


def exercise(executable, directory, checks):
    profile = directory / "profile"
    profile.mkdir()
    with p2.DockClient(executable, profile) as client:
        tools = {tool["name"] for tool in client.rpc("tools/list")["tools"]}
        checks.check("dock_p4_theme" in tools, "P4 MCP is advertised")
        items = add_items(client, directory)
        group = client.data("dock_p3_create", {"name": "Preview group"})["id"]
        client.data("dock_p3_move", {"id": items[0]["id"], "parentId": group})
        dock_configuration = contents(profile / "dock.json")
        test_styles(client, checks, directory)
        package, original = test_preferences(client, checks, directory)
        expected = test_effects(client, checks, group, directory)
        checks.check(contents(profile / "dock.json") == dock_configuration,
                     "theme changes never rewrite P3 Dock options, groups or layout configuration")
    with p2.DockClient(executable, profile) as client:
        saved = editor(client)["saved"]
        checks.check(saved["globalThemeId"] == expected["globalThemeId"] and saved["tokens"] == expected["tokens"]
                     and saved["overrides"] == expected["overrides"] and saved["tokens"]["cornerRadius"] == 3
                     and saved["performanceMode"] and not saved["animationsEnabled"],
                     "restart restores global theme, Dock override and performance settings")

    imported_profile = directory / "import-target"
    imported_profile.mkdir()
    with p2.DockClient(executable, imported_profile) as client:
        editor(client, "open")
        imported = editor(client, "import", path=str(package))
        checks.check(imported["saved"]["globalThemeId"] == "minimal"
                     and contents(imported_profile / "themes.json") is None,
                     "a valid imported file can be previewed before committing")
        saved = editor(client, "save")["saved"]
        imported_theme = next(theme for theme in saved["themes"] if theme["id"] == original["id"])
        checks.check(imported_theme == original and saved["globalThemeId"] == original["id"],
                     "successful import and apply round-trip all supplied resources and tokens")

    corrupt_profile = directory / "corrupt-theme-profile"
    corrupt_profile.mkdir()
    broken = corrupt_profile / "themes.json"
    corrupt = b'{"schemaVersion":999,"state":{}}'
    broken.write_bytes(corrupt)
    with p2.DockClient(executable, corrupt_profile) as client:
        fallback = editor(client, "open")
        editor(client, "theme", id="glass")
        client.call("dock_p4_theme", {"action": "save"}, expect_error=True)
        checks.check(fallback["saved"]["loadError"] and fallback["saved"]["globalThemeId"] == "minimal"
                     and fallback["rendering"]["nativeVisible"] and contents(broken) == corrupt,
                     "invalid saved theme configuration preserves its file and allows the default Dock to start")


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, default=ROOT / "artifacts/DesktopDock-P4-win-x64/DesktopDock.exe")
    parser.add_argument("--run-directory", type=Path, default=ROOT / "artifacts" / f"p4-theme-{uuid.uuid4().hex}")
    options = parser.parse_args()
    executable = options.executable.resolve(strict=True)
    directory = options.run_directory.resolve()
    directory.mkdir()
    checks = p2.Checks(directory / "mcp-results.log")
    checks.log(f"START exe={executable} directory={directory}")
    try:
        exercise(executable, directory, checks)
    except (AssertionError, KeyError, RuntimeError, OSError, ValueError, StopIteration) as error:
        checks.log(f"FAIL {type(error).__name__}: {error}")
        checks.log(f"END passed={checks.passed} failed=1")
        return 1
    checks.log(f"END passed={checks.passed} failed=0")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
