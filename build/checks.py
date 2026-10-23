#!/usr/bin/env python3
"""Release gates that run in CI on every pull request and again before every release.

Checks:
  plugins    every plugin under src/ has a valid meta.json, an "MKI " name, a 1200x675 logo.png, a README.md,
             a settings page built on the MKI kit, is in the solution, and is named in the README
  english    no German text anywhere in src/, ui/ or the README (all plugins are English only)
  private    no personal data: LAN addresses, home folders, e-mail addresses, server names, API keys
  security   no unescaped HTML from data, no eval, outbound HTTP clients without redirects/cookies/size cap,
             and every anonymous endpoint and request middleware listed in build/security-baseline.json
  versions   (--base REF) every plugin changed since REF has a new, higher version and a new changelog
  checklist  (--pr-body FILE) the pull request ticks every item of the release checklist (see RELEASING.md)

Usage:
  build/checks.py                          static checks
  build/checks.py --base origin/main       also the version check against that ref
  build/checks.py --pr-body body.md        also the checklist in a pull request description
"""
import argparse
import json
import pathlib
import re
import struct
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
TEXT = (".cs", ".js", ".html", ".css", ".json", ".md", ".py", ".yml", ".csproj")
BASELINE = ROOT / "build" / "security-baseline.json"
LOGO_SIZE = (1200, 675)

errors = []


def fail(where, message):
    errors.append(f"{where}: {message}")


def rel(path):
    return str(path.relative_to(ROOT))


def tracked_files():
    out = subprocess.run(["git", "ls-files"], cwd=ROOT, capture_output=True, text=True, check=True).stdout
    return [ROOT / f for f in out.splitlines() if f.endswith(TEXT) and (ROOT / f).is_file()]


def plugin_dirs():
    return sorted(p.parent for p in SRC.glob("*/meta.json"))


def png_size(path):
    head = path.read_bytes()[:24]
    if head[:8] != b"\x89PNG\r\n\x1a\n":
        return None
    return struct.unpack(">II", head[16:24])


# ---------------------------------------------------------------- plugins

def check_plugins():
    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    banner = re.search(r'<img src="assets/banner\.png" alt="([^"]*)"', readme)
    solution = (ROOT / "MkiJellyfinPlugins.sln").read_text(encoding="utf-8")
    guids = {}
    for folder in plugin_dirs():
        where = rel(folder)
        meta = json.loads((folder / "meta.json").read_text(encoding="utf-8"))
        name = meta.get("name", "")
        if not name.startswith("MKI "):
            fail(where, f'display name "{name}" must start with "MKI "')
        if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", meta.get("version", "")):
            fail(where, f'version "{meta.get("version")}" must look like 1.2.3.0')
        if meta.get("guid") in guids:
            fail(where, f"guid already used by {guids[meta['guid']]}")
        guids[meta.get("guid")] = where
        logo = folder / "logo.png"
        if not logo.exists():
            fail(where, "logo.png is missing")
        elif png_size(logo) != LOGO_SIZE:
            fail(where, f"logo.png is {png_size(logo)}, expected {LOGO_SIZE} (neon NCS-grid style, see RELEASING.md)")
        if not (folder / "README.md").exists():
            fail(where, "README.md is missing")
        page = folder / "Configuration" / "configPage.html"
        if not page.exists():
            fail(where, "Configuration/configPage.html is missing")
        elif "MKI-KIT:CSS BEGIN" not in page.read_text(encoding="utf-8"):
            fail(rel(page), "settings page does not use the MKI kit (add the MKI-KIT markers, run build/kit.py --write)")
        for project in folder.glob("*.csproj"):
            if project.name not in solution:
                fail(where, f"{project.name} is not in MkiJellyfinPlugins.sln, so CI does not build it")
        short = name.removeprefix("MKI ")
        if short not in readme:
            fail("README.md", f'does not mention "{name}"')
    if not (ROOT / "assets" / "banner.png").exists():
        fail("assets/banner.png", "missing")
    if not banner or not banner.group(1).strip():
        fail("README.md", "the banner image needs alt text")


# ---------------------------------------------------------------- english

# Words that only occur in German sentences. Two of them on one line, or any umlaut, means German text.
GERMAN = set("""und nicht ist mit für fuer oder wird werden wurde eine einen einer der auf bitte noch jetzt keine kein
sind auch wenn dann nur über ueber sie ich wir zum zur beim""".split())
# Single German words that would only ever be UI labels.
GERMAN_LABELS = set("""einstellungen speichern gespeichert abbrechen löschen loeschen hinzufügen hinzufuegen zurück
zurueck schließen schliessen benutzer anmelden abmelden suche suchen fehler warnung erfolgreich""".split())
# German-named wire values and data that stay as they are: changing them would break saved data or the API.
GERMAN_ALLOWED = {
    "fehler": "status key passed between seerr.js and its strings table",
    "benutzer": "structured log placeholder name {Benutzer}",
}


