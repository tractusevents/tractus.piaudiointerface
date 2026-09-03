# Tractus USB Audio Interface

Copyright 2026 Northern HCI Solutions Inc. o/a Tractus Events. This project's
original source code is licensed under the [Apache License 2.0](LICENSE).
NDI functionality requires a separately obtained SDK and remains subject to
NDI's own terms; see [NDI SDK dependency and licensing](NDI-LICENSING.md).

This project turns a Raspberry Pi 4 into four driverless USB Audio Class 2
devices. Each virtual device has:

- one mono recording input presented to the attached PC or Mac;
- one two-channel stereo playback output from the PC or Mac.

A USB audio interface attached to one of the Pi's USB-A ports supplies the
physical microphone and speaker connections. Its selected capture channel can
feed any or all four virtual microphones. The four virtual stereo outputs have
independent gains and are mixed into the physical stereo output.

A native 32-bit-float PipeWire DSP node performs the playback mix. It provides
click-free output gain, mute and solo, priority sidechain ducking, microphone
sidetone, an NDI return, and pushes sample-peak/RMS meter frames to the C#
control service without polling.

The selected physical microphone channel can also be published as a mono,
48 kHz NDI audio source. The native NDI node can simultaneously subscribe to
an audio-only NDI source and return it to the physical stereo output. Dedicated
worker threads keep blocking NDI calls and framesync resampling off PipeWire's
real-time thread. Sender/receiver state, discovered sources, queue health, and
dBFS readings are pushed through the same event stream.

## Status and important constraint

The implementation uses four instances of Linux's stock `f_uac2` ConfigFS
function. It intentionally uses adaptive host-to-Pi streams and disables USB
Feature Units. A CDC ACM function provides serial control. The resulting six IN
and five OUT endpoints fit the Pi 4 DWC2 device controller.

All host playback and recording devices use signed 16-bit PCM at 48 kHz. This
is the most interoperable Windows/macOS profile and keeps the complete gadget
full-duplex and driverless. Do not change either sample size to three bytes:
packed 24-bit Windows playback was measured to introduce a large discontinuity
at each 1 ms USB packet boundary on this Pi 4/kernel combination. Four-byte
Windows playback is also clean, but 16-bit in both directions keeps the host
format consistent.

Do not change `c_sync` to `async` or turn the four USB volume/mute controls on.
Either change consumes extra IN endpoints and the four-function gadget will no
longer fit.

The gadget preallocates 64 requests per stream to provide scheduling margin with
all four duplex functions active.

## Required software

- Raspberry Pi OS or Debian 13, 64-bit, on a Raspberry Pi 4
- .NET 10 SDK for building the control application
- `pipewire`, `pipewire-audio`, `pipewire-bin`, `wireplumber`
- `alsa-utils` and `usbutils`
- `build-essential`, `pkg-config`, and `libpipewire-0.3-dev`
- `libavahi-client3`
- Basic NDI 6.3 SDK unpacked at `/home/tractus/ndi63bsdk`

The installer installs all Debian packages except the .NET SDK and does not
download or redistribute the Basic NDI SDK. It compiles against and dynamically
loads the Pi 4 AArch64 NDI runtime already under `~/ndi63bsdk`. On this Pi, the
build SDK is already at `/home/tractus/.dotnet/dotnet`.

The installed web/control application is a self-contained, single-file
`linux-arm64` executable. The target Pi does not need a separately installed
.NET runtime after the application has been built. The static `wwwroot` web
assets are deployed beside the executable and likewise have no runtime
dependency.

### Native source layout

The two native executables use project-local headers for shared types,
constants, and cross-module declarations. Implementations remain in focused C
modules:

- `tractus-audio-dsp`: entrypoint/ports, real-time DSP engine, and control
  protocol;
- `tractus-ndi-audio`: entrypoint/ports, ring buffers, NDI workers/discovery,
  and control protocol.

Run `./tests/smoke.sh` after native changes. It builds every C module with
`-Wall -Wextra -Werror` in addition to running the application smoke tests.

## Build and test

From the repository root, run:

```bash
./tests/smoke.sh
```

The smoke suite restores and builds the C# application, publishes and executes
the self-contained `linux-arm64` control binary with `DOTNET_ROOT` pointed at a
nonexistent directory, compiles both native applications with warnings treated
as errors, checks the shell scripts, and validates routing against fake
PipeWire devices.

Override the local SDK paths when necessary:

```bash
DOTNET_BIN=/path/to/dotnet \
NDI_SDK_DIR=/path/to/ndi-sdk \
./tests/smoke.sh
```

These tests do not pass real audio or discover real NDI sources. Complete
hardware validation after deployment using the diagnostics and live API checks
below.

## Install

From this directory:

```bash
sudo ./scripts/install.sh --user tractus
sudo reboot
```

The reboot is required because the Pi 4's DWC2 peripheral controller is disabled
until the boot overlay is active. The installer adds this Pi-4-only block to
`/boot/firmware/config.txt`:

