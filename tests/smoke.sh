#!/usr/bin/env bash
set -euo pipefail

project_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
dotnet_bin=${DOTNET_BIN:-/home/tractus/.dotnet/dotnet}
control_project="$project_root/src/PiUsbAudio.Control/PiUsbAudio.Control.csproj"
app="$project_root/src/PiUsbAudio.Control/bin/Debug/net10.0/PiUsbAudio.Control.dll"
native_output=$(mktemp /tmp/tractus-audio-dsp-smoke.XXXXXX)
ndi_native_output=$(mktemp /tmp/tractus-ndi-audio-smoke.XXXXXX)
publish_output=$(mktemp -d /tmp/pi-usb-audio-standalone-smoke.XXXXXX)
standalone_app="$publish_output/PiUsbAudio.Control"
trap 'rm -f "$native_output" "$ndi_native_output"; rm -rf "$publish_output"' EXIT

bash -n "$project_root/scripts/pi-usb-audio-gadget" \
    "$project_root/scripts/pi-usb-audio-diagnose" \
    "$project_root/scripts/install.sh"
grep -q '^RestartForceExitStatus=SIGHUP$' \
    "$project_root/systemd/pi-usb-audio-router.service"
grep -q 'OpenNoControllingTerminal' \
    "$project_root/src/PiUsbAudio.Control/SerialControlService.cs"
grep -q 'serialControl.PauseAsync' \
    "$project_root/src/PiUsbAudio.Control/UsbGadgetControlService.cs"
grep -q 'action.lookup("unit") === "pi-usb-audio-gadget.service"' \
    "$project_root/config/50-pi-usb-audio-gadget.rules.in"
grep -q 'action.lookup("verb") === "restart"' \
    "$project_root/config/50-pi-usb-audio-gadget.rules.in"
grep -q 'subject.user === "@TARGET_USER@"' \
    "$project_root/config/50-pi-usb-audio-gadget.rules.in"
"$dotnet_bin" restore "$control_project" --runtime linux-arm64
"$dotnet_bin" build "$control_project" --no-restore
"$dotnet_bin" publish "$control_project" \
    --configuration Release --runtime linux-arm64 --self-contained true \
    -p:PublishSingleFile=true --output "$publish_output" --no-restore
[[ -x "$standalone_app" ]] || {
    printf 'Self-contained control executable was not published.\n' >&2
    exit 1
}
[[ ! -e "$publish_output/PiUsbAudio.Control.dll" ]] || {
    printf 'Framework-dependent control DLL was unexpectedly published.\n' >&2
    exit 1
}
[[ -f "$publish_output/wwwroot/index.html" ]] || {
    printf 'Published control application is missing its web interface.\n' >&2
    exit 1
}
cc -std=c11 -O2 -Wall -Wextra -Werror \
    "$project_root/src/tractus-audio-dsp/tractus-audio-dsp.c" \
    "$project_root/src/tractus-audio-dsp/dsp-engine.c" \
    "$project_root/src/tractus-audio-dsp/dsp-control.c" \
    -o "$native_output" -lm -pthread \
    $(pkg-config --cflags --libs libpipewire-0.3)
ndi_sdk_dir=${NDI_SDK_DIR:-/home/tractus/ndi63bsdk}
cc -std=c11 -O2 -Wall -Wextra -Werror -I"$ndi_sdk_dir/include" \
    "$project_root/src/tractus-ndi-audio/tractus-ndi-audio.c" \
    "$project_root/src/tractus-ndi-audio/ndi-ring-buffer.c" \
    "$project_root/src/tractus-ndi-audio/ndi-workers.c" \
    "$project_root/src/tractus-ndi-audio/ndi-control.c" \
    -o "$ndi_native_output" -lm -pthread -ldl \
    $(pkg-config --cflags --libs libpipewire-0.3)

