#!/usr/bin/env bash
# Thank Goodness You're Here! - Türkçe Yama / Turkish patch
# Installer for macOS and Linux (including Steam Deck / Proton).
#
#   ./install.sh "<game folder>"              install or update
#   ./install.sh --uninstall "<game folder>"  remove
#
# The game folder is the one holding "Thank Goodness You're Here!.exe" (Proton / Wine),
# or the folder holding the game's .app (macOS).
#
# Downloads BepInEx 6 from builds.bepinex.dev if the game doesn't have it yet, then the
# newest patch package from this project's GitHub releases. Needs curl and unzip.

set -euo pipefail

REPO="SkipperSkipTR/TGYH-TR"
PLUGIN="TGYHTurkishPatch"
BEPINEX_BUILD="788"
BEPINEX_COMMIT="5b766a3"
MARKER="installed-by-tgyh-tr.txt"

say() { printf '%s\n' "$*"; }
die() { printf 'HATA / ERROR: %s\n' "$*" >&2; exit 1; }

UNINSTALL=0
if [ "${1:-}" = "--uninstall" ]; then UNINSTALL=1; shift; fi
GAME_DIR="${1:-}"
[ -n "$GAME_DIR" ] || die "Kullanım / usage: $0 [--uninstall] \"<oyun klasörü / game folder>\""
[ -d "$GAME_DIR" ] || die "Klasör bulunamadı / no such folder: $GAME_DIR"
GAME_DIR="$(cd "$GAME_DIR" && pwd)"

command -v curl >/dev/null || die "curl gerekli / curl is required"
command -v unzip >/dev/null || die "unzip gerekli / unzip is required"

# --- Which build of the game is this? -------------------------------------------------
EXE="$GAME_DIR/Thank Goodness You're Here!.exe"
APP="$(find "$GAME_DIR" -maxdepth 1 -name '*.app' -print -quit 2>/dev/null || true)"

if [ -f "$EXE" ]; then
    # Windows build (Proton, Wine, CrossOver). 0x8664 in the PE header means 64-bit.
    pe_offset=$(od -An -t u4 -j 60 -N 4 "$EXE" | tr -d ' ')
    machine=$(od -An -t x2 -j $((pe_offset + 4)) -N 2 "$EXE" | tr -d ' ')
    if [ "$machine" = "8664" ]; then FLAVOUR="win-x64"; else FLAVOUR="win-x86"; fi
elif [ -n "$APP" ]; then
    FLAVOUR="macos-x64"
else
    die "Oyun bu klasörde bulunamadı / game not found in: $GAME_DIR"
fi

PLUGIN_DIR="$GAME_DIR/BepInEx/plugins/$PLUGIN"

