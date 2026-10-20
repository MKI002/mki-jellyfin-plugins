#!/usr/bin/env python3
"""Build every plugin under src/, zip the ones whose version is not yet in
manifest.json and add them to the manifest.

Usage:
  build/package.py --check                       validate meta.json, manifest.json and the MKI kit copies
  build/package.py --tag v1.0.0 --repo OWNER/REPO build zips into dist/ and update manifest.json
"""
import argparse
import datetime
import hashlib
import json
import pathlib
import subprocess
import sys
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
DIST = ROOT / "dist"
MANIFEST = ROOT / "manifest.json"
REQUIRED = ("guid", "name", "overview", "description", "owner", "category", "targetAbi", "version", "changelog")


def plugins():
    for meta_path in sorted(SRC.glob("*/meta.json")):
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        missing = [k for k in REQUIRED if not meta.get(k)]
        if missing:
            sys.exit(f"{meta_path}: missing {', '.join(missing)}")
        yield meta_path.parent, meta


def load_manifest():
    return json.loads(MANIFEST.read_text(encoding="utf-8")) if MANIFEST.exists() else []


def build(folder, meta):
    project = next(folder.glob("*.csproj"))
    out = folder / "bin" / "package"
    subprocess.run(
        ["dotnet", "build", str(project), "-c", "Release", "-o", str(out), f"-p:Version={meta['version']}"],
        check=True,
    )
    return out / f"{project.stem}.dll"


def package(folder, meta, tag, repo, timestamp):
    dll = build(folder, meta)
    slug = folder.name.removeprefix("Jellyfin.Plugin.").lower()
    DIST.mkdir(exist_ok=True)
    zip_path = DIST / f"{slug}_{meta['version']}.zip"
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(dll, dll.name)
        z.writestr("meta.json", json.dumps({**meta, "timestamp": timestamp}, indent=2, ensure_ascii=False))
        logo = folder / "logo.png"
        if logo.exists():
            z.write(logo, "logo.png")
    version = {
        "version": meta["version"],
        "changelog": meta["changelog"],
        "targetAbi": meta["targetAbi"],
        "sourceUrl": f"https://github.com/{repo}/releases/download/{tag}/{zip_path.name}",
        "checksum": hashlib.md5(zip_path.read_bytes()).hexdigest(),
        "timestamp": timestamp,
    }
    return zip_path, version


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--tag")
    parser.add_argument("--repo")
    args = parser.parse_args()

    manifest = load_manifest()
    found = list(plugins())
    if args.check:
        print(f"{len(found)} plugins, {len(manifest)} manifest entries: OK")
        sys.exit(subprocess.run([sys.executable, str(ROOT / "build" / "kit.py"), "--check"]).returncode)
    if not (args.tag and args.repo):
        parser.error("--tag and --repo are required unless --check is given")

    timestamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    built = []
    for folder, meta in found:
        entry = next((p for p in manifest if p["guid"] == meta["guid"]), None)
        if entry and any(v["version"] == meta["version"] for v in entry["versions"]):
            print(f"{meta['name']} {meta['version']} already released, skipping")
            continue
        zip_path, version = package(folder, meta, args.tag, args.repo, timestamp)
        if entry is None:
            entry = {"guid": meta["guid"], "versions": []}
            manifest.append(entry)
        entry.update({
            "name": meta["name"],
            "overview": meta["overview"],
            "description": meta["description"],
            "owner": meta["owner"],
            "category": meta["category"],
            "imageUrl": f"https://raw.githubusercontent.com/{args.repo}/main/src/{folder.name}/logo.png",
        })
        entry["versions"].insert(0, version)
        built.append(zip_path)
        print(f"Packaged {zip_path.name}")

    order = ("guid", "name", "overview", "description", "owner", "category", "imageUrl", "versions")
    manifest = [{k: p[k] for k in order} for p in sorted(manifest, key=lambda p: p["name"])]
    MANIFEST.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    DIST.mkdir(exist_ok=True)
    (DIST / "files.txt").write_text("".join(f"{p}\n" for p in built), encoding="utf-8")


if __name__ == "__main__":
    main()
