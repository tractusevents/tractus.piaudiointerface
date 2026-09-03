# Bitfocus Companion module specification

This is the implementation specification for a dedicated Tractus USB Audio
Interface connection module. HTTP is the preferred Companion transport because
it works from any machine on the control LAN and provides Server-Sent Events
(SSE) for feedback. USB serial is useful for local scripts and fallback control,
but is not required by the Companion module.

The controlled API is documented in [control-api.md](control-api.md). The
proposed module repository and manifest identity is:

```text
companion-module-tractus-usb-audio
manifest id: tractus-usb-audio
manifest type: connection
```

## Use with Companion before a dedicated module exists

Use a Companion module/action capable of issuing a custom HTTP request. Set the
method to `POST`, leave the request body empty, and use URLs such as:

| Button | URL |
| --- | --- |
| Mute Mic 4 | `http://192.168.1.91:5055/api/mics/4/mute` |
| Unmute Mic 2 | `http://192.168.1.91:5055/api/mics/2/unmute` |
| Mute all mics | `http://192.168.1.91:5055/api/mics/mute-all` |
| Unmute all mics | `http://192.168.1.91:5055/api/mics/unmute-all` |
| Mute Output 1 | `http://192.168.1.91:5055/api/outputs/1/mute` |
| Output 2 at 20% | `http://192.168.1.91:5055/api/outputs/2/gain?percent=20` |
| Master at 100% | `http://192.168.1.91:5055/api/master/gain?percent=100` |
| Sidetone on | `http://192.168.1.91:5055/api/sidetone/unmute` |
| Sidetone off | `http://192.168.1.91:5055/api/sidetone/mute` |
| NDI return off | `http://192.168.1.91:5055/api/ndi/receiver/disable` |

This provides actions but not authoritative button feedback. The dedicated
module below should consume `/api/events` for feedbacks and variables.

## Module configuration

Expose these connection fields:

| ID | Type | Default | Notes |
| --- | --- | --- | --- |
| `host` | text | none | IPv4 address or hostname of the Pi; do not include a URL scheme |
| `port` | number | `5055` | HTTP service port |
| `use_https` | checkbox | `false` | Reserved for installations with a trusted reverse proxy |
| `reconnect_seconds` | number | `2` | Initial SSE reconnect delay; cap exponential backoff at 10 seconds |

On initialization and configuration changes:

1. Request `GET /api/info` and require `apiVersion === 1`.
2. Request `GET /api/state` and publish all variables and feedbacks.
3. Open `GET /api/events` with `Accept: text/event-stream`.
4. Set Companion status to OK after valid state arrives.
5. On failure, show a connection error, close the old stream, retry with
   backoff, and fetch `/api/state` once after SSE has recovered.

The Pi API currently has no authentication. State that clearly in the module
configuration help rather than displaying unused username/password fields.

## Actions

Action and option IDs form part of saved Companion configurations. Keep these
IDs stable after release, or provide an upgrade script.

