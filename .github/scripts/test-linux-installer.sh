#!/bin/sh
# Copyright NEXTGGTECH. Apache License 2.0.

set -eu
source_path=$1
test_script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
test_root=$(mktemp -d "${TMPDIR:-/tmp}/aslm-installer-tests.XXXXXXXX")
trap 'rm -rf -- "$test_root"' 0

# Extract only the shipped definitions, leaving installation and package management unexecuted.
awk '
    /^# Confirm as the invoking user first;/ { exit }
    found { print }
    /^#__ASLM_INSTALLER_TEMPLATE_BELOW__$/ { found = 1 }
' "$source_path" > "$test_root/functions.sh"
bash -n "$source_path"
sh -n "$test_root/functions.sh"
. "$test_root/functions.sh"
mkdir "$test_root/bin"
for manager in apt-get dnf zypper pacman; do
    printf '#!/bin/sh\nexit 0\n' > "$test_root/bin/$manager"
    chmod 755 "$test_root/bin/$manager"
done
PATH="$test_root/bin:$PATH"
export PATH

# Check distribution classification without consulting the test host's actual os-release file.
verify_family()
{
    printf 'ID="%s"\nID_LIKE="%s"\nVERSION_ID="%s"\n' "$1" "$2" "$3" > "$test_root/os-release"
    detect_distribution "$test_root/os-release"
    [ "$family" = "$4" ] || { echo "Incorrect family for $1: $family" >&2; exit 1; }
}

verify_family ubuntu debian 24.04 debian
verify_family debian '' 13 debian
verify_family linuxmint 'ubuntu debian' 22 debian
verify_family fedora '' 43 fedora
verify_family almalinux 'rhel centos fedora' 10 el
verify_family rocky 'rhel centos fedora' 10 el
verify_family opensuse-leap 'suse opensuse' 16.0 suse
verify_family opensuse-tumbleweed 'opensuse suse' 20261115 suse
verify_family arch '' '' arch
verify_family manjaro arch '' arch

# Verify that old releases and unlisted derivatives reach the native-library check instead of being rejected.
verify_family ubuntu debian 22.04 debian
verify_family debian '' 12 debian
verify_family fedora '' 42 fedora
verify_family almalinux 'rhel fedora' 9 el
verify_family opensuse-leap suse 15.6 suse
verify_family custom-debian debian '' debian
verify_family custom-rpm fedora '' fedora

# Verify that an unknown distribution can select each available package manager without os-release hints.
original_path=$PATH
printf 'ID=custom\n' > "$test_root/os-release"
for manager in apt-get dnf zypper pacman; do
    mkdir "$test_root/only-$manager"
    cp "$test_root/bin/$manager" "$test_root/only-$manager/$manager"
    PATH="$test_root/only-$manager"
    detect_distribution "$test_root/os-release"
    PATH=$original_path
    [ "$package_manager" = "$manager" ]
    case "$manager" in
        apt-get) [ "$family" = debian ] ;;
        dnf) [ "$family" = fedora ] ;;
        zypper) [ "$family" = suse ] ;;
        pacman) [ "$family" = arch ] ;;
    esac
done

# Verify that missing metadata and package managers do not prevent checking preinstalled libraries.
mkdir "$test_root/no-managers"
PATH="$test_root/no-managers"
detect_distribution "$test_root/os-release"
[ "$family" = manual ] && [ -z "$package_manager" ]
detect_distribution "$test_root/missing-os-release"
[ "$family" = manual ] && [ -z "$package_manager" ]
PATH=$original_path

# Verify that a local archive needs no download client and missing remote-download tools are reported clearly.
mkdir "$test_root/installer-tools"
for utility in awk tar gzip sha256sum; do
    cp "$test_root/bin/apt-get" "$test_root/installer-tools/$utility"
done
(
    PATH="$test_root/installer-tools"
    family=manual
    archive_source=local-archive
    install_installer_tools
    archive_source=
    if (install_installer_tools) > "$test_root/missing-tools.log" 2>&1; then exit 1; fi
)
grep -q 'Install the missing installer tools manually.*curl' "$test_root/missing-tools.log"

# Verify the runtime probe without starting a GUI or consulting the test machine's native libraries.
payload="$test_root/payload"
stage="$test_root/runtime-stage"
mkdir -p "$payload/App" "$stage"
ASLM_TEST_RUNTIME_READY="$test_root/runtime-ready"
ASLM_TEST_RUNTIME_CALLS="$test_root/runtime-calls"
export ASLM_TEST_RUNTIME_READY ASLM_TEST_RUNTIME_CALLS
cat > "$payload/App/ASLM" <<'PROBE'
#!/bin/sh
set -eu
[ "$#" -eq 1 ] && [ "$1" = --check-runtime ] || exit 2
printf 'probe\n' >> "$ASLM_TEST_RUNTIME_CALLS"
if [ -f "$ASLM_TEST_RUNTIME_READY" ]; then
    printf 'ASLM_RUNTIME_OK\n'
    exit 0
