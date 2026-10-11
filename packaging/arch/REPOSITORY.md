# Signed pacman repository

The signed x86_64 repository is live at `https://packages.jitzu.dev/arch/x86_64`,
starting with **v0.1.50**. Its database and package signatures were verified from
the public endpoint, including a download-only pacman check with required
package and database signatures in an isolated keyring/database.

The R2 bucket `jitzu-packages` belongs to account
`5291daa21d5bfc2aa14cd567d33fc550`; `packages.jitzu.dev` is attached with minimum
TLS 1.2. The five production GitHub Actions secrets are configured. The dedicated
signing key's public fingerprint is:

```text
A239257C5BF7E6D68EF823C48E1ADE54F78F6A5B
```

The signing key was created with a two-year expiration. Its protected local
recovery directory is `~/.local/share/jitzu/arch-signing`; it contains the GnuPG
keyring and passphrase. Back up this directory securely outside the repository,
and do not share it. Renew or rotate the key before expiration.

## Maintainer setup

Set these GitHub Actions repository secrets through GitHub's secrets UI:

| Secret | Value |
| --- | --- |
| `R2_ACCESS_KEY_ID` | S3 access key ID for an R2 Object Read & Write token scoped **only** to `jitzu-packages` |
| `R2_SECRET_ACCESS_KEY` | Matching S3 secret access key |
| `ARCH_GPG_PRIVATE_KEY` | ASCII-armored secret signing key exported by the key custodian |
| `ARCH_GPG_PASSPHRASE` | Nonempty passphrase protecting that key |
| `ARCH_GPG_FINGERPRINT` | Full 40-hex primary-key fingerprint |

Do not put credentials/private keys in this repository. Provisioning keys and
tokens is separate from the release workflow. Publish the public key's
full fingerprint through an independently authenticated project channel before
announcing repository availability.

`publish-arch-repository` runs only after a successful tagged release, excluding
hyphenated prerelease tags. Manual branch builds do not publish. Missing secrets
produce an explicit warning and summary, without blocking release or AUR jobs;
configured publication failures fail only this independent job.

The job uses one shared concurrency group across tags. GitHub does not guarantee
queue order (and may replace pending jobs), so the script also compares package
versions using Arch's `vercmp` against the current DB and skips equal/older
versions. Only this workflow should write repository metadata. Packages and
signatures are uploaded before signed DB/files metadata under `arch/x86_64`;
old packages are retained for cached databases. The standalone GitHub release
package remains unsigned; repository signatures are separate.

R2 cannot atomically replace a DB and its detached signature together. During
publication or a failed metadata upload, clients may see a signature mismatch:
retry after publication/re-run completes; never disable signature verification.
Metadata uses `no-cache`; deployment assumes no overriding edge cache rule.
Same-version reruns skip; do not overwrite an existing version's package.

## Installation

Download `https://packages.jitzu.dev/arch/x86_64/jitzu-signing-key.asc`.
Inspect the fingerprint with `gpg --show-keys --with-fingerprint
jitzu-signing-key.asc` and compare it to the independently published full
fingerprint. HTTPS alone is not a substitute for this comparison.

```sh
sudo pacman-key --add jitzu-signing-key.asc
# Replace this placeholder with the verified full fingerprint:
sudo pacman-key --lsign-key VERIFIED_FULL_FINGERPRINT
```

Then append to `/etc/pacman.conf`:

```ini
[jitzu]
SigLevel = PackageRequired DatabaseRequired
Server = https://packages.jitzu.dev/arch/$arch
```

```sh
sudo pacman -Syu jz-bin
```

This repository supports x86_64 only. GitHub release downloads and
`yay -S jz-bin` remain available as alternatives.

## Local validation

The publication script requires Bash, Arch `repo-add`/`vercmp`, `bsdtar`, GnuPG
and AWS CLI. Never run it locally with production credentials. For offline tests,
replace `aws` on `PATH` with a fake object store, use a throwaway key in a
temporary scratch `GNUPGHOME`, and verify detached signatures and DB contents.
The script imports keys only into a temporary directory and cleans it on exit.
