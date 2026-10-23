#!/usr/bin/env python3
"""Build the settings page demos for GitHub Pages.

Every plugin's real Configuration/configPage.html (with the kit copy in it) goes into demo/page.html, next to
demo/shell.js, which stands in for Jellyfin's ApiClient, and the plugin's example data in demo/mocks/<Plugin>.js.
The pages are copied as they are, so the demos always show the current settings pages.

Usage:
  build/demo.py --out _site --fetch-fonts   build the site, with Noto Sans and Material Icons from npm (Pages workflow)
  build/demo.py --check                     build into a temporary folder and only report problems (Build workflow)
"""
import argparse
import hashlib
import html
import io
import json
import pathlib
import re
import shutil
import sys
import tarfile
import tempfile
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
DEMO = ROOT / "demo"
REPO = "https://github.com/MKI002/mki-jellyfin-plugins"
MANIFEST = "https://raw.githubusercontent.com/MKI002/mki-jellyfin-plugins/main/manifest.json"

# Fonts are fetched at build time instead of being kept in the repository. Pinned versions and checksums.
FONTS = [
    ("https://registry.npmjs.org/@fontsource/noto-sans/-/noto-sans-5.3.0.tgz",
     "f62f8ed40e378f15134e32bed2fd5718d81e9ec7a5603d1022d7b385e2297abc",
     [f"package/files/noto-sans-latin-{w}-normal.woff2" for w in (400, 500, 600, 700, 800)]),
    ("https://registry.npmjs.org/material-design-icons-iconfont/-/material-design-icons-iconfont-6.7.0.tgz",
     "854966e187be57bbbd4d7681b5ff19d7b695efa1a6823800ee4dce474e0e4853",
     ["package/dist/fonts/MaterialIcons-Regular.woff2"]),
]

# Plugin web files a settings page loads from the server, served from the demo instead: (source, published path, patch).
# Media Hero's preview draws images through bildUrl; the demo hands it drawn artwork, since there is no library.
BILDURL = "function bildUrl(item, art, breite) {\n"
STATIC = {
    "Jellyfin.Plugin.MediaHero": [
        ("Web/mediahero.js", "MediaHero/client.js",
         (BILDURL, BILDURL + "    if (window.MkiDemo && MkiDemo.image) return MkiDemo.image(item, art, breite);\n")),
        ("Web/mediahero.css", "MediaHero/client.css", None),
    ],
    "Jellyfin.Plugin.MkiPasswordPolicy": [
        ("Web/client.js", "MkiPasswordPolicy/client.js", None),
    ],
    "Jellyfin.Plugin.MkiLoginShowcase": [
        ("Web/login.js", "MkiLoginShowcase/login.js", None),
    ],
}

BODY_RE = re.compile(r"<body>\s*(.*?)\s*</body>", re.S)
PAGE_RE = re.compile(r'<div\s+id="([A-Za-z][\w-]*)"\s+data-role="page"')

problems = []


def fail(where, text):
    problems.append(f"{where}: {text}")


def plugins():
    """Plugins in the order the README lists them."""
    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    found = [p.parent for p in SRC.glob("*/meta.json")]
    return sorted(found, key=lambda p: (readme.find(f"src/{p.name}"), p.name))


def key_of(folder):
    return folder.name.removeprefix("Jellyfin.Plugin.")


def slug_of(folder):
    return re.sub(r"^mki", "", key_of(folder).lower())


def fill(template, values):
    out = template
    for k, v in values.items():
        out = out.replace("{{" + k + "}}", v)
    left = re.findall(r"\{\{[A-Z]+\}\}", out)
    if left:
        fail("demo templates", "unfilled " + ", ".join(sorted(set(left))))
    return out


def fetch_fonts(dest):
    dest.mkdir(parents=True, exist_ok=True)
    for url, sha, members in FONTS:
        with urllib.request.urlopen(url, timeout=60) as r:
            data = r.read()
        got = hashlib.sha256(data).hexdigest()
        if got != sha:
            sys.exit(f"{url}: checksum {got} does not match the pinned {sha}")
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as tar:
            for m in members:
                (dest / pathlib.Path(m).name).write_bytes(tar.extractfile(m).read())


