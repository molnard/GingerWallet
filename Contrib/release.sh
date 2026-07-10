#!/usr/bin/env bash

# Build, package, sign, and prepare GitHub release assets for Ginger Wallet.
# This is intentionally CI-friendly: secrets are read from environment variables,
# and release assets are written to ./packages.

set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: ./Contrib/release.sh <command>

Commands:
  debian       Build Linux x64 zip, tar.gz, and deb packages.
  appimage     Build Linux x64 AppImage.
  wininstaller Build and sign the Windows MSI installer.
  dmg          Build, sign, notarize, and package macOS x64/arm64 DMGs.
  checksums    Create unsigned SHA256SUMS for release assets.
  gpgsign      Create SHA256SUMS, PGP signatures, and Ginger signature.
  releasenote  Print release notes from Contrib/ReleaseTemplate.md.
USAGE
}

if [[ $# -ne 1 ]]; then
  usage
  exit 1
fi

COMMAND="$1"
ROOT_DIR="$(pwd)"
BUILD_DIR="$ROOT_DIR/build"
PACKAGES_DIR="$ROOT_DIR/packages"

DESKTOP="WalletWasabi.Fluent.Desktop"
DAEMON="WalletWasabi.Daemon"
DESKTOP_PROJECT="./$DESKTOP/$DESKTOP.csproj"
DAEMON_PROJECT="./$DAEMON/$DAEMON.csproj"

EXECUTABLE_NAME="wassabee"
DAEMON_EXECUTABLE_NAME="wassabeed"
APP_NAME="Ginger Wallet"
PACKAGE_FILE_NAME_PREFIX="Ginger"

is_true() {
  case "${1:-}" in
    true|TRUE|True|1|yes|YES|Yes)
      return 0
      ;;
    *)
      return 1
      ;;
  esac
}

resolve_release_tag() {
  if [[ -n "${RELEASE_TAG:-}" ]]; then
    echo "$RELEASE_TAG"
    return
  fi

  if [[ -n "${GITHUB_REF_NAME:-}" && "${GITHUB_REF_NAME}" == v* ]]; then
    echo "$GITHUB_REF_NAME"
    return
  fi

  if git describe --tags --abbrev=0 >/dev/null 2>&1; then
    git describe --tags --abbrev=0
    return
  fi

  local parsed_version
  parsed_version="$(grep -E "ClientVersion = new\(" WalletWasabi/Helpers/Constants.cs | sed -E 's/.*new\(([^)]*)\).*/\1/' | tr -d ' ')"
  parsed_version="${parsed_version//,/.}"
  echo "v$parsed_version"
}

RELEASE_TAG="$(resolve_release_tag)"
VERSION="${RELEASE_TAG#v}"
SHORT_VERSION="$(echo "$VERSION" | awk -F. '{ print $1 "." $2 "." $3 }')"

if [[ -z "$VERSION" || "$VERSION" == "$RELEASE_TAG" ]]; then
  echo "Release tag must look like v2.0.25.0. Got: $RELEASE_TAG" >&2
  exit 1
fi

mkdir -p "$BUILD_DIR" "$PACKAGES_DIR"

if [[ "${RUNNER_OS:-}" == "Windows" ]]; then
  ZIP_CMD="7z.exe"
else
  ZIP_CMD="zip"
fi

create_zip_from_dir() {
  local source_dir="$1"
  local zip_path="$2"

  rm -f "$zip_path"
  pushd "$source_dir" >/dev/null
  if [[ "$ZIP_CMD" == "7z.exe" ]]; then
    7z.exe a -tzip "$zip_path" . >/dev/null
  else
    zip -r "$zip_path" . >/dev/null
  fi
  popd >/dev/null
}

to_windows_path() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$1"
  else
    echo "$1"
  fi
}

to_unix_path() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -u "$1"
  else
    echo "$1"
  fi
}

base64_decode_to_file() {
  local value="$1"
  local destination="$2"

  if printf '%s' "$value" | base64 --decode > "$destination" 2>/dev/null; then
    return
  fi

  printf '%s' "$value" | base64 -D > "$destination"
}

