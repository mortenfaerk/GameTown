#!/usr/bin/env bash
#
# GameTown SMB share helper.
#
# Mounts an SMB/CIFS share where GameTown keeps its game archives, and makes the mount permanent and
# boot-safe. Run it as root on the machine GameTown is installed on:
#
#   ./smb-mount.sh //nas/games
#   ./smb-mount.sh '\\nas\games' --user morten --mountpoint /var/lib/gametown/games
#
# Why this exists as a separate script rather than fields in the setup wizard: GameTown runs as an
# unprivileged systemd service with NoNewPrivileges=true. Mounting needs CAP_SYS_ADMIN, so the
# application cannot do it however it is asked — and storing the share password in GameTown's
# database would put a credential that usually reaches far beyond one share into a file whose whole
# security model is "the data directory is 0700". The password instead ends up in a root-owned 0600
# credentials file that only the kernel's CIFS client reads.
#
# What it writes:
#   /etc/gametown/<name>.cred                          credentials, root-owned, 0600
#   /etc/systemd/system/<escaped-mountpoint>.mount     the mount unit
#   /etc/systemd/system/gametown.service.d/smb.conf    makes GameTown require the mount
#
# Re-running it for the same mountpoint updates all three (it asks first unless --force).
#
set -euo pipefail

APP_USER="gametown"
SERVICE="gametown"
MOUNTPOINT="/var/lib/gametown/games"
CRED_DIR="/etc/gametown"
SHARE=""
USERNAME=""
PASSWORD=""
DOMAIN=""
FORCE=0

info() { printf '\033[1;34m==>\033[0m %s\n' "$1"; }
warn() { printf '\033[1;33m==>\033[0m %s\n' "$1"; }
die()  { printf '\033[1;31m==>\033[0m %s\n' "$1" >&2; exit 1; }

usage() {
    cat <<'USAGE'
Usage: smb-mount.sh <share> [options]

  <share>                 //server/share  (\\server\share is accepted too)

  --user NAME             SMB username (prompted if omitted)
  --password-file FILE    read the password from FILE instead of prompting
  --domain NAME           SMB domain / workgroup
  --mountpoint DIR        where to mount it (default /var/lib/gametown/games)
  --force                 overwrite an existing mount unit for that mountpoint
  -h, --help              this text

The password is never taken on the command line: argv is world-readable through /proc while the
process runs. Use --password-file, or let it prompt.
USAGE
}

[[ $# -gt 0 ]] || { usage; exit 1; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --user)          USERNAME="${2:-}"; shift 2 ;;
        --password-file) [[ -r "${2:-}" ]] || die "Cannot read password file ${2:-}."
                         PASSWORD="$(<"$2")"; shift 2 ;;
        --domain)        DOMAIN="${2:-}"; shift 2 ;;
        --mountpoint)    MOUNTPOINT="${2:-}"; shift 2 ;;
        --force)         FORCE=1; shift ;;
        -h|--help)       usage; exit 0 ;;
        --password)      die "Refusing --password: it would be visible in ps output. Use --password-file." ;;
        -*)              die "Unknown option $1." ;;
        *)               [[ -z "$SHARE" ]] || die "More than one share given."
                         SHARE="$1"; shift ;;
    esac
done

[[ $EUID -eq 0 ]] || die "Run as root — mounting and writing systemd units both need it."
[[ -n "$SHARE" ]]  || die "No share given. See --help."

