#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

PROGRAM="${0##*/}"
REPOSITORY="redstore-noob/Mofumofu"
MODE=""
DOMAIN=""
INSTANCE="survival"
SERVER_DIR=""
RELEASE="latest"
SHOW_PAIR=0
INSTALL_DEPS=1
AGENT_PORT="${MOFU_AGENT_PORT:-28766}"
PROBE_PORT="${MOFU_PROBE_PORT:-28765}"
MC_VERSION="${MOFU_MC_VERSION:-1.21.4}"
MEMORY_MB="${MOFU_MEMORY_MB:-2048}"

log() { printf '[mofumofu] %s\n' "$*"; }
die() { printf '[mofumofu] ERROR: %s\n' "$*" >&2; exit 1; }

usage() {
    cat <<EOF
Usage:
  $PROGRAM --mode agent --domain admin.example.com --instance survival [--release v1.2.3]
  $PROGRAM --mode probe --domain admin.example.com --instance survival --server-dir /srv/panel/server
  $PROGRAM --show-pair --instance survival [--server-dir /srv/panel/server]

Options:
  --mode agent|probe   agent: full VPS lifecycle; probe: existing panel server
  --domain DOMAIN      public HTTPS domain used by Caddy
  --instance ID        safe instance id (letters, digits, dot, underscore, dash)
  --server-dir PATH    existing server directory (required for probe)
  --release TAG        release tag; default: latest
  --show-pair          print and securely remove the one-time pairing file
  --no-install-deps    do not install apt dependencies

Environment: MOFU_MC_VERSION, MOFU_MEMORY_MB, MOFU_AGENT_PORT, MOFU_PROBE_PORT
EOF
}

while (($#)); do
    case "$1" in
        --mode) MODE="${2:?}"; shift 2 ;;
        --domain) DOMAIN="${2:?}"; shift 2 ;;
        --instance) INSTANCE="${2:?}"; shift 2 ;;
        --server-dir) SERVER_DIR="${2:?}"; shift 2 ;;
        --release) RELEASE="${2:?}"; shift 2 ;;
        --show-pair) SHOW_PAIR=1; shift ;;
        --no-install-deps) INSTALL_DEPS=0; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "Unknown argument: $1" ;;
    esac
done

[[ "$INSTANCE" =~ ^[A-Za-z0-9._-]+$ && "$INSTANCE" != *..* ]] || die "Invalid instance id"
((EUID == 0)) || die "Run as root (sudo $PROGRAM ...)"

STATE_ROOT="/var/lib/mofumofu/nekoprobe"
if [[ -z "$SERVER_DIR" ]]; then
    if [[ "$MODE" == "agent" || "$SHOW_PAIR" == 1 ]]; then
        SERVER_DIR="/srv/mofumofu/servers/$INSTANCE"
    else
        die "--server-dir is required for probe mode"
    fi
fi
SERVER_DIR="$(readlink -m "$SERVER_DIR")"
PAIR_CANDIDATES=(
    "$STATE_ROOT/$INSTANCE/pairing.txt"
    "$SERVER_DIR/plugins/NekoProbe/pairing.txt"
    "$SERVER_DIR/plugins/NekoProbe/pairing.link"
)

if ((SHOW_PAIR)); then
    for pair_file in "${PAIR_CANDIDATES[@]}"; do
        if [[ -f "$pair_file" ]]; then
            cat "$pair_file"
            printf '\n'
            if command -v shred >/dev/null 2>&1; then shred -u "$pair_file"; else rm -f "$pair_file"; fi
            log "Pairing file consumed. Keep the printed link secret until pairing succeeds."
            exit 0
        fi
    done
    die "No pairing file found. It may already have been consumed."
fi

[[ "$MODE" == "agent" || "$MODE" == "probe" ]] || die "Use --mode agent or --mode probe"
[[ "$DOMAIN" =~ ^[A-Za-z0-9.-]+$ ]] || die "A valid public --domain is required"
[[ "$DOMAIN" != *..* ]] || die "Invalid domain"
[[ "$AGENT_PORT" =~ ^[0-9]+$ && "$PROBE_PORT" =~ ^[0-9]+$ ]] || die "Ports must be numeric"
[[ "$MEMORY_MB" =~ ^[0-9]+$ ]] || die "MOFU_MEMORY_MB must be numeric"
((AGENT_PORT > 1024 && AGENT_PORT < 65536 && PROBE_PORT > 1024 && PROBE_PORT < 65536)) || die "Invalid backend port"
((MEMORY_MB >= 512 && MEMORY_MB <= 1048576)) || die "MOFU_MEMORY_MB must be between 512 and 1048576"
[[ "$INSTANCE" != *$'\n'* && "$DOMAIN" != *$'\n'* ]] || die "Invalid newline in input"

