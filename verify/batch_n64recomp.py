"""Try every scanned N64Recomp port: add its setup to a scratch catalog, install it through RecompBox from the
player's ROM, start it, and keep it only if the game keeps the ROM and stays running.

Results go to D:/AI/n64recomp-results.json (resumable: ports already there are skipped).
"""
import json
import os
import shutil
import subprocess
import time

SCAN = "D:/AI/n64-scan.json"
CATALOG = "D:/AI/recompbox-picktest-catalog.json"
TRIAL = "D:/AI/recompbox-trial-catalog.json"
RESULTS = "D:/AI/n64recomp-results.json"
PLUGIN = r"D:\Source\LaunchBox-Windows-AI5\Tools\SM64EnhancedWindowsBuilder"
KEEP = ["general.json", "graphics.json", "controls.json", "sound.json", "mods.json", "mod_config", "mod_data"]
GRAPHICS = ('{\n  "wm_option": "Fullscreen",\n  "res_option": "Auto",\n  "ar_option": "Expand",\n  "rr_option": "Display",\n'
            '  "msaa_option": "MSAA4X",\n  "hpfb_option": "Auto",\n  "api_option": "Auto"\n}\n')


def fields(entry, graphics=True):
    f = {"roms": [{"label": "US", "sha1": entry["sha1"], "sizeBytes": entry["size"]}], "romFile": entry["gameIds"][0] + ".z64",
            "saves": "saves", "modsFolder": "mods", "portableFile": "portable.txt", "modOrderFile": "mods.json", "keep": KEEP,
            "defaultFiles": [{"path": "graphics.json", "content": GRAPHICS}]}
    if not graphics:
        del f["defaultFiles"]
    return f


def install(entry, target):
    env = dict(os.environ, RECOMPBOX_PICK=entry["id"], RECOMPBOX_PICK_SOURCE=entry["rom"], RECOMPBOX_PICK_CATALOG=TRIAL,
               RECOMPBOX_PICK_DIR=target)
    r = subprocess.run(["dotnet", "test", r"tests\SM64EnhancedWindowsBuilder.Tests", "-c", "Release", "--no-build",
                        "--filter", "InstallsPickFromGameCopyWhenAsked"], cwd=PLUGIN, env=env, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=900)
    if "Passed!" in r.stdout:
        return None
    lines = [l.strip() for l in r.stdout.splitlines() if "Error Message" in l or "Exception" in l]
    idx = r.stdout.find("Error Message:")
    return (r.stdout[idx:idx + 300] if idx >= 0 else "\n".join(lines[:3]) or r.stdout[-300:]).replace("\n", " ")


def launch(target, rom_file):
    exes = [f for f in os.listdir(target) if f.lower().endswith(".exe") and "crash" not in f.lower()]
    if not exes:
        return "no exe"
    exe = os.path.join(target, exes[0])
    p = subprocess.Popen([exe], cwd=target)
    time.sleep(10)
    running = p.poll() is None
    kept = os.path.exists(os.path.join(target, rom_file))
    portable = os.path.isdir(os.path.join(target, "mod_config")) or os.path.isdir(os.path.join(target, "mods"))
    subprocess.run(["taskkill", "/F", "/T", "/PID", str(p.pid)], capture_output=True)
    time.sleep(2)
    if running and kept:
        return None
    return f"running={running} romKept={kept} portable={portable} exe={exes[0]}"


scan = [e for e in json.load(open(SCAN, encoding="utf-8")) if e["kind"] == "n64recomp" and e.get("gameIds") and e.get("sha1")]
results = json.load(open(RESULTS, encoding="utf-8")) if os.path.exists(RESULTS) else {}
base = json.load(open(CATALOG, encoding="utf-8"))
ports = {p["id"]: p for p in base["ports"]}
RETRY = os.environ.get("RETRY") == "1"
for entry in scan:
    previous = results.get(entry["id"])
    if previous and not (RETRY and not previous["ok"] and "romKept=True" in previous["why"] and "running=False" in previous["why"]):
        continue
    graphics = not RETRY
    port = ports.get(entry["id"])
    if not port or not port.get("releases"):
        results[entry["id"]] = {"ok": False, "why": "no release in catalog"}
        continue
    port.update(fields(entry, graphics))
    if not graphics:
        port.pop("defaultFiles", None)
    json.dump(base, open(TRIAL, "w", encoding="utf-8"))
    target = os.path.join(r"D:\AI\picktest", "trial-" + entry["id"])
    shutil.rmtree(target, ignore_errors=True)
    try:
        why = install(entry, target)
        if why is None:
            why = launch(target, fields(entry)["romFile"])
    except subprocess.TimeoutExpired:
        why = "install timed out"
    results[entry["id"]] = {"ok": why is None, "why": why or "", "fields": fields(entry, graphics)}
    print(entry["id"], "OK" if why is None else "FAIL " + why, flush=True)
    json.dump(results, open(RESULTS, "w", encoding="utf-8"), indent=1)
    shutil.rmtree(target, ignore_errors=True)
print("passed", sum(1 for r in results.values() if r["ok"]), "of", len(results))
