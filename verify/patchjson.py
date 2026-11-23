"""Set fields on entries in machine-written catalog source files (sources/discovered/*.json), keeping their format.

usage: python -I patchjson.py <catalog repo> <spec.json>
spec.json: {"sources/discovered/nintendo-64.json": {"port-id": {"field": value, ...}}}
"""
import json
import sys

root, spec_path = sys.argv[1], sys.argv[2]
spec = json.load(open(spec_path, encoding="utf-8"))
for rel, ports in spec.items():
    path = f"{root}/{rel}"
    raw = open(path, encoding="utf-8", newline="").read()
    crlf = "\r\n" in raw
    entries = json.loads(raw)
    by_id = {e["id"]: e for e in entries}
    for port_id, fields in ports.items():
        by_id[port_id].update(fields)
    text = json.dumps(entries, indent=2, ensure_ascii=False) + "\n"
    open(path, "w", encoding="utf-8", newline="").write(text.replace("\n", "\r\n") if crlf else text)
    print("patched", rel, ", ".join(ports))