install_dependencies() {
    ((INSTALL_DEPS)) || return
    command -v apt-get >/dev/null 2>&1 || die "Automatic dependency installation currently supports apt-based distributions"
    export DEBIAN_FRONTEND=noninteractive
    apt-get update
    apt-get install -y curl ca-certificates jq openssl unzip gpg systemd util-linux openjdk-21-jre-headless
    if ! command -v caddy >/dev/null 2>&1; then
        log "Installing Caddy from its official apt repository"
        install -d -m 0755 /usr/share/keyrings /etc/apt/sources.list.d
        curl -fsSL "https://dl.cloudsmith.io/public/caddy/stable/gpg.key" | \
            gpg --dearmor --yes -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
        curl -fsSL "https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt" \
            -o /etc/apt/sources.list.d/caddy-stable.list
        apt-get update
        apt-get install -y caddy
    fi
}

download_verified() {
    local url="$1" sha_url="$2" destination="$3" expected actual temp sha_temp
    temp="${destination}.download"
    sha_temp="${destination}.sha256.download"
    curl -fL --retry 3 --connect-timeout 15 "$url" -o "$temp"
    curl -fL --retry 3 --connect-timeout 15 "$sha_url" -o "$sha_temp"
    expected="$(awk 'NR==1 {print $1}' "$sha_temp")"
    [[ "$expected" =~ ^[0-9a-fA-F]{64}$ ]] || die "Invalid checksum downloaded for $url"
    actual="$(sha256sum "$temp" | awk '{print $1}')"
    [[ "${expected,,}" == "$actual" ]] || die "SHA-256 mismatch for $url"
    chmod 0644 "$temp"
    mv -f "$temp" "$destination"
    rm -f "$sha_temp"
}

download_with_sha() {
    local url="$1" expected="$2" destination="$3" actual temp
    temp="${destination}.download"
    curl -fL --retry 3 --connect-timeout 15 "$url" -o "$temp"
    actual="$(sha256sum "$temp" | awk '{print $1}')"
    [[ "${expected,,}" == "$actual" ]] || die "SHA-256 mismatch for $url"
    mv -f "$temp" "$destination"
}

release_url() {
    local asset="$1"
    if [[ "$RELEASE" == "latest" ]]; then
        printf 'https://github.com/%s/releases/latest/download/%s' "$REPOSITORY" "$asset"
    else
        printf 'https://github.com/%s/releases/download/%s/%s' "$REPOSITORY" "$RELEASE" "$asset"
    fi
}

