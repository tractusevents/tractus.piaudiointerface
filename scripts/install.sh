#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
PROJECT_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd)
TARGET_USER=${SUDO_USER:-tractus}
INSTALL_PACKAGES=1
EDIT_BOOT_CONFIG=1

usage() {
    cat <<'EOF'
Usage: sudo ./scripts/install.sh [options]

Options:
  --user USER          User that owns the PipeWire session (default: SUDO_USER or tractus)
  --skip-packages      Do not install PipeWire/ALSA/USB packages
  --skip-boot-config   Do not add the DWC2 peripheral overlay
EOF
}

while (($#)); do
    case "$1" in
        --user)
            [[ $# -ge 2 ]] || { usage >&2; exit 2; }
            TARGET_USER=$2
            shift 2
            ;;
        --skip-packages)
            INSTALL_PACKAGES=0
            shift
            ;;
        --skip-boot-config)
            EDIT_BOOT_CONFIG=0
            shift
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            printf 'Unknown option: %s\n' "$1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

[[ ${EUID:-$(id -u)} -eq 0 ]] || { printf 'Run this installer with sudo.\n' >&2; exit 1; }
getent passwd "$TARGET_USER" >/dev/null || { printf 'User %s does not exist.\n' "$TARGET_USER" >&2; exit 1; }

TARGET_UID=$(id -u "$TARGET_USER")
TARGET_HOME=$(getent passwd "$TARGET_USER" | cut -d: -f6)
DOTNET="$TARGET_HOME/.dotnet/dotnet"

user_systemctl() {
    runuser -u "$TARGET_USER" -- env \
        HOME="$TARGET_HOME" \
        XDG_RUNTIME_DIR="/run/user/$TARGET_UID" \
        DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$TARGET_UID/bus" \
        systemctl --user "$@"
}

if ((INSTALL_PACKAGES)); then
    apt-get update
    apt-get install -y pipewire pipewire-audio pipewire-bin wireplumber alsa-utils usbutils \
        build-essential pkg-config libpipewire-0.3-dev libavahi-client3
fi

[[ -x "$DOTNET" ]] || {
    printf '.NET was not found at %s. Install the .NET 10 SDK for %s first.\n' "$DOTNET" "$TARGET_USER" >&2
    exit 1
}

DOTNET_MAJOR=$(runuser -u "$TARGET_USER" -- "$DOTNET" --version | cut -d. -f1)
[[ "$DOTNET_MAJOR" -ge 10 ]] || {
    printf '.NET 10 or later is required; found %s.\n' "$DOTNET_MAJOR" >&2
    exit 1
}

install -Dm755 "$PROJECT_ROOT/scripts/pi-usb-audio-gadget" /usr/local/sbin/pi-usb-audio-gadget
install -Dm755 "$PROJECT_ROOT/scripts/pi-usb-audio-diagnose" /usr/local/sbin/pi-usb-audio-diagnose
install -Dm644 "$PROJECT_ROOT/systemd/pi-usb-audio-gadget.service" \
    /etc/systemd/system/pi-usb-audio-gadget.service
install -Dm644 "$PROJECT_ROOT/systemd/pi-usb-audio-router.service" \
    /etc/systemd/user/pi-usb-audio-router.service
install -Dm644 "$PROJECT_ROOT/systemd/tractus-audio-dsp.service" \
    /etc/systemd/user/tractus-audio-dsp.service
install -Dm644 "$PROJECT_ROOT/systemd/tractus-ndi-audio.service" \
    /etc/systemd/user/tractus-ndi-audio.service
install -Dm644 "$PROJECT_ROOT/systemd/pi-usb-audio-realtime.conf" \
    "/etc/systemd/system/user@$TARGET_UID.service.d/pi-usb-audio-realtime.conf"
install -Dm644 "$PROJECT_ROOT/config/90-pi-usb-audio-serial.rules" \
    /etc/udev/rules.d/90-pi-usb-audio-serial.rules

if [[ ! -e /etc/default/pi-usb-audio ]]; then
    install -Dm644 "$PROJECT_ROOT/config/gadget.env" /etc/default/pi-usb-audio
fi

install -d -o "$TARGET_USER" -g "$TARGET_USER" "$TARGET_HOME/.config/pi-usb-audio"
install -d -o "$TARGET_USER" -g "$TARGET_USER" \
    "$TARGET_HOME/.config/wireplumber/wireplumber.conf.d"
install -o "$TARGET_USER" -g "$TARGET_USER" -m644 \
    "$PROJECT_ROOT/config/90-pi-usb-audio-alsa.conf" \
    "$TARGET_HOME/.config/wireplumber/wireplumber.conf.d/90-pi-usb-audio-alsa.conf"
if [[ ! -e "$TARGET_HOME/.config/pi-usb-audio/router.json" ]]; then
    install -o "$TARGET_USER" -g "$TARGET_USER" -m644 "$PROJECT_ROOT/config/router.json" \
        "$TARGET_HOME/.config/pi-usb-audio/router.json"
fi

BUILD_DIR=$(mktemp -d /tmp/pi-usb-audio-publish.XXXXXX)
router_was_active=0
dsp_was_active=0
ndi_was_active=0
cleanup() {
    result=$?
    rm -rf "$BUILD_DIR"
    if ((dsp_was_active)); then
        user_systemctl start tractus-audio-dsp.service >/dev/null 2>&1 || true
    fi
    if ((ndi_was_active)); then
        user_systemctl start tractus-ndi-audio.service >/dev/null 2>&1 || true
    fi
    if ((router_was_active)); then
        user_systemctl start pi-usb-audio-router.service >/dev/null 2>&1 || true
    fi
    exit "$result"
}
trap cleanup EXIT
chown "$TARGET_USER:$TARGET_USER" "$BUILD_DIR"
runuser -u "$TARGET_USER" -- env HOME="$TARGET_HOME" DOTNET_ROOT="$TARGET_HOME/.dotnet" \
    "$DOTNET" publish "$PROJECT_ROOT/src/PiUsbAudio.Control/PiUsbAudio.Control.csproj" \
    --configuration Release --runtime linux-arm64 --self-contained true \
    -p:PublishSingleFile=true --output "$BUILD_DIR"
[[ -x "$BUILD_DIR/PiUsbAudio.Control" ]] || {
    printf 'The self-contained control executable was not published.\n' >&2
    exit 1
}
cc -std=c11 -O2 -Wall -Wextra -Werror \
    "$PROJECT_ROOT/src/tractus-audio-dsp/tractus-audio-dsp.c" \
    "$PROJECT_ROOT/src/tractus-audio-dsp/dsp-engine.c" \
    "$PROJECT_ROOT/src/tractus-audio-dsp/dsp-control.c" \
    -o "$BUILD_DIR/tractus-audio-dsp.new" -lm -pthread \
    $(pkg-config --cflags --libs libpipewire-0.3)
NDI_SDK_DIR="$TARGET_HOME/ndi63bsdk"
NDI_HEADER="$NDI_SDK_DIR/include/Processing.NDI.Lib.h"
NDI_LIBRARY="$NDI_SDK_DIR/lib/aarch64-rpi4-linux-gnueabi/libndi.so.6"
[[ -f "$NDI_HEADER" && -e "$NDI_LIBRARY" ]] || {
    printf 'Basic NDI 6.3 SDK was not found at %s. Install it there before running this installer.\n' "$NDI_SDK_DIR" >&2
    exit 1
}
cc -std=c11 -O2 -Wall -Wextra -Werror -I"$NDI_SDK_DIR/include" \
    "$PROJECT_ROOT/src/tractus-ndi-audio/tractus-ndi-audio.c" \
    "$PROJECT_ROOT/src/tractus-ndi-audio/ndi-ring-buffer.c" \
    "$PROJECT_ROOT/src/tractus-ndi-audio/ndi-workers.c" \
    "$PROJECT_ROOT/src/tractus-ndi-audio/ndi-control.c" \
    -o "$BUILD_DIR/tractus-ndi-audio.new" -lm -pthread -ldl \
    $(pkg-config --cflags --libs libpipewire-0.3)

# Never replace managed assemblies underneath a running .NET process. This can
# make a later lazy type load fail even though the old process began normally.
if user_systemctl is-active --quiet pi-usb-audio-router.service 2>/dev/null; then
    router_was_active=1
    user_systemctl stop pi-usb-audio-router.service
fi
if user_systemctl is-active --quiet tractus-audio-dsp.service 2>/dev/null; then
    dsp_was_active=1
    user_systemctl stop tractus-audio-dsp.service
fi
if user_systemctl is-active --quiet tractus-ndi-audio.service 2>/dev/null; then
    ndi_was_active=1
    user_systemctl stop tractus-ndi-audio.service
fi
install -d /opt/pi-usb-audio
rm -f /opt/pi-usb-audio/PiUsbAudio.Control.dll \
    /opt/pi-usb-audio/PiUsbAudio.Control.deps.json \
    /opt/pi-usb-audio/PiUsbAudio.Control.runtimeconfig.json
cp -a "$BUILD_DIR"/. /opt/pi-usb-audio/
mv -f /opt/pi-usb-audio/tractus-audio-dsp.new /opt/pi-usb-audio/tractus-audio-dsp
mv -f /opt/pi-usb-audio/tractus-ndi-audio.new /opt/pi-usb-audio/tractus-ndi-audio
chown -R root:root /opt/pi-usb-audio
find /opt/pi-usb-audio -type d -exec chmod 755 {} +
find /opt/pi-usb-audio -type f -exec chmod a+r {} +

# Reload changed user units before restarting previously active services. The
# later reload still handles first installation when the user manager may not
# have been running yet.
if ((dsp_was_active || ndi_was_active || router_was_active)); then
    user_systemctl daemon-reload
fi
if ((dsp_was_active)); then
    user_systemctl start tractus-audio-dsp.service
    dsp_was_active=0
fi
if ((ndi_was_active)); then
    user_systemctl start tractus-ndi-audio.service
    ndi_was_active=0
fi
if ((router_was_active)); then
    user_systemctl start pi-usb-audio-router.service
    router_was_active=0
fi

if ((EDIT_BOOT_CONFIG)); then
    BOOT_CONFIG=/boot/firmware/config.txt
    [[ -e "$BOOT_CONFIG" ]] || BOOT_CONFIG=/boot/config.txt
    [[ -e "$BOOT_CONFIG" ]] || { printf 'Could not find Raspberry Pi config.txt.\n' >&2; exit 1; }

    pi4_config_has() {
        local setting_regex=$1
        awk -v setting_regex="$setting_regex" '
            BEGIN { section = "all" }
            /^[[:space:]]*\[[^]]+\][[:space:]]*$/ {
                section = tolower($0)
                gsub(/[[:space:]\[\]]/, "", section)
                next
            }
            {
                line = tolower($0)
                sub(/[[:space:]]*#.*/, "", line)
                if ((section == "all" || section == "pi4") && line ~ setting_regex) {
                    found = 1
                }
            }
            END { exit found ? 0 : 1 }
        ' "$BOOT_CONFIG"
    }

    if pi4_config_has '^[[:space:]]*dtoverlay=dwc2,dr_mode=host([[:space:]],]|[[:space:]]*$)'; then
        printf 'A DWC2 host-mode overlay exists in %s; remove or scope it before enabling gadget mode.\n' "$BOOT_CONFIG" >&2
        exit 1
    fi
    if ! pi4_config_has '^[[:space:]]*dtoverlay=dwc2(,dr_mode=peripheral)?[[:space:]]*$'; then
        {
            printf '\n# pi-usb-audio: Pi 4 USB-C peripheral controller\n'
            printf '[pi4]\n'
            printf 'dtoverlay=dwc2,dr_mode=peripheral\n'
            printf '[all]\n'
        } >>"$BOOT_CONFIG"
        printf 'Enabled DWC2 peripheral mode in %s.\n' "$BOOT_CONFIG"
    fi
fi

usermod -aG audio,dialout "$TARGET_USER"
loginctl enable-linger "$TARGET_USER"

systemctl daemon-reload
udevadm control --reload-rules
systemctl enable pi-usb-audio-gadget.service
systemctl start "user@$TARGET_UID.service"

runuser -u "$TARGET_USER" -- env \
    HOME="$TARGET_HOME" \
    XDG_RUNTIME_DIR="/run/user/$TARGET_UID" \
    DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$TARGET_UID/bus" \
    systemctl --user daemon-reload
runuser -u "$TARGET_USER" -- env \
    HOME="$TARGET_HOME" \
    XDG_RUNTIME_DIR="/run/user/$TARGET_UID" \
    DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$TARGET_UID/bus" \
    systemctl --user enable --now pipewire.socket wireplumber.service \
        tractus-audio-dsp.service tractus-ndi-audio.service pi-usb-audio-router.service

cat <<EOF

Installation complete.

1. Reboot: sudo reboot
2. After reboot, run: /usr/local/sbin/pi-usb-audio-diagnose
3. Open the control page from a trusted network: http://<pi-address>:5055/

The control service listens on all interfaces and has no authentication.
Do not expose port 5055 to the public internet.
EOF
