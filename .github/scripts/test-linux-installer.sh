#!/bin/sh
# Copyright NEXTGGTECH. Apache License 2.0.

set -eu
source_path=$1
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
for manager in apt dnf zypper pacman; do
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

# Require a clear refusal for a distribution below its declared support floor.
verify_refusal()
{
    if (verify_family "$1" "$2" "$3" unused) > "$test_root/refusal.log" 2>&1; then
        echo "Unsupported distribution was accepted: $1 $3" >&2
        exit 1
    fi
    grep -q 'or later is required' "$test_root/refusal.log"
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
verify_refusal ubuntu debian 22.04
verify_refusal debian '' 12
verify_refusal fedora '' 42
verify_refusal almalinux 'rhel fedora' 9
verify_refusal opensuse-leap suse 15.6
require_gtk_version 4.12.0
require_gtk_version 4.14.2+ds-1ubuntu1
require_gtk_version 1:4.18.6-2
if (require_gtk_version 4.8.3) > "$test_root/gtk.log" 2>&1; then exit 1; fi
grep -q 'GTK 4.12 or later is required' "$test_root/gtk.log"

# Simulate an EL repository lacking WebKit without invoking a real package manager or adding a repository.
dnf_candidate()
{
    [ "$1" != gtk4 ] || printf '4.14.5\n'
}

family=el
if (install_dnf_dependencies) > "$test_root/epel.log" 2>&1; then exit 1; fi
grep -q 'Enable the matching EPEL and CRB repositories manually' "$test_root/epel.log"
printf 'Linux installer policy checks passed.\n'