write_build_info() {
  local output_dir="$1"
  local runtime_version
  runtime_version="$(dotnet --list-runtimes | awk 'NR==1 { print $2 }')"

  cat > "$output_dir/BUILDINFO.json" <<EOF
{
  "NetRuntimeVersion": "$runtime_version",
  "NetSdkVersion": "$(dotnet --version)",
  "GitCommitHash": "$(git rev-parse HEAD)"
}
EOF
}

clear_sha512_tags() {
  local output_dir="$1"
  find "$output_dir" -name "*.deps.json" -type f -exec sed -i.bak 's/"sha512": "sha512-[^"]*"/"sha512": ""/g' {} +
  find "$output_dir" -name "*.deps.json.bak" -type f -delete
}

delete_git_metadata_files() {
  local output_dir="$1"
  find "$output_dir" \( -name ".gitattributes" -o -name ".gitignore" \) -type f -delete
}

prune_microservice_binaries() {
  local output_dir="$1"
  local platform="$2"
  local binaries_dir="$output_dir/Microservices/Binaries"

  if [[ ! -d "$binaries_dir" ]]; then
    return
  fi

  find "$binaries_dir" -mindepth 1 -maxdepth 1 -type d ! -name "$platform" -exec rm -rf {} +
}

publish_platform() {
  local platform="$1"
  local output_dir="$BUILD_DIR/$platform"
  local executable_extension=""

  if [[ "$platform" == win* ]]; then
    executable_extension=".exe"
  fi

  rm -rf "$output_dir"
  mkdir -p "$output_dir"

  write_build_info "$output_dir"

  dotnet restore "$DESKTOP_PROJECT" --locked-mode
  dotnet publish "$DESKTOP_PROJECT" \
    --configuration Release \
    --runtime "$platform" \
    --force \
    --output "$output_dir" \
    --self-contained true \
    --disable-parallel \
    --no-cache \
    --no-restore \
    --property:SelfContained=true \
    --property:VersionPrefix="$VERSION" \
    --property:DebugType=none \
    --property:DebugSymbols=false \
    --property:ErrorReport=none \
    --property:DocumentationFile='' \
    --property:Deterministic=true \
    /clp:ErrorsOnly

  dotnet restore "$DAEMON_PROJECT" --locked-mode
  dotnet publish "$DAEMON_PROJECT" \
    --configuration Release \
    --runtime "$platform" \
    --force \
    --output "$output_dir" \
    --self-contained true \
    --disable-parallel \
    --no-cache \
    --no-restore \
    --property:SelfContained=true \
    --property:VersionPrefix="$VERSION" \
    --property:DebugType=none \
    --property:DebugSymbols=false \
    --property:ErrorReport=none \
    --property:DocumentationFile='' \
    --property:Deterministic=true \
    /clp:ErrorsOnly

  clear_sha512_tags "$output_dir"
  prune_microservice_binaries "$output_dir" "$platform"
  delete_git_metadata_files "$output_dir"

  mv "$output_dir/WalletWasabi.Fluent.Desktop$executable_extension" "$output_dir/$EXECUTABLE_NAME$executable_extension"
  mv "$output_dir/WalletWasabi.Daemon$executable_extension" "$output_dir/$DAEMON_EXECUTABLE_NAME$executable_extension"
  rm -f "$output_dir/WalletWasabi.Fluent$executable_extension"

  if [[ "$platform" != win* ]]; then
    chmod 0755 "$output_dir/$EXECUTABLE_NAME" "$output_dir/$DAEMON_EXECUTABLE_NAME"
  fi
}

package_zip_and_tar() {
  local platform="$1"
  local output_dir="$BUILD_DIR/$platform"
  local package_platform="$platform"

  if [[ "$platform" == osx-* ]]; then
    package_platform="macOS-${platform#osx-}"
  fi

  create_zip_from_dir "$output_dir" "$PACKAGES_DIR/$PACKAGE_FILE_NAME_PREFIX-$VERSION-$package_platform.zip"

  if [[ "$platform" == linux-* ]]; then
    local source_date_epoch="${SOURCE_DATE_EPOCH:-$(git log -1 --pretty=%ct)}"
    local parent_dir
    local base_name
    parent_dir="$(dirname "$output_dir")"
    base_name="$(basename "$output_dir")"

    tar --sort=name \
      --mtime="@$source_date_epoch" \
      --owner=0 \
      --group=0 \
      --numeric-owner \
      --transform="s|^$base_name|$PACKAGE_FILE_NAME_PREFIX-$VERSION|" \
      --pax-option=exthdr.name=%d/PaxHeaders/%f,delete=atime,delete=ctime \
      -pczf "$PACKAGES_DIR/$PACKAGE_FILE_NAME_PREFIX-$VERSION-$platform.tar.gz" \
      -C "$parent_dir" \
      "$base_name"
  fi
}

