# The device layer

DevFlow observes a running app from **inside** it: the in-app agent walks the MAUI visual tree,
reads properties, and drives semantic input. That vantage point is precise but bounded — everything
it knows arrives through a process living inside the app, so it goes blind whenever the app is not
both running and frontmost:

- the app **crashed**, and the agent died with it
- a **system dialog** (permissions, share sheet, photo picker) is on top and is not in the MAUI tree
- the **soft keyboard** is covering a control, invisibly to layout diagnostics
- the app **has not launched yet**, so there is nothing to attach to

The device layer is a second, independent vantage point that survives all four. It observes and
controls the device *around* the app: lifecycle, live frames, physical input, and environment.

> The device layer is **optional**. On a machine with no device host, and for every desktop MAUI
> app, it reports every capability as unavailable and DevFlow behaves exactly as it did before.
> That is a guarantee, not a best effort — see [Degradation](#degradation).

## Contracts

| Contract | Document |
|---|---|
| Device surface | [`schemas/device-surface-v1.json`](spec/schemas/device-surface-v1.json) |

> **Not to be confused with [`schemas/device.json`](spec/schemas/device.json).** That contract is
> the device as the *app* sees it — `DeviceInfo`, battery, connectivity, sensors, permissions — read
> through the in-app agent and surfaced in the Inspector's Device data tab. This contract is the
> device as the *host* sees it: lifecycle, display geometry, and control. They describe the same
> physical device from the two vantage points, and are deliberately separate documents because
> they have different owners, different availability, and different trust.

## Pairing an app to its device

Without pairing, a user picks an app and a device separately and nothing connects them — the device
layer would be a second tool rather than part of DevFlow. Pairing is what makes one selection mean
both.

A running app reports what it can observe about its own host in the broker registration's
`deviceId` field, in a compact wire form:

```
platform=ios;udid=1E2D3C4B-5A69
platform=android;serial=emulator-5554;avd=Pixel_8_API_35
```

| Platform | Signal | Strength |
|---|---|---|
| iOS / Mac Catalyst simulator | `SIMULATOR_UDID`, injected into the process environment by the simulator runtime | **Exact** — it is the UDID `simctl` uses |
| Android emulator | `ro.boot.qemu.avd_name` | **Primary** — the name `avdmanager` knows |
| Android emulator | `ro.serialno` | **Secondary** — *not* the adb serial; adb addresses an emulator as `emulator-<consolePort>`, derived from the console port, so these agree only on some configurations |
| Windows / macOS desktop | none | No virtual device exists; nothing pairs |

The broker joins that identity against the devices the device layer discovered. Two rules keep a
wrong pairing from being worse than no pairing:

1. **Exact beats weak.** A serial or UDID match always wins over a name match.
2. **Ambiguity refuses.** If two devices match equally well, the result is *no pairing*. Silently
   choosing the wrong device would apply every subsequent coordinate to the wrong screen — a
   failure that looks plausible rather than raising anything.

Resolution is best-effort throughout: a platform API that throws, or a host we do not recognise,
degrades to no pairing and never prevents the app from registering.

## Coordinate spaces

Drawing the MAUI tree overlay on a device frame crosses four coordinate spaces. Every boundary is a
place to be silently and plausibly wrong, so the whole chain lives in one type,
`DeviceCoordinateSpace`, rather than being recomputed by each caller.

```
frame pixels          what the client decoded from the video stream or screenshot
  ÷ streamScale       the client asks the encoder to scale down to its panel size
device pixels         DisplayGeometry.pixelWidth × pixelHeight
  ÷ displayScale      DisplayGeometry.scale — reported, never assumed
device points         DisplayGeometry.pointWidth × pointHeight
  − app window origin the app window's rectangle within the screen
app logical units     the space MAUI element bounds live in
```

The transform deliberately **stops at app logical units**. Subtracting the visual tree's root
offset is the existing renderer's job and is not duplicated.

### Why each factor is carried rather than assumed

- **`streamScale`** — a client rendering into a narrow side panel asks the encoder not to spend
  bandwidth on a full 3× framebuffer. Frame pixels are therefore not device pixels.
- **`displayScale`** — reported by the device rather than derived from a device-model lookup, so a
  backend can correct it.
- **app window origin** — the app does not own the whole screen. On a phone it is inset by the
  status and navigation bars. This is the one value the device layer cannot infer on its own.
- **app logical size, separately from window point size** — normally equal, but carried apart so a
  platform whose logical units differ from display points does not skew the overlay.

### Rotation

`frameQuarterTurns` records the clockwise quarter-turns applied to the device screen to produce the
frame. **Zero is the default and the common case**: both simulator screenshots and the emulator
video stream already arrive in the device's current orientation, so the reported geometry is
already correct.

It is modelled explicitly anyway, because a backend that delivers frames in the display's *natural*
orientation would otherwise produce an overlay that is correct in portrait and transposed in
landscape — which ships, because nobody tests landscape first.

Note that `DisplayGeometry.orientation` and `frameQuarterTurns` are **different things**: the first
describes what the display is presenting, the second describes what was done to the frame. They are
kept apart so a backend change cannot silently redefine either.

### Falling outside the app window

`DeviceToApp` returns `null` when a point lies outside the app window. That is not an error — it is
the signal that an interaction must fall through to the device layer instead of being mis-sent to
the app agent. A tap on a permission dialog, the soft keyboard, or the navigation bar is a device
tap; a tap on a MAUI element is a semantic tap that records a durable selector.

## Degradation

The device layer is optional at every level. Absence is an ordinary state — but it is carefully
distinguished from *failure*, because collapsing the two is actively harmful: an incompatible or
unauthenticated host reporting as "absent" looks exactly like a machine with no device layer, so a
real and fixable break silently presents as a missing feature that nobody investigates.

| `DeviceHostAvailability` | Meaning | Typical cause |
|---|---|---|
| `Absent` | No host running | Not installed, or the daemon has idled out |
| `NotResponding` | State file exists, host silent | Stale file after a crash |
| `Unauthorized` | Host rejected our control token | Host restarted and reissued one |
| `Incompatible` | Protocol major version we do not support | Host updated ahead of DevFlow |
| `Available` | Usable | — |

| Situation | Behaviour |
|---|---|
| Device exists but lacks a capability | Refused with a reason naming the capability |
| Desktop MAUI app | No device identity, no pairing, no change in behaviour |

Discovery is **file-based and never launches anything as a side effect of looking**. A DevFlow
session that only wants to know whether device control is possible must not start a background
daemon to find out.

> Note that the host's state file exists only while the host is **running** — it is removed on
> shutdown. Its absence therefore means "no host running", which is not the same as "not
> installed". Distinguishing those is the job of the managed-install path, not of discovery.

## Talking to the host

The wire boundary is pinned by contract tests against a stub speaking the real protocol, because
the adapter's failure mode is silence — a wrong field name or a missing credential fails in a way
indistinguishable from "not installed". Degradation tests cannot catch that; only a successful
authenticated round trip proves the binding.

- State file: `~/.mobile-canvas/host.json`, fields `schemaVersion`, `port`, `processId`,
  `controlToken`, `version`.
- Health: `GET /api/v1/status`.
- Every request carries `Authorization: Bearer {controlToken}`. This is a trusted local control
  credential, distinct from the single-use bootstrap secret the host issues to canvas panels.
- The protocol **major** version is checked before any request is made. An unsupported major is
  refused up front rather than optimistically driven, because guessing risks silently wrong device
  control instead of a clean error. A host that reports no version predates schema stamping and is
  assumed compatible.

## Using it

The broker is the single front door: the CLI, the Inspector, and the MCP server all read the same
device list and share one idea of which app is on which device.

### CLI

```bash
maui devflow devices list              # devices, each paired with the app inside it
maui devflow devices list --json
maui devflow devices boot <device-id>
maui devflow devices shutdown <device-id>
```

`list` distinguishes its two empty states, because they need different actions from the reader:

```
No device host is installed or running.            # install/start the host
No emulators or simulators were found. Create one. # host is fine, nothing to drive
```

### MCP

| Tool | Purpose |
|---|---|
| `maui_device_list` | Devices, each paired with the app agent inside it |
| `maui_device_boot` | Boot and wait until driveable |
| `maui_device_shutdown` | Power off without erasing |
| `maui_device_tap` | Tap a physical point |

This set is deliberately small. Every tool added competes for an agent's attention against the
~79 existing `maui_*` tools, and a bloated surface measurably degrades tool selection, so only
operations the in-app agent structurally cannot perform are exposed.

`maui_device_tap` exists for UI the visual tree cannot reach — permission dialogs, the soft
keyboard, OS navigation, or anything before launch or after a crash. Its description steers callers
back to `maui_tap` for anything inside the app, because a selector survives a layout change and a
coordinate does not.

### Broker HTTP

| Route | Purpose |
|---|---|
| `GET /api/devices` | Devices plus host availability |
| `POST /api/devices/{id}/boot` | Boot |
| `POST /api/devices/{id}/shutdown` | Shut down |
| `POST /api/devices/{id}/tap?x=&y=` | Tap at a device point |

The device list carries the host's availability alongside the devices so a caller can tell "no
devices" from "no device layer" without a second request.

## Implementation

| Type | Responsibility |
|---|---|
| `IDeviceSurface` | The device layer's contract. Never throws for an unsupported operation |
| `NullDeviceSurface` | The honest no-op used when there is no device layer |
| `MobileCanvasDeviceSurface` | Adapter over a locally installed device host |
| `MobileCanvasHost` | File-based discovery of that host |
| `DeviceCoordinateSpace` | The full coordinate chain, with round-trip guarantees |
| `DeviceIdentity` / `DeviceIdentityMatcher` | Pairing an app agent to its device |
| `DeviceIdentityProvider` | Agent-side self-reporting (Agent.Core, with platform overrides) |

## Testing

```bash
dotnet test src/DevFlow/Microsoft.Maui.DevFlow.Tests/ --filter "FullyQualifiedName~Device"
```

The coordinate tests pin the round trip across every orientation, density and stream scale, and
additionally pin specific corners. A round-trip test alone is not sufficient: an inverse-consistent
but *mirrored* transform round-trips perfectly and is still wrong.
