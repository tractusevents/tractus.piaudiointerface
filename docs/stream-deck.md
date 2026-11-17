# Stream Deck Mini microphone controls

An original Elgato Stream Deck Mini (`0fd9:0063`) connected to a Pi USB-A host
port is detected automatically by the audio control service. No desktop session,
Elgato application, Companion installation, or extra Python runtime is needed.

Buttons are numbered left to right across the top row, then the bottom row:

```text
Mic 1    Mic 2       Mic 3
Mic 4    Unassigned  PANIC - MUTE ALL
```

Each press of buttons 1–4 toggles the corresponding Pi-to-PC USB microphone feed once.
Releasing or holding a button has no additional effect. The four feeds are
independent; the stereo PC playback outputs and the NDI microphone sender are
not affected. Button 5 has no action.

Button 6 is the red **PANIC / MUTE ALL** key. Every press mutes all four mic
feeds in one configuration/routing update. Holding or releasing it has no
additional effect, and repeated presses keep every mic muted. It takes
precedence over mic toggles reported in the same key snapshot. Use buttons 1–4
to unmute individual feeds afterward. The panic key does not change playback,
sidetone, or NDI sender settings.

Each microphone key displays its mic number, friendly channel name, and state:

- green **LIVE**: that mic feed is enabled;
- red **MUTED**: that mic feed is disabled;
- amber **ERROR**: routing is not confirmed healthy;
- amber **OFFLINE**: the control service was stopped or deck control disabled.

The controller reads the shared router configuration and refreshes changed
key images about every 200 ms. Web UI, HTTP API, serial, and existing keyboard
actions therefore appear on the deck. A tap inverts the persisted state under
the configuration update lock; it does not alternate a separate local latch.
HID input/image work runs independently of routing commands and audio threads.
Interrupted Linux HID reads resume on the next poll without closing the device;
interrupted writes retry the same unsent report. Real I/O failures still trigger
reconnection and include the native error in diagnostics.

Unplugging the deck does not change any mic state. The service retries discovery
every second and redraws all keys after reconnecting. Reports queued while
connecting establish a baseline so an already-held key is ignored until it is
released and pressed again. Shutdown also discards queued actions from the old
connection. A crash or power loss can leave the last image on the screen until
the service reconnects; the screen alone cannot detect such failures.

Existing mini-keyboard hold/latch controls continue operating on the same mic
states. Their normal release actions, including the existing reset to released
states when their input session restarts, can override a previous deck toggle.
The deck displays the resulting state. Disable unwanted keyboard channel actions
in the web UI if using the deck in their place.

## Configuration and installation

The normal installer installs `config/90-pi-usb-audio-stream-deck.rules`, granting
the `input` group read/write access only to this Mini model. The service user is
already added to that group by installation. The service runs as the ordinary
audio user. Device paths such as `/dev/hidraw2` are rediscovered, not hardcoded.
The HID handle is locked to prevent simultaneous cooperating controller instances.

The optional `streamDeck` object in the main `router.json`/configuration API is:

```json
{"streamDeck":{"enabled":true,"brightness":30}}
```

Defaults enable automatic detection and set brightness to 30 percent.
Brightness accepts integers from 0 through 100. Configuration changes are picked
up while running, with no USB gadget re-enumeration required. Only the first
discovered original Mini is controlled; other Stream Deck models are excluded.

## Diagnostics and API

- `GET /api/stream-deck`: connection, error, displayed-key descriptions, physical
  button-press count, last button, and last press timestamp.
- `GET /api/events`: includes an initial `stream-deck` status event and subsequent
  changes. The web UI's Controls tab also shows connection status.
- `POST /api/mics/{1-4}/toggle`: atomically invert one microphone's enabled state,
  apply routing, persist it, and return the usual control state.

Successful configuration persistence alone does not guarantee a functioning
capture device. Check audio routing diagnostics if keys show **ERROR**.

Run `./tests/smoke.sh` before deployment. Stream Deck tests cover full-report
edge detection, simultaneous buttons, image encoding/framing, concurrent toggles,
external-state feedback, friendly names, disconnect/reconnect, and disable.
They use fake HID and PipeWire backends and do not touch live audio.
An additional native HID fixture verifies recovery from interrupted system calls
through the actual P/Invoke boundary and checks that real failures still surface.

Protocol references: [Elgato Mini HID documentation](https://docs.elgato.com/streamdeck/hid/mini/)
and the MIT-licensed [python-elgato-streamdeck Mini implementation](https://github.com/abcminiuser/python-elgato-streamdeck/blob/master/src/StreamDeck/Devices/StreamDeckMini.py).
