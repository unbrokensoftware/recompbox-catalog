"""For scanned ports of kind "other": download the Windows release the catalog picks and record what is in it.

Writes D:/AI/other-inspect.json with the top-level files, exe names, archives (.o2r/.otr), config.yml, readme text.
Downloads go to D:/AI/other-dl/<id>/ (zip only, never run).
"""
import fnmatch
import io
import json
import os
import re
import subprocess
import sys
import zipfile

SCAN = sys.argv[1] if len(sys.argv) > 1 else "D:/AI/n64-scan.json"
OUT = sys.argv[2] if len(sys.argv) > 2 else "D:/AI/other-inspect.json"
catalog = {p["id"]: p for p in json.load(open("D:/AI/recompbox-picktest-catalog.json", encoding="utf-8"))["ports"]}
results = json.load(open(OUT, encoding="utf-8")) if os.path.exists(OUT) else {}


def release_assets(repo, version):
    r = subprocess.run(["gh", "api", f"repos/{repo}/releases/tags/{version}"], capture_output=True, text=True, encoding="utf-8")
    if r.returncode != 0:
        return []
    return [(a["name"], a["browser_download_url"], a["size"]) for a in json.loads(r.stdout).get("assets", [])]


for entry in json.load(open(SCAN, encoding="utf-8")):
    if entry["kind"] != "other" or entry["id"] in results:
        continue
    port = catalog.get(entry["id"])
    info = {"repo": entry["repo"], "title": entry["title"]}
    try:
        version = port["releases"][0]["version"] if port and port.get("releases") else ""
        assets = release_assets(entry["repo"], version) if version else []
        pattern = next((a["pattern"] for a in port["assets"]), "") if port else ""
        chosen = next((a for a in assets if fnmatch.fnmatch(a[0], pattern)), None)
        if not chosen:
            info["why"] = f"no asset for {pattern} in {version}"
        elif chosen[2] > 400 * 1024 * 1024:
            info["why"] = f"asset too big to inspect ({chosen[2]})"
        else:
            data = subprocess.run(["curl", "-sL", chosen[1]], capture_output=True, timeout=600).stdout
            info["asset"] = chosen[0]
            if chosen[0].lower().endswith(".zip"):
                z = zipfile.ZipFile(io.BytesIO(data))
                names = [i.filename for i in z.infolist()]
                root = os.path.commonprefix(names) if len(names) > 1 else ""
                root = root[:root.rfind("/") + 1] if "/" in root else ""
                rel = [n[len(root):] for n in names]
                info["top"] = sorted({n.split("/")[0] + ("/" if "/" in n else "") for n in rel if n})[:40]
                info["exes"] = [n for n in rel if n.lower().endswith(".exe")]
                info["archives"] = [n for n in rel if n.lower().endswith((".o2r", ".otr"))]
                for n in rel:
                    low = n.lower()
                    if low in ("config.yml", "readme.txt", "readme.md") or low.endswith("/readme.txt"):
                        text = z.read(root + n).decode("utf-8", "replace")
                        info[low] = text[:1500] if "readme" in low else "\n".join(
                            l for l in text.splitlines() if re.match(r"^[0-9a-f]{40}:", l) or "binary:" in l)
            else:
                info["why"] = "not a zip"
    except Exception as e:  # noqa: BLE001 - record and continue
        info["why"] = f"error {e}"
    results[entry["id"]] = info
    print(entry["id"], info.get("why") or (info.get("exes"), info.get("archives")), flush=True)
    json.dump(results, open(OUT, "w", encoding="utf-8"), indent=1)
