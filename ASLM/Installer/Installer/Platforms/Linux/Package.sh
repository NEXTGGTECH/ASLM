#!/usr/bin/env bash
# Copyright NEXTGGTECH. Apache License 2.0.

set -euo pipefail

# Packs a complete Linux publish into one self-extracting system installer.
package_installer()
{
    local build_root output version script_dir stage payload_digest executable required
    build_root=$(realpath "$1")
    output=$(realpath -m "$2")
    version=$3
    script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
    [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Invalid installer version.' >&2; return 1; }

    # Fail before producing an installer if publish omitted an executable, runtime or first-run templates.
    for executable in ASLM App/ASLM Patcher/aslm-patcher; do test -x "$build_root/$executable"; done
    for required in libcoreclr.so libhostfxr.so libhostpolicy.so seed/Data/App/ASLM_Data.json; do
        test -f "$build_root/App/$required"
    done
    test -f "$build_root/App/hicolor/scalable/apps/appicon.svg"
    mkdir -p "$script_dir/../../obj" "$(dirname -- "$output")"
    stage=$(mktemp -d "$script_dir/../../obj/linux-installer.XXXXXXXX")
    trap 'rm -rf -- "$stage"' EXIT
    trap 'exit 1' HUP INT TERM

    # The base is program code only: user data is seeded by the application, never by a root installer.
    tar -C "$build_root" -czf "$stage/aslm-base.tar.gz" App Patcher
    cp "$build_root/ASLM" "$stage/ASLM"
    cp "$build_root/App/hicolor/scalable/apps/appicon.svg" "$stage/appicon.svg"
    tar -C "$stage" -czf "$stage/payload.tar.gz" ASLM aslm-base.tar.gz appicon.svg
    payload_digest=$(sha256sum "$stage/payload.tar.gz")
    payload_digest=${payload_digest%% *}

    # Publish atomically so a canceled build cannot leave a seemingly usable, truncated installer.
    sed -e "s/@ASLM_PAYLOAD_SHA256@/$payload_digest/g" -e "s/@ASLM_VERSION@/$version/g" \
        "$script_dir/Installer.sh" > "$stage/installer.sh"
    cat "$stage/payload.tar.gz" >> "$stage/installer.sh"
    chmod 755 "$stage/installer.sh"
    mv -f -- "$stage/installer.sh" "$output"
    rm -rf -- "$stage"
    trap - EXIT HUP INT TERM
    printf 'Linux installer: %s\n' "$output"
}

[[ $# -eq 3 ]] || { echo 'Usage: Package.sh BUILD_ROOT OUTPUT VERSION' >&2; exit 1; }
package_installer "$@"
