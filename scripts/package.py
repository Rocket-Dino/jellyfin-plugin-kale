#!/usr/bin/env python3
"""Package the Kale Jellyfin plugin as a Jellyfin plugin repository.

    python3 scripts/package.py --base-url https://getkale.app/jellyfin   # → dist/
    python3 scripts/package.py --site <kale-site checkout> --channel beta --changelog "…"

With --site (what release.sh does) the zips go to the site's public/jellyfin/, shared by both
channels, and the channel's manifest (beta: public/jellyfin/beta/manifest.json) gets this version
merged in. It refuses a version that isn't higher than every one the channel already offers, and
one whose zips already exist (re-packaging changes the checksum under a manifest that names it).
--keep N keeps that many versions in the manifest; the default 1 is deliberate (README "Rolling
back": offering an older version invites a downgrade that breaks the plugin on Jellyfin 12.1).

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

# ⚠ Kale Share (invitations) is TestFlight-only: name it here, in Plugin.Description and on
# getkale.app/jellyfin only once it ships in the App Store version.
OVERVIEW = "Easy requests and soundtrack fixes for the Kale app"
DESCRIPTION = ("Optional extras for the Kale app; Kale works without it. Everyone in your household can "
               "ask for films and shows through your Seerr, with no Seerr sign-in on each device; the Seerr "
               "API key stays on this server. An admin can make a file's default soundtrack the right one, "
               "in place, with undo. Source: GPL-3.0-or-later.")


def vkey(v):
    return tuple(int(x) for x in v.split("."))


def version():
    text = open(os.path.join(PROJECT, "Jellyfin.Plugin.Kale.csproj")).read()
    return re.search(r"<AssemblyVersion>(.+?)</AssemblyVersion>", text).group(1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base-url", help="where the zips will be served from, no trailing slash "
                                       "(default with --site: https://getkale.app/jellyfin)")
    ap.add_argument("--site", help="a kale-site checkout: write zips + the channel's manifest into it")
    ap.add_argument("--channel", choices=["beta", "stable"], default="beta", help="with --site (default beta)")
    ap.add_argument("--keep", type=int, default=1, help="with --site: versions the manifest keeps (default 1)")
    ap.add_argument("--changelog", help="what changed, in a sentence or two (required with --site)")
    ap.add_argument("--out", default=DIST, help="output folder (default dist); the sandbox install test uses its own")
    args = ap.parse_args()
    ver = version()
    manifest_path, previous = None, []
    if args.site and not (args.changelog or "").strip():
        raise SystemExit("--changelog is required for a release: it is what admins read in Jellyfin's catalogue")
    args.changelog = args.changelog or "Development build, not published."
    if args.site:
        jellyfin = os.path.join(os.path.abspath(args.site), "public", "jellyfin")
        if not os.path.isdir(jellyfin):
            raise SystemExit(f"{args.site} is not a kale-site checkout (no public/jellyfin)")
        out = jellyfin
        args.base_url = args.base_url or "https://getkale.app/jellyfin"
        manifest_path = os.path.join(jellyfin, *(["beta"] if args.channel == "beta" else []), "manifest.json")
        if os.path.exists(manifest_path):
            previous = json.load(open(manifest_path))[0]["versions"]
        offered = sorted({v["version"] for v in previous}, key=vkey)
        if offered and vkey(ver) <= vkey(offered[-1]):
            raise SystemExit(f"{ver} is not higher than {offered[-1]}, which the {args.channel} channel already offers "
                             "— bump <AssemblyVersion> in the csproj")
        clash = [t["label"] for t in TARGETS if os.path.exists(os.path.join(out, f"kale-jellyfin_{ver}_{t['label']}.zip"))]
        if clash:
            raise SystemExit(f"zips for {ver} already exist in {out} ({', '.join(clash)}): a published version is never rebuilt")
    else:
        if not args.base_url:
            raise SystemExit("--base-url is required without --site")
        out = os.path.abspath(args.out)
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

    if manifest_path:
        kept = sorted({v["version"] for v in previous}, key=vkey, reverse=True)[:max(args.keep - 1, 0)]
        versions += [v for v in previous if v["version"] in kept]
    else:
        manifest_path = os.path.join(out, "manifest.json")
    manifest = [{"guid": GUID, "name": "Kale", "description": DESCRIPTION, "overview": OVERVIEW,
                 "owner": "Rocket Dino", "category": "General", "versions": versions}]
    os.makedirs(os.path.dirname(manifest_path), exist_ok=True)
    with open(manifest_path, "w") as f:
        json.dump(manifest, f, indent=2)
    print(manifest_path, "offers", sorted({v["version"] for v in versions}, key=vkey), "— not published")


if __name__ == "__main__":
    main()