```ini
[pi4]
dtoverlay=dwc2,dr_mode=peripheral
[all]
```

It does not alter `cmdline.txt`; systemd loads the gadget modules and builds the
ConfigFS descriptors after boot.

The default USB VID/PID in `config/gadget.env` is for private prototyping only.
Set an assigned VID/PID in `/etc/default/pi-usb-audio` before distributing a
device.

For routine C, C#, or web-interface deployments after the Pi has already been
configured, use:

```bash
sudo ./scripts/install.sh --user tractus --skip-packages --skip-boot-config
```

This performs a Release publish, installs the artifacts under
`/opt/pi-usb-audio`, and restarts the running user services. It briefly
interrupts audio but does not require a reboot unless the USB gadget or boot
configuration changed.

Windows caches audio interface names using the USB device instance identity.
The default configuration appends `-v2` to the derived USB serial and advertises
device revision 2.00 so Windows reads the current Tractus descriptor strings as
a new instance. Increment `USB_SERIAL_SUFFIX` again after future naming changes.

## Physical hookup and power

### Development hookup

Use this when the computer's USB-C port explicitly supports a high-current
downstream device:

```text
PC USB-C data/power port ── full data USB-C cable ── Pi 4 USB-C port

Pi 4 USB-A port ── self-powered USB audio interface
               or ── powered USB hub ── USB audio interface
```

The Pi 4 USB-C connector is the gadget connection. The four USB-A connectors
remain host ports and are where the physical audio interface belongs.

A charge-only cable will not work. A PC USB-A to USB-C connection is generally
limited to 500 or 900 mA and is not a dependable way to power a Pi 4. Even with
USB-C, not every computer supplies the 5 V / 3 A recommended for a Pi 4.

Keep the external audio interface off the Pi's power budget by using a
self-powered interface or a powered USB hub. After testing, run:

```bash
vcgencmd get_throttled
```

`throttled=0x0` means no current or historical undervoltage/throttling was
detected since boot. A nonzero value means the power arrangement needs work.

### Reliable installed hookup

For unattended use, use a purpose-built USB-C data/power injector that:

- passes USB 2 data between the PC and Pi;
- supplies a regulated 5.1 V, 3 A to the Pi;
- prevents the external supply from back-feeding the PC's VBUS.

```text
PC USB data ─────────────┐
                        ├── USB-C data/power injector ── Pi 4 USB-C
5.1 V / 3 A supply ─────┘

Pi USB-A ── powered hub or self-powered USB interface
```

Do not use a passive Y cable that simply joins two 5 V supplies. Do not power
the GPIO 5 V pins while also accepting PC VBUS unless an engineered adapter
isolates the supplies; otherwise either source can back-feed the other.

## Verify the gadget

After the reboot and before troubleshooting routing, run:

```bash
sudo systemctl status pi-usb-audio-gadget
sudo /usr/local/sbin/pi-usb-audio-diagnose
```

The diagnostics should show:

- a DWC2 USB Device Controller under `/sys/class/udc`;
- four `uac2.deviceN` ConfigFS functions;
- four duplex `UAC2 Gadget` ALSA cards;
- the physical USB interface as another ALSA card.

On Windows, look in both **Sound > Input** and **Sound > Output**. On macOS, use
**Audio MIDI Setup**. Hosts may display identical generic names with numeric
suffixes. The descriptors advertise `Tractus USB Audio In 1` through `4` and
`Tractus USB Audio Out 1` through `4`; some Windows panels may prepend a
localized role such as `Microphone` or `Speakers`.

## Configure the physical interface and routing

The control API listens on all Pi network interfaces at port 5055. Open this
directly from a trusted LAN:

```text
http://<pi-address>:5055/
```

There is no authentication. Do not expose port 5055 to the public internet;
use a firewall or SSH tunnel when the network is not trusted. Select:

1. the physical capture device and microphone channel;
2. the physical stereo playback device and left/right channels;
3. which virtual microphones receive that channel and each microphone's
   software gain;
4. which virtual outputs are mixed, each output's percentage, and the physical
   output master level.

The physical-output section also has live faders for microphone sidetone and
the stereo NDI return. Sidetone uses the selected capture channel, duplicates
it to left and right, and is muted by default. Select an NDI source under
**Processing & NDI** before enabling the NDI return. Both paths remain metered
while muted.

The ON/MUTED buttons apply immediately. Gain sliders coalesce changes at about
90 ms while they are dragged, apply the final value on release, and snap to a
visible 100% detent. **Save and apply** is only needed for physical device or
channel selection changes.

Ducking can use any combination of USB outputs 1-4 and **Self / local
microphone** as triggers. The loudest selected source drives the detector.
Selected USB trigger outputs remain at normal level while unselected USB
outputs are ducked. Self detects the selected physical capture channel even
when sidetone is muted.

The configuration is saved in
`/home/tractus/.config/pi-usb-audio/router.json`. `20%` is a linear amplitude of
`0.2`, approximately -14 dB.