def build(out, fonts):
    if out.exists():
        shutil.rmtree(out)
    (out / "assets" / "mocks").mkdir(parents=True)
    for name in ("shell.css", "shell.js"):
        shutil.copy(DEMO / name, out / "assets" / name)
    shutil.copy(ROOT / "assets" / "banner.png", out / "assets" / "banner.png")
    (out / ".nojekyll").write_text("", encoding="utf-8")
    if fonts:
        fetch_fonts(out / "fonts")

    page_tpl = (DEMO / "page.html").read_text(encoding="utf-8")
    index_tpl = (DEMO / "index.html").read_text(encoding="utf-8")
    entries = []
    for folder in plugins():
        meta = json.loads((folder / "meta.json").read_text(encoding="utf-8"))
        entries.append((folder, meta, slug_of(folder)))

    def nav(current):
        rows = []
        for folder, meta, slug in entries:
            here = ' aria-current="page"' if slug == current else ""
            rows.append(f'    <a class="demo-drawer__link" href="{slug}.html"{here}><img src="assets/{slug}.png" alt="">'
                        f'<span>{html.escape(meta["name"].removeprefix("MKI "))}</span></a>')
        return "\n".join(rows)

    cards = []
    for folder, meta, slug in entries:
        where = folder.relative_to(ROOT)
        mock = DEMO / "mocks" / f"{key_of(folder)}.js"
        page = folder / "Configuration" / "configPage.html"
        if not mock.is_file():
            fail(where, f"no example data for the demo: add {mock.relative_to(ROOT)}")
            continue
        text = page.read_text(encoding="utf-8")
        body = BODY_RE.search(text)
        if not body or not PAGE_RE.search(body.group(1)):
            fail(page.relative_to(ROOT), 'no <body> with a <div id=... data-role="page"> in it')
            continue
        mock_text = mock.read_text(encoding="utf-8")
        if meta["guid"] not in mock_text:
            fail(mock.relative_to(ROOT), f'does not fill in the settings of plugin {meta["guid"]} ({meta["name"]})')
        shutil.copy(mock, out / "assets" / "mocks" / mock.name)
        shutil.copy(folder / "logo.png", out / "assets" / f"{slug}.png")
        for src, dest, patch in STATIC.get(folder.name, []):
            data = (folder / src).read_text(encoding="utf-8")
            if patch:
                if data.count(patch[0]) != 1:
                    fail(f"{where}/{src}", f"demo patch point {patch[0].strip()!r} not found exactly once")
                data = data.replace(patch[0], patch[1])
            (out / dest).parent.mkdir(parents=True, exist_ok=True)
            (out / dest).write_text(data, encoding="utf-8")
        name = meta["name"]
        (out / f"{slug}.html").write_text(fill(page_tpl, {
            "NAME": html.escape(name), "LOGO": f"assets/{slug}.png", "MOCK": mock.name, "REPO": REPO,
            "README": f"{REPO}/tree/main/src/{folder.name}#readme", "NAV": nav(slug), "PAGE": body.group(1),
        }), encoding="utf-8")
        cards.append(
            f'      <a class="demo-card" href="{slug}.html"><img src="assets/{slug}.png" alt="" width="1200" height="675" loading="lazy">'
            f'<span class="demo-card__body"><b>{html.escape(name)}</b><span>{html.escape(meta["overview"])}</span>'
            f'<span class="demo-card__go">Open the settings page<span class="material-icons" aria-hidden="true">arrow_forward</span></span></span></a>')

    for mock in (DEMO / "mocks").glob("*.js"):
        if not (SRC / f"Jellyfin.Plugin.{mock.stem}").is_dir():
            fail(mock.relative_to(ROOT), "no plugin with this name in src/")
    (out / "index.html").write_text(fill(index_tpl, {"CARDS": "\n".join(cards), "REPO": REPO, "MANIFEST": MANIFEST}),
                                    encoding="utf-8")
    return len(cards)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default="_site", help="folder for the site (default: _site)")
    parser.add_argument("--fetch-fonts", action="store_true", help="download the pinned web fonts from npm")
    parser.add_argument("--check", action="store_true", help="build into a temporary folder and only report problems")
    args = parser.parse_args()
    if args.check:
        with tempfile.TemporaryDirectory() as tmp:
            count = build(pathlib.Path(tmp) / "site", False)
    else:
        count = build((ROOT / args.out) if not pathlib.Path(args.out).is_absolute() else pathlib.Path(args.out), args.fetch_fonts)
    if problems:
        print("Demo site problems:\n  " + "\n  ".join(problems))
        sys.exit(1)
    print(f"Demo site: {count} settings pages" + ("" if args.check else f" in {args.out}"))


if __name__ == "__main__":
    main()
