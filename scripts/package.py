#!/usr/bin/env python3
"""Package the Kale Jellyfin plugin as a Jellyfin plugin repository.

    python3 scripts/package.py --base-url https://getkale.app/jellyfin

Builds both targets (net10.0 for Jellyfin 12.x, net9.0 for 10.11), zips each DLL
with its meta.json, and writes `manifest.json` — the file a Jellyfin admin adds
under Dashboard → Plugins → Repositories. Output: dist/.

It does NOT publish anything. Hosting the manifest makes the plugin installable
by anyone who has the URL, which is a public release.
"""
import argparse, datetime, hashlib, json, os, re, subprocess, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PROJECT = os.path.join(ROOT, "src", "Jellyfin.Plugin.Kale")
DIST = os.path.join(ROOT, "dist")
GUID = "6e519877-6d61-4016-957f-10a897d9d7ce"

# Which build each Jellyfin line loads (a 10.11 server cannot load the net10.0
# build — proved on the sandbox, 2026-10-04).
TARGETS = [
    {"tfm": "net10.0", "targetAbi": "12.1.0.0", "label": "jellyfin-12"},
    {"tfm": "net9.0", "targetAbi": "10.11.0.0", "label": "jellyfin-10.11"},
]

OVERVIEW = "Easy requests and soundtrack fixes for the Kale app"
DESCRIPTION = ("Lets everyone in your household ask for films and shows from the Kale app through "
               "your Seerr, with no Seerr sign-in on each device; the Seerr API key stays on this "
               "server. Also lets an admin make a file's default soundtrack the right one, in place, "
               "with undo. Source: GPL-3.0-or-later.")


def version():
    text = open(os.path.join(PROJECT, "Jellyfin.Plugin.Kale.csproj")).read()
    return re.search(r"<AssemblyVersion>(.+?)</AssemblyVersion>", text).group(1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base-url", required=True, help="where the zips will be served from, no trailing slash")
    ap.add_argument("--changelog", default="First release: requests from Kale without a Seerr sign-in on each device; "
                                           "make a soundtrack the file's default, in place, with undo.")
    ap.add_argument("--out", default=DIST, help="output folder (default dist); the sandbox install test uses its own")
    args = ap.parse_args()
    out = os.path.abspath(args.out)

    ver = version()
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    os.makedirs(out, exist_ok=True)
    subprocess.run(["dotnet", "build", PROJECT, "-c", "Release", "-v", "quiet", "-nologo"], check=True)

    versions = []
    for t in TARGETS:
        dll = os.path.join(PROJECT, "bin", "Release", t["tfm"], "Jellyfin.Plugin.Kale.dll")
        meta = {"guid": GUID, "name": "Kale", "description": DESCRIPTION, "overview": OVERVIEW,
                "owner": "Rocket Dino", "category": "General", "version": ver,
                "targetAbi": t["targetAbi"], "changelog": args.changelog, "timestamp": stamp,
                "status": "Active", "autoUpdate": True, "imagePath": "", "assemblies": ["Jellyfin.Plugin.Kale.dll"]}
        name = f"kale-jellyfin_{ver}_{t['label']}.zip"
        path = os.path.join(out, name)
        with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
            z.write(dll, "Jellyfin.Plugin.Kale.dll")
            z.writestr("meta.json", json.dumps(meta, indent=2))
        md5 = hashlib.md5(open(path, "rb").read()).hexdigest()
        versions.append({"version": ver, "changelog": args.changelog, "targetAbi": t["targetAbi"],
                         "sourceUrl": f"{args.base_url}/{name}", "checksum": md5, "timestamp": stamp})
        print(f"{name}  md5 {md5}")

    manifest = [{"guid": GUID, "name": "Kale", "description": DESCRIPTION, "overview": OVERVIEW,
                 "owner": "Rocket Dino", "category": "General", "versions": versions}]
    with open(os.path.join(out, "manifest.json"), "w") as f:
        json.dump(manifest, f, indent=2)
    print("manifest.json written to", out, "— not published")


if __name__ == "__main__":
    main()
