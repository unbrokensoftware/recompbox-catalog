"""Owned PS1 ports built on the psxrecomp kit: read each release zip for what a one-click setup needs.

Writes D:/AI/psxrecomp-scan.json: id -> {marker, exe, buildDir, bios: [{stem, sha256, size}], openbios, asset, rom}.
Nothing downloaded is run.
"""
import fnmatch
import io
import json
import re
import subprocess
import zipfile

catalog = {p["id"]: p for p in json.load(open("D:/AI/recompbox-picktest-catalog.json", encoding="utf-8"))["ports"]}
owned = {}
for line in open("D:/AI/owned-ports.txt", encoding="utf-8"):
    m = re.match(r"(\S+) \((\d+), (\w+)\): Sony Playstation / (.*?) -> \w+ \| (.*)", line.strip())
    if m and m.group(3) == "Untested":
        owned.setdefault(m.group(1), m.group(5))

out = {}
for port_id, rom in owned.items():
    port = catalog.get(port_id)
    if not port or not port.get("releases"):
        continue
    repo = port["repositoryUrl"].replace("https://github.com/", "").strip("/")
    tree = subprocess.run(["gh", "api", f"repos/{repo}/git/trees/HEAD?recursive=1", "-q", ".tree[].path"], capture_output=True, text=True, encoding="utf-8")
    paths = tree.stdout.split()
    if "codegen_setup.c" not in paths and not any(p.endswith("psxrecomp_cli.py") for p in paths) and ".gitmodules" not in paths:
        continue
    rel = subprocess.run(["gh", "api", f"repos/{repo}/releases/tags/{port['releases'][0]['version']}"], capture_output=True, text=True, encoding="utf-8")
    if rel.returncode != 0:
        continue
    pattern = port["assets"][0]["pattern"]
    asset = next((a for a in json.loads(rel.stdout).get("assets", []) if fnmatch.fnmatch(a["name"], pattern)), None)
    if not asset or not asset["name"].endswith(".zip"):
        continue
    data = subprocess.run(["curl", "-sL", asset["browser_download_url"]], capture_output=True, timeout=900).stdout
    try:
        z = zipfile.ZipFile(io.BytesIO(data))
    except zipfile.BadZipFile:
        continue
    names = z.namelist()
    root = names[0].split("/")[0] + "/" if all(n.startswith(names[0].split("/")[0] + "/") for n in names) else ""
    if root + "psxrecomp/psxrecomp_cli.py" not in names or root + "codegen_setup.c" not in names:
        continue
    setup = z.read(root + "codegen_setup.c").decode("utf-8", "replace")
    marker = re.search(r'gen_marker_relpath\s*=\s*"([^"]+)"', setup)
    exe = re.search(r'exe_basename\s*=\s*"([^"]+)"', setup)
    build = re.search(r'build_dir_name\s*=\s*"([^"]+)"', setup)
    bios = []
    for n in names:
        if re.match(re.escape(root) + r"(psxrecomp/)?bios/[^/]+\.toml$", n):
            t = z.read(n).decode("utf-8", "replace")
            sha = re.search(r'sha256\s*=\s*"([0-9a-fA-F]{64})"', t)
            size = re.search(r'size\s*=\s*(\d+)', t)
            if sha:
                bios.append({"stem": n.rsplit("/", 1)[1][:-5], "sha256": sha.group(1).lower(), "size": int(size.group(1)) if size else 524288})
    game = z.read(root + "game.toml").decode("utf-8", "replace") if root + "game.toml" in names else ""
    openbios = re.search(r"openbios\s*=\s*(true|false)", game)
    out[port_id] = {"repo": repo, "asset": asset["name"], "rom": rom, "marker": marker.group(1).split("/")[-1] if marker else None,
                    "exe": exe.group(1) if exe else None, "buildDir": build.group(1) if build else "build-release", "bios": bios,
                    "openbios": openbios.group(1) if openbios else None, "root": root}
    print(port_id, out[port_id]["exe"], out[port_id]["marker"], [b["stem"] for b in bios], out[port_id]["openbios"], flush=True)
    json.dump(out, open("D:/AI/psxrecomp-scan.json", "w", encoding="utf-8"), indent=1)
print(len(out), "psxrecomp ports")
