# Guide for reproducible builds

[Reproducible builds](https://reproducible-builds.org/) provide an independently verifiable path from source code to binary code. This guide explains how to rebuild Ginger Wallet and compare the result with an official release.

## Artifact scope

Ginger publishes both portable archives and signed installers. Their verification boundaries are different:

| Artifact | Verification boundary |
| --- | --- |
| `Ginger-<version>-win-x64.zip` | The extracted application payload is reproducible and can be compared file by file. |
| Linux and macOS portable ZIPs | The extracted application payload can be compared file by file. |
| Windows MSI | The MSI and its two Ginger executables contain Authenticode signatures and timestamps. Verify the signatures and compare the application payload as described below; do not expect flat-file SHA-256 equality. |
| macOS DMG | Apple signing and notarization modify the application bundle. The signed DMG is not expected to be bit-for-bit identical to an unsigned local build. |

Archive metadata can differ even when every extracted file matches, so verification compares the extracted payload rather than the ZIP container bytes.

## 1. Match the official build environment

Install [Git](https://git-scm.com/) and the exact [.NET SDK](https://dotnet.microsoft.com/download) recorded in the release's `BUILDINFO.json`. The file is included in every portable archive and installed application directory. Its `NetSdkVersion` and `NetRuntimeVersion` fields identify the toolchain used for the official build.

The official Windows payload is produced on Windows. Use Windows 10 or later when verifying Windows artifacts because platform-dependent files can differ when the same target is published from another operating system.

If more than one SDK is installed, update the repository's `global.json` to select the exact `NetSdkVersion` and set `rollForward` to `disable` before building.

## 2. Build the release tag

Every release has a corresponding tag in the [Ginger Wallet repository](https://github.com/GingerPrivacy/GingerWallet/releases). Replace `<version>` below with a release version such as `2.0.26`.

```powershell
git clone --depth 1 --branch "v<version>" https://github.com/GingerPrivacy/GingerWallet.git
Set-Location GingerWallet\WalletWasabi.Packager
dotnet nuget locals all --clear
dotnet restore --locked-mode
dotnet run -- --onlybinaries
$repoRoot = Resolve-Path ..
```

The output directories are under `WalletWasabi.Fluent.Desktop\bin\dist`:

```text
win-x64
linux-x64
osx-x64
osx-arm64
```

`--onlybinaries` stops before installer creation and code signing. These directories are the unsigned reference payloads.

## 3. Compare portable archives

Download the portable archive for the same version and target from [GitHub Releases](https://github.com/GingerPrivacy/GingerWallet/releases). For example, compare the Windows ZIP in PowerShell:

```powershell
Expand-Archive "Ginger-<version>-win-x64.zip" -DestinationPath official-win-x64
$builtRoot = Join-Path $repoRoot "WalletWasabi.Fluent.Desktop\bin\dist\win-x64"
git diff --no-index --exit-code `
  $builtRoot `
  "official-win-x64"
```

Exit code `0` and no reported differences mean that every extracted file matches. Use the equivalent target directory when checking a Linux or macOS portable ZIP.

## 4. Verify the signed Windows MSI

The release process creates the portable ZIP first, signs `wassabee.exe` and `wassabeed.exe`, builds the MSI from that signed directory, and finally signs the MSI itself. Authenticode embeds a certificate and an RFC 3161 timestamp in each signed file, so independently built unsigned files cannot have the same flat-file SHA-256 value.

First verify the MSI and extract its payload without installing it:

```powershell
$msi = Resolve-Path "Ginger-<version>.msi"
$signature = Get-AuthenticodeSignature $msi
if ($signature.Status -ne "Valid") {
  throw "Invalid MSI Authenticode signature: $($signature.StatusMessage)"
}

$extractRoot = Join-Path $PWD "official-msi"
New-Item -ItemType Directory -Force $extractRoot | Out-Null
Start-Process msiexec.exe -Wait -ArgumentList @(
  "/a", "`"$msi`"", "/qn", "TARGETDIR=`"$extractRoot`""
)
```

Compare the extracted `GingerWallet` directory with the locally built `win-x64` directory. All files except the two Ginger executables must match exactly. Verify each executable's signature and compare its Authenticode image hash:

```powershell
$msiRoot = Resolve-Path "official-msi\GingerWallet"
$signedFiles = [Collections.Generic.HashSet[string]]::new(
  [string[]]@("wassabee.exe", "wassabeed.exe"),
  [StringComparer]::OrdinalIgnoreCase
)

function Get-UnsignedPayloadManifest([string] $root) {
  Get-ChildItem $root -Recurse -File | ForEach-Object {
    $relativePath = [IO.Path]::GetRelativePath($root, $_.FullName)
    if (-not $signedFiles.Contains($relativePath)) {
      [pscustomobject]@{
        RelativePath = $relativePath
        SHA256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
      }
    }
  }
}

$payloadDiff = Compare-Object `
  (Get-UnsignedPayloadManifest $builtRoot) `
  (Get-UnsignedPayloadManifest $msiRoot) `
  -Property RelativePath, SHA256
if ($payloadDiff) {
  $payloadDiff | Format-Table
  throw "Unsigned MSI payload files do not match the reproducible build."
}

foreach ($name in "wassabee.exe", "wassabeed.exe") {
  $built = Join-Path $builtRoot $name
  $signed = Join-Path $msiRoot $name

  $signature = Get-AuthenticodeSignature $signed
  if ($signature.Status -ne "Valid") {
    throw "Invalid signature on ${name}: $($signature.StatusMessage)"
  }

  $builtHash = (Get-AppLockerFileInformation -Path $built).Hash
  $signedHash = (Get-AppLockerFileInformation -Path $signed).Hash
  if ($builtHash -ne $signedHash) {
    throw "Authenticode image hash mismatch for ${name}."
  }
}

Write-Output "MSI payload matches the reproducible build modulo valid Authenticode signatures."
```

Windows calculates the Authenticode image hash without the PE checksum, certificate-table entry, or certificate table. Consequently, the hash remains stable when a signature or timestamp is added or removed while still covering the executable image. See Microsoft's documentation on [Authenticode/PE image hashes](https://learn.microsoft.com/windows/security/application-security/application-control/app-control-for-business/design/select-types-of-rules-to-create#more-information-about-hashes).

This procedure verifies that the signed executables contain the reproducible application image. It does **not** make the MSI itself bit-for-bit reproducible. In addition to signatures and timestamps, the WiX project generates installer identifiers while building the package.

## 5. Signed macOS artifacts

Apple stores code signatures inside Mach-O binaries and the application bundle. Gatekeeper requires distributed applications to be signed and notarized, so the official DMG is expected to differ from an unsigned local build. Use the portable macOS ZIP for reproducible payload comparison, and use Apple's `codesign` and `spctl` tools to validate the distributed signed application.

## Bitcoin Core bundled binaries

Ginger bundles upstream Bitcoin Core `bitcoind` binaries as microservice runtime artifacts. Ginger does not independently rebuild Bitcoin Core release binaries as part of its deterministic build process.

`WalletWasabi/Microservices/Binaries/UpgradeBitcoinCoreBinaries.ps1` verifies official Bitcoin Core release authenticity and integrity before replacing bundled binaries:

1. Downloads release archives, `SHA256SUMS`, and `SHA256SUMS.asc` from `https://bitcoincore.org/bin/bitcoin-core-<version>/`.
2. Verifies `SHA256SUMS.asc` with pinned Bitcoin Core release signer fingerprints.
3. Verifies each downloaded archive against the signed `SHA256SUMS` entry.
4. Extracts only `bitcoind` / `bitcoind.exe` into the platform microservice folders.

This release verification is separate from independently proving that Bitcoin Core's published binaries are reproducible builds. Bitcoin Core documents that additional verification path under "Additional verification with reproducible builds" on the official download page:

https://bitcoincore.org/en/download/

Bitcoin Core reproducible build attestations are published in the `bitcoin-core/guix.sigs` repository:

https://github.com/bitcoin-core/guix.sigs

For website release binaries, the relevant per-release files are the signed `all.SHA256SUMS` files under the version-specific builder directories, for example:

```text
<version>/<builder>/all.SHA256SUMS
```

Those attestations can be used to verify that independent Guix builders reproduced the same release artifact hashes as the official website binaries. For Ginger client packaging, this deterministic build boundary is intentionally upstream: use the Bitcoin Core documentation and `bitcoin-core/guix.sigs` attestations as the source of truth for independent Bitcoin Core reproducible build verification.
