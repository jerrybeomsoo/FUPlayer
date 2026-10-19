#!/usr/bin/env bash
# Install the portable binary; the enabled listener stays idle until a LAN client requests DSP.
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: install-dsp-server.sh /path/to/FUPlayer-DspServer [options]
  --user                 Install for this account (requires login lingering for boot startup)
  --listen PRIVATE_IP    Default 0.0.0.0; the server accepts only private/LAN peers
  --port NUMBER          Listener UDP port, default 58181
  --worker-start NUMBER  First worker UDP port, default 58182
  --worker-end NUMBER    Last worker UDP port, default 58213
  --cpu                  Disable GPU convolution
  --dry-run              Print the service unit without installing anything

System installation: sudo bash install-dsp-server.sh ./FUPlayer-DspServer
User installation:   bash install-dsp-server.sh ./FUPlayer-DspServer --user
EOF
}

[[ $# -gt 0 ]] || { usage; exit 2; }
if [[ $1 == --help ]]; then usage; exit 0; fi
binary=$(realpath -- "$1"); shift
scope=system; listen=0.0.0.0; port=58181; first=58182; last=58213; cpu=; dry=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --user) scope=user; shift ;;
        --listen) listen=${2:?missing address}; shift 2 ;;
        --port) port=${2:?missing port}; shift 2 ;;
        --worker-start) first=${2:?missing first port}; shift 2 ;;
        --worker-end) last=${2:?missing last port}; shift 2 ;;
        --cpu) cpu=' --cpu'; shift ;;
        --dry-run) dry=1; shift ;;
        *) printf 'Unknown option: %s\n' "$1" >&2; exit 2 ;;
    esac
done
[[ -f $binary && -x $binary ]] || { printf 'The server must be an executable file.\n' >&2; exit 2; }
[[ $listen =~ ^[0-9a-fA-F:.]+$ ]] || { printf 'Use a numeric bind address.\n' >&2; exit 2; }
for value in "$port" "$first" "$last"; do
    [[ $value =~ ^[0-9]+$ && $value -ge 1024 && $value -le 65535 ]] || { printf 'Ports must be 1024..65535.\n' >&2; exit 2; }
done
[[ $first -le $last && ( $port -lt $first || $port -gt $last ) ]] || { printf 'Listener and worker ports must not overlap.\n' >&2; exit 2; }
systemctl_args=(); identity=; hardening=; groups=
if [[ $scope == user ]]; then
    prefix="$HOME/.local/lib/fuplayer-dsp"; cache="$HOME/.cache/fuplayer-dsp"
    unitdir="$HOME/.config/systemd/user"; systemctl_args=(--user)
    if [[ $dry == 0 && $(loginctl show-user "$(id -u)" -p Linger --value) != yes ]]; then
        printf 'Boot startup requires lingering. Run: sudo loginctl enable-linger %q\n' "$(id -un)" >&2
        exit 1
    fi
else
    [[ $dry == 1 || $EUID == 0 ]] || { printf 'Run the system installation with sudo, or select --user.\n' >&2; exit 1; }
    prefix=/opt/fuplayer-dsp; cache=/var/cache/fuplayer-dsp; unitdir=/etc/systemd/system
    identity=$'User=fuplayer-dsp\nGroup=fuplayer-dsp\nCacheDirectory=fuplayer-dsp\nCacheDirectoryMode=0700'
    for group in render video; do if getent group "$group" >/dev/null; then groups+=" $group"; fi; done
    if [[ -n $groups ]]; then identity+=$'\nSupplementaryGroups='"${groups# }"; fi
    hardening=$'ProtectSystem=strict\nProtectHome=true\nPrivateTmp=true'
fi
# systemd treats % as a specifier and quotes/escapes arguments independently of a shell.
unit_quote() { local value=$1; value=${value//\\/\\\\}; value=${value//\"/\\\"}; value=${value//%/%%}; printf '"%s"' "$value"; }
executable=$(unit_quote "$prefix/FUPlayer-DspServer")
cached=$(unit_quote "$cache")
extraction=$(unit_quote "DOTNET_BUNDLE_EXTRACT_BASE_DIR=$cache/runtime")
unit=$(cat <<EOF
# Managed by FUPlayer install-dsp-server.sh
[Unit]
Description=FUPlayer Network DSP Server
After=network.target

[Service]
Type=simple
$identity
ExecStart=$executable --listen $listen --port $port --worker-port-start $first --worker-port-end $last --cache $cached$cpu
Environment=$extraction
Restart=on-failure
RestartSec=2
TimeoutStopSec=10
KillMode=control-group
NoNewPrivileges=true
$hardening
MemoryAccounting=true
UMask=0077

[Install]
WantedBy=$(if [[ $scope == user ]]; then printf default.target; else printf multi-user.target; fi)
EOF
)
if [[ $dry == 1 ]]; then printf '%s\n' "$unit"; exit 0; fi
unitpath="$unitdir/fuplayer-dsp.service"
if [[ -e $unitpath ]] && ! head -n 1 "$unitpath" | grep -Fxq '# Managed by FUPlayer install-dsp-server.sh'; then
    printf 'Existing service was not created by this installer; left it unchanged: %s\n' "$unitpath" >&2; exit 1
fi
if [[ $scope == system ]]; then
    if ! getent passwd fuplayer-dsp >/dev/null; then useradd --system --user-group --home-dir "$cache" --shell /usr/sbin/nologin fuplayer-dsp; fi
    install -d -m 0700 -o fuplayer-dsp -g fuplayer-dsp "$cache"
else
    install -d -m 0700 "$cache"
fi
install -d -m 0755 "$prefix" "$unitdir"
systemctl "${systemctl_args[@]}" stop fuplayer-dsp.service 2>/dev/null || true
install -m 0755 -- "$binary" "$prefix/FUPlayer-DspServer.new"
mv -f -- "$prefix/FUPlayer-DspServer.new" "$prefix/FUPlayer-DspServer"
printf '%s\n' "$unit" > "$unitpath"
systemctl "${systemctl_args[@]}" daemon-reload
systemctl "${systemctl_args[@]}" enable --now fuplayer-dsp.service
systemctl "${systemctl_args[@]}" --no-pager --full status fuplayer-dsp.service
printf '\nEnabled at boot. Allow UDP %s and %s-%s through the LAN firewall.\n' "$port" "$first" "$last"
printf 'The listener waits without loading models/GPU; workers exit when playback stops or pauses.\n'
