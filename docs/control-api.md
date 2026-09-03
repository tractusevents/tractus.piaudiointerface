# Tractus USB Audio control API

This document describes control API version 1. The service listens on port
`5055` on every network interface by default. Replace `192.168.1.91` in the
examples with the current address of the Pi.

```text
http://192.168.1.91:5055
```

The API has no authentication or TLS. Use it only on a trusted control network,
or restrict access to TCP port 5055 with a firewall. All JSON property names are
camel-case.

## Direction terminology

- A **mic** is a mono recording endpoint presented by the Pi to the attached
  PC. The selected physical capture channel feeds each enabled mic.
- An **output** is a stereo playback endpoint presented by the Pi to the PC.
  Every enabled output is mixed into the selected physical playback device.
- **Master gain** is applied after the four virtual outputs are mixed, before
  audio reaches the physical playback device.
- **Sidetone** is the selected physical capture channel duplicated to the
  physical playback device; it is muted by default.
- **NDI return** is a discovered NDI source converted to 48 kHz stereo and
  mixed into the physical playback device.

Device numbers are always `1` through `4`.

## Discover the protocol

```http
GET /api/info
```

Example response:

```json
{
  "service": "Tractus USB Audio Interface",
  "apiVersion": 1,
  "serialProtocolVersion": 1,
  "virtualDeviceCount": 4,
  "capabilities": [
    "ducking", "multi-trigger-ducking", "self-ducking", "solo",
    "push-meters", "server-sent-events",
    "sidetone", "ndi-audio", "ndi-audio-receiver", "ndi-source-discovery",
    "push-ndi-status", "push-ndi-sources", "usb-gadget-diagnostics"
  ]
}
```

A controller should reject an unsupported major `apiVersion` and show a useful
connection error.

## Live mix controls

