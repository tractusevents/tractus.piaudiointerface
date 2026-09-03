# USB serial control protocol

The Tractus USB Audio Interface already exposes a CDC ACM serial function over
the same USB-C gadget connection as the eight audio endpoints. It does not need
another cable or consume a Pi USB-A host port.

Windows uses its built-in `usbser.sys` class driver, so no Tractus driver is
needed. Look under **Device Manager > Ports (COM & LPT)** for **USB Serial
Device (COMx)**. The port normally appears as `/dev/cu.usbmodem*` on macOS and
`/dev/ttyACM*` on Linux.

## Port settings and framing

- 115200 baud, 8 data bits, no parity, 1 stop bit;
- no hardware or software flow control;
- UTF-8 text, one command per line;
- LF terminates a command; CR is ignored, so CRLF also works;
- one single-line JSON response is returned for every command.

CDC ACM transports bytes over USB rather than a physical UART clock, so the
115200 setting is conventional. The host should still configure it consistently.
Command names are case-insensitive.

## Commands

| Command | Meaning |
| --- | --- |
| `HELP` | Return the supported command syntax |
| `STATUS` | Return current configuration and routing state |
| `MUTE MIC <1-4\|ALL>` | Mute one or every PC microphone feed |
| `UNMUTE MIC <1-4\|ALL>` | Enable one or every PC microphone feed |
| `MUTE OUTPUT <1-4\|ALL>` | Remove one or every PC playback endpoint from the mix |
| `UNMUTE OUTPUT <1-4\|ALL>` | Add one or every PC playback endpoint to the mix |
| `GAIN MIC <1-4> <0-150>` | Set one microphone gain in percent |
| `GAIN OUTPUT <1-4> <0-150>` | Set one output mix level in percent |
| `GAIN MASTER <0-150>` | Set physical playback master gain in percent |
| `SIDETONE ON` | Route the selected microphone channel to physical playback |
| `SIDETONE OFF` | Mute sidetone; this is the default |
| `GAIN SIDETONE <0-150>` | Set sidetone level in percent |
| `SOLO OUTPUT <1-4>` | Add an output to the solo set |
| `SOLO EXCLUSIVE <1-4>` | Solo only one output and clear all other solos |
| `UNSOLO OUTPUT <1-4\|ALL>` | Clear one or every output solo |
| `DUCK ON` | Enable sidechain ducking |
| `DUCK OFF` or `DUCK BYPASS` | Bypass sidechain ducking |
| `DUCK PRIORITY <1-4>` | Select the protected priority output |
| `DUCK PRIORITY SELF` | Detect the local captured microphone and duck all four outputs |
| `DUCK TRIGGERS` | Return the selected trigger-source list |
| `DUCK TRIGGER <SELF\|1-4> <ON\|OFF>` | Add or remove one trigger source |
| `DUCK THRESHOLD <-90..0>` | Set threshold in dBFS |
| `DUCK DEPTH <0..60>` | Set non-trigger attenuation in dB |
| `DUCK ATTACK <1..2000>` | Set attack in milliseconds |
| `DUCK HOLD <0..5000>` | Set hold in milliseconds |
| `DUCK RELEASE <1..10000>` | Set release in milliseconds |
| `METERS` | Return the latest peak/RMS and gain-reduction meter frame |
| `NDI ON` | Enable the NDI microphone sender and its capture link |
| `NDI OFF` | Disable and remove the NDI microphone sender |
| `NDI NAME <source name>` | Set the persisted NDI source name |
| `NDI STATUS` | Return receiver count, mic dBFS, queue, and error counters |
| `NDI SOURCES` | Return the current discovered NDI source list |
| `NDI RECEIVE SOURCE <source name>` | Select the full discovered receiver source name |
| `NDI RECEIVE ON` | Start the audio-only receiver and unmute its DSP return |
| `NDI RECEIVE OFF` | Stop and mute the receiver |
| `GAIN NDI <0-150>` | Set the NDI-return level in percent |

`OUT` is accepted as an alias for `OUTPUT`. For convenience, an omitted target
defaults to `MIC`, so `MUTE 4`, `MUTE ALL`, and `UNMUTE ALL` operate on
microphone feeds. Use the explicit form in automation to make intent clear.

Mute and gain commands apply and persist immediately. Device and channel
mappings are intentionally managed through the web UI or HTTP API, not through
this serial protocol.

NDI commands also apply and persist immediately. `NDI ON` makes the selected
physical microphone discoverable to NDI receivers on the LAN. `NDI OFF` destroys
the network sender; it does not change any of the four USB microphone feeds.
The receiver commands are independent of the sender. Select a source before
using `NDI RECEIVE ON`.

## Responses

Success:

```json
{"ok":true,"message":"MIC muted","state":{"configuration":{...},"routing":{...}}}
```

Failure:

```json
{"ok":false,"message":"Device number must be 1-4."}
```

`STATUS` and every successful mutation contain state suitable for controller
feedback. `METERS` returns the latest DSP frame. Protocol version 1 does not
send unsolicited serial data because that can interfere with simple
command/response controllers. Controllers needing push feedback should use the
HTTP `/api/events` SSE stream; it receives meter and state changes without
polling.

## Windows PowerShell example

Change `COM7` to the port shown by Device Manager:

```powershell
$port = [System.IO.Ports.SerialPort]::new('COM7', 115200)
$port.NewLine = "`n"
$port.ReadTimeout = 2000
$port.Open()

$port.WriteLine('MUTE MIC 4')
$reply = $port.ReadLine() | ConvertFrom-Json
$reply

$port.WriteLine('STATUS')
$state = $port.ReadLine() | ConvertFrom-Json
$state.state.configuration.devices

$port.Close()
```

Only one application should hold the COM port at a time. Always close or
dispose the port when the controller exits.

## Pi-side verification

```bash
ls -l /dev/ttyGS0
find /sys/kernel/config/usb_gadget/pi-usb-audio/configs/c.1 \
  -maxdepth 1 -name 'acm.*' -type l -print
systemctl --user status pi-usb-audio-router
```

The Pi-side device should be `/dev/ttyGS0`, owned by group `dialout`, and the
`acm.control` function should be linked into the active gadget configuration.
If Windows does not create a COM port, reconnect the Pi gadget cable and inspect
the composite device in Device Manager before changing software.

Microsoft documents automatic use of the inbox CDC ACM serial driver here:
[USB serial driver (Usbser.sys)](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/usb-driver-installation-based-on-compatible-ids).