write_caddy() {
    local backend_port="$1" snippet="/etc/caddy/Caddyfile.d/mofumofu-${INSTANCE}.caddy"
    install -d -m 0755 /etc/caddy/Caddyfile.d
    cat >"$snippet" <<EOF
${DOMAIN} {
    encode zstd gzip
    @internal path /internal/*
    respond @internal 404
    request_body {
        max_size 3MB
    }
    reverse_proxy 127.0.0.1:${backend_port}
}
EOF
    if [[ ! -f /etc/caddy/Caddyfile ]]; then
        printf 'import /etc/caddy/Caddyfile.d/*.caddy\n' >/etc/caddy/Caddyfile
    elif ! grep -Fq '/etc/caddy/Caddyfile.d/*.caddy' /etc/caddy/Caddyfile; then
        printf '\nimport /etc/caddy/Caddyfile.d/*.caddy\n' >>/etc/caddy/Caddyfile
    fi
    caddy validate --config /etc/caddy/Caddyfile
    systemctl enable --now caddy
    systemctl reload caddy
}

install_probe() {
    local probe_dir="$SERVER_DIR/plugins"
    install -d -m 0755 "$probe_dir"
    download_verified "$(release_url NekoProbe.jar)" "$(release_url NekoProbe.jar.sha256)" \
        "$probe_dir/NekoProbe.jar"
}

install_agent_mode() {
    local arch agent_asset server_root state_dir="$STATE_ROOT/$INSTANCE"
    server_root="$(dirname "$SERVER_DIR")"
    arch="$(uname -m)"
    case "$arch" in
        x86_64|amd64) arch="x64" ;;
        aarch64|arm64) arch="arm64" ;;
        *) die "Unsupported CPU architecture: $arch" ;;
    esac
    agent_asset="Mofumofu.ServerAgent-linux-${arch}"

    id mofumofu >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin mofumofu
    install -d -o root -g root -m 0755 /opt/mofumofu-agent
    install -d -o root -g mofumofu -m 0750 /etc/mofumofu
    install -d -o mofumofu -g mofumofu -m 0750 "$server_root" "$SERVER_DIR" "$state_dir"
    install -d -o mofumofu -g mofumofu -m 0750 "$SERVER_DIR/plugins" "$SERVER_DIR/backups"

    download_verified "$(release_url "$agent_asset")" "$(release_url "$agent_asset.sha256")" \
        "/opt/mofumofu-agent/Mofumofu.ServerAgent"
    chmod 0755 /opt/mofumofu-agent/Mofumofu.ServerAgent
    install_probe

    if [[ ! -f "$SERVER_DIR/server.jar" ]]; then
        log "Resolving Paper $MC_VERSION and its publisher SHA-256"
        local metadata core_url core_sha
        metadata="$(curl -fsSL "https://api.papermc.io/v2/projects/paper/versions/${MC_VERSION}/builds")"
        core_url="$(jq -r '.builds[-1] | "https://api.papermc.io/v2/projects/paper/versions/'"$MC_VERSION"'/builds/\(.build)/downloads/\(.downloads.application.name)"' <<<"$metadata")"
        core_sha="$(jq -r '.builds[-1].downloads.application.sha256' <<<"$metadata")"
        [[ "$core_sha" =~ ^[0-9a-fA-F]{64}$ ]] || die "Paper publisher did not provide a valid SHA-256"
        download_with_sha "$core_url" "$core_sha" "$SERVER_DIR/server.jar"
    fi
    [[ -f "$SERVER_DIR/eula.txt" ]] || printf 'eula=true\n' >"$SERVER_DIR/eula.txt"
    if [[ ! -f "$SERVER_DIR/server.properties" ]]; then
        local rcon_password
        rcon_password="$(openssl rand -hex 32)"
        cat >"$SERVER_DIR/server.properties" <<EOF
server-port=25565
max-players=20
motd=Mofumofu remote server
enable-rcon=true
rcon.port=25575
rcon.password=${rcon_password}
broadcast-rcon-to-ops=false
EOF
        chmod 0600 "$SERVER_DIR/server.properties"
    fi
    if [[ ! -f "$SERVER_DIR/server.json" ]]; then
        jq -n --arg id "$INSTANCE" --arg name "$INSTANCE" --arg version "$MC_VERSION" \
            --argjson memory "$MEMORY_MB" \
            '{id:$id,name:$name,core:"paper",coreVersion:"",mcVersion:$version,port:25565,maxPlayers:20,javaPath:"/usr/bin/java",memoryMB:$memory,memoryMinMB:($memory/2),extraJavaArgs:[],autoRestart:true}' \
            >"$SERVER_DIR/server.json"
    fi

    cat >"/etc/mofumofu/${INSTANCE}.json" <<EOF
{
  "ServerAgent": {
    "ServerRootDirectory": "$server_root",
    "InstanceId": "$INSTANCE",
    "ProbeRegistryPath": "$state_dir/registry.json",
    "ProbeBaseUrl": "http://127.0.0.1:$PROBE_PORT",
    "BindUrl": "http://127.0.0.1:$AGENT_PORT",
    "EventJournalPath": "$state_dir/events.jsonl",
    "AuditPath": "$state_dir/audit.jsonl"
  }
}
EOF
    chown root:mofumofu "/etc/mofumofu/${INSTANCE}.json"
    chmod 0640 "/etc/mofumofu/${INSTANCE}.json"

    cat >"/etc/systemd/system/mofumofu-agent-${INSTANCE}.service" <<EOF
[Unit]
Description=Mofumofu ServerAgent ($INSTANCE)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=mofumofu
Group=mofumofu
WorkingDirectory=$SERVER_DIR
Environment=MOFU_NEKO_INSTANCE_ID=$INSTANCE
Environment=MOFU_NEKO_STATE_DIR=$state_dir
Environment=MOFU_NEKO_PUBLIC_URL=https://$DOMAIN
Environment=MOFU_NEKO_PORT=$PROBE_PORT
Environment=MOFU_NEKO_AGENT_CALLBACK=http://127.0.0.1:$AGENT_PORT/internal/probe/events
ExecStart=/opt/mofumofu-agent/Mofumofu.ServerAgent --config /etc/mofumofu/${INSTANCE}.json --urls http://127.0.0.1:$AGENT_PORT
Restart=on-failure
RestartSec=5
KillMode=control-group
TimeoutStopSec=45
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
ReadWritePaths=$server_root $state_dir

[Install]
WantedBy=multi-user.target
EOF

    chown -R mofumofu:mofumofu "$SERVER_DIR" "$state_dir"
    systemctl daemon-reload

    if [[ ! -f "$state_dir/registry.json" ]]; then
        log "Starting a one-time local Paper bootstrap to initialize NekoProbe"
        systemctl reset-failed "mofumofu-probe-bootstrap-${INSTANCE}" 2>/dev/null || true
        systemd-run --quiet --unit="mofumofu-probe-bootstrap-${INSTANCE}" \
            --property="User=mofumofu" --property="Group=mofumofu" \
            --property="WorkingDirectory=$SERVER_DIR" \
            --property="Environment=MOFU_NEKO_INSTANCE_ID=$INSTANCE" \
            --property="Environment=MOFU_NEKO_STATE_DIR=$state_dir" \
            --property="Environment=MOFU_NEKO_PUBLIC_URL=https://$DOMAIN" \
            --property="Environment=MOFU_NEKO_PORT=$PROBE_PORT" \
            --property="Environment=MOFU_NEKO_AGENT_CALLBACK=http://127.0.0.1:$AGENT_PORT/internal/probe/events" \
            /usr/bin/java "-Xms$((MEMORY_MB / 2))M" "-Xmx${MEMORY_MB}M" -jar "$SERVER_DIR/server.jar" nogui
        for _ in $(seq 1 120); do
            [[ -f "$state_dir/registry.json" && -f "$state_dir/pairing.txt" ]] && break
            sleep 1
        done
        [[ -f "$state_dir/registry.json" ]] || die "Probe initialization failed; inspect journalctl -u mofumofu-probe-bootstrap-${INSTANCE}"
        systemctl stop "mofumofu-probe-bootstrap-${INSTANCE}" || true
    fi

    systemctl enable --now "mofumofu-agent-${INSTANCE}"
    write_caddy "$AGENT_PORT"
    log "Agent mode installed. Run: sudo $PROGRAM --show-pair --instance $INSTANCE"
}

install_probe_mode() {
    [[ -d "$SERVER_DIR" ]] || die "Panel server directory does not exist: $SERVER_DIR"
    [[ -f "$SERVER_DIR/server.jar" || -n "$(compgen -G "$SERVER_DIR/*.jar" || true)" ]] || \
        die "No existing server jar found; probe mode does not create a server"
    install_probe
    install -d -m 0700 "$SERVER_DIR/plugins/NekoProbe"
    cat >"$SERVER_DIR/plugins/NekoProbe/config.yml" <<EOF
instanceId: $INSTANCE
port: $PROBE_PORT
publicUrl: https://$DOMAIN
stateDir: $SERVER_DIR/plugins/NekoProbe
agentCallback: ""
EOF
    chmod 0600 "$SERVER_DIR/plugins/NekoProbe/config.yml"
    chown -R "$(stat -c '%U:%G' "$SERVER_DIR")" "$SERVER_DIR/plugins/NekoProbe" "$SERVER_DIR/plugins/NekoProbe.jar"
    write_caddy "$PROBE_PORT"
    log "Probe installed. Restart MC from your panel, then run: sudo $PROGRAM --show-pair --instance $INSTANCE --server-dir $SERVER_DIR"
}

install_dependencies
for tool in curl jq sha256sum caddy java systemctl; do command -v "$tool" >/dev/null 2>&1 || die "Missing dependency: $tool"; done
case "$MODE" in
    agent) install_agent_mode ;;
    probe) install_probe_mode ;;
esac

if command -v ss >/dev/null 2>&1; then
    public_backends="$(ss -ltnH | awk '{print $4}' | \
        grep -E "(^0\.0\.0\.0:|^\[::\]:|^\*:)(25575|${PROBE_PORT}|${AGENT_PORT})$" || true)"
    [[ -z "$public_backends" ]] || \
        die "RCON/Agent/Probe must not listen on a wildcard address: $public_backends"
    if command -v ufw >/dev/null 2>&1 && ufw status | grep -q '^Status: active'; then
        ufw allow 443/tcp >/dev/null
        ufw deny 25575/tcp >/dev/null
        ufw deny "$PROBE_PORT"/tcp >/dev/null
        if [[ "$MODE" == "agent" ]]; then
            ufw deny "$AGENT_PORT"/tcp >/dev/null
            ufw allow 25565/tcp >/dev/null
        fi
    else
        log "Ensure the host firewall allows 443 and the game port, but never publishes RCON or backend ports."
    fi
fi
