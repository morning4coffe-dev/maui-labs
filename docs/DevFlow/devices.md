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
  status and navigation bars. **The app reports this itself**: only it can observe where its window
  sits, so `DevFlowAgentService.GetWindowScreenOrigin` is overridden per platform (the `UIWindow`
  frame on iOS, the content view's on-screen location on Android) and surfaced as
  `windowScreenX`/`windowScreenY` on the agent status. When a platform cannot say, it reports
  `null` and consumers assume the window fills the screen rather than guessing an inset — a
  fabricated origin taps the wrong place while looking entirely plausible.
- **app logical size, separately from window point size** — normally equal, but carried apart so a
  platform whose logical units differ from display points does not skew the overlay.

## Arbitration

Device input goes through the **same mutation lease** as app input. The key is the paired agent's
id when an app is running, and `device:{deviceId}` when one is not — because a device tap can
happen before launch or after a crash, when there is no agent to key on.

Sharing the key is the point: a device-level tap and a `maui_tap` mutate the same screen, so two
independent locks would let two sessions drive one device each believing it had exclusive control.

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

## Layer fallthrough in the Inspector

The Inspector's Interact mode is **layer-aware**, and deliberately not a third mode the user has to
manage. Making the human choose "app tap" or "device tap" would be the moment the abstraction
failed, so the highest-fidelity layer that can service the click is chosen automatically:

| Where the click lands | What happens | Recorded as |
|---|---|---|
| A MAUI element, app running | Semantic tap through the agent — **today's exact path** | `tap` with a durable selector |
| Outside the app window | Device tap | not recorded; the user is told why |
| Anywhere, app crashed or not launched | Device tap | not recorded |

The last row is what turns the disconnected overlay from a dead end into something usable: the
agent dies with the app, but the device does not, so a click can still dismiss a crash dialog.

> **Status.** The crash-survival row works today. The *outside the app window* row works when the
> device reports its screen size: the Inspector then renders the device screen as the substrate
> with the app window inset at its real origin, so there are finally pixels outside the app to
> click. Where a device does not report a display, the Inspector falls back to rendering the app
> window alone and only the crash-survival row applies.

Everything about the second coordinate space lives beside `toAppCoords` in `devflow.js` rather than
being recomputed per handler — six handlers each doing their own arithmetic is exactly how an
overlay ends up correct in portrait and subtly wrong in landscape.

## The substrate

`#df-stage` was always a scaling wrapper around `#app-viewport`. With a device layer it becomes the
**device screen**, and the app window is inset within it at the origin the app reported:

```
#df-stage        ← the device screen (clicks here go to the device)
  └ #app-viewport ← the app window, inset at (originX, originY)
      └ elements  ← the MAUI overlay, unchanged
```

`HtmlRenderer` is untouched. Elements are still positioned relative to `#app-viewport`, which still
means the app window, so `Fit`, `Bounds`, hover, selection, and hit candidates all keep working.

## Hosts

There is nothing per-host to wire. The browser, the VS Code webview, and the Copilot Canvas all
embed the **same** broker-hosted inspector (`/inspector/{agentId}/`), so the substrate, the
fallthrough, and the Device tab reach all three at once. That is the shared-inspector architecture
paying for itself: a device picker or a capability flag implemented three times would be three
chances to disagree.

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
maui devflow devices setup             # what the device layer needs on this machine
maui devflow devices list              # devices, each paired with the app inside it
maui devflow devices list --json
maui devflow devices boot <device-id>
maui devflow devices shutdown <device-id>
```

`setup` diagnoses rather than downloads — silently fetching and executing a binary is not something
a diagnostic command should do — and gives each state a *different* instruction, because
"not installed" and "installed but unusable" look identical to a user and need opposite responses.

`list` distinguishes its two empty states, for the same reason:

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

## Recording out-of-app interactions

Flows record semantic steps against MAUI elements, which is what makes them durable. A first-run
permission prompt, a share sheet, or the soft keyboard is not in the visual tree at all, so a flow
that touched one could not be authored — the recording dead-ended.

A **device step** closes that gap without weakening the rest. It rides as an additive
`deviceSteps` extension field and carries both a coordinate and a description of the native view
under it, so replay can match by text or id and fall back to coordinates only when it must. A step
with neither is reported as `IsFragile`, reusing the vocabulary the flow recorder already applies to
a selector without an `AutomationId`: it blocks nothing, it tells a reviewer which steps will break
first when the UI moves.

Prefer a **precondition** over a device step wherever one exists. A flow that grants the permission
up front never sees the prompt, and a test that sets up its own environment is deterministic in a
way that one tapping through a dialog is not.

## Live video

Where the device reports `liveStream`, the Inspector replaces the polled screenshot with decoded
H.264 painted onto the stage backdrop. Three properties matter more than the picture:

- **It never touches the agent.** Frames come from the device host through the broker's proxy, so
  video costs the app nothing — and keeps arriving after the app has died, which is what makes the
  crash-survival view live rather than frozen.
- **It never drives the tree.** Frame cadence and tree cadence are separate. 50 frames a second
  must not mean 50 visual-tree pulls a second, so the video path schedules no refreshes at all.
- **It always degrades.** No WebCodecs, no `liveStream` capability, a refused upstream, a decoder
  error — every one falls back to the screenshot path the Inspector was already using.

### Why it is proxied

The browser never connects to the device host. That host authenticates control clients with a
bearer token, and a browser cannot attach headers to a WebSocket — nor should that token ever be
placed in a page, a URL, or a DOM a framed document could read. The broker holds it server-side and
the page presents only the embed token, which also keeps the broker the single front door.

**A WebSocket is not subject to the same-origin policy**, so the proxy enforces two gates before
upgrading, and a page that fails either gets a 403:

1. **Loopback origin** — proves the caller is on this machine.
2. **The embed token** — proves it is an Inspector session, not merely some other local page. A dev
   server or a docs site on localhost passes the first gate and must still be refused.

Without both, any page a user visited could open a live feed of their device screen and the broker
would helpfully authenticate it on their behalf.

The proxy is **one-directional** on purpose: the video channel carries frames out and nothing in.
Input travels the HTTP control path where the mutation lease arbitrates it. A socket that also
accepted commands would be a second, unarbitrated way to drive the device.

### Cadence

While video is live the screenshot fetch is skipped entirely, because it is a stale duplicate of
pixels the stream is already painting. Video therefore makes the Inspector *cheaper*: the frame
half of every poll disappears and the tree half is unchanged.

### Stream framing

H.264 arrives as an Annex-B byte stream that aligns to neither WebSocket messages nor frames, so
`inspector-video.js` reassembles it:

- NAL units are split on 3- and 4-byte start codes, and the trailing bytes are returned as a **raw
  remainder including its start code** so the caller can prepend it to the next chunk. Returning
  the payload alone would make the reassembled buffer begin mid-unit and silently drop exactly the
  frames that straddle a message boundary.
- Parameter sets are emitted with the picture that follows them, not alone — feeding an SPS as if
  it were a frame yields a decoder that configures and never outputs anything.
- A picture is closed by what **follows** it, not by the slice itself. A single picture may be
  coded as several slice NALs — common on hardware encoders under a bandwidth cap — and submitting
  each as its own picture feeds the decoder partial frames, which it rejects and never recovers
  from. A new picture is detected by the slice header's `first_mb_in_slice` being zero. The cost is
  one frame of latency, which is the right trade for a stream that works on every encoder.
- Frames before the first keyframe are discarded: a decoder started mid-stream has no reference
  frames, so they would decode to garbage. This is why a stream takes a moment to appear.

## Implementation

| Type | Responsibility |
|---|---|
| `IDeviceSurface` | The device layer's contract. Never throws for an unsupported operation |
| `NullDeviceSurface` | The honest no-op used when there is no device layer |
| `MobileCanvasDeviceSurface` | Adapter over a locally installed device host |
| `MobileCanvasHost` | File-based discovery of that host |
| `DeviceCoordinateSpace` | The full coordinate chain, with round-trip guarantees |
| `inspector-video.js` | Annex-B reassembly and WebCodecs decoding for the live stream |
| `BrokerServer.Video.cs` | Authenticated one-directional video proxy |
| `DeviceIdentity` / `DeviceIdentityMatcher` | Pairing an app agent to its device |
| `DeviceIdentityProvider` | Agent-side self-reporting (Agent.Core, with platform overrides) |

## Testing

```bash
dotnet test src/DevFlow/Microsoft.Maui.DevFlow.Tests/ --filter "FullyQualifiedName~Device"
```

The coordinate tests pin the round trip across every orientation, density and stream scale, and
additionally pin specific corners. A round-trip test alone is not sufficient: an inverse-consistent
but *mirrored* transform round-trips perfectly and is still wrong.