build_linux_packages() {
  local platform="linux-x64"
  local output_dir="$BUILD_DIR/$platform"
  local debian_package_dir="$BUILD_DIR/deb-package"
  local debian="$debian_package_dir/DEBIAN"
  local debian_usr="$debian_package_dir/usr"
  local debian_bin="$debian_usr/local/bin"
  local debian_app="$debian_usr/share/applications"
  local debian_icons="$debian_usr/share/icons/hicolor"
  local install_dir="/usr/local/bin/gingerwallet"

  publish_platform "$platform"
  package_zip_and_tar "$platform"

  rm -rf "$debian_package_dir"
  mkdir -p "$debian" "$debian_bin" "$debian_app" "$debian_icons"

  for icon_file in WalletWasabi.Fluent.Desktop/Assets/WasabiLogo*.png; do
    local size
    size="$(echo "$icon_file" | grep -oE '[0-9]+' | head -1)"
    if [[ -n "$size" ]]; then
      mkdir -p "$debian_icons/${size}x${size}/apps"
      cp "$icon_file" "$debian_icons/${size}x${size}/apps/$EXECUTABLE_NAME.png"
    fi
  done

  local installed_size
  installed_size="$(du -s "$output_dir" | cut -f1)"

  cat > "$debian/control" <<EOF
Package: $EXECUTABLE_NAME
Priority: optional
Section: utils
Maintainer: GingerPrivacy info@gingerwallet.io
Version: $VERSION
Homepage: https://gingerwallet.io
Vcs-Git: git://github.com/GingerPrivacy/GingerWallet.git
Vcs-Browser: https://github.com/GingerPrivacy/GingerWallet
Architecture: amd64
License: Open Source (MIT)
Installed-Size: $installed_size
Recommends: policykit-1
Description: open-source, non-custodial, privacy focused Bitcoin wallet
 Built-in Tor, coinjoin, payjoin and coin control features.
EOF

  cat > "$debian/postinst" <<EOF
#!/usr/bin/env sh
$install_dir/Microservices/Binaries/linux-x64/hwi installudevrules
exit 0
EOF
  chmod 0775 "$debian/postinst"

  cat > "$debian_app/$EXECUTABLE_NAME.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Ginger Wallet
StartupWMClass=Ginger Wallet
GenericName=Bitcoin Wallet
Comment=Privacy focused Bitcoin wallet.
Icon=$EXECUTABLE_NAME
Terminal=false
Exec=$EXECUTABLE_NAME
Categories=Office;Finance;
Keywords=bitcoin;wallet;crypto;blockchain;ginger;privacy;coinjoin;payjoin;
EOF
  chmod 0644 "$debian_app/$EXECUTABLE_NAME.desktop"

  cp -a "$output_dir" "$debian_bin/gingerwallet"

  cat > "$debian_bin/$EXECUTABLE_NAME" <<EOF
#!/usr/bin/env sh
$install_dir/$EXECUTABLE_NAME "\$@"
EOF

  cat > "$debian_bin/$DAEMON_EXECUTABLE_NAME" <<EOF
#!/usr/bin/env sh
$install_dir/$DAEMON_EXECUTABLE_NAME "\$@"
EOF

  chmod 0755 "$debian_bin/gingerwallet" "$debian_bin/$EXECUTABLE_NAME" "$debian_bin/$DAEMON_EXECUTABLE_NAME"
  find "$debian_bin/gingerwallet" -type f -exec chmod 0644 {} +
  find "$debian_bin/gingerwallet" -type d -exec chmod 0755 {} +
  chmod 0755 "$debian_bin/gingerwallet/$EXECUTABLE_NAME" "$debian_bin/gingerwallet/$DAEMON_EXECUTABLE_NAME"
  chmod 0755 "$debian_bin/gingerwallet/Microservices/Binaries/linux-x64/hwi"
  chmod 0755 "$debian_bin/gingerwallet/Microservices/Binaries/linux-x64/bitcoind"
  chmod 0755 "$debian_bin/gingerwallet/Microservices/Binaries/linux-x64/Tor/tor"

  dpkg-deb -Zxz --build "$debian_package_dir" "$PACKAGES_DIR/$PACKAGE_FILE_NAME_PREFIX-$VERSION.deb"
}

