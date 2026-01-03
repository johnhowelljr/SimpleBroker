\
#!/usr/bin/env bash
set -euo pipefail

# SimpleEndpointBus - Proxmox LXC broker creator (GitHub Release artifact)
#
# What it does:
# - Prompts for CT settings (CTID, hostname, resources, network, storage)
# - Creates a Debian 12 LXC with nesting/keyctl enabled
# - Downloads a *compiled* broker artifact from a GitHub Release (tar.gz)
# - Installs broker into /opt/simple-endpoint-bus/broker
# - Installs + enables a systemd service (simple-endpoint-bus-broker)
#
# Run this on the Proxmox host as root.

need_cmd() {
  command -v "$1" >/dev/null 2>&1 || { echo "Missing required command: $1"; exit 1; }
}

need_cmd_or_install() {
  local cmd="$1"
  local pkg="$2"
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "Installing missing dependency: $pkg"
    apt-get update -y >/dev/null
    apt-get install -y "$pkg" >/dev/null
  fi
}

github_api() {
  local url="$1"
  local token="${2:-}"
  if [[ -n "$token" ]]; then
    curl -fsSL -H "Authorization: Bearer $token" -H "Accept: application/vnd.github+json" "$url"
  else
    curl -fsSL -H "Accept: application/vnd.github+json" "$url"
  fi
}

prompt() {
  local var="$1"
  local msg="$2"
  local def="${3:-}"
  local val=""
  if [[ -n "$def" ]]; then
    read -r -p "$msg [$def]: " val
    val="${val:-$def}"
  else
    read -r -p "$msg: " val
  fi
  printf -v "$var" '%s' "$val"
}

yesno() {
  local var="$1"
  local msg="$2"
  local def="${3:-y}"
  local val=""
  read -r -p "$msg [${def}/$( [[ "$def" == "y" ]] && echo "n" || echo "y" )]: " val
  val="${val:-$def}"
  val="$(echo "$val" | tr '[:upper:]' '[:lower:]')"
  if [[ "$val" == "y" || "$val" == "yes" ]]; then
    printf -v "$var" '1'
  else
    printf -v "$var" '0'
  fi
}

need_cmd pct
need_cmd pveam
need_cmd pvesm
need_cmd awk
need_cmd sed
need_cmd grep
need_cmd cut
need_cmd tr

if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
  echo "Run as root on the Proxmox host."
  exit 1
fi

need_cmd_or_install curl curl
need_cmd_or_install tar tar
need_cmd_or_install jq jq

echo "=== SimpleEndpointBus Broker LXC Creator (GitHub Release) ==="
echo

DEFAULT_TEMPLATE="debian-12-standard_12.2-1_amd64.tar.zst"
DEFAULT_STORAGE="$(pvesm status -content rootdir 2>/dev/null | awk 'NR==2{print $1}' || true)"
DEFAULT_STORAGE="${DEFAULT_STORAGE:-local-lvm}"

prompt CTID "Container ID (CTID)" "120"
prompt HOSTNAME "Hostname" "seb-broker"
prompt PASSWORD "Root password for container" ""
prompt STORAGE "Storage (for rootfs)" "$DEFAULT_STORAGE"
prompt DISK "Disk size (e.g. 8G, 16G)" "8G"
prompt CORES "CPU cores" "2"
prompt MEMORY "Memory (MB)" "1024"
prompt SWAP "Swap (MB)" "512"
prompt BRIDGE "Network bridge" "vmbr0"

echo
echo "Network config:"
echo "  1) DHCP"
echo "  2) Static IP"
prompt NETMODE "Choose 1 or 2" "1"

IPCFG=""
STATIC_IP=""
GATEWAY=""

if [[ "$NETMODE" == "2" ]]; then
  prompt STATIC_IP "Static IP with CIDR (e.g. 10.1.1.50/24)" ""
  prompt GATEWAY "Gateway (e.g. 10.1.1.1)" ""
  IPCFG="ip=${STATIC_IP},gw=${GATEWAY}"
