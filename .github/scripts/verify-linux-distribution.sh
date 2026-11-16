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
install_dir=/opt/ASLM
home_dir=/home/aslm-check
profile="$home_dir/.local/share/ASLM"
display_pid=
display_backend=x11
mkdir -p "$results_root"

# Preserve application diagnostics after either success or failure without changing the original exit status.
collect_results()
{
    result=$?
    trap - 0
    if [ -n "$display_pid" ]; then
        kill "$display_pid" 2>/dev/null || true
        wait "$display_pid" 2>/dev/null || true
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
    . "${1:-/etc/os-release}"
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
                test ! -e "$install_dir"
                dnf install -y dnf-plugins-core epel-release
                dnf config-manager --set-enabled crb
                display_backend=wayland
                dnf install -y shadow-utils util-linux dbus-daemon weston python3 dejavu-sans-fonts
            else
                dnf install -y shadow-utils util-linux dbus-x11 xorg-x11-server-Xvfb \
                    python3 dejavu-sans-fonts
            fi ;;
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
test -x "$install_dir/ASLM"
test -L /usr/local/bin/aslm
test -f /usr/local/share/applications/com.nextggtech.aslm.desktop
useradd --create-home --shell /bin/sh aslm-check

# Use Wayland on EL 10, which removed the Xorg server; all other targets retain the X11 coverage.
runtime_dir="$home_dir/.run"
install -d -m 700 -o aslm-check -g aslm-check "$runtime_dir"
if [ "$display_backend" = wayland ]; then
    x11_display=
    wayland_display=aslm-wayland
    runuser -u aslm-check -- env XDG_RUNTIME_DIR="$runtime_dir" \
        weston --backend=headless-backend.so --renderer=pixman --shell=kiosk-shell.so --socket=aslm-wayland \
        --width=1280 --height=800 --idle-time=0 --no-config > "$results_root/display.log" 2>&1 &
    display_pid=$!
    display_socket="$runtime_dir/aslm-wayland"
else
    x11_display=:99
    wayland_display=
    Xvfb :99 -screen 0 1280x800x24 -nolisten tcp -ac > "$results_root/display.log" 2>&1 &
    display_pid=$!
    display_socket=/tmp/.X11-unix/X99
fi
attempt=0
while [ ! -S "$display_socket" ]; do
    kill -0 "$display_pid" 2>/dev/null || { cat "$results_root/display.log" >&2; exit 1; }
    attempt=$((attempt + 1))
    [ "$attempt" -lt 15 ] || { echo 'The virtual display did not become ready.' >&2; exit 1; }
    sleep 1
done

# Run the shipped GTK/WebKit handlers as a normal desktop user, preserving the application's WebKit sandbox.
runuser -u aslm-check -- env HOME="$home_dir" ASLM_ROOT="$profile" XDG_DATA_HOME="$home_dir/.local/share" \
    XDG_CACHE_HOME="$home_dir/.cache" XDG_RUNTIME_DIR="$runtime_dir" \
    DISPLAY="$x11_display" WAYLAND_DISPLAY="$wayland_display" \
    GDK_BACKEND="$display_backend" GSK_RENDERER=cairo dbus-run-session -- \
    python3 "$source_root/.github/scripts/verify-linux-distribution.py" "$archive"