build_appimage() {
  local platform="linux-x64"
  local output_dir="$BUILD_DIR/$platform"
  local appimagetool_dir="$BUILD_DIR/appimagetool"
  local appdir="$BUILD_DIR/appimage/AppDir"
  local appdir_usr="$appdir/usr"
  local appdir_bin="$appdir_usr/bin"

  publish_platform "$platform"
  mkdir -p "$appimagetool_dir"

  if [[ ! -x "$appimagetool_dir/appimagetool" ]]; then
    curl -L -o "$appimagetool_dir/appimagetool-x86_64.AppImage" \
      "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
    chmod +x "$appimagetool_dir/appimagetool-x86_64.AppImage"

    pushd "$appimagetool_dir" >/dev/null
    ./appimagetool-x86_64.AppImage --appimage-extract >/dev/null 2>&1
    mv squashfs-root/AppRun appimagetool
    mv squashfs-root/usr .
    rm -rf squashfs-root appimagetool-x86_64.AppImage
    popd >/dev/null
  fi

  rm -rf "$appdir"
  mkdir -p "$appdir_bin" "$appdir_usr/share/applications" "$appdir_usr/share/icons/hicolor"
  cp -a "$output_dir/." "$appdir_bin/"

  for icon_file in WalletWasabi.Fluent.Desktop/Assets/WasabiLogo*.png; do
    local size
    size="$(echo "$icon_file" | grep -oE '[0-9]+' | head -1)"
    if [[ -n "$size" ]]; then
      mkdir -p "$appdir_usr/share/icons/hicolor/${size}x${size}/apps"
      cp "$icon_file" "$appdir_usr/share/icons/hicolor/${size}x${size}/apps/$EXECUTABLE_NAME.png"
    fi
  done
  cp WalletWasabi.Fluent.Desktop/Assets/WasabiLogo256.png "$appdir/$EXECUTABLE_NAME.png"

  cat > "$appdir/$EXECUTABLE_NAME.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Ginger Wallet
StartupWMClass=Ginger Wallet
GenericName=Bitcoin Wallet
Comment=Privacy focused Bitcoin wallet.
Icon=$EXECUTABLE_NAME
Terminal=false
Exec=$EXECUTABLE_NAME
Categories=Office;Finance;
Keywords=bitcoin;wallet;crypto;blockchain;ginger;privacy;coinjoin;payjoin;
EOF
  cp "$appdir/$EXECUTABLE_NAME.desktop" "$appdir_usr/share/applications/"

  cat > "$appdir/AppRun" <<EOF
#!/usr/bin/env sh
HERE="\$(dirname "\$(readlink -f "\${0}")")"
export PATH="\${HERE}/usr/bin:\${PATH}"
exec "\${HERE}/usr/bin/$EXECUTABLE_NAME" "\$@"
EOF
  chmod 0755 "$appdir/AppRun"
  chmod 0755 "$appdir_bin/$EXECUTABLE_NAME" "$appdir_bin/$DAEMON_EXECUTABLE_NAME"
  chmod 0755 "$appdir_bin/Microservices/Binaries/linux-x64/hwi"
  chmod 0755 "$appdir_bin/Microservices/Binaries/linux-x64/bitcoind"
  chmod 0755 "$appdir_bin/Microservices/Binaries/linux-x64/Tor/tor"

  ARCH=x86_64 "$appimagetool_dir/appimagetool" "$appdir" "$PACKAGES_DIR/$PACKAGE_FILE_NAME_PREFIX-$VERSION.AppImage"
}