else
  IPCFG="ip=dhcp"
fi

echo
yesno UNPRIV "Unprivileged container?" "y"
yesno ONBOOT "Start on boot?" "y"
yesno STARTNOW "Start immediately after creation?" "y"

echo
echo "Broker artifact source (GitHub Release):"
prompt GH_REPO "GitHub repo (owner/name)" "YOURORG/SimpleEndpointBus"
prompt GH_RELEASE "Release tag (e.g. v1.0.0) or 'latest'" "latest"
prompt GH_ASSET "Asset file name (tar.gz)" "seb-broker-linux-x64.tar.gz"
yesno GH_TOKEN_USE "Use a GitHub token? (private repo or avoid rate limits)" "n"

GH_TOKEN=""
if [[ "$GH_TOKEN_USE" == "1" ]]; then
  prompt GH_TOKEN "GitHub token (repo access)" ""
fi

echo
echo "Using template: $DEFAULT_TEMPLATE"
echo "Checking template availability..."

if ! pveam list local | grep -q "$DEFAULT_TEMPLATE"; then
  echo "Template not found locally. Downloading to 'local'..."
  pveam update >/dev/null
  pveam download local "$DEFAULT_TEMPLATE"
fi

TEMPLATE_PATH="local:vztmpl/$DEFAULT_TEMPLATE"

echo
echo "Creating CT $CTID..."
NET0="name=eth0,bridge=${BRIDGE},${IPCFG}"
FEATURES="nesting=1,keyctl=1"
UNPRIV_OPT="0"
if [[ "$UNPRIV" == "1" ]]; then UNPRIV_OPT="1"; fi

pct create "$CTID" "$TEMPLATE_PATH" \
  --hostname "$HOSTNAME" \
  --password "$PASSWORD" \
  --cores "$CORES" \
  --memory "$MEMORY" \
  --swap "$SWAP" \
  --rootfs "${STORAGE}:${DISK}" \
  --net0 "$NET0" \
  --features "$FEATURES" \
  --unprivileged "$UNPRIV_OPT" \
  --onboot "$ONBOOT" \
  --start 0

if [[ "$STARTNOW" == "1" ]]; then
  echo "Starting CT $CTID..."
  pct start "$CTID"
else
  echo "CT created but not started. Start it with: pct start $CTID"
  exit 0
fi

echo "Waiting for container networking..."
sleep 3

CT_IP=""
if [[ "$NETMODE" == "2" ]]; then
  CT_IP="${STATIC_IP%%/*}"
else
  for _ in {1..25}; do
    CT_IP="$(pct exec "$CTID" -- bash -lc "ip -4 -o addr show dev eth0 | awk '{print \$4}' | cut -d/ -f1 | head -n1" 2>/dev/null || true)"
    if [[ -n "$CT_IP" ]]; then break; fi
    sleep 1
  done
fi

if [[ -z "$CT_IP" ]]; then
  echo "WARNING: Could not determine container IP automatically."
  echo "You must edit /etc/default/simple-endpoint-bus-broker inside the container and set SEB_BaseUri."
  CT_IP="CHANGE_ME"
fi

SEB_BASEURI="http://${CT_IP}:8080"

echo
echo "Downloading compiled broker artifact from GitHub..."
TMP_ART="/tmp/seb-broker-artifact.$$"
rm -rf "$TMP_ART"
mkdir -p "$TMP_ART"

if [[ "$GH_RELEASE" == "latest" ]]; then
  REL_JSON="$(github_api "https://api.github.com/repos/$GH_REPO/releases/latest" "$GH_TOKEN")"
else
  REL_JSON="$(github_api "https://api.github.com/repos/$GH_REPO/releases/tags/$GH_RELEASE" "$GH_TOKEN")"
fi

