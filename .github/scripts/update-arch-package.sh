#!/usr/bin/env bash
set -euo pipefail

version="${1:?Usage: update-arch-package.sh <version> <zip-sha256>}"
hash="${2:?Usage: update-arch-package.sh <version> <zip-sha256>}"
[[ "$version" =~ ^[0-9][0-9A-Za-z.+-]*$ ]] || { echo "Invalid version: $version" >&2; exit 1; }
[[ "$hash" =~ ^[0-9a-f]{64}$ ]] || { echo "Invalid SHA256" >&2; exit 1; }
license_hash=$(sha256sum LICENSE | cut -d' ' -f1)
# Arch pkgver cannot contain hyphens; preserve the release tag for source URLs.
sed -i \
    -e "s/^pkgver=.*/pkgver=${version//-/_}/" \
    -e "s/^_releasever=.*/_releasever=$version/" \
    -e "s/^pkgrel=.*/pkgrel=1/" \
    -e "/^sha256sums=/,+1c\\sha256sums=('$hash'\\n            '$license_hash')" \
    packaging/arch/PKGBUILD