fi
printf '%s\n' 'GTK 4.8.3 is installed; GTK 4.12.0 or later is required.' >&2
exit 1
PROBE
chmod 755 "$payload/App/ASLM"
touch "$ASLM_TEST_RUNTIME_READY"
(
    # Reject an unexpected package-manager call when native libraries already work.
    install_dependencies()
    {
        fail 'Compatible native libraries must not require package installation.'
    }

    verify_family ubuntu debian 22.04 debian
    ensure_runtime_dependencies
    PATH="$test_root/no-managers"
    detect_distribution "$test_root/os-release"
    PATH=$original_path
    [ "$family" = manual ]
    ensure_runtime_dependencies
)
[ "$(wc -l < "$ASLM_TEST_RUNTIME_CALLS")" -eq 2 ]

# Verify that a failed probe triggers dependency installation and is repeated before accepting the runtime.
rm "$ASLM_TEST_RUNTIME_READY"
: > "$ASLM_TEST_RUNTIME_CALLS"
(
    # Simulate installing the missing library without changing system packages.
    install_dependencies()
    {
        touch "$ASLM_TEST_RUNTIME_READY"
        printf 'installed\n' >> "$test_root/runtime-installs"
    }

    ensure_runtime_dependencies
) > "$test_root/runtime-repair.log" 2>&1
[ "$(wc -l < "$ASLM_TEST_RUNTIME_CALLS")" -eq 2 ]
[ "$(wc -l < "$test_root/runtime-installs")" -eq 1 ]
grep -q 'GTK 4.8.3 is installed' "$test_root/runtime-repair.log"

# Verify that an incompatible runtime still fails after package installation and explains the actual library error.
rm "$ASLM_TEST_RUNTIME_READY"
if (
    # Leave the old library in place to simulate repositories that cannot provide a compatible version.
    install_dependencies()
    {
        return 0
    }

    ensure_runtime_dependencies
) > "$test_root/runtime-failure.log" 2>&1; then exit 1; fi
grep -q 'GTK 4.8.3 is installed' "$test_root/runtime-failure.log"
grep -q 'The existing installation was kept' "$test_root/runtime-failure.log"

# Verify that missing libraries on a manually managed distribution produce actionable diagnostics.
family=manual
package_manager=
if (ensure_runtime_dependencies) > "$test_root/runtime-manual.log" 2>&1; then exit 1; fi
grep -q 'GTK 4.8.3 is installed' "$test_root/runtime-manual.log"
grep -q 'Install the reported native dependencies manually' "$test_root/runtime-manual.log"

# Simulate an EL repository lacking WebKit without invoking a real package manager or adding a repository.
dnf_candidate()
{
    return 0
}

family=el
if (install_dnf_dependencies) > "$test_root/epel.log" 2>&1; then exit 1; fi
grep -q 'Enable the matching EPEL and CRB repositories manually' "$test_root/epel.log"

# Execute the SUSE dependency path against Zypper XML and verify that archive inspection has an awk provider.
zypper()
{
    case " $* " in
        *' search '*) printf '<solvable edition="2.50.0-1"/>\n' ;;
        *' install '*) printf '%s\n' "$*" >> "$test_root/suse-packages" ;;
    esac
}

install_suse_dependencies
grep -Eq '(^| )gawk($| )' "$test_root/suse-packages"

# Exercise desktop preparation with package-manager doubles, without installing packages or touching /opt.
sed -n '/^prepare_desktop()/,/^}/p' "$test_script_dir/verify-linux-distribution.sh" > "$test_root/desktop-functions.sh"
. "$test_root/desktop-functions.sh"
results_root="$test_root/desktop-results"
install_dir="$test_root/installation"
installer="$test_root/without-epel.sh"
archive="$test_root/release.tar.gz"
mkdir "$results_root"
printf '#!/bin/sh\nprintf "Enable EPEL manually.\\n" >&2\nexit 1\n' > "$installer"

# Record requests made by the desktop setup instead of changing the test host's repositories or packages.
dnf()
{
    printf '%s\n' "$*" >> "$test_root/desktop-packages"
}

printf 'ID=almalinux\n' > "$test_root/desktop-os-release"
display_backend=x11
prepare_desktop "$test_root/desktop-os-release"
[ "$display_backend" = wayland ]
grep -Eq '(^| )weston($| )' "$test_root/desktop-packages"
if grep -q Xvfb "$test_root/desktop-packages"; then exit 1; fi
: > "$test_root/desktop-packages"
printf 'ID=fedora\n' > "$test_root/desktop-os-release"
display_backend=x11
prepare_desktop "$test_root/desktop-os-release"
[ "$display_backend" = x11 ]
grep -q xorg-x11-server-Xvfb "$test_root/desktop-packages"
printf 'Linux installer and desktop policy checks passed.\n'
