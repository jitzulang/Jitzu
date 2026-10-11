#!/usr/bin/env bash
# Run only under the workflow's shared publication concurrency lock.
set -euo pipefail
[[ $# == 1 ]] || { echo "Usage: $0 artifact-directory" >&2; exit 2; }
for tool in aws gpg repo-add bsdtar vercmp; do command -v "$tool" >/dev/null; done
for name in AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY ARCH_GPG_PRIVATE_KEY ARCH_GPG_PASSPHRASE ARCH_GPG_FINGERPRINT; do
    [[ -n ${!name:-} ]] || { echo "Missing required secret: $name" >&2; exit 1; }
done
[[ $ARCH_GPG_FINGERPRINT =~ ^[A-Fa-f0-9]{40}$ ]] || { echo "Expected full signing-key fingerprint" >&2; exit 1; }
shopt -s nullglob
packages=("$1"/*.pkg.tar.zst)
[[ ${#packages[@]} == 1 ]] || { echo "Expected exactly one package" >&2; exit 1; }
package=$(realpath "${packages[0]}")
work=$(mktemp -d)
trap 'gpgconf --kill gpg-agent >/dev/null 2>&1 || true; rm -rf "$work"' EXIT
export GNUPGHOME="$work/gnupg"
mkdir -m 700 "$GNUPGHOME"
cd "$work"
endpoint=https://5291daa21d5bfc2aa14cd567d33fc550.r2.cloudflarestorage.com
prefix=s3://jitzu-packages/arch/x86_64
export AWS_DEFAULT_REGION=auto AWS_EC2_METADATA_DISABLED=true AWS_PAGER=""
s3() { aws --endpoint-url "$endpoint" "$@"; }
bsdtar -xOf "$package" .PKGINFO > incoming.info
name=$(sed -n 's/^pkgname = //p' incoming.info)
version=$(sed -n 's/^pkgver = //p' incoming.info)
arch=$(sed -n 's/^arch = //p' incoming.info)
[[ $name == jz-bin && $arch == x86_64 && -n $version ]] || { echo "Unexpected package metadata" >&2; exit 1; }
# Listing distinguishes an empty bucket from authentication/network failures.
s3 s3api list-objects-v2 --bucket jitzu-packages --prefix arch/x86_64/jitzu.db.tar.gz \
    --query 'Contents[].Key' --output text > keys
if grep -Fxq arch/x86_64/jitzu.db.tar.gz keys; then
    s3 s3 cp "$prefix/jitzu.db.tar.gz" current.db
    bsdtar -xOf current.db '*/desc' > current.desc
    current=$(awk '/^%VERSION%$/ { getline; print }' current.desc)
    [[ -n $current && $(grep -c '^%VERSION%$' current.desc) == 1 ]] || { echo "Unexpected current repository" >&2; exit 1; }
    if [[ $(vercmp "$version" "$current") -le 0 ]]; then
        echo "Skipping $version: repository already contains $current (no downgrades or replacement)."
        exit 0
    fi
fi
printf '%s' "$ARCH_GPG_PRIVATE_KEY" | gpg --batch --import
sign() {
    printf '%s' "$ARCH_GPG_PASSPHRASE" |
        gpg --batch --yes --pinentry-mode loopback --passphrase-fd 0 \
            --local-user "$ARCH_GPG_FINGERPRINT" --detach-sign "$1"
    gpg --batch --verify "$1.sig" "$1"
}
cp "$package" .
filename=$(basename "$package")
sign "$filename"
repo-add jitzu.db.tar.gz "$filename"
sign jitzu.db.tar.gz
sign jitzu.files.tar.gz
gpg --armor --export "$ARCH_GPG_FINGERPRINT" > jitzu-signing-key.asc
[[ -s jitzu-signing-key.asc ]]
# Never delete older packages: cached metadata must continue to resolve.
for file in "$filename" "$filename.sig"; do
    s3 s3 cp "$file" "$prefix/$file" --cache-control 'public,max-age=31536000,immutable'
done
s3 s3 cp jitzu-signing-key.asc "$prefix/jitzu-signing-key.asc" --cache-control 'no-cache'
# Upload regular files, not repo-add's local symlinks. Metadata is revalidated.
for kind in files db; do
    s3 s3 cp "jitzu.$kind.tar.gz" "$prefix/jitzu.$kind" --cache-control 'no-cache'
    s3 s3 cp "jitzu.$kind.tar.gz.sig" "$prefix/jitzu.$kind.sig" --cache-control 'no-cache'
done
# Canonical DB is the last upload: its version marks completed publication.
# A failed earlier upload leaves the old version, allowing a retry to repair it.
for file in jitzu.files.tar.gz jitzu.files.tar.gz.sig jitzu.db.tar.gz.sig jitzu.db.tar.gz; do
    s3 s3 cp "$file" "$prefix/$file" --cache-control 'no-cache'
done
