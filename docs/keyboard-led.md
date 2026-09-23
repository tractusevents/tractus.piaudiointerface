# Mini-keyboard LED feedback

The attached keyboard identifies as `514c:8851`. USB interface 0 exposes
report ID `03` with a 64-byte payload; the ordinary keyboard/dial uses interface
1. The transport uses HIDAPI's hidraw backend and retains the leading report
ID, sending exactly 65 bytes. It does not detach the keyboard driver.

Feedback is whole-keyboard, per layer. It reflects the four router microphone
`inputEnabled` values, not PC application mute state, signal level, output
mute state, or whether a latch is active. Any enabled microphone means red;
all four disabled means green. The independent worker checks current router
state every 100 ms, coalesces intervening changes, and sends only changes.
It retries disconnected/unavailable devices every second.

## Configuration

Within `keyboardControl`:

- `ledFeedbackEnabled`: opt-in, default false; requires hardware controls enabled.
- `ledLayer`: active layer 1–3, default 1.

The RGB command is:

```text
03 FE B0 LL 08 00 00 00 00 00 01 00 CC [zero padding to 65 bytes]
```

`LL` is 1–3. Steady colors are red `CC=11`, green `CC=41`.
The encoding matches the supplied reverse-engineered notes and the
[k884x implementation](https://github.com/kriomant/ch57x-keyboard-tool/blob/master/src/keyboard/k884x.rs):
`(color << 4) | mode`, where steady mode is 1, red is 1, and green is 4.
That related implementation alone does not establish compatibility with every
VID/PID in the allowlist. On 2026-09-08, both colors were sent to the attached
`514c:8851` keyboard on layer 1 without a commit, and the user visually
confirmed steady red and steady green. Other models remain unverified.

The optional commit is `03 FD FE FF`, zero-padded to 65 bytes, followed by a
200 ms wait. Automatic feedback never sends this commit: the attached keyboard
responds without it. It never rewrites key bindings or invokes factory or
firmware-update commands.

## Diagnostics

Disable automatic feedback before using the diagnostic command. List supported
configuration interfaces:

```sh
/opt/pi-usb-audio/PiUsbAudio.Control keyboard-hid
```

Using the listed path (do not assume it is always hidraw0):

```sh
/opt/pi-usb-audio/PiUsbAudio.Control keyboard-hid --path /dev/hidraw0 --backup /tmp/keyboard-backup.json
/opt/pi-usb-audio/PiUsbAudio.Control keyboard-hid --path /dev/hidraw0 --color red --layer 1
/opt/pi-usb-audio/PiUsbAudio.Control keyboard-hid --path /dev/hidraw0 --color green --layer 1
```

Backups contain raw reports and never overwrite an existing file. The reader
matches responses by key/layer, retries missing records, and reports incomplete
reads explicitly. Model queries may be delayed; an empty response does not
identify a model. A partial backup must not be used to rewrite a layer.
The attached unit returned 23 distinct records per layer during validation,
even after retries; its saved dumps are explicitly marked incomplete. This
does not prevent LED-only commands, which do not rewrite these records.

The color commands do not commit unless `--commit` is explicitly supplied.
Use `PI_USB_AUDIO_HID_DEBUG=1` for interface-selection diagnostics.

## Per-key investigation (2026-09-08)

Per-key channel feedback is **not verified or enabled**. Firmware-driven
keypress effects can address individual LEDs, but do not establish a host API
for independent, persistent red/green channel indicators.

The diagnostic project `tools/KeyboardLedProbe` reproduces the static RGB-slot
packet from the related
[K8850 implementation](https://github.com/skarard/ch57x-keyboard-tool/blob/feat/k8850-led-config/src/keyboard/k8850.rs).
On the attached `514c:8851`, the user reported no visible change for wire layer
0 or 1. Both writes returned success, which is not an acknowledgment that the
firmware understood or applied them. Subsequent known-good global color writes
failed; tracing the LED worker confirmed `write(..., 65) = -1 ETIMEDOUT`.
The cause is not established. Further experimental writes were stopped.

The probe re-enabled automatic feedback in the live configuration, but the
baseline color could not be restored while writes were timing out. The audio
service was not restarted, and its channel 1 latch remained set until the user
unplugged/replugged **only the mini-keyboard**. After reconnecting, automatic
global red writes succeeded again. A keyboard disconnect clears session latches:
with the current mute-while-held channel 1 mapping, that unmutes channel 1.
Arrange a safe audio state before reconnecting.

The probe also contains exact legacy LED packets from
[issue 180](https://github.com/kriomant/ch57x-keyboard-tool/issues/180), which
reportedly illuminated single keys on a different `1189:8840` model. After
recovery, the `legacy-green` case (`03 FE B0 01 08 00 05 01 00 23 00 34`,
zero-padded to 65 bytes) was sent without a commit. The user observed **all keys
white**, not a single green key. This did not demonstrate per-key control.
The `legacy-white` case has not been sent. Do not treat command names or packet
framing tests as hardware compatibility evidence.

After the legacy test, the baseline color write succeeded, the probe exited,
and the automatic worker reported connected/red with no error. No per-key
renderer was deployed. The input service remained running throughout.

### Static inspection of a related March 2025 vendor application

The 2025-03-18 Windows archive linked by
[SikaiCase's download page](https://sikaicase.com/de/blogs/support/setting-for-software)
contains `widget.o`. It was downloaded and disassembled without running the
application. This is a related vendor build, **not** the exact under-six-key
package: the user's direct download later resolved to the September 2024
archive inspected below.

Artifact SHA-256:

```text
archive: 701a30fec3400437bd8c7f9005e35b9b4f2d0a3e6a27088fef0b50b7c30ecf03
widget.o: 06c3c3a0b9fa1336fb765eeafae7fe105830703148a3a776922207556ddb8898
```

This build really has two LED paths. These are disassembly findings, not
hardware compatibility claims:

- `Widget::SetRgb_Led_Key(int)` at `.text+0x7f10` builds the global `FE B0`
  record and changes the mode/color nibble at record offset 11.
- `Set_Rgb_KeyColor`'s color-selection lambda writes separate RGB bytes into
  `KeyBoard_KeyLed[layer * 48 + (key - 1) * 3]` at `.text+0x6cd..0x762`.
- `Widget::HID_write()` tests `.bss+0x5d16 == 0x0a` at `.text+0x2408`.
  Only this branch reaches the separate RGB-array sender at `.text+0x28b4`.
- `Read_KeyBoard_KeyNum()` copies model-response byte 4 (including report ID)
  into that discriminator at `.text+0x68a15..0x68a3e`.
- The array sender builds `03 FE B0 LL MM`, followed immediately by 48 RGB
  bytes for 16 keys and zero padding to 65 bytes. It loops `LL=0,1,2` and gets
  `MM` from `RGB_LED_Md[LL]`. There is **no extra base-color triplet**: colors
  begin at full-report offset 5, not offset 8 as in the earlier related-tool
  probes. A flash commit later in the function depends on other modified
  records; the RGB-only branch does not set that commit flag.

No corrected-array packet has been sent. A fresh bounded model query returned
no response, so its hardware-type discriminator remains unverified. The probe
restored the normal color afterward. Do not assume that the presence of this
path in vendor software makes it compatible with `514c:8851`, and never use
the factory/model-setting command to force a match.

### Exact under-six-key package supplied by the user

The user's [direct download](http://www.videyt.com/en/DownLoad/115441.html?a=download)
works when requested with the vendor download-list page as HTTP referrer.
It redirects to
[the September 2024 RAR](http://r.videyt.com/20241108/New%20English%20software%20is%20set%20in%20the%20upgrade%20model-20240908.rar).
The archive directory is dated `20240908`; its `widget.o` is dated 2024-09-07.
Only static inspection was performed; no Windows application was executed and
no keyboard commands were sent during this comparison.

Artifact SHA-256:

```text
archive: ecdc3561c142cbd67a50b93bd61beb24c21ce4a9e0eb3c49584494d128237872
widget.o: 0d1f092f7fa798f6186d8d032be55f94582c05923bb1c999e2c0548d2bfbf911
```

Findings from this exact build:

- `Widget::SetRgb_Led_Key(int)` is at `.text+0x3ae0`. Its argument indexes the
  mode/color lookup table at `this+0x800`, not a physical key's RGB slot.
  Instructions at `0x3b2b..0x3b4f` construct the layer's global `FE B0` record;
  `0x3b88..0x3bb4` replace the mode or color nibble at record offset 11;
  `0x3c03` sets count 1; `0x3c1c..0x3c1f` marks only slot 0 modified.
- `Widget::HID_write()` at `.text+0x1440` serializes modified 50-byte records
  into 65-byte reports and sends the normal `FD FE FF` commit. Unlike the
  March 2025 build, it has no separate RGB-array transmission branch.
- There is no `Set_Rgb_KeyColor`, `KeyBoard_KeyLed`, `RgbLED_Change_Flag`, or
  `RGB_LED_Md` symbol. The other `hid_write` call sites in `widget.o` belong to
  configuration reads, model queries, firmware-update entry, and model-setting
  operations—not another LED sender. The last two were only inspected, never
  used.
- Per-key `_BK` UI names exist, but are not evidence of a USB per-key color
  API. The traced lighting setter still writes the single global color/effect
  value.

This establishes that the LED path in the exact supplied application is
global per layer. It does **not** prove that no undocumented command exists in
the keyboard firmware. Independent persistent red/green channel LEDs remain
unimplemented: none of the tested packets or this application's traced LED
path establishes a compatible mechanism. Further hardware experiments need
model-specific protocol evidence, such as a vendor-supported per-key command
or a capture of the same model performing independent color changes.

### Probe lifecycle

Probes are manual, LED-only, do not commit to flash, and leave the production
input service running. They temporarily disable the LED worker through the
configuration file without triggering an input-service reload, and re-enable
it on exit or after 180 seconds without a command. This cannot guarantee
hardware recovery from an unsupported packet. Avoid concurrent configuration
changes during a probe: the configuration store's lock is process-local.
After a timeout/error, recover and verify normal global feedback before
considering more tests. Never substitute guessed key-binding, factory, or
firmware-update packets.

## Deployment

Install `libhidapi-hidraw0` and
`config/90-pi-usb-audio-keyboard.rules` into `/etc/udev/rules.d/`, reload udev
rules and replug the keyboard (or trigger its hidraw devices). The router user
must be in the `input` group. The project's installer performs this setup.
