#!/usr/bin/env bash
#
# GameTown installer / upgrader.
#
# Installs GameTown as a systemd service on a Debian/Ubuntu-ish LXC container or VM, in the style of
# the Proxmox community helper scripts. Run it again over an existing install to upgrade: the data
# directory is never touched, only the application directory is replaced.
#
# There are no credentials to generate. SQLite removed that entire class of install-time secret —
# there is no database server, no role and no password. The first administrator is created through
# the web UI at /setup on first visit.
#
#   curl -fsSL https://.../install.sh | bash
#
set -euo pipefail

APP_NAME="gametown"
APP_USER="gametown"
APP_DIR="/opt/gametown"
DATA_DIR="/var/lib/gametown"
SERVICE_FILE="/etc/systemd/system/${APP_NAME}.service"
PORT="${GAMETOWN_PORT:-5187}"

# Where to build from. Defaults to the directory holding this script, so it works from a checkout.
SRC_DIR="${GAMETOWN_SRC:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)}"

info()  { printf '\033[1;34m==>\033[0m %s\n' "$1"; }
warn()  { printf '\033[1;33m==>\033[0m %s\n' "$1"; }
die()   { printf '\033[1;31m==>\033[0m %s\n' "$1" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "Run as root (needed for the service user and systemd unit)."

# ---------------------------------------------------------------- upgrade or fresh?
# Deciding on the DATA directory, not the app directory: the app directory is what we are about to
# replace, so its presence says nothing about whether there is a library to preserve.
if [[ -d "$DATA_DIR" && -f "$DATA_DIR/gametown.db" ]]; then
    UPGRADE=1
    info "Existing install found at $DATA_DIR — upgrading, data left untouched."
else
    UPGRADE=0
    info "No existing data found — fresh install."
fi

# ---------------------------------------------------------------- build
command -v dotnet >/dev/null 2>&1 || die "The .NET SDK is required to build. Install it, or use a prebuilt release."

info "Publishing (self-contained, so the target does not need a .NET runtime)…"
PUBLISH_DIR="$(mktemp -d)"
trap 'rm -rf "$PUBLISH_DIR"' EXIT

dotnet publish "$SRC_DIR/API" \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    --output "$PUBLISH_DIR" \
    /p:PublishSingleFile=false

# ---------------------------------------------------------------- service user and directories
if ! id -u "$APP_USER" >/dev/null 2>&1; then
    info "Creating service user $APP_USER…"
    useradd --system --home-dir "$DATA_DIR" --shell /usr/sbin/nologin "$APP_USER"
fi

mkdir -p "$DATA_DIR"

# The four things that must survive an upgrade. Anything left in $APP_DIR is destroyed below.
mkdir -p "$DATA_DIR/games"    # uploaded archives
mkdir -p "$DATA_DIR/media"    # re-hosted cover art and screenshots
mkdir -p "$DATA_DIR/keys"     # Data Protection keyring — losing it signs everyone out

# ---------------------------------------------------------------- database
if [[ $UPGRADE -eq 0 ]]; then
    command -v sqlite3 >/dev/null 2>&1 || die "sqlite3 is required to create the database (apt install sqlite3)."
    info "Creating database…"
    sqlite3 "$DATA_DIR/gametown.db" < "$SRC_DIR/Database/sqlite/01_schema.sql"
    sqlite3 "$DATA_DIR/gametown.db" < "$SRC_DIR/Database/sqlite/02_seed.sql"
else
    # One file, so the cheapest possible safety net: a failed upgrade becomes a restore rather than
    # a support thread.
    BACKUP="$DATA_DIR/gametown.db.backup-$(date +%Y%m%d-%H%M%S)"
    info "Backing up the database to $BACKUP…"
    sqlite3 "$DATA_DIR/gametown.db" ".backup '$BACKUP'"
    info "Schema upgrades are applied by the application at startup."
fi

# ---------------------------------------------------------------- install application
info "Installing to $APP_DIR…"
systemctl stop "$APP_NAME" 2>/dev/null || true
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR"
cp -r "$PUBLISH_DIR/." "$APP_DIR/"

chown -R "$APP_USER":"$APP_USER" "$APP_DIR" "$DATA_DIR"
chmod 700 "$DATA_DIR"
chmod 600 "$DATA_DIR/gametown.db"
# The keyring decrypts every auth cookie; it is as sensitive as the database.
chmod 700 "$DATA_DIR/keys"

# ---------------------------------------------------------------- systemd
info "Writing $SERVICE_FILE…"
cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=GameTown
After=network.target

[Service]
Type=notify
User=$APP_USER
WorkingDirectory=$APP_DIR
ExecStart=$APP_DIR/API
Restart=always
RestartSec=5

# The data directory is the only thing that must be known before the app starts; everything else is
# read from the database it points at and edited in the admin UI.
Environment=ConnectionStrings__DefaultConnection=Data Source=$DATA_DIR/gametown.db
Environment=ASPNETCORE_URLS=http://0.0.0.0:$PORT
Environment=ASPNETCORE_ENVIRONMENT=Production

# Plain HTTP on the LAN is the default and is sound here: authentication is a same-origin
# SameSite=Lax cookie, not the SameSite=None; Secure cookie that used to force HTTPS. To terminate
# TLS (Caddy with ACME DNS-01 works without exposing the host to the internet), set:
#   Environment=RequireHttps=true

NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectHome=true
ReadWritePaths=$DATA_DIR

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "$APP_NAME" >/dev/null 2>&1 || true
systemctl start "$APP_NAME"

IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
IP="${IP:-localhost}"

echo
if [[ $UPGRADE -eq 1 ]]; then
    info "Upgraded. Your library, uploads and sign-ins are unchanged."
    echo "    http://${IP}:${PORT}"
else
    info "Installed. Finish setup in a browser:"
    echo "    http://${IP}:${PORT}/setup"
    echo
    warn "That page creates the administrator account and stops responding once one exists."
fi
echo
echo "    Data:    $DATA_DIR   (back this up — nothing else matters)"
echo "    Logs:    journalctl -u $APP_NAME -f"
