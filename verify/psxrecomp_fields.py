"""Catalog fields for psxrecomp ports from D:/AI/psxrecomp-scan.json. Import fields(id) or run to print one."""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SCAN = json.load(open("D:/AI/psxrecomp-scan.json", encoding="utf-8"))
SCRIPT = open(os.path.join(HERE, "psxrecomp-setup.cmd"), encoding="utf-8").read()
PACK = {"id": "retroporting-cmake-clang", "name": "RetroPorting compiler pack 1.0.14",
        "url": "https://github.com/RetroPortingToolKit/RetroPorting-Toolchains/releases/download/v1.0.14/cmake-clang-v1-windows-x64.zip",
        "sha256": "28da9742385e7ff875b3d9311e8ed89dbdc84f27b6ecba2bc0d0acc11f6d2b4d", "sizeBytes": 209497009}
LABELS = {"SCPH1001": "SCPH-1001 (USA)", "SCPH5500": "SCPH-5500 (Japan)", "SCPH5501": "SCPH-5501 (USA)",
          "SCPH5552": "SCPH-5552 (Europe)", "SCPH7000": "SCPH-7000 (Japan)"}


def fields(port_id):
    s = SCAN[port_id]
    bios = [{"label": LABELS.get(b["stem"], b["stem"]), "sha256": b["sha256"], "sizeBytes": b["size"], "file": f"bios/{b['stem']}.BIN"}
            for b in s["bios"] if b["stem"].upper().startswith("SCPH")]
    build = s["buildDir"] or "build-release"
    return {
        "sourceTypes": [".chd", ".cue"],
        "keepSourceCopy": True,
        "bios": bios,
        "tools": [PACK],
        "defaultFiles": [{"path": "recompbox-setup.cmd", "content": SCRIPT}],
        "setup": {"run": "recompbox-setup.cmd", "args": ["{source}", "{bios}", "{tool:" + PACK["id"] + "}", s["marker"], s["exe"], build],
                  "creates": f"{build}/{s['exe']}.exe"},
        "executable": f"{build}/{s['exe']}.exe",
        "launchArgs": ["--no-launcher", "--bios", "{bios}"],
        "keep": [f"{build}/memcards", f"{build}/saves", f"{build}/settings.toml", f"{build}/input.ini", f"{build}/keybinds.ini"],
    }


if __name__ == "__main__":
    print(json.dumps(fields(sys.argv[1]), indent=1)[:3000])
