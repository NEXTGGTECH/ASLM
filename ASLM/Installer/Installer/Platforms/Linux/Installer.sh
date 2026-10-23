#!/bin/sh
# Copyright NEXTGGTECH. Apache License 2.0.

set -eu

install_dir=/opt/ASLM
command_path=/usr/local/bin/aslm
desktop_path=/usr/local/share/applications/com.nextggtech.aslm.desktop
payload_digest='@ASLM_PAYLOAD_SHA256@'
version='@ASLM_VERSION@'
stage=
replaced=0
installed=0
command_created=0
desktop_written=0
completed=0

# Reports an installation error without starting the application as root.
fail()
{
    printf 'ASLM: %s\n' "$*" >&2
    exit 1
}

# Restores a previous system installation on failure and removes this invocation's staging files.
cleanup()
{
    result=$?
    trap - 0
    retain=0
    if [ "$completed" -ne 1 ]; then
        # Undo desktop integration before restoring the program directory.
        if [ "$desktop_written" -eq 1 ]; then
            if [ -f "$stage/previous.desktop" ]; then
                cp -p "$stage/previous.desktop" "$desktop_path" || retain=1
            else
                rm -f -- "$desktop_path" || retain=1
            fi
        fi
        if [ "$command_created" -eq 1 ]; then rm -f -- "$command_path" || retain=1; fi
        if [ "$installed" -eq 1 ]; then mv -- "$install_dir" "$stage/failed" || retain=1; fi
        if [ "$replaced" -eq 1 ]; then mv -- "$stage/previous" "$install_dir" || retain=1; fi
    fi

    # The private staging directory is created under /opt, on the same filesystem as the installation.
    if [ "$retain" -eq 0 ]; then
        case "$stage" in /opt/.ASLM-install.*) rm -rf -- "$stage" ;; esac
    else
        printf 'ASLM: recovery files retained in %s\n' "$stage" >&2
    fi
    exit "$result"
}

# Installs only missing native runtime packages for the supported Ubuntu release; .NET is bundled.
install_dependencies()
{
    . /etc/os-release
    [ "$ID" = ubuntu ] && [ "$VERSION_ID" = 26.04 ] ||
        fail 'Automatic installation currently requires Ubuntu 26.04 AMD64.'

    missing=
    for package in ca-certificates libbrotli1 libc6 libgcc-s1 libgssapi-krb5-2 libicu78 \
        libssl3t64 libstdc++6 tzdata zlib1g libgtk-4-1 libwebkitgtk-6.0-4 \
        libfontconfig1 libfreetype6 xdg-utils; do
        if [ "$(dpkg-query -W -f='${Status}' "$package" 2>/dev/null || true)" != 'install ok installed' ]; then
            missing="$missing $package"
        fi
    done
    if [ -n "$missing" ]; then
        apt-get update
        # Package names come only from the fixed list above, not from installer arguments or user data.
        apt-get install --no-install-recommends -y $missing
    fi
}

# Validates the embedded payload before modifying the system installation.
extract_payload()
{
    payload_line=$(awk '/^__ASLM_PAYLOAD_BELOW__$/ { print NR + 1; exit }' "$installer")
    [ -n "$payload_line" ] || fail 'Embedded payload was not found.'
    tail -n +"$payload_line" "$installer" > "$stage/payload.tar.gz"
    printf '%s  %s\n' "$payload_digest" "$stage/payload.tar.gz" | sha256sum --check --status ||
        fail 'Installer payload is damaged.'

    # The outer archive contains exactly three regular files, with no paths or links.
    entries=$(tar -tzf "$stage/payload.tar.gz" | LC_ALL=C sort)
    expected=$(printf '%s\n' ASLM appicon.svg aslm-base.tar.gz | LC_ALL=C sort)
    [ "$entries" = "$expected" ] || fail 'Unexpected installer payload layout.'
    tar -tvzf "$stage/payload.tar.gz" | awk 'substr($0, 1, 1) != "-" { bad=1 } END { exit bad }' ||
        fail 'Installer payload contains a directory or link.'
    mkdir "$stage/new"
    tar -xzf "$stage/payload.tar.gz" -C "$stage/new" --no-same-owner
    chmod 755 "$stage/new" "$stage/new/ASLM"
    chmod 644 "$stage/new/aslm-base.tar.gz" "$stage/new/appicon.svg"
}

# Replaces only the shared launcher/base archive and registers the normal-user desktop entry.
install_application()
{
    # Refuse to replace unrelated files or follow installation-directory links.
    [ ! -L "$install_dir" ] || fail "$install_dir is a symbolic link."
    if [ -e "$install_dir" ]; then
        [ -d "$install_dir" ] && [ -f "$install_dir/ASLM" ] && [ -f "$install_dir/aslm-base.tar.gz" ] ||
            fail "$install_dir is not an ASLM installation."
    fi
    if [ -e "$command_path" ] || [ -L "$command_path" ]; then
        [ -L "$command_path" ] && [ "$(readlink "$command_path")" = "$install_dir/ASLM" ] ||
            fail "$command_path already belongs to another application."
    fi
    [ ! -L "$desktop_path" ] || fail "$desktop_path is a symbolic link."
    if [ -e "$desktop_path" ]; then
        grep -qx 'Exec=/opt/ASLM/ASLM' "$desktop_path" || fail "$desktop_path belongs to another application."
        cp -p "$desktop_path" "$stage/previous.desktop"
    fi

    # Directory renames keep a complete previous installation available until integration succeeds.
    if [ -d "$install_dir" ]; then
        mv -- "$install_dir" "$stage/previous"
        replaced=1
    fi
    mv -- "$stage/new" "$install_dir"
    installed=1
    mkdir -p /usr/local/bin /usr/local/share/applications
    if [ ! -L "$command_path" ]; then
        ln -s "$install_dir/ASLM" "$command_path"
        command_created=1
    fi
    # Use a regular source file for both initial creation and replacement of the desktop entry.
    cat > "$stage/aslm.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=ASLM
Comment=Advanced System Language Models
Exec=/opt/ASLM/ASLM
Icon=/opt/ASLM/appicon.svg
Terminal=false
Categories=Utility;
DESKTOP
    desktop_written=1
    install -m 644 "$stage/aslm.desktop" "$desktop_path"
    completed=1
}

# Elevate only this system installation; profiles are initialized later by the non-root launcher.
[ "$(uname -s)" = Linux ] && [ "$(uname -m)" = x86_64 ] || fail 'This installer requires Linux AMD64.'
case "$payload_digest" in @*) fail 'Run the packaged installer, not its source template.' ;; esac
installer=$(readlink -f -- "$0")
if [ "$(id -u)" -ne 0 ]; then
    command -v sudo >/dev/null 2>&1 || fail 'Run this installer with administrator privileges.'
    exec sudo /bin/sh "$installer" "$@"
fi
[ "$#" -eq 0 ] || fail 'This installer takes no arguments; the system directory is /opt/ASLM.'
umask 022
mkdir -p /opt
stage=$(mktemp -d /opt/.ASLM-install.XXXXXXXX)
trap cleanup 0
trap 'exit 1' HUP INT TERM
extract_payload
install_dependencies
install_application
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database /usr/local/share/applications || true
fi
printf 'ASLM %s installed in %s.\nRun ASLM from the application menu or with: aslm\nDo not run ASLM with sudo.\n' "$version" "$install_dir"
exit 0
__ASLM_PAYLOAD_BELOW__