| Action ID | User-facing name | Options | Operation |
| --- | --- | --- | --- |
| `set_mic_mute` | Set microphone mute | `device`: 1-4 or all; `muted`: boolean | POST the corresponding mic mute/unmute endpoint |
| `toggle_mic_mute` | Toggle microphone mute | `device`: 1-4 | Choose mute/unmute from cached state; refresh state first if unknown |
| `set_output_mute` | Set output mute | `device`: 1-4 or all; `muted`: boolean | POST the corresponding output mute/unmute endpoint |
| `toggle_output_mute` | Toggle output mute | `device`: 1-4 | Choose mute/unmute from cached state; refresh state first if unknown |
| `set_mic_gain` | Set microphone gain | `device`: 1-4; `percent`: 0-150 | POST `/api/mics/{device}/gain` |
| `set_output_gain` | Set output level | `device`: 1-4; `percent`: 0-150 | POST `/api/outputs/{device}/gain` |
| `set_master_gain` | Set master output gain | `percent`: 0-150 | POST `/api/master/gain` |
| `set_output_solo` | Set output solo | `device`: 1-4; `solo`: boolean | POST the corresponding solo/unsolo endpoint |
| `solo_output_exclusive` | Solo output exclusively | `device`: 1-4 | POST `/api/outputs/{device}/solo-exclusive` |
| `clear_all_solos` | Clear all output solos | none | POST `/api/outputs/unsolo-all` |
| `set_ducking` | Enable/bypass ducking | `enabled`: boolean | POST `/api/ducking/enable` or `/bypass` |
| `set_duck_priority` | Set sole trigger source | `device`: self or 1-4 | Compatibility action using `/api/ducking/priority-self` or `/priority` |
| `set_duck_trigger` | Add/remove duck trigger | `source`: self or 1-4; `enabled`: boolean | POST `/api/ducking/triggers/{source}/enable` or `/disable` |
| `set_duck_threshold` | Set duck threshold | `dbfs`: -90 to 0 | POST `/api/ducking/threshold` |
| `set_duck_depth` | Set duck depth | `db`: 0-60 | POST `/api/ducking/depth` |
| `set_duck_timing` | Set duck timing | attack, hold, release milliseconds | POST the three timing endpoints |
| `refresh_state` | Refresh state | none | GET `/api/state` |
| `set_ndi_enabled` | Enable/disable NDI microphone | `enabled`: boolean | POST `/api/ndi/enable` or `/disable` |
| `set_ndi_name` | Set NDI source name | `name`: text | POST `/api/ndi/name` |
| `set_sidetone` | Enable/mute sidetone | `enabled`: boolean | POST `/api/sidetone/unmute` or `/mute` |
| `set_sidetone_gain` | Set sidetone level | `percent`: 0-150 | POST `/api/sidetone/gain` |
| `set_ndi_receiver_source` | Select NDI return source | `name`: discovered source | POST `/api/ndi/receiver/source` |
| `set_ndi_receiver_enabled` | Enable/disable NDI return | `enabled`: boolean | POST `/api/ndi/receiver/enable` or `/disable` |
| `set_ndi_receiver_gain` | Set NDI return level | `percent`: 0-150 | POST `/api/ndi/receiver/gain` |

After every successful action, parse the returned control state immediately,
update variables, and recheck affected feedbacks. Do not wait for the next SSE
event. Throw or return an action error for non-2xx responses so Companion logs
the failure.

Mappings should not be exposed as button actions in the first module release.
They are installation settings that require an explicit save in the web UI.

## Feedbacks

Use boolean feedbacks unless a value feedback is specifically useful. Built-in
feedback inversion can turn an `enabled` feedback into a `muted` indication.

| Feedback ID | Condition/options |
| --- | --- |
| `connected` | A valid API v1 state has been received and the connection is current |
| `routing_ok` | `routing.success` is true and `routing.gadgetCount` is 4 |
| `mic_enabled` | Selected device's `inputEnabled` is true |
| `output_enabled` | Selected device's `outputEnabled` is true |
| `mic_gain_matches` | Selected device's mic gain equals the selected percentage |
| `output_gain_matches` | Selected device's output gain equals the selected percentage |
| `master_gain_matches` | Master gain equals the selected percentage |
| `output_soloed` | Selected output's `outputSolo` is true |
| `ducking_enabled` | Ducking is enabled rather than bypassed |
| `ducking_active` | Latest pushed meter frame reports active gain reduction |
| `duck_trigger_selected` | Selected Self/output source is in `ducking.triggerSources` |
| `signal_present` | Selected output's pre-mute peak exceeds a configurable dBFS threshold |
| `mix_clipping` | Final mix peak is at or above 0 dBFS |
| `ndi_enabled` | Persisted NDI configuration is enabled |
| `ndi_sender_online` | Latest `ndi` event reports an active sender instance |
| `ndi_receiver_connected` | Latest `ndi` event reports one or more receivers |
| `ndi_signal_present` | NDI microphone peak exceeds a configurable dBFS threshold |
| `sidetone_enabled` | Persisted sidetone state is enabled |
| `sidetone_signal_present` | Pushed sidetone peak exceeds a configurable dBFS threshold |
| `ndi_return_enabled` | Persisted NDI receiver state is enabled |
| `ndi_return_connected` | Native receiver is connected to its selected source |
| `ndi_return_signal_present` | Pushed NDI-return peak exceeds a configurable dBFS threshold |

Compare gains after converting multipliers to rounded integer percentages. This
matches the web UI and avoids floating-point equality surprises.

Whenever state changes, call the current module API's targeted feedback check
methods for the affected definitions. On a new full state or reconnect, recheck
all feedbacks.

## Variables

