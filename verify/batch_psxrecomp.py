"""Install every owned psxrecomp port through RecompBox, start it like Play, screenshot it, and record the result.

The port only sees RecompBox's copies of the disc and BIOS. The player's original disc is checked before and after
each port; the batch stops at once if it ever changes. Screenshots go to D:/AI/psx-shots/<id>.png for review.
Results: D:/AI/psxrecomp-results.json (resumable). "ok" here means installed, running, no message box; the
screenshot decides whether it reached the game.
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from psxrecomp_fields import SCAN, fields  # noqa: E402

CATALOG = "D:/AI/recompbox-picktest-catalog.json"
TRIAL = "D:/AI/recompbox-trial-catalog.json"
RESULTS = "D:/AI/psxrecomp-results.json"
SHOTS = "D:/AI/psx-shots"
PLUGIN = r"D:\Source\LaunchBox-Windows-AI5\Tools\SM64EnhancedWindowsBuilder"
BIOS = os.path.expandvars(r"%LOCALAPPDATA%\LaunchBox\SM64EnhancedWindowsBuilder\Bios")
os.makedirs(SHOTS, exist_ok=True)

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


def windows(pid):
    found = []

    def cb(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            cls = ctypes.create_unicode_buffer(64)
            user32.GetClassNameW(hwnd, cls, 64)
            title = ctypes.create_unicode_buffer(256)
            user32.GetWindowTextW(hwnd, title, 256)
            found.append((hwnd, cls.value, title.value))
        return True

    user32.EnumWindows(EnumProc(cb), 0)
    return found


def snapshot(hwnd, path):
    ps = (f"Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; using System.Drawing; public static class S {{ "
          f"[DllImport(\"user32.dll\")] static extern bool GetWindowRect(IntPtr h, out R r); [DllImport(\"user32.dll\")] static extern bool PrintWindow(IntPtr h, IntPtr d, uint f); "
          f"public struct R {{ public int L, T, Ri, B; }} public static void Grab(IntPtr h, string p) {{ R r; GetWindowRect(h, out r); var b = new Bitmap(Math.Max(1, r.Ri - r.L), Math.Max(1, r.B - r.T)); "
          f"using (var g = Graphics.FromImage(b)) {{ var dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }} b.Save(p); }} }}' -ReferencedAssemblies System.Drawing; "
          f"[S]::Grab([IntPtr]{hwnd}, '{path}')")
    subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, timeout=60)


def install(port_id, rom, target):
    env = dict(os.environ, RECOMPBOX_PICK=port_id, RECOMPBOX_PICK_SOURCE=rom, RECOMPBOX_PICK_CATALOG=TRIAL, RECOMPBOX_PICK_DIR=target,
               RECOMPBOX_BIOS_FOLDERS=BIOS)
    r = subprocess.run(["dotnet", "test", r"tests\SM64EnhancedWindowsBuilder.Tests", "-c", "Release", "--no-build", "--filter",
                        "InstallsPickFromGameCopyWhenAsked"], cwd=PLUGIN, env=env, capture_output=True, text=True, encoding="utf-8",
                       errors="replace", timeout=5400)
    if "Passed!" in r.stdout:
        return None
    i = r.stdout.find("Error Message:")
    return r.stdout[i + 14:i + 260].strip().replace("\n", " ") if i >= 0 else "install failed"


def play(port_id, target):
    marker = json.load(open(os.path.join(target, "recompbox-port.json"), encoding="utf-8"))
    f = fields(port_id)
    exe = os.path.join(target, f["executable"].replace("/", os.sep))
    p = subprocess.Popen(f'"{exe}" {marker.get("arguments", "")}', cwd=os.path.dirname(exe))
    time.sleep(50)
    if p.poll() is not None:
        return f"exited with {p.returncode}"
    wins = windows(p.pid)
    boxes = [t for _, c, t in wins if c == "#32770"]
    main = next((h for h, c, _ in wins if c != "#32770"), None) or (wins[0][0] if wins else None)
    if main:
        snapshot(main, os.path.join(SHOTS, port_id + ".png").replace("/", "\\"))
    subprocess.run(["taskkill", "/F", "/T", "/PID", str(p.pid)], capture_output=True)
    time.sleep(3)
    return f"message box: {boxes}" if boxes else None


results = json.load(open(RESULTS, encoding="utf-8")) if os.path.exists(RESULTS) else {}
base = json.load(open(CATALOG, encoding="utf-8"))
for port_id, info in SCAN.items():
    if port_id in results or not any(b["stem"].startswith("SCPH") for b in info["bios"]):
        continue
    rom = info["rom"]
    before = os.stat(rom).st_mtime
    trial = dict(base, ports=[dict(p, **fields(port_id)) if p["id"] == port_id else p for p in base["ports"]])
    json.dump(trial, open(TRIAL, "w", encoding="utf-8"))
    target = os.path.join(r"D:\AI\picktest", "psx-" + port_id)
    shutil.rmtree(target, ignore_errors=True)
    started = time.time()
    try:
        why = install(port_id, rom, target)
        if why is None:
            why = play(port_id, target)
    except subprocess.TimeoutExpired:
        why = "timed out"
    if os.stat(rom).st_mtime != before:
        print("STOP: the original disc changed:", rom, flush=True)
        sys.exit(2)
    shutil.rmtree(target, ignore_errors=True)
    results[port_id] = {"ok": why is None, "why": why or "", "minutes": round((time.time() - started) / 60, 1), "fields": fields(port_id)}
    print(port_id, "OK" if why is None else "FAIL " + why, results[port_id]["minutes"], "min", flush=True)
    json.dump(results, open(RESULTS, "w", encoding="utf-8"), indent=1)
print("done", sum(1 for r in results.values() if r["ok"]), "of", len(results))
