#!/usr/bin/env bash
# Build PattNOC from this tree and install/update it on Arch Linux.
#
# Usage:
#   ./build-install-arch.sh                # build, update install, restart app if it was running
#   ./build-install-arch.sh --build-only   # publish only, touch nothing in the install dir
#   ./build-install-arch.sh --update-cores # also refresh Xray-core + geo rules
#   ./build-install-arch.sh --clean        # dotnet clean before publish
#   ./build-install-arch.sh --yes          # no confirmation prompts (stop running app, install)
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

PROJECT="v2rayN/v2rayN.Desktop/v2rayN.Desktop.csproj"
DEST="${PATTNOC_DIR:-$HOME/.local/share/PattNOC}"
DESKTOP_FILE="$HOME/.local/share/applications/pattnoc.desktop"

# Runtime data written by the app — never touched on update.
DATA_DIRS=(bin binConfigs guiConfigs guiLogs guiTemps)

RID=""
VERSION=""
CLEAN=0
BUILD_ONLY=0
UPDATE_CORES=0
LAUNCH=auto # auto = restart only if it was running before
ASSUME_YES=0

die() { echo "[x] $*" >&2; exit 1; }
log() { echo "[+] $*"; }
warn() { echo "[!] $*" >&2; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --build-only)   BUILD_ONLY=1; shift ;;
    --update-cores) UPDATE_CORES=1; shift ;;
    --clean)        CLEAN=1; shift ;;
    --no-launch)    LAUNCH=never; shift ;;
    --launch)       LAUNCH=always; shift ;;
    --yes|-y)       ASSUME_YES=1; shift ;;
    --version)      VERSION="${2:?--version needs a value}"; shift 2 ;;
    --arch)         RID="linux-${2:?--arch needs x64 or arm64}"; shift 2 ;;
    -h|--help)      sed -n '2,9p' "$0"; exit 0 ;;
    *) die "Unknown option: $1 (see --help)" ;;
  esac
done

# --- environment checks -------------------------------------------------
command -v dotnet >/dev/null 2>&1 ||
  die "dotnet SDK not found. On Arch: sudo pacman -S dotnet-sdk"
command -v rsync >/dev/null 2>&1 ||
  die "rsync not found. On Arch: sudo pacman -S rsync"

case "$(uname -m)" in
  x86_64)  : "${RID:=linux-x64}" ;;
  aarch64) : "${RID:=linux-arm64}" ;;
  *) die "Unsupported arch: $(uname -m)" ;;
esac

# --- submodules ---------------------------------------------------------
if [[ -f .gitmodules ]]; then
  git submodule sync --recursive >/dev/null 2>&1 || true
  git submodule update --init --recursive >/dev/null 2>&1 ||
    warn "submodule update failed; continuing (build may fail if GlobalHotKeys is missing)"
fi

# --- build --------------------------------------------------------------
if [[ "$CLEAN" -eq 1 ]]; then
  log "Cleaning..."
  dotnet clean "$PROJECT" -c Release >/dev/null
fi

log "Publishing $PROJECT ($RID, Release, self-contained single file)..."
dotnet publish "$PROJECT" \
  -c Release -r "$RID" \
  -p:SelfContained=true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  ${VERSION:+-p:Version="$VERSION"}

PUB="v2rayN/v2rayN.Desktop/bin/Release/net10.0/${RID}/publish"
[[ -f "$PUB/PattNOC" ]] || die "Publish output not found: $PUB/PattNOC"
chmod +x "$PUB/PattNOC"

# Without IncludeNativeLibrariesForSelfExtract the SDK drops libe_sqlite3.so and friends
# from both the bundle and the publish dir, and the app dies with DllNotFoundException.
if [[ ! -f "$PUB/libe_sqlite3.so" ]] &&
   ! grep -aq "database disk image is malformed" "$PUB/PattNOC"; then
  die "Native libs are missing from the bundle. Re-run with --clean."
fi
log "Build OK: $PUB/PattNOC"

[[ "$BUILD_ONLY" -eq 1 ]] && { echo "[*] --build-only: skipping install."; exit 0; }

# --- stop running instance ---------------------------------------------
was_running=0
if pgrep -x PattNOC >/dev/null 2>&1; then
  was_running=1
  if [[ "$ASSUME_YES" -eq 0 ]]; then
    echo "" >&2
    echo "  PattNOC is currently running." >&2
    echo "  It must be stopped before the update can be installed." >&2
    read -r -p "  Stop it now? [Y/n] " ans || ans=""
    if [[ "${ans:-Y}" =~ ^[Nn] ]]; then
      die "Update aborted — nothing was installed. Re-run and answer Y, or use --yes."
    fi
  fi
  log "Stopping PattNOC..."
  pkill -x PattNOC || true
  for _ in $(seq 1 20); do
    pgrep -x PattNOC >/dev/null 2>&1 || break
    sleep 0.5
  done
  pgrep -x PattNOC >/dev/null 2>&1 && die "PattNOC did not stop; close it and rerun."
fi

# --- install ------------------------------------------------------------
mkdir -p "$DEST"

# Replace app files, keep the runtime data dirs (configs, DB, logs, cores).
excludes=()
for d in "${DATA_DIRS[@]}"; do excludes+=(--exclude="/$d/"); done

log "Installing to $DEST ..."
rsync -a --delete "${excludes[@]}" "$PUB/" "$DEST/"
chmod +x "$DEST/PattNOC"

