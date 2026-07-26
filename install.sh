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
#   curl -fsSL https://raw.githubusercontent.com/mortenfaerk/GameTown/master/install.sh | bash
#
# The target needs no .NET runtime and no sqlite3: the release is a self-contained build, and the
# application creates its own database from a schema embedded in the binary.
#
# Environment overrides:
#   GAMETOWN_PORT=5187          port to listen on
#   GAMETOWN_VERSION=v0.1.0     install a specific release instead of the latest
#   GAMETOWN_FORCE=1            reinstall even if that version is already installed
#   GAMETOWN_SRC=/path/to/repo  build from a checkout instead of downloading (development)
#   GAMETOWN_BASE_URL=http://…  fetch the release from elsewhere (the CI install test); needs
#                               GAMETOWN_VERSION set as well
#
set -euo pipefail

APP_NAME="gametown"
APP_USER="gametown"
APP_DIR="/opt/gametown"
DATA_DIR="/var/lib/gametown"
SERVICE_FILE="/etc/systemd/system/${APP_NAME}.service"
VERSION_FILE="${APP_DIR}/VERSION"
PORT="${GAMETOWN_PORT:-5187}"
REPO="mortenfaerk/GameTown"

info()  { printf '\033[1;34m==>\033[0m %s\n' "$1"; }
warn()  { printf '\033[1;33m==>\033[0m %s\n' "$1"; }
die()   { printf '\033[1;31m==>\033[0m %s\n' "$1" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "Run as root (needed for the service user and systemd unit)."

# ---------------------------------------------------------------- architecture
# Checked before anything is downloaded. The release is a self-contained linux-x64 build, so on any
# other architecture the binary simply will not execute — and "Exec format error" from systemd three
# steps later is a much worse way to find that out.
ARCH="$(uname -m)"
[[ "$ARCH" == "x86_64" ]] || die "GameTown releases are built for x86_64 only; this machine is $ARCH."

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

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

# ---------------------------------------------------------------- acquire the application
# Two ways in. Downloading a published release is the normal path and needs nothing but curl and tar.
# Building from a checkout is for development, and is opt-in via GAMETOWN_SRC precisely so that a
# machine without the .NET SDK — which is every machine this is meant for — never reaches it.
if [[ -n "${GAMETOWN_SRC:-}" ]]; then
    [[ -d "$GAMETOWN_SRC/API" ]] || die "GAMETOWN_SRC=$GAMETOWN_SRC does not look like a GameTown checkout."
    command -v dotnet >/dev/null 2>&1 || die "Building from source needs the .NET SDK."

    info "Building from $GAMETOWN_SRC (self-contained)…"
    dotnet publish "$GAMETOWN_SRC/API" \
        --configuration Release \
        --runtime linux-x64 \
        --self-contained true \
        --output "$STAGE/app" \
        /p:PublishSingleFile=false

    TAG="source-$(date +%Y%m%d-%H%M%S)"
else
    command -v curl >/dev/null 2>&1 || die "curl is required (apt install curl)."
    command -v tar  >/dev/null 2>&1 || die "tar is required (apt install tar)."

    if [[ -n "${GAMETOWN_VERSION:-}" ]]; then
        TAG="$GAMETOWN_VERSION"
    else
        # /releases/latest redirects to /releases/tag/vX.Y.Z, so the tag can be read off the redirect
        # target. Avoids the GitHub API, which is rate-limited for unauthenticated callers, and
        # avoids needing jq to parse it.
        info "Resolving the latest release…"
        LATEST_URL="$(curl -fsSLI -o /dev/null -w '%{url_effective}' \
            "https://github.com/${REPO}/releases/latest")" \
            || die "Could not reach GitHub to resolve the latest release."
        TAG="${LATEST_URL##*/}"
        [[ "$TAG" == v* ]] || die "Could not determine the latest version (got '$TAG')."
    fi

    # Already at this version? Then there is nothing to do. Without this check every re-run
    # re-downloads and re-unpacks ~80 MB, and this script is also the upgrade command people are told
    # to re-run.
    if [[ -f "$VERSION_FILE" && -z "${GAMETOWN_FORCE:-}" ]] && [[ "$(cat "$VERSION_FILE")" == "$TAG" ]]; then
        info "$TAG is already installed. Nothing to do (GAMETOWN_FORCE=1 to reinstall)."
        systemctl start "$APP_NAME" 2>/dev/null || true
        exit 0
    fi

    TARBALL="gametown-${TAG}-linux-x64.tar.gz"

    # GAMETOWN_BASE_URL exists so the CI install test can point this script at a locally built
    # artifact instead of a published release — otherwise the installer could only ever be tested
    # after shipping, which is the wrong order. It requires an explicit version, because there is no
    # "latest" to resolve against an arbitrary URL.
    if [[ -n "${GAMETOWN_BASE_URL:-}" ]]; then
        [[ -n "${GAMETOWN_VERSION:-}" ]] || die "GAMETOWN_BASE_URL requires GAMETOWN_VERSION to be set too."
        BASE_URL="${GAMETOWN_BASE_URL%/}"
    else
        BASE_URL="https://github.com/${REPO}/releases/download/${TAG}"
    fi

    info "Downloading $TAG…"
    curl -fsSL -o "$STAGE/$TARBALL"   "$BASE_URL/$TARBALL"   || die "Could not download $TARBALL."
    curl -fsSL -o "$STAGE/SHA256SUMS" "$BASE_URL/SHA256SUMS" || die "Could not download SHA256SUMS."

    # Before unpacking, not after. This script is piped into a root shell from the internet; the
    # checksum is the one thing standing between a corrupted or substituted download and files landing
    # in /opt. --ignore-missing lets SHA256SUMS cover assets this install does not fetch.
    info "Verifying checksum…"
    ( cd "$STAGE" && sha256sum --check --ignore-missing --quiet SHA256SUMS ) \
        || die "Checksum verification FAILED for $TARBALL. Refusing to install."

    mkdir -p "$STAGE/app"
    tar -xzf "$STAGE/$TARBALL" -C "$STAGE/app"
fi

# ---------------------------------------------------------------- service user and directories
if ! id -u "$APP_USER" >/dev/null 2>&1; then
    info "Creating service user $APP_USER…"
    useradd --system --home-dir "$DATA_DIR" --shell /usr/sbin/nologin "$APP_USER"
fi

mkdir -p "$DATA_DIR"

# The things that must survive an upgrade. Anything left in $APP_DIR is destroyed below.
mkdir -p "$DATA_DIR/games"    # uploaded archives
mkdir -p "$DATA_DIR/media"    # re-hosted cover art and screenshots
mkdir -p "$DATA_DIR/keys"     # Data Protection keyring — losing it signs everyone out

# ---------------------------------------------------------------- stop the service
# Ahead of the backup, not after it. Nothing may hold the database open while it is copied: a plain
# file copy of a live WAL database silently produces a backup that cannot be recovered.
#
# Harmless on a fresh install: there is no unit to stop yet.
info "Stopping $APP_NAME…"
systemctl stop "$APP_NAME" 2>/dev/null || true

# ---------------------------------------------------------------- database
# Nothing to do on a fresh install. The application creates its own schema at startup from a baseline
# embedded in the binary, which is what lets this script ship without the DDL or the sqlite3 CLI.
if [[ $UPGRADE -eq 1 ]]; then
    # One file, so the cheapest possible safety net: a failed upgrade becomes a restore rather than a
    # support thread. The -wal and -shm sidecars are copied too — a WAL database is not just the main
    # file, and restoring it without them can lose the most recent committed transactions.
    BACKUP="$DATA_DIR/gametown.db.backup-$(date +%Y%m%d-%H%M%S)"
    info "Backing up the database to $BACKUP…"
    cp "$DATA_DIR/gametown.db" "$BACKUP"
    for sidecar in -wal -shm; do
        [[ -f "$DATA_DIR/gametown.db$sidecar" ]] && cp "$DATA_DIR/gametown.db$sidecar" "$BACKUP$sidecar"
    done
    info "Schema upgrades are applied by the application at startup."
fi

# ---------------------------------------------------------------- install application
info "Installing to $APP_DIR…"
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR"
cp -r "$STAGE/app/." "$APP_DIR/"

# Read on the next run to decide whether there is anything to do.
printf '%s\n' "$TAG" > "$VERSION_FILE"

chown -R "$APP_USER":"$APP_USER" "$APP_DIR" "$DATA_DIR"
chmod 700 "$DATA_DIR"
# Guarded: on a fresh install the database does not exist yet — the application creates it on first
# start, inside a directory that is already 700, so it is never exposed in the meantime.
[[ -f "$DATA_DIR/gametown.db" ]] && chmod 600 "$DATA_DIR/gametown.db"
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
#
# The quotes are load-bearing. systemd splits Environment= on whitespace, so the unquoted form set
# ConnectionStrings__DefaultConnection to "Data" and then a junk second variable "Source=…" — and the
# application aborted at startup with "the configured connection string is not a valid SQLite one".
# Every other Environment= line here is single-token and does not need them.
Environment="ConnectionStrings__DefaultConnection=Data Source=$DATA_DIR/gametown.db"
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
    info "Upgraded to $TAG. Your library, uploads and sign-ins are unchanged."
    echo "    http://${IP}:${PORT}"
else
    info "Installed $TAG. Finish setup in a browser:"
    echo "    http://${IP}:${PORT}/setup"
    echo
    warn "That page creates the administrator account and stops responding once one exists."
fi
echo
echo "    Data:    $DATA_DIR   (back this up — nothing else matters)"
echo "    Logs:    journalctl -u $APP_NAME -f"
