#!/bin/sh
# Copyright NEXTGGTECH. Apache License 2.0.

set -eu
export LC_ALL=C
export DEBIAN_FRONTEND=noninteractive

source_root=$1
artifact_root=$2
results_root=$3
installer="$artifact_root/ASLM-linux-amd64.sh"
archive="$artifact_root/ASLM-linux-amd64.tar.gz"
home_dir=/home/aslm-check
profile="$home_dir/.local/share/ASLM"
xvfb_pid=
mkdir -p "$results_root"

# Preserve application diagnostics after either success or failure without changing the original exit status.
collect_results()
{
    result=$?
    trap - 0
    if [ -n "$xvfb_pid" ]; then
        kill "$xvfb_pid" 2>/dev/null || true
        wait "$xvfb_pid" 2>/dev/null || true
    fi
    if [ -d "$profile/.aslm-update/logs" ]; then
        cp -R "$profile/.aslm-update/logs" "$results_root/application-logs" || true
    fi
    exit "$result"
}

trap collect_results 0

# Prepare a disposable test desktop; package installation by ASLM itself is exercised separately below.
prepare_desktop()
{
    . /etc/os-release
    case "$ID" in
        ubuntu|debian)
            apt-get update
            DEBIAN_FRONTEND=noninteractive apt-get install --no-install-recommends -y \
                xvfb dbus-x11 python3 util-linux passwd fonts-dejavu-core ;;
        fedora|almalinux)
            if [ "$ID" = almalinux ]; then
                # Verify the EPEL message before the test environment enables that repository.
                if printf 'yes\n' | sh "$installer" --archive "$archive" > "$results_root/without-epel.log" 2>&1; then
                    echo 'The installer unexpectedly succeeded without the required EPEL repository.' >&2
                    exit 1
                fi
                grep -q 'EPEL' "$results_root/without-epel.log"
                test ! -e /opt/ASLM
                dnf install -y dnf-plugins-core epel-release
                dnf config-manager --set-enabled crb
            fi
            dnf install -y shadow-utils util-linux dbus-x11 xorg-x11-server-Xvfb \
                python3 dejavu-sans-fonts ;;
        opensuse-leap|opensuse-tumbleweed)
            zypper --non-interactive refresh
            if [ "$ID" = opensuse-tumbleweed ]; then
                zypper --non-interactive dist-upgrade --no-recommends
            fi
            zypper --non-interactive install --no-recommends shadow util-linux dbus-1 \
                xorg-x11-server-Xvfb python3 dejavu-fonts ;;
        arch)
            # Fully upgrade a rolling test image before installing desktop tools or ASLM dependencies.
            pacman -Syu --noconfirm
            pacman -S --needed --noconfirm shadow util-linux dbus xorg-server-xvfb python ttf-dejavu ;;
        *) echo "Unexpected test distribution: $ID" >&2; exit 1 ;;
    esac
}

prepare_desktop
printf 'yes\n' | sh "$installer" --archive "$archive"
test -x /opt/ASLM/ASLM
test -L /usr/local/bin/aslm
test -f /usr/local/share/applications/com.nextggtech.aslm.desktop
useradd --create-home --shell /bin/sh aslm-check

# Start the same virtual X server on every distro without relying on a distro-specific xvfb-run wrapper.
Xvfb :99 -screen 0 1280x800x24 -nolisten tcp -ac > "$results_root/xvfb.log" 2>&1 &
xvfb_pid=$!
attempt=0
while [ ! -S /tmp/.X11-unix/X99 ]; do
    kill -0 "$xvfb_pid" 2>/dev/null || { cat "$results_root/xvfb.log" >&2; exit 1; }
    attempt=$((attempt + 1))
    [ "$attempt" -lt 15 ] || { echo 'Xvfb did not become ready.' >&2; exit 1; }
    sleep 1
done

# Run the shipped GTK/WebKit handlers as a normal desktop user, preserving the application's WebKit sandbox.
runuser -u aslm-check -- env HOME="$home_dir" ASLM_ROOT="$profile" XDG_DATA_HOME="$home_dir/.local/share" \
    XDG_CACHE_HOME="$home_dir/.cache" DISPLAY=:99 GDK_BACKEND=x11 GSK_RENDERER=cairo \
    dbus-run-session -- python3 "$source_root/.github/scripts/verify-linux-distribution.py" "$archive"