# --- optional: refresh Xray-core + geo rules -----------------------------
if [[ "$UPDATE_CORES" -eq 1 ]]; then
  command -v curl >/dev/null && command -v unzip >/dev/null ||
    die "--update-cores needs curl and unzip (pacman -S curl unzip)"

  bin="$DEST/bin"
  mkdir -p "$bin/xray" "$bin/srss"

  ver="$(curl -fsSLI -o /dev/null -w '%{url_effective}' \
        https://github.com/patterniha/Xray-core/releases/latest |
        sed -E 's#.*/releases/tag/v##')" || ver=""
  [[ "$ver" =~ ^[0-9] ]] || die "Could not resolve patterniha/Xray-core version"

  case "$RID" in
    linux-x64)   zipurl="https://github.com/patterniha/Xray-core/releases/download/v${ver}/Xray-linux-64.zip" ;;
    linux-arm64) zipurl="https://github.com/patterniha/Xray-core/releases/download/v${ver}/Xray-linux-arm64-v8a.zip" ;;
    *) die "No Xray URL for RID $RID" ;;
  esac

  log "Updating Xray-core v$ver ..."
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  curl -fL "$zipurl" -o "$tmp/xray.zip"
  unzip -qo "$tmp/xray.zip" -d "$tmp"
  install -m 755 "$tmp/xray" "$bin/xray/xray"

  log "Updating geo rules..."
  curl -fsSL -o "$bin/geosite.dat"  "https://github.com/Chocolate4U/Iran-v2ray-rules/releases/latest/download/geosite.dat"
  curl -fsSL -o "$bin/geoip.dat"    "https://github.com/Chocolate4U/Iran-v2ray-rules/releases/latest/download/geoip.dat"
  curl -fsSL -o "$bin/geoip-only-cn-private.dat" "https://raw.githubusercontent.com/Loyalsoldier/geoip/release/geoip-only-cn-private.dat"
  curl -fsSL -o "$bin/Country.mmdb" "https://raw.githubusercontent.com/Loyalsoldier/geoip/release/Country.mmdb"
  curl -fsSL -o "$bin/geoip.metadb" "https://github.com/MetaCubeX/meta-rules-dat/releases/latest/download/geoip.metadb"

  for f in geoip-private geoip-cn geoip-facebook geoip-fastly geoip-google geoip-netflix geoip-telegram geoip-twitter; do
    curl -fsSL -o "$bin/srss/$f.srs" "https://raw.githubusercontent.com/2dust/sing-box-rules/refs/heads/rule-set-geoip/$f.srs"
  done
  for f in geosite-cn geosite-gfw geosite-google geosite-greatfire geosite-geolocation-cn geosite-category-ads-all geosite-private; do
    curl -fsSL -o "$bin/srss/$f.srs" "https://raw.githubusercontent.com/2dust/sing-box-rules/refs/heads/rule-set-geosite/$f.srs"
  done
  for f in geosite-category-ir geoip-ir; do
    curl -fsSL -o "$bin/srss/$f.srs" "https://raw.githubusercontent.com/chocolate4u/Iran-sing-box-rules/rule-set/$f.srs"
  done

  rm -rf "$tmp"
  trap - EXIT
fi

# --- desktop entry + icon ----------------------------------------------
mkdir -p "$(dirname "$DESKTOP_FILE")"

cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Version=1.0
Name=PattNOC
GenericName=Proxy Client
Comment=v2rayN fork - Xray / sing-box proxy client
Exec=$DEST/PattNOC
Path=$DEST
Icon=pattnoc
Terminal=false
StartupNotify=true
StartupWMClass=PattNOC
Categories=Network;
Keywords=v2ray;xray;sing-box;proxy;vpn;clash;PattNOC;
EOF
chmod 644 "$DESKTOP_FILE"

SRC_ICON="$SCRIPT_DIR/v2rayN/v2rayN.Desktop/v2rayN.png"
if [[ -f "$SRC_ICON" ]]; then
  # Install at several sizes: some DEs/docks ignore 256x256-only icons.
  for s in 16 24 32 48 64 128 256; do
    d="$HOME/.local/share/icons/hicolor/${s}x${s}/apps"
    mkdir -p "$d"
    cp -f "$SRC_ICON" "$d/pattnoc.png"
  done
  # Fallback location picked up without any icon cache.
  mkdir -p "$HOME/.local/share/pixmaps"
  cp -f "$SRC_ICON" "$HOME/.local/share/pixmaps/pattnoc.png"
else
  warn "source icon missing: $SRC_ICON — menu icon will be blank"
fi
if command -v update-desktop-database >/dev/null; then
  update-desktop-database "$HOME/.local/share/applications" >/dev/null 2>&1 || true
else
  warn "update-desktop-database not found (pacman -S desktop-file-utils); menu icon may not appear until relogin"
fi
if command -v gtk-update-icon-cache >/dev/null; then
  gtk-update-icon-cache -f "$HOME/.local/share/icons/hicolor" >/dev/null 2>&1 || true
else
  warn "gtk-update-icon-cache not found (pacman -S gtk-update-icon-cache); menu icon may not appear until relogin"
fi
command -v kbuildsycoca6 >/dev/null &&
  kbuildsycoca6 >/dev/null 2>&1 || true

# --- launch -------------------------------------------------------------
should_launch=0
case "$LAUNCH" in
  always) should_launch=1 ;;
  never)  should_launch=0 ;;
  auto)   should_launch="$was_running" ;;
esac

if [[ "$should_launch" -eq 1 ]]; then
  log "Starting PattNOC..."
  (cd "$DEST" && setsid nohup ./PattNOC >/dev/null 2>&1 &)
fi

log "Done."
echo "    app:  $DEST/PattNOC"
echo "    data: $DEST/guiConfigs (configs/DB), $DEST/bin (cores)"
