#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_ROOT=/opt/valheim-port/dotnet
export PATH=/opt/valheim-port/dotnet:$PATH
PKG="./dist/MagiCorp-RememberServerPassword-1.1.0.zip"
test -s "$PKG"
read -rsp "Thunderstore service token: " TS_TOKEN
echo
trap 'unset TS_TOKEN' EXIT
/root/.dotnet/tools/tcli publish --file "$PKG" --repository https://thunderstore.io --token "$TS_TOKEN"