These operations apply immediately and are persisted immediately. They never
require the web UI's **Save device mappings** button. Successful requests return
HTTP 200 with the updated [control state](#control-state) as JSON.

| Method | Path | Meaning |
| --- | --- | --- |
| `POST` | `/api/mics/{1-4}/mute` | Stop the physical capture feed to one PC mic |
| `POST` | `/api/mics/{1-4}/unmute` | Enable the physical capture feed to one PC mic |
| `POST` | `/api/mics/mute-all` | Mute all four PC mic feeds |
| `POST` | `/api/mics/unmute-all` | Enable all four PC mic feeds |
| `POST` | `/api/mics/{1-4}/gain?percent={0-150}` | Set one mic's digital gain |
| `POST` | `/api/outputs/{1-4}/mute` | Remove one PC playback endpoint from the physical mix |
| `POST` | `/api/outputs/{1-4}/unmute` | Add one PC playback endpoint to the physical mix |
| `POST` | `/api/outputs/mute-all` | Remove all four PC playback endpoints from the mix |
| `POST` | `/api/outputs/unmute-all` | Add all four PC playback endpoints to the mix |
| `POST` | `/api/outputs/{1-4}/gain?percent={0-150}` | Set one playback endpoint's mix level |
| `POST` | `/api/outputs/{1-4}/solo` | Add one output to the active solo set |
| `POST` | `/api/outputs/{1-4}/solo-exclusive` | Clear other solos and solo only this output |
| `POST` | `/api/outputs/{1-4}/unsolo` | Remove one output from the solo set |
| `POST` | `/api/outputs/unsolo-all` | Clear the complete solo set |
| `POST` | `/api/master/gain?percent={0-150}` | Set the physical playback master gain |
| `POST` | `/api/sidetone/mute` | Mute microphone sidetone |
| `POST` | `/api/sidetone/unmute` | Enable microphone sidetone |
| `POST` | `/api/sidetone/gain?percent={0-150}` | Set sidetone level |

`100` is unity gain. Values above 100 provide digital gain and can clip when
sources are summed.

When one or more outputs are soloed, only enabled members of the solo set are
audible. Solo does not alter mute state or gain, and clearing all solos restores
the normal enabled mix. DSP-only controls use click-free gain ramps and do not
rebuild the PipeWire graph.

## Ducking / sidechain controls

Select any combination of Self and outputs 1-4 as trigger sources. Each source
is evaluated independently and the loudest detector level controls the shared
duck gain. Selected USB outputs remain at their normal level; only unselected
USB outputs are attenuated. Self uses the selected physical capture channel
directly and works while sidetone is muted. At least one trigger must remain
selected. Settings apply and persist immediately.

| Method | Path | Range/meaning |
| --- | --- | --- |
| `POST` | `/api/ducking/enable` | Enable sidechain ducking |
| `POST` | `/api/ducking/bypass` | Bypass sidechain ducking with a click-free return to unity |
| `GET` | `/api/ducking/triggers` | Read `{ sources: [0..4] }`; `0` means Self |
| `PUT` | `/api/ducking/triggers` | Replace the trigger set with `{ sources: [0,1,2] }` |
| `POST` | `/api/ducking/triggers/{0-4}/enable` | Add Self (`0`) or output 1-4 as a trigger |
| `POST` | `/api/ducking/triggers/{0-4}/disable` | Remove a trigger; the final trigger cannot be removed |
| `POST` | `/api/ducking/priority?device={0-4}` | Compatibility: replace the set with one trigger |
| `POST` | `/api/ducking/priority-self` | Compatibility: replace the set with Self only |
| `POST` | `/api/ducking/threshold?dbfs={-90..0}` | Detector threshold in dBFS |
| `POST` | `/api/ducking/depth?db={0..60}` | Attenuation applied to non-trigger outputs |
| `POST` | `/api/ducking/attack?milliseconds={1..2000}` | Gain-reduction attack time |
| `POST` | `/api/ducking/hold?milliseconds={0..5000}` | Hold time after signal falls below threshold and hysteresis |
| `POST` | `/api/ducking/release?milliseconds={1..10000}` | Return-to-normal time |

The defaults are bypassed, output 1 as the sole trigger, `-30 dBFS` threshold, `18 dB`
depth, `10 ms` attack, `150 ms` hold, and `400 ms` release. The detector has 3
dB of hysteresis to prevent chatter.

## NDI microphone sender

The NDI sender uses the selected physical capture device and channel. Its state
and source name persist immediately; the device/channel mapping remains subject
to the mappings save described below. NDI is disabled by default.

| Method | Path | Meaning |
| --- | --- | --- |
| `GET` | `/api/ndi` | Read `{ enabled, sourceName }` |
| `PUT` | `/api/ndi` | Replace both NDI settings with a JSON body |
| `POST` | `/api/ndi/enable` | Create the sender and route the selected microphone to it |
| `POST` | `/api/ndi/disable` | Remove its capture link and destroy the sender |
| `POST` | `/api/ndi/name?name={UTF-8 name}` | Rename/recreate the source without changing enable state |
| `GET` | `/api/ndi/status` | Latest pushed native sender status, or HTTP 204 before one arrives |

Example complete settings update:

```http
PUT /api/ndi
Content-Type: application/json

{"enabled":true,"sourceName":"Tractus Studio Microphone"}
```

The name must be non-empty, contain no control characters, and occupy at most
127 UTF-8 bytes. The audio format is mono planar float at 48 kHz. There is no
video stream. Enabling it makes the microphone source discoverable to NDI
receivers on the LAN.

`GET /api/ndi/status` returns data shaped like:

```json
{
  "timestamp": "2026-09-03T00:31:14.225Z",
  "sequence": 41,
  "enabled": true,
  "senderOnline": true,
  "connections": 1,
  "peakDbfs": -8.4,
  "rmsDbfs": -19.7,
  "queueMilliseconds": 49.8,
  "underruns": 0,
  "overruns": 0,
  "receiverEnabled": true,
  "receiverConnected": true,
  "receiverPeakDbfs": -10.2,
  "receiverRmsDbfs": -18.6,
  "receiverQueueMilliseconds": 30.0,
  "receiverUnderruns": 0,
  "receiverOverruns": 0
}
```

Peak/RMS describe the audio actually submitted to NDI. Queue and error counters
diagnose clocking or scheduling problems. Applications should consume the
`ndi` SSE event instead of polling this endpoint for live feedback.

## NDI audio receiver

The audio-only receiver subscribes by full discovered NDI source name. It asks
framesync for exactly 48 kHz, two-channel audio in the number of samples needed
by its queue, then routes the stereo result through the NDI-return DSP fader to
the physical playback device. It is disabled by default.

| Method | Path | Meaning |
| --- | --- | --- |
| `GET` | `/api/ndi/sources` | Latest discovered source list |
| `GET` | `/api/ndi/receiver` | Read `{ enabled, sourceName, gain }` |
| `PUT` | `/api/ndi/receiver` | Replace all receiver settings with a JSON body |
| `POST` | `/api/ndi/receiver/source?name={full source name}` | Select a source |
| `POST` | `/api/ndi/receiver/enable` | Start receiving the selected source |
| `POST` | `/api/ndi/receiver/disable` | Stop the receiver and mute its return |
| `POST` | `/api/ndi/receiver/gain?percent={0-150}` | Set the live NDI-return fader |

Example:

```bash
curl -X POST 'http://192.168.1.91:5055/api/ndi/receiver/source?name=STUDIO%20%28Program%29'
curl -X POST http://192.168.1.91:5055/api/ndi/receiver/enable
curl -X POST 'http://192.168.1.91:5055/api/ndi/receiver/gain?percent=75'
```

An enabled receiver requires a non-empty source name. Source names occupy at
most 511 UTF-8 bytes and cannot contain control characters. `GET
/api/ndi/sources` is a snapshot; consume `ndi-sources` SSE events for live
discovery updates without polling.

Examples:

```bash
curl -X POST http://192.168.1.91:5055/api/mics/4/mute
curl -X POST http://192.168.1.91:5055/api/mics/unmute-all
curl -X POST 'http://192.168.1.91:5055/api/outputs/2/gain?percent=20'
curl -X POST 'http://192.168.1.91:5055/api/master/gain?percent=100'
```

PowerShell:

```powershell
$base = 'http://192.168.1.91:5055'
Invoke-RestMethod -Method Post -Uri "$base/api/mics/4/mute"
Invoke-RestMethod -Method Post -Uri "$base/api/outputs/2/gain?percent=20"
```

## Device and channel mappings

Only the physical capture device, capture channel, physical playback device,
and playback channel assignments use an explicit save operation in the web UI.
The mappings-only endpoint cannot overwrite live mute or gain values.

```http
GET /api/mappings
PUT /api/mappings
Content-Type: application/json
```

Example request body:

```json
{
  "physicalCaptureNode": "alsa_input.usb-example.analog-stereo",
  "physicalCaptureChannel": "FL",
  "physicalPlaybackNode": "alsa_output.usb-example.analog-stereo",
  "physicalPlaybackLeftChannel": "FL",
  "physicalPlaybackRightChannel": "FR"
}
```

`PUT /api/mappings` persists the five fields, reapplies the routing graph, and
returns the updated control state. Node and port identifiers can be discovered
with `GET /api/nodes`.

## Control state

```http
GET /api/state
```

The result has two top-level properties:

- `configuration`: persisted mappings and live mix settings;
- `routing`: the most recent graph-apply result, or `null` before the first
  apply.

Abbreviated example:

```json
{
  "configuration": {
    "physicalPlaybackGain": 1.0,
    "sidetone": { "enabled": false, "gain": 1.0 },
    "ducking": {
      "enabled": false,
      "priorityDevice": 1,
      "triggerSources": [1, 3],
      "thresholdDbfs": -30.0,
      "depthDb": 18.0,
      "attackMilliseconds": 10.0,
      "holdMilliseconds": 150.0,
      "releaseMilliseconds": 400.0
    },
    "ndiReceiver": { "enabled": false, "sourceName": "", "gain": 1.0 },
    "devices": [
      {
        "number": 1,
        "inputEnabled": true,
        "inputGain": 1.0,
        "outputEnabled": true,
        "outputGain": 0.2,
        "outputSolo": false
      }
    ]
  },
  "routing": {
    "timestamp": "2026-09-02T23:42:23.6387249+00:00",
    "gadgetCount": 4,
    "linksCreated": 14,
    "linksRemoved": 0,
    "warnings": [],
    "errors": [],
    "success": true
  }
}
```

Gain values in state are multipliers: `1.0` is 100%, `0.2` is 20%, and `1.5`
is 150%.

## USB OTG / gadget diagnostics

```http
GET /api/gadget
```

This read-only endpoint reports the live ConfigFS and USB Device Controller
state. It includes the available and bound UDC, enumeration state and speed,
USB VID/PID/revision and strings, active UAC2/CDC ACM functions, `/dev/ttyGS0`
presence, per-function channel/sample format, request count, and the latest
PipeWire routing result. `healthy` is true when the gadget is bound and
configured by the host with four UAC2 functions and the serial-control
function present.

Use `problems` for a concise fault list. The complete values are intentionally
included so an installer can distinguish missing DWC2/ConfigFS setup, an
unbound gadget, a host enumeration issue, a missing serial function, and an
audio-routing problem without shell access to the Pi.

## Meter snapshot

```http
GET /api/meters
```

This returns the most recent DSP meter frame, or HTTP 204 before the first
frame. Values are sample-peak and RMS dBFS over an approximately 100 ms window:

```json
{
  "timestamp": "2026-09-03T00:11:31.878653+00:00",
  "sequence": 78,
  "duckingActive": true,
  "duckGainReductionDb": 18.0,
  "devices": [
    { "number": 1, "peakDbfs": -2.96, "rmsDbfs": -12.92 }
  ],
  "sidetone": { "peakDbfs": -18.2, "rmsDbfs": -27.4 },
  "ndiReceiver": { "peakDbfs": -9.1, "rmsDbfs": -17.8 },
  "mix": { "peakDbfs": -2.96, "rmsDbfs": -12.92 }
}
```

Silence is reported as `-120`. Positive peaks indicate that the floating-point
mix exceeds digital full scale and will clip when converted to the physical
output format. These are sample meters, not oversampled true-peak meters.
Each device meter is post-fader but pre-mute, pre-solo, and pre-duck, so input
from the host remains visible while that output mix is muted. The `mix` meter
is post-mix and post-master and therefore represents audio sent to the physical
output. `sidetone` and `ndiReceiver` are also post-fader/pre-mute, so their
signals remain visible while their monitor paths are muted. During active
ducking, subtract `duckGainReductionDb` from each unselected device meter to
obtain the current ducked dBFS value.

`GET /api/meters` is useful for diagnostics. Applications should use the push
stream below for live displays instead of polling it.

## Real-time feedback stream

```http
GET /api/events
Accept: text/event-stream
```

The Server-Sent Events stream sends an initial `state` event, then pushes
`state` whenever configuration/routing changes, `meters` about ten times per
second directly from the DSP event bridge, `ndi` with sender and receiver
health, and `ndi-sources` whenever NDI discovery changes:

```text
event: state
data: {"configuration":{...},"routing":{...}}

event: meters
data: {"sequence":79,"duckingActive":false,...}

event: ndi
data: {"sequence":41,"senderOnline":true,"receiverConnected":true,...}

event: ndi-sources
data: {"sequence":7,"sources":["STUDIO (Program)","STAGE (Mix)"]}
```

Controllers should update variables and feedbacks from each event. There is no
meter or state polling behind this stream. If it disconnects, the browser
`EventSource` API reconnects automatically; other clients should reconnect with
backoff and fetch `GET /api/state` once after reconnect. A successful live
control response also contains fresh state and can be applied immediately.

Test the stream with:

```bash
curl -N http://192.168.1.91:5055/api/events
```

## Diagnostic and administrative endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/status` | Last routing result and currently visible PipeWire sources/sinks |
| `GET` | `/api/gadget` | Live USB OTG, UDC, ConfigFS function, format, and CDC serial diagnostics |
| `GET` | `/api/nodes` | Selectable non-gadget audio nodes and their ports |
| `GET` | `/api/ndi/status` | Latest NDI sender/receiver status snapshot |
| `GET` | `/api/ndi/sources` | Latest discovered NDI source list |
| `POST` | `/api/apply` | Reconcile the routing graph with persisted configuration |
| `GET` | `/api/config` | Read the complete persisted configuration |
| `PUT` | `/api/config` | Replace the complete configuration and apply it |

`PUT /api/config` is intended for administration and backup/restore. Companion
and other live controllers should use the narrow endpoints above so they cannot
accidentally replace unrelated settings.

Treat every non-2xx response, malformed JSON response, or request timeout as a
failed operation. Keep the last known state for display, mark the connection as
degraded, and fetch `/api/state` when communication resumes.
