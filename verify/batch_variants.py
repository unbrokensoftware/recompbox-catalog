"""Try setup recipes for ports whose exe shows how it takes a ROM, keeping the first that starts cleanly.

Recipes, in order, built from D:/AI/other-strings2.json:
  stored  - ROM placed as <game id>.z64 (N64ModernRuntime's stored ROM) with portable.txt
  romflag - ROM placed as rom.z64 and passed with --rom
  plain   - ROM placed as rom.z64 and passed as the only argument
A recipe passes when the game still runs after 10 s, kept the ROM and shows no message box.
Results: D:/AI/variant-results.json (resumable).
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import re
import shutil
import subprocess
import time

STRINGS = "D:/AI/other-strings2.json"
SCAN = "D:/AI/n64-scan.json"
CATALOG = "D:/AI/recompbox-picktest-catalog.json"
TRIAL = "D:/AI/recompbox-trial-catalog.json"
RESULTS = "D:/AI/variant-results.json"
PLUGIN = r"D:\Source\LaunchBox-Windows-AI5\Tools\SM64EnhancedWindowsBuilder"
KEEP = ["general.json", "graphics.json", "controls.json", "sound.json", "mods.json", "mod_config", "mod_data", "saves"]

user32 = ctypes.windll.user32
EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


def dialogs(pid):
    found = []

    def cb(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            name = ctypes.create_unicode_buffer(64)
            user32.GetClassNameW(hwnd, name, 64)
            if name.value == "#32770":
                title = ctypes.create_unicode_buffer(256)
                user32.GetWindowTextW(hwnd, title, 256)
                found.append(title.value)
        return True

    user32.EnumWindows(EnumProc(cb), 0)
    return found


def game_id(strings):
    for s in strings:
        m = re.fullmatch(r"(?:[A-Za-z@~]{0,3})?([a-z0-9_\-]+\.n64\.us(?:\.[0-9]+(?:\.[0-9]+)?)?)", s.strip())
        if m:
            return m.group(1)
    return None


def recipes(info):
    out = []
    gid = game_id(info["rom"])
    if gid:
        out.append(("stored", {"romFile": gid + ".z64", "portableFile": "portable.txt"}))
    if info["arg"]:
        out.append(("romflag", {"romFile": "rom.z64", "launchArgs": ["--rom", "{rom}"], "portableFile": "portable.txt"}))
    if any("<rom.z64>" in s or "usage: %s <" in s for s in info["rom"]):
        out.append(("plain", {"romFile": "rom.z64", "launchArgs": ["{rom}"], "portableFile": "portable.txt"}))
    return out


def install(port_id, rom, target):
    env = dict(os.environ, RECOMPBOX_PICK=port_id, RECOMPBOX_PICK_SOURCE=rom, RECOMPBOX_PICK_CATALOG=TRIAL, RECOMPBOX_PICK_DIR=target)
    r = subprocess.run(["dotnet", "test", r"tests\SM64EnhancedWindowsBuilder.Tests", "-c", "Release", "--no-build", "--filter",
                        "InstallsPickFromGameCopyWhenAsked"], cwd=PLUGIN, env=env, capture_output=True, text=True, encoding="utf-8",
                       errors="replace", timeout=900)
    if "Passed!" in r.stdout:
        return None
    i = r.stdout.find("Error Message:")
    return r.stdout[i:i + 250].replace("\n", " ") if i >= 0 else r.stdout[-250:].replace("\n", " ")


def launch(target, port):
    marker = json.load(open(os.path.join(target, "recompbox-port.json"), encoding="utf-8"))
    exe = port.get("executable") or ""
    if not exe:
        exes = sorted((os.path.join(dp, f) for dp, _, fs in os.walk(target) for f in fs if f.lower().endswith(".exe")),
                      key=lambda p: -os.path.getsize(p))
        exe = os.path.relpath(exes[0], target) if exes else ""
    if not exe:
        return "no exe", ""
    full = os.path.join(target, exe)
    p = subprocess.Popen(f'"{full}" {marker.get("arguments", "")}', cwd=os.path.dirname(full))
    time.sleep(10)
    running = p.poll() is None
    boxes = dialogs(p.pid) if running else []
    kept = os.path.exists(os.path.join(target, port["romFile"]))
    subprocess.run(["taskkill", "/F", "/T", "/PID", str(p.pid)], capture_output=True)
    time.sleep(2)
    if running and kept and not boxes:
        return None, exe.replace("\\", "/")
    return f"running={running} romKept={kept} boxes={boxes}", exe


strings = json.load(open(STRINGS, encoding="utf-8"))
scan = {e["id"]: e for e in json.load(open(SCAN, encoding="utf-8"))}
results = json.load(open(RESULTS, encoding="utf-8")) if os.path.exists(RESULTS) else {}
base = json.load(open(CATALOG, encoding="utf-8"))
ports = {p["id"]: p for p in base["ports"]}
for port_id, info in strings.items():
    if port_id in results or port_id not in scan or "sha1" not in scan[port_id]:
        continue
    entry = scan[port_id]
    tried = []
    result = {"ok": False, "why": "no recipe", "fields": {}}
    for name, extra in recipes(info):
        fields = {"roms": [{"label": "US", "sha1": entry["sha1"], "sizeBytes": entry["size"]}], "saves": "saves", "modsFolder": "mods",
                  "keep": KEEP, **extra}
        port = dict(ports[port_id], **fields)
        port.pop("defaultFiles", None)
        trial = dict(base, ports=[port if p["id"] == port_id else p for p in base["ports"]])
        json.dump(trial, open(TRIAL, "w", encoding="utf-8"))
        target = os.path.join(r"D:\AI\picktest", "trial-" + port_id)
        shutil.rmtree(target, ignore_errors=True)
        try:
            why = install(port_id, entry["rom"], target)
            exe = ""
            if why is None:
                why, exe = launch(target, port)
        except subprocess.TimeoutExpired:
            why = "timed out"
        shutil.rmtree(target, ignore_errors=True)
        tried.append(f"{name}: {why or 'ok'}")
        if why is None:
            if exe and "/" in exe:
                fields["executable"] = exe
            result = {"ok": True, "why": name, "fields": fields}
            break
        result = {"ok": False, "why": " ; ".join(tried), "fields": {}}
    results[port_id] = result
    print(port_id, "OK " + result["why"] if result["ok"] else "FAIL " + result["why"], flush=True)
    json.dump(results, open(RESULTS, "w", encoding="utf-8"), indent=1)
print("passed", sum(1 for r in results.values() if r["ok"]), "of", len(results))