# --- Uninstall ------------------------------------------------------------------------
if [ "$UNINSTALL" = 1 ]; then
    rm -rf "$PLUGIN_DIR" "$GAME_DIR/BepInEx/plugins/TGYPatcher"
    # Only remove BepInEx if this script installed it.
    if [ -f "$GAME_DIR/BepInEx/$MARKER" ]; then
        rm -rf "$GAME_DIR/BepInEx" "$GAME_DIR/dotnet"
        rm -f "$GAME_DIR/winhttp.dll" "$GAME_DIR/doorstop_config.ini" "$GAME_DIR/.doorstop_version" \
              "$GAME_DIR/changelog.txt" "$GAME_DIR/run_bepinex.sh" "$GAME_DIR/libdoorstop.dylib"
    fi
    say "Yama silindi / patch removed."
    exit 0
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# --- BepInEx --------------------------------------------------------------------------
if [ ! -f "$GAME_DIR/BepInEx/core/BepInEx.Unity.IL2CPP.dll" ]; then
    [ ! -d "$GAME_DIR/BepInEx/core" ] || die "Farklı bir BepInEx kurulu; önce kaldırın / a different BepInEx is installed; remove it first"

    file="BepInEx-Unity.IL2CPP-$FLAVOUR-6.0.0-be.$BEPINEX_BUILD%2B$BEPINEX_COMMIT.zip"
    say "BepInEx 6.0.0-be.$BEPINEX_BUILD ($FLAVOUR) indiriliyor / downloading..."
    curl -fL --progress-bar -o "$TMP/bepinex.zip" "https://builds.bepinex.dev/projects/bepinex_be/$BEPINEX_BUILD/$file"
    unzip -qo "$TMP/bepinex.zip" -d "$GAME_DIR"
    [ -f "$GAME_DIR/BepInEx/core/BepInEx.Unity.IL2CPP.dll" ] || die "BepInEx paketi beklenen dosyaları içermiyor / unexpected BepInEx package"
    echo "BepInEx was installed by the TGYH Turkish patch installer; uninstalling the patch removes it." > "$GAME_DIR/BepInEx/$MARKER"

    if [ ! -f "$GAME_DIR/BepInEx/config/BepInEx.cfg" ]; then
        mkdir -p "$GAME_DIR/BepInEx/config"
        printf '[Logging.Console]\n\nEnabled = false\n' > "$GAME_DIR/BepInEx/config/BepInEx.cfg"
    fi

    if [ "$FLAVOUR" = "macos-x64" ] && [ -f "$GAME_DIR/run_bepinex.sh" ]; then
        # The launcher script has to be told which app to start.
        app_name="$(basename "$APP")"
        sed -i.bak "s|^executable_name=.*|executable_name=\"$app_name\"|" "$GAME_DIR/run_bepinex.sh"
        # BepInEx ships an x86_64-only .NET runtime, but its launcher prefers the arm64 half of
        # a universal game on Apple Silicon. Make it start the x86_64 half (through Rosetta).
        sed -i.bak 's|ARCHPREFERENCE="arm64,x86_64"|ARCHPREFERENCE="x86_64"|' "$GAME_DIR/run_bepinex.sh"
        rm -f "$GAME_DIR/run_bepinex.sh.bak"
        chmod +x "$GAME_DIR/run_bepinex.sh"
    fi
fi

# --- Patch package --------------------------------------------------------------------
say "Son yama sürümü aranıyor / looking for the newest patch release..."
url="$(curl -fsSL -H 'User-Agent: TGYH-TR-Installer' "https://api.github.com/repos/$REPO/releases?per_page=30" \
    | grep -o '"browser_download_url": *"[^"]*/TGYHTurkishPatch[^"/]*\.zip"' | head -n 1 | sed 's/.*"\(https[^"]*\)"/\1/' || true)"
[ -n "$url" ] || die "Yayınlanmış yama paketi bulunamadı / no published patch package found"

say "Yama indiriliyor / downloading: $(basename "$url")"
curl -fL --progress-bar -o "$TMP/patch.zip" "$url"

rm -rf "$PLUGIN_DIR" "$GAME_DIR/BepInEx/plugins/TGYPatcher"
unzip -qo "$TMP/patch.zip" -d "$GAME_DIR"
[ -f "$PLUGIN_DIR/$PLUGIN.dll" ] || die "Yama paketi beklenen dosyaları içermiyor / unexpected patch package"

say ""
say "Yama kuruldu / patch installed${PLUGIN_DIR:+ ($(cat "$PLUGIN_DIR/version.txt" 2>/dev/null || echo '?'))}."
case "$FLAVOUR" in
    win-*)
        say "Steam (Proton) başlatma seçeneklerine şunu ekleyin / add this to the Steam launch options:"
        say '    WINEDLLOVERRIDES="winhttp=n,b" %command%'
        ;;
    macos-x64)
        say "Oyunu şu betikle başlatın / start the game through:"
        say "    \"$GAME_DIR/run_bepinex.sh\""
        say "Apple Silicon: oyun Rosetta ile çalışır (softwareupdate --install-rosetta) / the game runs through Rosetta."
        ;;
esac
say "İlk açılış 1-2 dakika sürebilir / the first launch can take 1-2 minutes."
