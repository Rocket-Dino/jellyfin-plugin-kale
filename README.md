# Kale for Jellyfin

A small server plugin for [Kale](https://getkale.app), a Jellyfin app for Apple TV, iPhone and iPad.
Kale works without it. With it installed:

- **Everyone in your household can request films and shows** through [Seerr](https://github.com/seerr-team/seerr)
  from the Kale app, without each person signing in to Seerr. Seerr's own permissions still decide who
  may request and who may approve. The Seerr API key stays on your server.
- **Kale can fix a file's default soundtrack** when an admin asks, by changing only the Matroska
  header's flags. No other bytes change, and it can be undone.

Supports Jellyfin 10.11 and 12.x.

## Install

An admin usually installs it from the Kale app, which asks before restarting Jellyfin. By hand:

1. Jellyfin Dashboard → Plugins → Repositories → add `https://getkale.app/jellyfin/manifest.json`
2. Catalogue → **Kale** → Install, then restart Jellyfin.

## Remove

Dashboard → Plugins → Kale → Uninstall, restart Jellyfin, and remove the repository. Kale goes back
to signing each person in to Seerr.

## Build and test

```sh
python3 scripts/package.py --base-url https://example.com/jellyfin   # builds dist/ for both Jellyfin versions
scripts/test.sh                                                       # unit tests on .NET 10 and .NET 9
```

## Licence

GPL-3.0-or-later. See [LICENSE](LICENSE). © Rocket Dino Pty Ltd.