def check_english():
    files = [f for f in tracked_files() if f.is_relative_to(SRC) or f.is_relative_to(ROOT / "ui") or f.name == "README.md"]
    literal = re.compile(r"'(?:[^'\\\n]|\\.)*'|\"(?:[^\"\\\n]|\\.)*\"|`[^`]*`")
    for path in files:
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            where = f"{rel(path)}:{number}"
            if re.search("[äöüÄÖÜß]", line):
                fail(where, "German character (all plugins are English only)")
                continue
            words = {w.lower() for w in re.findall(r"[A-Za-z]+", line)}
            hits = words & GERMAN
            if len(hits) >= 2:
                fail(where, f"looks like German text ({', '.join(sorted(hits))})")
                continue
            for lit in literal.findall(line):
                for word in re.findall(r"[A-Za-z]+", lit):
                    w = word.lower()
                    if w in GERMAN_LABELS and w not in GERMAN_ALLOWED:
                        fail(where, f'German label "{word}" in a string')


# ---------------------------------------------------------------- private

PRIVATE = [
    (re.compile(r"\b(?:10|192\.168|172\.(?:1[6-9]|2\d|3[01]))(?:\.\d{1,3}){2,3}\b"), "LAN address"),
    (re.compile(r"\b100\.(?:6[4-9]|[7-9]\d|1[01]\d|12[0-7])(?:\.\d{1,3}){2}\b"), "Tailscale/CGNAT address"),
    (re.compile(r"[A-Za-z]:\\\\?Users\\\\?[A-Za-z]|/home/[a-z][\w.-]*/|/Users/[a-z][\w.-]*/|/mnt/(?:user|project)"), "personal folder path"),
    (re.compile(r"[\w.%+-]+@(?![\w.-]*(?:example\.(?:com|org)|users\.noreply\.github\.com|anthropic\.com))[\w-]+\.[\w.-]+"), "e-mail address"),
    (re.compile(r"\b[\w-]+\.(?:lan|fritz\.box|home\.arpa|ts\.net|duckdns\.org)\b"), "server name"),
    (re.compile(r"\b(?!(?:seerr|jellyfin|server|example)\.local\b)[\w-]+\.local\b"), "server name"),
    (re.compile(r"\b[0-9a-f]{32}\b"), "API key or token"),
    (re.compile(r"(?i)(?:api_?key|token|secret|password)\s*[:=]\s*[\"'][A-Za-z0-9+/_-]{16,}[\"']"), "hard-coded secret"),
]


def check_private():
    for path in tracked_files():
        if path.name in ("manifest.json",) or path == pathlib.Path(__file__).resolve():
            continue  # manifest.json holds md5 checksums; this file holds the patterns
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            for pattern, label in PRIVATE:
                m = pattern.search(line)
                if m:
                    fail(f"{rel(path)}:{number}", f'{label} "{m.group(0)}" (keep personal data out of the repo)')


# ---------------------------------------------------------------- security

