#!/bin/zsh
# Unit tests for every target, each on its own runtime:
#   net10.0 (Jellyfin 12.1) on the .NET 10 that `dotnet` is,
#   net9.0  (Jellyfin 10.11) on a real .NET 9 — `brew install dotnet@9` (keg-only, no sudo).
# Without the .NET 9 runtime the net9.0 leg aborts rather than quietly rolling forward.
set -euo pipefail
cd "${0:A:h}/.."
DOTNET9=${DOTNET9:-$(brew --prefix dotnet@9 2>/dev/null)/libexec/dotnet}
[[ -x $DOTNET9 ]] || { echo "no .NET 9 at $DOTNET9 — brew install dotnet@9" >&2; exit 1; }

dotnet test tests/Jellyfin.Plugin.Kale.Tests -nologo -f net10.0
dotnet test tests/Jellyfin.Plugin.Kale.Tests -nologo -f net9.0 -- RunConfiguration.DotNetHostPath=$DOTNET9
