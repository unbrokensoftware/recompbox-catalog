"""For inspected "other" ports: download the release again, read its main exe's strings and look for how it finds
the ROM. Writes D:/AI/other-strings.json. Nothing is run.
"""
import io
import json
import re
import subprocess
import zipfile
import fnmatch

inspect = json.load(open("D:/AI/other-inspect.json", encoding="utf-8"))
catalog = {p["id"]: p for p in json.load(open("D:/AI/recompbox-picktest-catalog.json", encoding="utf-8"))["ports"]}
out = {}
ROMNAME = re.compile(rb"[A-Za-z0-9_\-]+(?:\.[A-Za-z0-9_\-]+){0,4}\.(?:z64|n64|v64)\b")
HINTS = [b"portable.txt", b"baserom", b"Select ROM", b"select a ROM", b"--rom", b"rom_path", b"GetOpenFileName", b"IFileOpenDialog",
         b"SDL_ShowOpenFileDialog", b"recomp::start", b"N64ModernRuntime", b".o2r", b"torch"]

for port_id, info in inspect.items():
    if not info.get("exes"):
        continue
    port = catalog[port_id]
    version = port["releases"][0]["version"]
    rel = subprocess.run(["gh", "api", f"repos/{info['repo']}/releases/tags/{version}"], capture_output=True, text=True, encoding="utf-8")
    assets = json.loads(rel.stdout).get("assets", []) if rel.returncode == 0 else []
    asset = next((a for a in assets if a["name"] == info.get("asset")), None)
    if not asset:
        continue
    data = subprocess.run(["curl", "-sL", asset["browser_download_url"]], capture_output=True, timeout=600).stdout
    z = zipfile.ZipFile(io.BytesIO(data))
    main = [i for i in z.infolist() if i.filename.lower().endswith(".exe") and "/" not in i.filename.split("/", 1)[-1] or
            i.filename.lower().endswith(".exe") and i.filename.count("/") <= 1]
    main = sorted(main, key=lambda i: -i.file_size)[:1]
    if not main:
        continue
    blob = z.read(main[0])
    names = sorted({m.decode("ascii", "replace") for m in ROMNAME.findall(blob)})[:12]
    hints = [h.decode() for h in HINTS if h.lower() in blob.lower()]
    out[port_id] = {"exe": main[0].filename, "romNames": names, "hints": hints, "files": [i.filename for i in z.infolist()][:30]}
    print(port_id, main[0].filename, names[:5], hints, flush=True)
json.dump(out, open("D:/AI/other-strings.json", "w", encoding="utf-8"), indent=1)
