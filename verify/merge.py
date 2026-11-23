import glob
import json
import os

CACHE = os.path.expandvars(r"%LOCALAPPDATA%\LaunchBox\SM64EnhancedWindowsBuilder\Catalog\catalog.json")
REPO = r"D:\Source\recompbox-catalog"
FIELDS = ("roms", "romFile", "executable", "launchArgs", "keep", "defaultFiles", "setup", "sourceTypes", "saves",
          "modsFolder", "portableFile", "modOrderFile", "requirement", "verified")

catalog = json.load(open(CACHE, encoding="utf-8"))
sources = {}
files = [os.path.join(REPO, "sources", "seed.json"), os.path.join(REPO, "sources", "curated.json")]
files += glob.glob(os.path.join(REPO, "sources", "discovered", "*.json"))
for name in files:
    data = json.load(open(name, encoding="utf-8"))
    for port in data if isinstance(data, list) else data.get("ports", []):
        if isinstance(port, dict) and "id" in port:
            sources[port["id"]] = port
for port in catalog["ports"]:
    source = sources.get(port["id"])
    if source:
        port.update({k: v for k, v in source.items() if k in FIELDS})
json.dump(catalog, open(r"D:\AI\recompbox-picktest-catalog.json", "w", encoding="utf-8"))
print("merged", len(sources), "source entries")
