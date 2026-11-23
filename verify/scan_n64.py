"""Sort the owned N64 ports by how RecompBox could set them up.

Reads D:/AI/owned-ports.txt and the test catalog, hashes the player's ROM, and looks in each repo for an
N64ModernRuntime game entry (game_id) or a Torch config. Writes D:/AI/n64-scan.json.
"""
import base64
import hashlib
import json
import re
import subprocess
import zipfile

DONE = {"perfect-dark", "2ship2harkinian", "sm64coopdx", "mariokart64recomp", "goemon64recomp", "bm64recomp",
        "ghostship", "paperboat", "lighthouse", "battleship"}


def gh(path):
    r = subprocess.run(["gh", "api", path], capture_output=True, text=True, encoding="utf-8")
    return json.loads(r.stdout) if r.returncode == 0 and r.stdout.strip() else None


def raw(repo, path):
    data = gh(f"repos/{repo}/contents/{path}")
    if not data or "content" not in data:
        return None
    return base64.b64decode(data["content"]).decode("utf-8", "replace")


def rom_sha1(path):
    if path.lower().endswith(".zip"):
        with zipfile.ZipFile(path) as z:
            entry = max(z.infolist(), key=lambda e: e.file_size)
            data = z.read(entry)
    else:
        data = open(path, "rb").read()
    magic = data[:4].hex()
    if magic == "37804012":
        data = bytes(b for pair in zip(data[1::2], data[0::2]) for b in pair)
    elif magic == "40123780":
        data = b"".join(data[i:i + 4][::-1] for i in range(0, len(data), 4))
    return hashlib.sha1(data).hexdigest(), len(data), magic


catalog = {p["id"]: p for p in json.load(open("D:/AI/recompbox-picktest-catalog.json", encoding="utf-8"))["ports"]}
owned = {}
for line in open("D:/AI/owned-ports.txt", encoding="utf-8"):
    m = re.match(r"(\S+) \((\d+), (\w+)\): Nintendo 64 / (.*?) -> \w+ \| (.*)", line.strip())
    if m and m.group(3) == "Untested" and m.group(1) not in DONE:
        owned.setdefault(m.group(1), (m.group(4), m.group(5)))

results = []
for port_id, (title, rom) in owned.items():
    port = catalog.get(port_id)
    if not port:
        continue
    repo = re.sub(r"^https://github.com/", "", port["repositoryUrl"]).strip("/")
    entry = {"id": port_id, "title": title, "rom": rom, "repo": repo, "stars": port.get("stars", 0),
             "assets": [a["pattern"] for a in port["assets"]], "version": port["releases"][0]["version"] if port["releases"] else ""}
    try:
        entry["sha1"], entry["size"], entry["magic"] = rom_sha1(rom)
    except Exception as e:  # noqa: BLE001 - report and keep scanning
        entry["romError"] = str(e)
    main = raw(repo, "src/main/main.cpp")
    if main and "game_id" in main:
        ids = re.findall(r'\.game_id\s*=\s*u8?"([^"]+)"', main)
        entry["kind"] = "n64recomp"
        entry["gameIds"] = ids
        config = raw(repo, "src/game/config.cpp") or ""
        entry["portable"] = "portable.txt" in config
    elif raw(repo, "config.yml") is not None:
        entry["kind"] = "torch"
    else:
        entry["kind"] = "other"
    results.append(entry)
    print(port_id, entry["kind"], entry.get("gameIds", ""), entry.get("sha1", entry.get("romError")))

json.dump(results, open("D:/AI/n64-scan.json", "w", encoding="utf-8"), indent=1)
