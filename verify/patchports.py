"""Add or replace fields on hand-formatted catalog source entries.

usage: python -I patchports.py <catalog repo> <spec.json>
spec.json: {"sources/seed.json": {"port-id": {"field": value, ...}}, ...}
String fields that already exist are replaced in place; other fields are appended as new lines.
Existing non-string fields are replaced only when listed under "_replace".
"""
import json
import re
import sys

root, spec_path = sys.argv[1], sys.argv[2]
spec = json.load(open(spec_path, encoding="utf-8"))


def find_entry(text, port_id):
    start = text.index(f'"id": "{port_id}"')
    end = text.index("\n  }", start)
    return start, end


for rel, ports in spec.items():
    path = f"{root}/{rel}"
    text = open(path, encoding="utf-8").read()
    for port_id, fields in ports.items():
        replace = set(fields.pop("_replace", []))
        start, end = find_entry(text, port_id)
        entry = text[start:end]
        added = []
        for key, value in fields.items():
            encoded = json.dumps(value, ensure_ascii=False)
            string_field = re.search(rf'"{key}": ("(?:[^"\\]|\\.)*")', entry)
            line_field = re.search(rf'\n    "{key}": .*', entry)
            if string_field and isinstance(value, str):
                entry = entry[:string_field.start(1)] + encoded + entry[string_field.end(1):]
            elif key in replace and line_field:
                entry = entry[:line_field.start()] + f'\n    "{key}": {encoded}' + (',' if line_field.group(0).rstrip().endswith(',') else '') + entry[line_field.end():]
            elif f'"{key}":' in entry:
                raise SystemExit(f"{port_id} already has {key}; list it in _replace")
            else:
                added.append(f'    "{key}": {encoded}')
        if added:
            entry = entry.rstrip() + ",\n" + ",\n".join(added)
        text = text[:start] + entry + text[end:]
    json.loads(text)
    open(path, "w", encoding="utf-8").write(text)
    print("patched", rel, ", ".join(ports))