# ---------------------------------------------------------------- normalise the share address
# \\nas\games and //nas/games are the same thing said by different operating systems, and an
# operator copying the address out of Windows will have the first form. Accept both rather than
# rejecting one on a technicality.
SHARE="${SHARE//\\//}"
[[ "$SHARE" == //*/* ]] || die "Share must look like //server/share (got '$SHARE')."

command -v mount.cifs >/dev/null 2>&1 \
    || die "mount.cifs is missing. Install it first: apt install cifs-utils"

id -u "$APP_USER" >/dev/null 2>&1 \
    || die "There is no $APP_USER user — install GameTown before mounting its archive directory."

APP_UID="$(id -u "$APP_USER")"
APP_GID="$(id -g "$APP_USER")"

# ---------------------------------------------------------------- credentials
if [[ -z "$USERNAME" ]]; then
    read -r -p "SMB username: " USERNAME
    [[ -n "$USERNAME" ]] || die "A username is required."
fi

if [[ -z "$PASSWORD" ]]; then
    read -r -s -p "SMB password for $USERNAME: " PASSWORD
    echo
fi
[[ -n "$PASSWORD" ]] || die "An empty password is almost certainly not what you meant."

UNIT_NAME="$(systemd-escape -p --suffix=mount "$MOUNTPOINT")"
UNIT_FILE="/etc/systemd/system/${UNIT_NAME}"
CRED_FILE="${CRED_DIR}/$(systemd-escape -p "$MOUNTPOINT").cred"

if [[ -e "$UNIT_FILE" && $FORCE -eq 0 ]]; then
    warn "$UNIT_FILE already exists."
    read -r -p "Replace it? [y/N] " reply
    [[ "$reply" == [yY] ]] || die "Left alone. Re-run with --force to skip this prompt."
fi

# umask before the write, not chmod after: chmod leaves a window in which the password is on disk
# world-readable, and that is the whole file.
info "Writing credentials to $CRED_FILE…"
mkdir -p "$CRED_DIR"
chmod 700 "$CRED_DIR"
(
    umask 077
    {
        printf 'username=%s\n' "$USERNAME"
        printf 'password=%s\n' "$PASSWORD"
        [[ -n "$DOMAIN" ]] && printf 'domain=%s\n' "$DOMAIN"
    } > "$CRED_FILE"
)
chown root:root "$CRED_FILE"
unset PASSWORD

# ---------------------------------------------------------------- the mount unit
mkdir -p "$MOUNTPOINT"

info "Writing $UNIT_FILE…"
cat > "$UNIT_FILE" <<EOF
[Unit]
Description=GameTown game archives on $SHARE
After=network-online.target
Wants=network-online.target

[Mount]
What=$SHARE
Where=$MOUNTPOINT
Type=cifs
# uid/gid hand the files to the service account: CIFS applies the *share's* rights, so without these
# every file arrives owned by root and GameTown cannot write its own uploads.
#
# nofail keeps a NAS that is off from blocking boot. GameTown itself is made to require this mount
# below, so "share down" stops the service instead of silently writing to the local disk underneath.
Options=credentials=$CRED_FILE,uid=$APP_UID,gid=$APP_GID,file_mode=0660,dir_mode=0770,nofail,_netdev
TimeoutSec=30

[Install]
WantedBy=multi-user.target
EOF
chmod 644 "$UNIT_FILE"

# ---------------------------------------------------------------- tie the service to the mount
# Without this the failure mode is silent and expensive: the share goes away, the mountpoint reverts
# to an ordinary empty directory on the root filesystem, and GameTown cheerfully accepts uploads into
# it. They vanish from view the moment the share comes back, and the root filesystem fills up.
DROPIN_DIR="/etc/systemd/system/${SERVICE}.service.d"
info "Making $SERVICE require the mount…"
mkdir -p "$DROPIN_DIR"
cat > "${DROPIN_DIR}/smb.conf" <<EOF
# Written by smb-mount.sh. GameTown will not start unless $MOUNTPOINT is mounted.
[Unit]
RequiresMountsFor=$MOUNTPOINT

[Service]
ReadWritePaths=$MOUNTPOINT
EOF

systemctl daemon-reload

info "Mounting $SHARE at $MOUNTPOINT…"
systemctl enable "$UNIT_NAME" >/dev/null 2>&1 || true
systemctl start "$UNIT_NAME" || die "Mount failed. Check: journalctl -u $UNIT_NAME"

# ---------------------------------------------------------------- prove it
# systemctl start returning 0 is not proof that GameTown can use the share: the mount can succeed
# with rights that leave the service account unable to write. Verify as the user that will do it.
findmnt --noheadings --output FSTYPE --target "$MOUNTPOINT" | grep -q cifs \
    || die "$MOUNTPOINT is mounted, but not as cifs. Refusing to report success."

PROBE="${MOUNTPOINT}/.gametown-write-test-$$"
if runuser -u "$APP_USER" -- touch "$PROBE" 2>/dev/null; then
    runuser -u "$APP_USER" -- rm -f "$PROBE"
else
    die "Mounted, but $APP_USER cannot write to $MOUNTPOINT. Check the share's own permissions for $USERNAME."
fi

if systemctl is-active --quiet "$SERVICE"; then
    info "Restarting $SERVICE so it picks up the mount…"
    systemctl restart "$SERVICE"
fi

echo
info "Done. $SHARE is mounted at $MOUNTPOINT and $APP_USER can write to it."
echo
echo "    Set the archive directory to this path in GameTown:"
echo "        $MOUNTPOINT"
echo "    (first run: the /setup page — afterwards: Administer → Settings)"
echo
echo "    Credentials: $CRED_FILE  (root-only; back it up with the rest of /etc)"
echo "    Unmount:     systemctl disable --now $UNIT_NAME"