def check_security():
    baseline = json.loads(BASELINE.read_text(encoding="utf-8"))
    anonymous, filters, mvc, mvcSetup = [], [], [], []
    for path in tracked_files():
        if not path.is_relative_to(SRC):
            continue
        text = path.read_text(encoding="utf-8")
        where = rel(path)
        if path.suffix in (".js", ".html"):
            # innerHTML and friends only with fixed markup made of string literals; data goes in via textContent.
            for m in re.finditer(r"\.(innerHTML|outerHTML)\s*\+?=\s*([^;]*);|insertAdjacentHTML\(([^;]*)\);", text):
                value = m.group(2) if m.group(2) is not None else m.group(3).split(",", 1)[-1]
                rest = re.sub(r"'(?:[^'\\]|\\.)*'|\"(?:[^\"\\]|\\.)*\"|`[^`$]*`|[+()\s]", "", value)
                line = text.count("\n", 0, m.start()) + 1
                if rest and not re.match(r"\s*(?://|\*)", text.splitlines()[line - 1]):
                    fail(f"{where}:{line}", "HTML built from data; use textContent or createElement instead")
            for m in re.finditer(r"\beval\s*\(|\bnew\s+Function\s*\(|document\.write\s*\(", text):
                fail(f"{where}:{text.count(chr(10), 0, m.start()) + 1}", f'"{m.group(0)}" is not allowed')
        if path.suffix == ".cs":
            for m in re.finditer(r"new\s+HttpClient\s*\(", text):
                line = text.count("\n", 0, m.start()) + 1
                block = text[text.rfind("\n", 0, m.start()):m.start() + 600]
                head = text[text.rfind("\n", 0, m.start()):m.start()]
                for need in ("AllowAutoRedirect = false", "UseCookies = false", "MaxResponseContentBufferSize"):
                    if need not in block:
                        fail(f"{where}:{line}", f"outbound HttpClient needs {need}")
                if "static readonly" not in head:
                    fail(f"{where}:{line}", "outbound HttpClient must be one static readonly instance")
            for m in re.finditer(r"\[AllowAnonymous\][^{;]*?\b(\w+)\s*\(", text, re.S):
                anonymous.append(f"{where}::{m.group(1)}")
            for m in re.finditer(r"class\s+(\w+)\s*:[^{]*\bIStartupFilter\b", text):
                filters.append(f"{where}::{m.group(1)}")
            # MVC filters and conventions run around Jellyfin's own API actions and can change their arguments and
            # results: the classes, and every place that adds one to MVC.
            for m in re.finditer(r"class\s+(\w+)(?:\s*<[^>{]*>)?\s*:[^{]*\b(?:I(?:Async)?(?:AlwaysRun)?(?:Action|Result|Resource"
                                 r"|Authorization|Exception|Page)Filter|IFilterFactory|(?:Action|Result|Exception)FilterAttribute"
                                 r"|I\w*ModelConvention)\b", text):
                mvc.append(f"{where}::{m.group(1)}")
            for m in re.finditer(r"\b(?:Post)?Configure\s*<\s*MvcOptions\s*>|\bAddMvcOptions\b|\b(?:Filters|Conventions)\.Add\w*(?:\s*<[^>]*>)?", text):
                call = re.sub(r"\s+", "", m.group(0))
                mvcSetup.append(f"{where}::{call}")
    for kind, found in (("anonymousEndpoints", anonymous), ("requestMiddleware", filters), ("mvcFilters", mvc),
                        ("mvcSetup", mvcSetup)):
        known = set(baseline.get(kind, []))
        for item in sorted(set(found) - known):
            fail("build/security-baseline.json", f"new {kind} entry {item} needs a security review "
                 "(RELEASING.md step 3), then add it to the baseline")
        for item in sorted(known - set(found)):
            fail("build/security-baseline.json", f"{kind} entry {item} no longer exists, remove it")


# ---------------------------------------------------------------- versions

def git(*args):
    return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, check=True).stdout


def as_tuple(version):
    return tuple(int(x) for x in version.split("."))


def check_versions(base):
    manifest = json.loads((ROOT / "manifest.json").read_text(encoding="utf-8"))
    changed = git("diff", "--name-only", f"{base}...HEAD").splitlines()
    kit_changed = any(f.startswith("ui/") for f in changed)
    for folder in plugin_dirs():
        prefix = rel(folder) + "/"
        code = [f for f in changed if f.startswith(prefix) and not f.endswith(".md")]
        if kit_changed and (folder / "Configuration" / "configPage.html").exists():
            code.append("ui/ (shared kit)")
        if not code:
            continue
        meta = json.loads((folder / "meta.json").read_text(encoding="utf-8"))
        entry = next((p for p in manifest if p["guid"] == meta["guid"]), None)
        if entry is None or not entry["versions"]:
            continue  # a new plugin: its first version is new by definition
        latest = max(entry["versions"], key=lambda v: as_tuple(v["version"]))
        if as_tuple(meta["version"]) <= as_tuple(latest["version"]):
            fail(prefix + "meta.json", f"files changed ({code[0]}{' ...' if len(code) > 1 else ''}) but version "
                 f"{meta['version']} is not higher than released {latest['version']}: bump it")
        elif meta["changelog"].strip() == latest["changelog"].strip():
            fail(prefix + "meta.json", "changelog is still the one from the last release: describe this version")


# ---------------------------------------------------------------- checklist

def check_checklist(body_path):
    body = pathlib.Path(body_path).read_text(encoding="utf-8")
    items = re.findall(r"^\s*- \[( |x|X)\] (.+)$", body, re.M)
    expected = re.findall(r"^\s*- \[ \] (.+)$", (ROOT / ".github" / "pull_request_template.md").read_text(encoding="utf-8"), re.M)
    ticked = {text.split(":")[0].strip() for mark, text in items if mark in "xX" or re.search(r"\bn/a\b", text, re.I)}
    for item in expected:
        key = item.split(":")[0].strip()
        if key not in ticked:
            fail("pull request description", f'release checklist item not done: "{key}" (tick it, or write n/a and why)')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", help="git ref to compare against for the version check")
    parser.add_argument("--pr-body", help="file holding the pull request description")
    args = parser.parse_args()
    steps = [("plugins", check_plugins), ("english", check_english), ("private", check_private),
             ("security", check_security)]
    if args.base:
        steps.append(("versions", lambda: check_versions(args.base)))
    if args.pr_body:
        steps.append(("checklist", lambda: check_checklist(args.pr_body)))
    for name, step in steps:
        before = len(errors)
        step()
        print(f"{name:10} {'OK' if len(errors) == before else f'{len(errors) - before} problem(s)'}")
    if errors:
        print("\n" + "\n".join(errors))
        sys.exit(1)


if __name__ == "__main__":
    main()