export PATH="$project_root/tests/fake-bin:$PATH"
list_output=$("$dotnet_bin" "$app" list)
grep -q 'Example USB Capture' <<<"$list_output"
standalone_list_output=$(env \
    DOTNET_ROOT=/nonexistent \
    DOTNET_ROOT_ARM64=/nonexistent \
    DOTNET_MULTILEVEL_LOOKUP=0 \
    "$standalone_app" list)
grep -q 'Example USB Capture' <<<"$standalone_list_output"

friendly_names_output=$("$dotnet_bin" "$app" gadget-names \
    --config "$project_root/tests/router-friendly-names.json")
grep -qx 'To Teams (Tractus USB Audio 1)' <<<"$friendly_names_output"
grep -qx 'Zoom Return (Tractus USB Audio 2)' <<<"$friendly_names_output"
grep -qx 'Tractus USB Audio 3' <<<"$friendly_names_output"
grep -qx 'Broadcast (Tractus USB Audio 4)' <<<"$friendly_names_output"

keyboard_actions_output=$("$dotnet_bin" "$app" gadget-names \
    --config "$project_root/tests/router-keyboard-actions.json")
[[ $(grep -c '^Tractus USB Audio [1-4]$' <<<"$keyboard_actions_output") -eq 4 ]]

if invalid_friendly_name_output=$("$dotnet_bin" "$app" gadget-names \
    --config "$project_root/tests/router-invalid-friendly-name.json" 2>&1); then
    printf 'Expected a USB friendly name above 64 UTF-8 bytes to be rejected.\n' >&2
    exit 1
fi
grep -q 'friendlyName must be at most 64 UTF-8 bytes' <<<"$invalid_friendly_name_output"

apply_output=$("$dotnet_bin" "$app" apply --config "$project_root/tests/router.json")
grep -q '"GadgetCount": 4' <<<"$apply_output"
grep -q '"LinksCreated": 17' <<<"$apply_output"
grep -q '"Success": true' <<<"$apply_output"

ndi_apply_output=$("$dotnet_bin" "$app" apply --config "$project_root/tests/router-ndi.json")
grep -q '"GadgetCount": 4' <<<"$ndi_apply_output"
grep -q '"LinksCreated": 18' <<<"$ndi_apply_output"
grep -q '"Success": true' <<<"$ndi_apply_output"

ndi_receiver_apply_output=$("$dotnet_bin" "$app" apply \
    --config "$project_root/tests/router-ndi-receiver.json")
grep -q '"GadgetCount": 4' <<<"$ndi_receiver_apply_output"
grep -q '"LinksCreated": 17' <<<"$ndi_receiver_apply_output"
grep -q '"Success": true' <<<"$ndi_receiver_apply_output"

if invalid_receiver_output=$("$dotnet_bin" "$app" apply \
    --config "$project_root/tests/router-ndi-receiver-invalid.json"); then
    printf 'Expected an enabled NDI receiver without a source to be rejected.\n' >&2
    exit 1
fi
grep -q 'ndiReceiver.sourceName must be selected' <<<"$invalid_receiver_output"

if invalid_gain_output=$("$dotnet_bin" "$app" apply \
    --config "$project_root/tests/router-invalid-gain.json"); then
    printf 'Expected gain above 150%% to be rejected.\n' >&2
    exit 1
fi
grep -q 'physicalPlaybackGain must be between 0.0 and 1.5' <<<"$invalid_gain_output"

if invalid_keyboard_output=$("$dotnet_bin" "$app" apply \
    --config "$project_root/tests/router-invalid-keyboard.json"); then
    printf 'Expected invalid keyboard control mappings to be rejected.\n' >&2
    exit 1
fi
grep -q 'keyboardControl.gainStepPercent must be between 1 and 25' <<<"$invalid_keyboard_output"
grep -q 'keyboardControl.microphoneGainDevice must be between 1 and 4' <<<"$invalid_keyboard_output"
grep -q 'keyboardControl.channels\[1\].button must use a key event' <<<"$invalid_keyboard_output"
grep -q 'keyboardControl.channels\[2\].action is invalid' <<<"$invalid_keyboard_output"

printf 'Smoke tests passed.\n'
