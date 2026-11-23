"""Cartridge ports (SNES, NES, GBA, Game Boy, Genesis, ...) the player owns: find how each takes its ROM and test it.

For each port: download its Windows release (zip), read its main exe's strings for ROM file names and a --rom flag,
then try recipes in order: named (ROM placed under a name the exe mentions), romflag (--rom <rom>), plain (<rom>),
folder (ROM placed next to the exe under its own name). A recipe passes only when the game, with the ROM, still runs
after 10 s with no message box, while the same install without the ROM exits or shows one.
Results: D:/AI/cart-results.json (resumable).
"""
import ctypes
import ctypes.wintypes as wt
import fnmatch
import hashlib
import io
import json
import os
import re
import shutil
import subprocess
import sys
import time
import zipfile

PLATFORMS = {"Super Nintendo Entertainment System": [".sfc", ".smc"], "Nintendo Entertainment System": [".nes"],
             "Nintendo Game Boy Advance": [".gba"], "Nintendo Game Boy": [".gb"], "Nintendo Game Boy Color": [".gbc", ".gb"],
             "Sega Genesis": [".md", ".gen", ".bin", ".smd"], "Sega 32X": [".32x"], "Sega Master System": [".sms"],
             "Nintendo Virtual Boy": [".vb"]}
CATALOG = "D:/AI/recompbox-picktest-catalog.json"
TRIAL = "D:/AI/recompbox-trial-catalog.json"
RESULTS = "D:/AI/cart-results.json"
PLUGIN = r"D:\Source\LaunchBox-Windows-AI5\Tools\SM64EnhancedWindowsBuilder"
only = set(sys.argv[1:])

user32 = ctypes.windll.user32
EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