DL_URL="$(echo "$REL_JSON" | jq -r --arg NAME "$GH_ASSET" '.assets[] | select(.name==$NAME) | .browser_download_url' | head -n1)"
if [[ -z "$DL_URL" || "$DL_URL" == "null" ]]; then
  echo "ERROR: Could not find asset '$GH_ASSET' in release '$GH_RELEASE' for repo '$GH_REPO'."
  echo "Tip: verify the asset name in the GitHub Release."
  exit 1
fi

echo "Downloading: $DL_URL"
if [[ -n "$GH_TOKEN" ]]; then
  curl -fL -H "Authorization: Bearer $GH_TOKEN" -o "$TMP_ART/$GH_ASSET" "$DL_URL"
else
  curl -fL -o "$TMP_ART/$GH_ASSET" "$DL_URL"
fi

echo "Extracting..."
tar -C "$TMP_ART" -xzf "$TMP_ART/$GH_ASSET"

if [[ ! -f "$TMP_ART/SimpleEndpointBus.Broker" ]]; then
  echo "ERROR: Extracted artifact does not contain SimpleEndpointBus.Broker at the root."
  echo "Your tar.gz should be created with:"
  echo "  tar -C publish/broker/linux-x64 -czf seb-broker-linux-x64.tar.gz ."
  exit 1
fi

echo
echo "Installing broker into container..."
echo "  CTID: $CTID"
echo "  IP:   $CT_IP"
echo "  URI:  $SEB_BASEURI"
echo

pct exec "$CTID" -- bash -lc "mkdir -p /opt/simple-endpoint-bus/broker && useradd --system --home /nonexistent --shell /usr/sbin/nologin seb >/dev/null 2>&1 || true"

# Push extracted files into the container
while IFS= read -r -d '' f; do
  rel="${f#$TMP_ART/}"
  dest="/opt/simple-endpoint-bus/broker/$rel"
  pct exec "$CTID" -- bash -lc "mkdir -p \"$(dirname "$dest")\""
  pct push "$CTID" "$f" "$dest" --perms 0755 >/dev/null
done < <(find "$TMP_ART" -type f -print0)

pct exec "$CTID" -- bash -lc "chmod +x /opt/simple-endpoint-bus/broker/SimpleEndpointBus.Broker && chown -R seb:seb /opt/simple-endpoint-bus/broker"

pct exec "$CTID" -- bash -lc "cat > /etc/systemd/system/simple-endpoint-bus-broker.service <<'EOF'
[Unit]
Description=SimpleEndpointBus Broker
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
WorkingDirectory=/opt/simple-endpoint-bus/broker
ExecStart=/opt/simple-endpoint-bus/broker/SimpleEndpointBus.Broker
EnvironmentFile=-/etc/default/simple-endpoint-bus-broker
Restart=always
RestartSec=2
KillSignal=SIGINT
TimeoutStopSec=20

NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/opt/simple-endpoint-bus/broker
RestrictSUIDSGID=true
LockPersonality=true

User=seb
Group=seb

[Install]
WantedBy=multi-user.target
EOF"

pct exec "$CTID" -- bash -lc "cat > /etc/default/simple-endpoint-bus-broker <<EOF
SEB_BaseUri=${SEB_BASEURI}
ASPNETCORE_URLS=http://0.0.0.0:8080
EOF"

pct exec "$CTID" -- bash -lc "systemctl daemon-reload && systemctl enable --now simple-endpoint-bus-broker"

rm -rf "$TMP_ART" || true

echo
echo "✅ Done."
echo
echo "Health check:"
echo "  curl ${SEB_BASEURI}/health"
echo
echo "Logs:"
echo "  pct exec ${CTID} -- journalctl -u simple-endpoint-bus-broker -f"
echo
echo "Ports:"
echo "  TCP 8080 (broker API)"
echo "  UDP 37777 (LAN discovery)"
echo
echo "NOTE: If Proxmox/LXC firewall is enabled, allow UDP 37777 and TCP 8080."