## NDI audio

NDI is disabled by default. In the web editor, open **NDI microphone sender**,
choose a source name, and enable it. It uses the same physical capture device
and channel selected at the top of the page; enabling it adds one additional
fan-out link and does not change which of the four USB microphones are enabled.

The sender publishes one channel of planar 32-bit float audio at 48 kHz. It does
not send video. The source name defaults to `Tractus USB Audio Microphone` and
can be changed live. Normal NDI receivers will commonly display the computer
name as well as this configured source name.

`clock_audio=true` is used deliberately. NDI submission therefore runs only on
a dedicated worker thread, never in the PipeWire real-time callback. A lock-free
input ring and a small adaptive queue absorb drift between the physical USB
interface clock and the NDI clock. If the worker falls behind, the real-time
callback drops data instead of blocking the USB audio graph; underrun/overrun
counters and the sent-audio peak/RMS level are visible in the UI and API.

NDI discovery and media are intended for a trusted LAN. Enabling the sender
makes the microphone source discoverable to NDI receivers on that network.

The receiver is also disabled by default. Its live source list is populated by
NDI discovery without HTTP polling. After selecting a source, enable **NDI
return** in either the mixer or Processing & NDI tab. The receiver requests
48 kHz, two-channel float audio from NDI framesync in the exact block sizes
needed to maintain its PipeWire queue; mono sources are expanded to stereo and
other source formats are converted by framesync. Its fader and mute state apply
in DSP without rebuilding the audio graph.

The same operations are available through the installed standalone executable:

```bash
/opt/pi-usb-audio/PiUsbAudio.Control list
/opt/pi-usb-audio/PiUsbAudio.Control apply
systemctl --user status pi-usb-audio-router
```

PipeWire performs adaptive resampling because the PC USB clock and the physical
USB interface clock are independent. A direct `arecord | aplay` bridge will
eventually underrun or overrun and is not suitable for continuous operation.
The installer grants PipeWire real-time scheduling and gives the emulated UAC2
ALSA devices additional buffer headroom.

## HTTP and Companion control

Mute and gain controls apply and persist immediately. Only physical playback
device, capture device, and channel mappings require the web UI's **Save device
mappings** action. The mappings-only API prevents that save from overwriting a
concurrent live mix update.

The endpoints below accept `POST` and return the updated state as JSON:

```text
/api/mics/1/mute                 /api/mics/1/unmute
/api/mics/mute-all               /api/mics/unmute-all
/api/mics/1/gain?percent=150
/api/outputs/1/mute              /api/outputs/1/unmute
/api/outputs/mute-all            /api/outputs/unmute-all
/api/outputs/1/gain?percent=20
/api/outputs/1/solo-exclusive    /api/outputs/1/unsolo
/api/outputs/unsolo-all
/api/master/gain?percent=100
/api/ducking/enable              /api/ducking/bypass
/api/ducking/priority?device=1
/api/ducking/threshold?dbfs=-30  /api/ducking/depth?db=18
/api/ndi/enable                  /api/ndi/disable
/api/ndi/name?name=Studio%20Mic
```

`GET /api/state` returns configuration and routing feedback. `GET /api/gadget`
reports live USB OTG, ConfigFS, UDC, format, and serial-function diagnostics.
`GET /api/events`
is a push-only Server-Sent Events stream carrying state changes and 10 Hz meter
frames. `GET /api/meters` returns the latest snapshot for diagnostics. These
URLs work with Companion's Generic HTTP requests; a dedicated Companion module
can use the event stream for button feedback and levels without polling.

Full protocol and integration documentation:

- [HTTP control API](docs/control-api.md)
- [Companion module specification](docs/companion-module.md)
- [USB serial control](docs/serial-control.md)

## USB serial control

The composite gadget also appears as a driverless CDC ACM serial port: a COM
port on Windows and `/dev/cu.usbmodem*` on macOS. Use 115200 8-N-1; the baud
setting is conventional because USB CDC does not use a physical baud clock.
Commands are newline-delimited and every command returns a one-line JSON result:

```text
STATUS
MUTE MIC 4
UNMUTE MIC 2
MUTE ALL
UNMUTE ALL
MUTE OUTPUT 3
GAIN MIC 2 150
GAIN OUTPUT 1 20
GAIN MASTER 100
SOLO EXCLUSIVE 1
UNSOLO OUTPUT ALL
DUCK ON
DUCK PRIORITY 1
DUCK THRESHOLD -30
DUCK BYPASS
NDI ON
NDI NAME Studio Mic
NDI STATUS
METERS
HELP
```

## Service and recovery commands

```bash
sudo systemctl restart pi-usb-audio-gadget
systemctl --user restart pi-usb-audio-router
systemctl --user restart tractus-audio-dsp
systemctl --user restart tractus-ndi-audio
sudo /usr/local/sbin/pi-usb-audio-diagnose
```

Changing the number of functions, channel counts, sample size, or sample rate
requires USB re-enumeration. Live route enables and output gains do not.
