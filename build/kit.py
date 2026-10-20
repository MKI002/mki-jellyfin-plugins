#!/usr/bin/env python3
"""Copy the shared MKI kit (ui/mki-kit.css, ui/mki-kit.js) into every plugin settings page.

Each configPage.html carries the kit between markers:
  /* MKI-KIT:CSS BEGIN ... */ ... /* MKI-KIT:CSS END */   inside the page's <style>
  /* MKI-KIT:JS BEGIN ... */  ... /* MKI-KIT:JS END */    inside the page's own script
In the CSS copy ".mkiui" becomes the page's "#PageId", so pages never style each other.

Usage:
  build/kit.py --write   update every page from ui/
  build/kit.py --check   fail if a page's copy differs from ui/ (run by build/package.py --check in CI)
"""
import argparse
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
UI = ROOT / "ui"
PAGES = sorted((ROOT / "src").glob("*/Configuration/configPage.html"))
CSS_RE = re.compile(r"(/\* MKI-KIT:CSS BEGIN[^*]*\*/\n)(.*?)(/\* MKI-KIT:CSS END \*/)", re.S)
JS_RE = re.compile(r"(/\* MKI-KIT:JS BEGIN[^*]*\*/\n)(.*?)(/\* MKI-KIT:JS END \*/)", re.S)
ID_RE = re.compile(r'<div\s+id="([A-Za-z][\w-]*)"\s+data-role="page"')


def kit_css(page_id):
    css = (UI / "mki-kit.css").read_text(encoding="utf-8")
    css = re.sub(r"\A/\*.*?\*/\s*", "", css, count=1, flags=re.S)  # the header comment stays in ui/
    return css.replace(".mkiui", "#" + page_id)


def kit_js():
    return (UI / "mki-kit.js").read_text(encoding="utf-8")


def render(text, path):
    m = ID_RE.search(text)
    if not m:
        sys.exit(f"{path}: no <div id=... data-role=\"page\"> found")
    for name, rx in (("CSS", CSS_RE), ("JS", JS_RE)):
        if len(rx.findall(text)) != 1:
            sys.exit(f"{path}: expected exactly one MKI-KIT:{name} block")
    text = CSS_RE.sub(lambda x: x.group(1) + kit_css(m.group(1)) + x.group(3), text)
    return JS_RE.sub(lambda x: x.group(1) + kit_js() + x.group(3), text)


def check():
    stale = [p for p in PAGES if render(p.read_text(encoding="utf-8"), p) != p.read_text(encoding="utf-8")]
    if stale:
        names = ", ".join(str(p.relative_to(ROOT)) for p in stale)
        sys.exit(f"MKI kit out of date in {names}. Run: python3 build/kit.py --write")
    print(f"MKI kit: {len(PAGES)} pages up to date")


def write():
    for p in PAGES:
        old = p.read_text(encoding="utf-8")
        if "MKI-KIT:CSS BEGIN" not in old:
            print(f"skipped {p.relative_to(ROOT)} (no MKI-KIT markers)")
            continue
        new = render(old, p)
        if new != old:
            p.write_text(new, encoding="utf-8")
            print(f"updated {p.relative_to(ROOT)}")


def main():
    parser = argparse.ArgumentParser()
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--write", action="store_true")
    group.add_argument("--check", action="store_true")
    args = parser.parse_args()
    write() if args.write else check()


if __name__ == "__main__":
    main()