Define variables once, then update their values in one batch whenever a state
event or action response arrives:

```text
connected
routing_ok
gadget_count
links_created
links_removed
last_error
master_gain_percent
ducking_enabled
ducking_active
ducking_priority
ducking_triggers
duck_gain_reduction_db
mix_peak_dbfs
mix_rms_dbfs
ndi_enabled
ndi_sender_online
ndi_connections
ndi_peak_dbfs
ndi_rms_dbfs
ndi_queue_ms
ndi_underruns
ndi_overruns
sidetone_enabled
sidetone_gain_percent
sidetone_peak_dbfs
sidetone_rms_dbfs
ndi_return_enabled
ndi_return_source
ndi_return_gain_percent
ndi_return_connected
ndi_return_peak_dbfs
ndi_return_rms_dbfs
ndi_return_queue_ms
ndi_return_underruns
ndi_return_overruns

mic_1_enabled ... mic_4_enabled
mic_1_muted   ... mic_4_muted
mic_1_gain_percent ... mic_4_gain_percent

output_1_enabled ... output_4_enabled
output_1_muted   ... output_4_muted
output_1_gain_percent ... output_4_gain_percent
output_1_solo ... output_4_solo
output_1_peak_dbfs ... output_4_peak_dbfs
output_1_rms_dbfs  ... output_4_rms_dbfs
```

`last_error` should be the joined `routing.errors`, or the connection error
when no current API state is available. With Companion Module API 2.x,
`setVariableDefinitions` takes an object keyed by variable ID.

## Presets

Provide these presets in the first release:

- eight stateful mute buttons: Mic 1-4 and Output 1-4;
- four exclusive Solo buttons and Clear All Solos;
- Ducking Enable/Bypass and trigger toggles for Self/Output 1-4;
- Mute All Mics and Unmute All Mics;
- Mute All Outputs and Unmute All Outputs;
- Master 0%, 50%, and 100%;
- a routing-health status button;
- NDI Enable/Disable and an NDI online/receiver status button.
- Sidetone Enable/Mute, NDI Return Enable/Disable, and discovered NDI return
  source selection.

Toggle presets should use the corresponding toggle action, show the device
number, and use `mic_enabled` or `output_enabled` feedback for their active
style. A muted style should be visually distinct and readable without relying
only on colour.

## Suggested source structure

Start from Bitfocus's current TypeScript module template, not a copied old
module:

```text
companion/
  HELP.md
  manifest.json
src/
  main.ts
  actions.ts
  feedbacks.ts
  presets.ts
  variables.ts
  api.ts
  upgrades.ts
```

Use the runtime and Module API version selected by the current template. As of
Companion Module API 2.0, modules use ESM and a Node 22 runtime, the manifest
requires `"type": "connection"`, and variable definitions use an object rather
than an array. Do not pin development instructions to an unreleased API.

Typical development commands from the template are:

```bash
yarn install
yarn build
yarn dev
yarn companion-module-build
```

Point Companion's **Developer modules path** at the parent directory containing
the module repository. Companion can then load and reload the local module.

Official references:

- [Companion module TypeScript template](https://github.com/bitfocus/companion-module-template-ts)
- [Getting started with connection modules](https://companion.free/for-developers/module-development/home/)
- [Local developer modules](https://companion.free/for-developers/module-development/local-modules/)
- [Action definitions](https://companion.free/for-developers/module-development/connection-basics/actions/)
- [Feedback definitions](https://companion.free/for-developers/module-development/connection-basics/feedbacks/)
- [Variable definitions](https://companion.free/for-developers/module-development/connection-basics/variables/)

## Acceptance tests

Before publishing the module, verify:

1. Connection status becomes OK after `/api/info` and `/api/state` succeed.
2. Every discrete mute and unmute action affects only its selected route.
3. All-device actions affect exactly four routes.
4. Gain boundary values are accepted and invalid values cannot be configured.
5. Button feedback changes immediately when control is performed in the
   web UI, over serial, or from a second Companion instance.
6. Meter, duck-active, NDI status, and NDI discovery variables update from SSE
   without HTTP polling.
7. A Pi reboot, USB audio interface reconnect, HTTP interruption, and Companion
   configuration edit all recover without recreating buttons.
8. A routing failure shows a degraded/error state while mute and gain settings
   remain available as the last known persisted state.
9. Packaged-module testing succeeds before distribution.