def boxes(pid):
    found = []

    def cb(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            name = ctypes.create_unicode_buffer(64)
            user32.GetClassNameW(hwnd, name, 64)
            if name.value == "#32770":
                found.append(name.value)
        return True

    user32.EnumWindows(EnumProc(cb), 0)
    return found


def rom_bytes(path):
    if path.lower().endswith(".zip"):
        with zipfile.ZipFile(path) as z:
            entry = max(z.infolist(), key=lambda e: e.file_size)
            return entry.filename, z.read(entry)
    return os.path.basename(path), open(path, "rb").read()


def fingerprint(name, data):
    if name.lower().endswith((".sfc", ".smc")) and len(data) % 1024 == 512:
        data = data[512:]
    return hashlib.sha1(data).hexdigest(), len(data)


def owned_ports():
    rows = {}
    for line in open("D:/AI/owned-ports.txt", encoding="utf-8"):
        m = re.match(r"(\S+) \((\d+), (\w+)\): (.*?) / (.*?) -> \w+ \| (.*)", line.strip())
        if m and m.group(3) == "Untested" and m.group(4) in PLATFORMS:
            rows.setdefault(m.group(1), (m.group(4), m.group(6)))
    return rows


def release_zip(port):
    repo = port["repositoryUrl"].replace("https://github.com/", "").strip("/")
    r = subprocess.run(["gh", "api", f"repos/{repo}/releases/tags/{port['releases'][0]['version']}"], capture_output=True, text=True, encoding="utf-8")
    if r.returncode != 0:
        return None
    pattern = port["assets"][0]["pattern"]
    asset = next((a for a in json.loads(r.stdout).get("assets", []) if fnmatch.fnmatch(a["name"], pattern)), None)
    if not asset or not asset["name"].lower().endswith(".zip") or asset["size"] > 400 * 1024 * 1024:
        return None
    return zipfile.ZipFile(io.BytesIO(subprocess.run(["curl", "-sL", asset["browser_download_url"]], capture_output=True, timeout=600).stdout))


def recipes(z, exts):
    exes = sorted((i for i in z.infolist() if i.filename.lower().endswith(".exe")), key=lambda i: -i.file_size)
    blob = z.read(exes[0]) if exes else b""
    strings = [s.decode() for s in re.findall(rb"[\x20-\x7e]{4,120}", blob)]
    names = []
    for s in strings:
        for m in re.finditer(r"([A-Za-z0-9_\-.]+(" + "|".join(re.escape(e) for e in exts) + r"))\b", s):
            n = m.group(1).lstrip("./")
            if n and n.lower() not in (e.lstrip(".") for e in exts) and not n.startswith(".") and n not in names and len(n) < 40:
                names.append(n)
    ext = exts[0]
    out = [("named:" + n, {"romFile": n}) for n in names[:3]]
    if any("--rom" in s for s in strings):
        out.append(("romflag", {"romFile": "rom" + ext, "launchArgs": ["--rom", "{rom}"]}))
    out.append(("plain", {"romFile": "rom" + ext, "launchArgs": ["{rom}"]}))
    return out


def install(port_id, rom, target):
    env = dict(os.environ, RECOMPBOX_PICK=port_id, RECOMPBOX_PICK_SOURCE=rom, RECOMPBOX_PICK_CATALOG=TRIAL, RECOMPBOX_PICK_DIR=target)
    r = subprocess.run(["dotnet", "test", r"tests\SM64EnhancedWindowsBuilder.Tests", "-c", "Release", "--no-build", "--filter",
                        "InstallsPickFromGameCopyWhenAsked"], cwd=PLUGIN, env=env, capture_output=True, text=True, encoding="utf-8",
                       errors="replace", timeout=900)
    if "Passed!" in r.stdout:
        return None
    i = r.stdout.find("Error Message:")
    return r.stdout[i + 14:i + 220].strip().replace("\n", " ") if i >= 0 else "install failed"


def main_exe(target):
    exes = sorted((os.path.join(dp, f) for dp, _, fs in os.walk(target) for f in fs if f.lower().endswith(".exe")), key=lambda p: -os.path.getsize(p))
    return exes[0] if exes else None


def run(target, exe, args):
    p = subprocess.Popen(f'"{exe}" {args}', cwd=os.path.dirname(exe))
    time.sleep(10)
    running = p.poll() is None
    shown = boxes(p.pid) if running else []
    subprocess.run(["taskkill", "/F", "/T", "/PID", str(p.pid)], capture_output=True)
    time.sleep(2)
    return running and not shown


results = json.load(open(RESULTS, encoding="utf-8")) if os.path.exists(RESULTS) else {}
base = json.load(open(CATALOG, encoding="utf-8"))
ports = {p["id"]: p for p in base["ports"]}
for port_id, (platform, rom) in owned_ports().items():
    if port_id in results or (only and port_id not in only) or port_id not in ports or not ports[port_id].get("releases"):
        continue
    port = ports[port_id]
    try:
        name, data = rom_bytes(rom)
        sha1, size = fingerprint(name, data)
        z = release_zip(port)
    except Exception as e:  # noqa: BLE001 - record and continue
        results[port_id] = {"ok": False, "why": f"error {e}"}
        continue
    if z is None:
        results[port_id] = {"ok": False, "why": "no zip release"}
        print(port_id, "FAIL no zip release", flush=True)
        continue
    tried, result = [], None
    for recipe, extra in recipes(z, PLATFORMS[platform]):
        fields = {"roms": [{"label": "US", "sha1": sha1, "sizeBytes": size}], **extra}
        trial_port = dict(port, **fields)
        json.dump(dict(base, ports=[trial_port if p["id"] == port_id else p for p in base["ports"]]), open(TRIAL, "w", encoding="utf-8"))
        target = os.path.join(r"D:\AI\picktest", "trial-" + port_id)
        shutil.rmtree(target, ignore_errors=True)
        try:
            why = install(port_id, rom, target)
            if why is None:
                exe = main_exe(target)
                args = json.load(open(os.path.join(target, "recompbox-port.json"), encoding="utf-8")).get("arguments", "")
                with_rom = run(target, exe, args)
                os.remove(os.path.join(target, extra["romFile"].replace("/", os.sep)))
                without_rom = run(target, exe, args)
                if with_rom and not without_rom:
                    rel = os.path.relpath(exe, target).replace("\\", "/")
                    if "/" in rel:
                        fields["executable"] = rel
                    result = {"ok": True, "why": recipe, "fields": fields}
                why = None if result else f"withRom={with_rom} withoutRom={without_rom}"
        except subprocess.TimeoutExpired:
            why = "timed out"
        shutil.rmtree(target, ignore_errors=True)
        tried.append(f"{recipe}: {why or 'ok'}")
        if result:
            break
    results[port_id] = result or {"ok": False, "why": " ; ".join(tried)}
    print(port_id, ("OK " + result["why"]) if result else "FAIL " + " ; ".join(tried)[:200], flush=True)
    json.dump(results, open(RESULTS, "w", encoding="utf-8"), indent=1)
print("passed", sum(1 for r in results.values() if r["ok"]), "of", len(results))