build_windows_installer() {
  local platform="win-x64"
  local windows_installer_dir="WalletWasabi.WindowsInstaller"
  local build_installer_dir="$BUILD_DIR/win-installer"
  local output_dir="$BUILD_DIR/$platform"
  local wix_bin_dir

  publish_platform "$platform"
  package_zip_and_tar "$platform"

  if [[ -z "${WIX:-}" ]]; then
    echo "WIX environment variable is not set. Install WiX Toolset before building the MSI." >&2
    exit 1
  fi

  wix_bin_dir="$(to_unix_path "$WIX")/bin"
  mkdir -p "$build_installer_dir"

  local output_dir_win
  local components_generated_win
  local installer_dir_win
  local desktop_project_dir_win
  output_dir_win="$(to_windows_path "$output_dir")"
  components_generated_win="$(to_windows_path "$build_installer_dir/ComponentsGenerated.wxs")"
  installer_dir_win="$(to_windows_path "$windows_installer_dir")"
  desktop_project_dir_win="$(to_windows_path "$DESKTOP")"

  "$wix_bin_dir/heat.exe" dir "$output_dir_win" \
    -o "$components_generated_win" \
    -cg PublishedComponents \
    -pog Binaries \
    -dr INSTALLFOLDER \
    -srd -scom -gg -sfrag -sreg \
    -var var.BasePath

  rm -f ./*.wixobj
  "$wix_bin_dir/candle.exe" \
    "$installer_dir_win\\Components.wxs" \
    "$installer_dir_win\\Directories.wxs" \
    "$installer_dir_win\\Product.wxs" \
    "$components_generated_win" \
    -arch x64 \
    -dBuildVersion="$VERSION" \
    -dBasePath="$output_dir_win" \
    -dWalletWasabi.Fluent.Desktop.ProjectDir="$desktop_project_dir_win"

  "$wix_bin_dir/light.exe" \
    ./*.wixobj \
    -loc "$installer_dir_win\\Common.wxl" \
    -ext "$wix_bin_dir/WixUIExtension.dll" \
    -ext "$wix_bin_dir/WixUtilExtension.dll" \
    -b "$output_dir_win" \
    -out "$PACKAGES_DIR/$PACKAGE_FILE_NAME_PREFIX-$VERSION.msi"

  rm -f ./*.wixobj ./*.wixpdb "$PACKAGES_DIR"/*.wixpdb

  if is_true "${CODE_SIGN:-false}"; then
    : "${AZURE_KEY_VAULT_URL:?Missing AZURE_KEY_VAULT_URL}"
    : "${AZURE_CERTIFICATE_NAME:?Missing AZURE_CERTIFICATE_NAME}"
    : "${AZURE_CLIENT_ID:?Missing AZURE_CLIENT_ID}"
    : "${AZURE_CLIENT_SECRET:?Missing AZURE_CLIENT_SECRET}"
    : "${AZURE_TENANT_ID:?Missing AZURE_TENANT_ID}"

    azuresigntool sign \
      -kvu "$AZURE_KEY_VAULT_URL" \
      -kvc "$AZURE_CERTIFICATE_NAME" \
      -kvi "$AZURE_CLIENT_ID" \
      -kvs "$AZURE_CLIENT_SECRET" \
      --azure-key-vault-tenant-id "$AZURE_TENANT_ID" \
      -tr "http://timestamp.digicert.com" \
      "$PACKAGES_DIR/$PACKAGE_FILE_NAME_PREFIX-$VERSION.msi"
  else
    echo "CODE_SIGN is false; leaving Windows MSI unsigned."
  fi
}

build_macos_dmg() {
  if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "The dmg command must run on macOS." >&2
    exit 1
  fi

  local cert_path=""
  local p12_path=""
  local keychain_name=""
  local keychain_path=""
  local keychain_password=""
  local profile_name="GingerNotarize"

  if is_true "${CODE_SIGN:-false}"; then
    : "${MAC_CER:?Missing MAC_CER}"
    : "${MAC_P12:?Missing MAC_P12}"
    : "${MAC_P12_PASSWORD:?Missing MAC_P12_PASSWORD}"
    : "${MAC_TEAMID:?Missing MAC_TEAMID}"
    : "${MAC_APPLEID:?Missing MAC_APPLEID}"
    : "${MAC_APPLEPSSWD:?Missing MAC_APPLEPSSWD}"

    cert_path="$BUILD_DIR/MacCertificate.cer"
    p12_path="$BUILD_DIR/MacP12.p12"
    keychain_name="ginger-release-${GITHUB_RUN_ID:-local}.keychain-db"
    keychain_path="$HOME/Library/Keychains/$keychain_name"
    keychain_password="$(uuidgen)"

    base64_decode_to_file "$MAC_CER" "$cert_path"
    base64_decode_to_file "$MAC_P12" "$p12_path"

    cleanup_keychain() {
      security delete-keychain "$keychain_name" >/dev/null 2>&1 || true
    }
    trap cleanup_keychain EXIT

    security create-keychain -p "$keychain_password" "$keychain_name"
    security set-keychain-settings "$keychain_name"
    security unlock-keychain -p "$keychain_password" "$keychain_name"
    security list-keychains -s "$keychain_name"
    security import "$cert_path" -k "$keychain_path" -T /usr/bin/codesign
    security import "$p12_path" -k "$keychain_path" -P "$MAC_P12_PASSWORD" -T /usr/bin/codesign
    security set-key-partition-list -S apple-tool:,apple: -k "$keychain_password" "$keychain_path"
    xcrun notarytool store-credentials "$profile_name" \
      --apple-id "$MAC_APPLEID" \
      --team-id "$MAC_TEAMID" \
      --password "$MAC_APPLEPSSWD" \
      --keychain "$keychain_path"
  else
    echo "CODE_SIGN is false; building unsigned macOS packages without notarization."
  fi

  for platform in osx-x64 osx-arm64; do
    publish_platform "$platform"
    package_zip_and_tar "$platform"

    local current_arch="${platform#osx-}"
    local plist_arch="$current_arch"
    if [[ "$current_arch" == "x64" ]]; then
      plist_arch="x86_64"
    fi

    local package_zip="$PACKAGES_DIR/$PACKAGE_FILE_NAME_PREFIX-$VERSION-macOS-$current_arch.zip"
    local osx_build_dir="$BUILD_DIR/$platform-dmg"
    local dmg_path="$osx_build_dir/dmg"
    local app_path="$dmg_path/$APP_NAME.app"
    local app_contents_path="$app_path/Contents"
    local app_macos_path="$app_contents_path/MacOS"
    local app_res_path="$app_contents_path/Resources"
    local info_file_path="$app_contents_path/Info.plist"
    local entitlements_path="$ROOT_DIR/WalletWasabi.Packager/Content/Osx/entitlements.plist"
    local dmg_file_path="$PACKAGES_DIR/$PACKAGE_FILE_NAME_PREFIX-$VERSION.dmg"

    if [[ "$current_arch" == "arm64" ]]; then
      dmg_file_path="$PACKAGES_DIR/$PACKAGE_FILE_NAME_PREFIX-$VERSION-arm64.dmg"
    fi

    rm -rf "$osx_build_dir"
    mkdir -p "$app_macos_path" "$app_res_path"
    cp -R WalletWasabi.Packager/Content/Osx/App/. "$app_path/"
    unzip -q "$package_zip" -d "$app_macos_path"

    /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $SHORT_VERSION" "$info_file_path"
    /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $SHORT_VERSION" "$info_file_path"
    /usr/libexec/PlistBuddy -c "Delete :LSArchitecturePriority" "$info_file_path" >/dev/null 2>&1 || true
    /usr/libexec/PlistBuddy -c "Add :LSArchitecturePriority array" "$info_file_path"
    /usr/libexec/PlistBuddy -c "Add :LSArchitecturePriority:0 string $plist_arch" "$info_file_path"

    chmod -R u+rwX,go+rX,go-w "$app_path"
    find "$app_path" -name ".DS_Store" -type f -delete
    while IFS= read -r mach_file; do
      chmod u+x "$mach_file"
    done < <(find "$app_path" -type f -print0 | xargs -0 file | awk -F: '/Mach-O/ { print $1 }')

    if is_true "${CODE_SIGN:-false}"; then
      local sign_arguments=(--sign "$MAC_TEAMID" --verbose --force --options runtime --timestamp --entitlements "$entitlements_path")
      while IFS= read -r -d '' file; do
        if [[ "$(basename "$file")" != "$EXECUTABLE_NAME" ]]; then
          codesign "${sign_arguments[@]}" "$file"
        fi
      done < <(find "$app_path" -type f -print0 | sort -z)

      codesign "${sign_arguments[@]}" "$app_macos_path/$EXECUTABLE_NAME"
      codesign "${sign_arguments[@]}" "$app_path"
      codesign -dv --verbose=4 "$app_path" 2>&1 | grep "$MAC_TEAMID"

      ditto -c -k --keepParent "$app_path" "$package_zip"
      xcrun notarytool submit "$package_zip" --keychain "$keychain_path" --keychain-profile "$profile_name" --wait
      xcrun stapler staple "$app_path"
      xcrun stapler validate "$app_path"
      spctl -a -t exec -vv "$app_path"
    fi
    ditto -c -k --keepParent "$app_path" "$package_zip"

    cp -R WalletWasabi.Packager/Content/Osx/Dmg/. "$dmg_path/"
    if [[ -f "$dmg_path/.DS_Store.dat" ]]; then
      mv "$dmg_path/.DS_Store.dat" "$dmg_path/.DS_Store"
    fi
    cp WalletWasabi.Packager/Content/Osx/WasabiLogo.icns "$dmg_path/.VolumeIcon.icns"
    ln -s /Applications "$dmg_path/Applications"

    local dmg_uncompressed="$osx_build_dir/Ginger.tmp.dmg"
    hdiutil create "$dmg_uncompressed" -ov -volname "$APP_NAME" -fs HFS+ -srcfolder "$dmg_path"
    hdiutil convert "$dmg_uncompressed" -format UDZO -o "$dmg_file_path"

    if is_true "${CODE_SIGN:-false}"; then
      codesign "${sign_arguments[@]}" "$dmg_file_path"
      codesign -dv --verbose=4 "$dmg_file_path" 2>&1 | grep "$MAC_TEAMID"
      xcrun notarytool submit "$dmg_file_path" --keychain "$keychain_path" --keychain-profile "$profile_name" --wait
      xcrun stapler staple "$dmg_file_path"
      xcrun stapler validate "$dmg_file_path"
    fi
  done
}

write_checksums() {
  pushd "$PACKAGES_DIR" >/dev/null
  rm -f SHA256SUMS SHA256SUMS.asc SHA256SUMS.gingersig

  for file in ./*; do
    if [[ -f "$file" && "$file" != *.asc && "$file" != *.gingersig && "$(basename "$file")" != "SHA256SUMS" ]]; then
      sha256sum "$file" >> SHA256SUMS
    fi
  done
  popd >/dev/null
}

sign_packages() {
  : "${SIGNING_GINGER_KEY:?Missing SIGNING_GINGER_KEY}"

  write_checksums

  pushd "$PACKAGES_DIR" >/dev/null
  for file in ./*; do
    if [[ -f "$file" && "$file" != *.asc && "$file" != *.gingersig && "$(basename "$file")" != "SHA256SUMS" ]]; then
      gpg --armor --detach-sign --output "$file.asc" "$file"
    fi
  done

  gpg --sign --digest-algo sha256 -a --clearsign --armor --output SHA256SUMS.asc SHA256SUMS

  cat > signer.fsx <<'EOF'
#r "nuget:NBitcoin, 9.0.0"
open System
open System.IO
open System.Security.Cryptography
open NBitcoin

let args = Environment.GetCommandLineArgs()
let filePath = args[2]
let gingerPrivateKey = Key.Parse(args[3], Network.Main)
filePath
|> File.ReadAllBytes
|> SHA256.HashData
|> uint256
|> gingerPrivateKey.Sign
|> _.ToDER()
|> Convert.ToBase64String
|> Console.WriteLine
EOF

  dotnet fsi signer.fsx SHA256SUMS.asc "$SIGNING_GINGER_KEY" > SHA256SUMS.gingersig
  rm signer.fsx
  popd >/dev/null
}

print_release_note() {
  if [[ -f Contrib/ReleaseHighlights.md ]]; then
    sed -e "s/{version}/$VERSION/g" \
      -e "/{highlights}/r ./Contrib/ReleaseHighlights.md" \
      -e "/{highlights}/d" \
      ./Contrib/ReleaseTemplate.md
  else
    sed -e "s/{version}/$VERSION/g" \
      -e "s/{highlights}/Release highlights are pending./g" \
      ./Contrib/ReleaseTemplate.md
  fi
}

case "$COMMAND" in
  debian)
    build_linux_packages
    ;;
  appimage)
    build_appimage
    ;;
  wininstaller)
    build_windows_installer
    ;;
  dmg)
    build_macos_dmg
    ;;
  checksums)
    write_checksums
    ;;
  gpgsign)
    sign_packages
    ;;
  releasenote)
    print_release_note
    ;;
  *)
    usage
    exit 1
    ;;
esac
