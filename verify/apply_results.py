"""Write passed trial results into the catalog sources (marked verified) and into the test catalog worktree.

usage: python -I apply_results.py <results.json> [<results.json> ...]
Each results file maps port id -> {"ok": bool, "fields": {...}}.
"""
import glob
import json
import os
import sys

REPO = r"D:\Source\recompbox-catalog"
TEST = r"D:\Source\recompbox-catalog-test"

passed = {}
for name in sys.argv[1:]:
    for port_id, result in json.load(open(name, encoding="utf-8")).items():
        if result.get("ok"):
            passed[port_id] = dict(result["fields"], verified=True)

remaining = dict(passed)
files = [os.path.join(REPO, "sources", "seed.json"), os.path.join(REPO, "sources", "curated.json")]
for path in glob.glob(os.path.join(REPO, "sources", "discovered", "*.json")):
    raw = open(path, encoding="utf-8", newline="").read()
    entries = json.loads(raw)
    changed = [e["id"] for e in entries if e["id"] in remaining]
    if not changed:
        continue
    for entry in entries:
        if entry["id"] in remaining:
            entry.update(remaining.pop(entry["id"]))
    text = json.dumps(entries, indent=2, ensure_ascii=False) + "\n"
    open(path, "w", encoding="utf-8", newline="").write(text.replace("\n", "\r\n") if "\r\n" in raw else text)
    print(os.path.basename(path), len(changed), "ports")
if remaining:
    print("not in discovered sources (patch by hand):", ", ".join(remaining))

test_path = os.path.join(TEST, "catalog.json")
raw = open(test_path, encoding="utf-8", newline="").read()
catalog = json.loads(raw)
count = 0
for port in catalog["ports"]:
    if port["id"] in passed:
        port.update(passed[port["id"]])
        count += 1
open(test_path, "w", encoding="utf-8", newline="").write(json.dumps(catalog, indent=2, ensure_ascii=False).replace("\n", "\r\n"))
print("test catalog:", count, "ports;", sum(1 for p in catalog["ports"] if p.get("verified")), "verified in total")
