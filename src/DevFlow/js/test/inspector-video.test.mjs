import test from "node:test";
import assert from "node:assert/strict";
import { dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const sourceRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const videoModule = pathToFileURL(
  resolve(sourceRoot, "Cli/Microsoft.Maui.Cli/DevFlow/Inspector/Web/inspector-video.js"),
).href;

const {
  splitNalUnits,
  groupAccessUnits,
  nalType,
  toAnnexB,
  isVideoSupported,
  DeviceVideoSurface,
} = await import(videoModule);

// H.264 arrives as a byte stream that does not align to WebSocket messages or to frames. Every bug
// in this layer looks like "the video is corrupt" rather than like a framing mistake, so the
// splitting and grouping are pinned directly.

const START_LONG = [0, 0, 0, 1];
const START_SHORT = [0, 0, 1];

function nal(type, ...payload) {
  return [type & 0x1f, ...payload];
}

function bytes(...parts) {
  return new Uint8Array(parts.flat());
}

test("splits units on 4-byte start codes", () => {
  const input = bytes(START_LONG, nal(7, 1, 2), START_LONG, nal(8, 3), START_LONG, nal(5, 9, 9));
  const { units } = splitNalUnits(input);

  // The final unit is the remainder: without a following start code we cannot know it is complete.
  assert.equal(units.length, 2);
  assert.equal(nalType(units[0]), 7);
  assert.equal(nalType(units[1]), 8);
});

test("splits units on 3-byte start codes", () => {
  const input = bytes(START_SHORT, nal(7, 1), START_SHORT, nal(5, 2), START_SHORT, nal(1, 3));
  const { units } = splitNalUnits(input);

  assert.equal(units.length, 2);
  assert.equal(nalType(units[0]), 7);
  assert.equal(nalType(units[1]), 5);
});

test("returns the trailing bytes as a raw remainder rather than emitting a partial unit", () => {
  // The decisive property: a message boundary mid-NAL must not produce half a slice. The
  // remainder keeps its start code so it can simply be prepended to the next chunk.
  const input = bytes(START_LONG, nal(5, 1, 2, 3));
  const { units, remainder } = splitNalUnits(input);

  assert.equal(units.length, 0);
  assert.deepEqual([...remainder], [0, 0, 0, 1, 5, 1, 2, 3]);
});

test("a unit split across two messages reassembles intact", () => {
  const whole = bytes(START_LONG, nal(5, 1, 2, 3, 4), START_LONG, nal(1, 9));

  const first = whole.subarray(0, 6);
  const second = whole.subarray(6);

  const a = splitNalUnits(first);
  const combined = new Uint8Array(a.remainder.length + second.length);
  combined.set(a.remainder, 0);
  combined.set(second, a.remainder.length);
  const b = splitNalUnits(combined);

  const recovered = a.units.concat(b.units);
  assert.equal(recovered.length, 1);
  assert.equal(nalType(recovered[0]), 5);
  assert.deepEqual([...recovered[0]], [5, 1, 2, 3, 4]);
});

test("groups parameter sets with the keyframe that follows them", () => {
  // Handing an SPS to the decoder as if it were a picture is a classic way to get a decoder that
  // configures and then never outputs anything.
  const units = [
    Uint8Array.from(nal(7, 1)),
    Uint8Array.from(nal(8, 2)),
    Uint8Array.from(nal(5, 3)),
  ];

  const { frames } = groupAccessUnits(units);

  assert.equal(frames.length, 1);
  assert.equal(frames[0].key, true);
  assert.equal(frames[0].nals.length, 3);
});

test("marks non-IDR slices as delta frames", () => {
  const { frames } = groupAccessUnits([Uint8Array.from(nal(1, 1))]);

  assert.equal(frames.length, 1);
  assert.equal(frames[0].key, false);
});

test("holds incomplete groups back as pending", () => {
  // Parameter sets with no following slice are not a picture yet.
  const { frames, pending } = groupAccessUnits([Uint8Array.from(nal(7, 1)), Uint8Array.from(nal(8, 2))]);

  assert.equal(frames.length, 0);
  assert.equal(pending.length, 2);
});

test("re-emits Annex-B with 4-byte start codes", () => {
  const out = toAnnexB([Uint8Array.from(nal(5, 7)), Uint8Array.from(nal(1, 8))]);

  assert.deepEqual([...out], [0, 0, 0, 1, 5, 7, 0, 0, 0, 1, 1, 8]);
});

test("reports unsupported when WebCodecs is missing", () => {
  assert.equal(isVideoSupported({}), false);
  assert.equal(isVideoSupported({ VideoDecoder: function () {}, EncodedVideoChunk: function () {} }), true);
});

test("degrades instead of starting when the browser cannot decode", () => {
  // The Inspector must fall back to screenshots, not show a dead panel.
  let reason = null;
  const surface = new DeviceVideoSurface({
    url: "ws://localhost/ws/video",
    canvas: {},
    scope: {},
    onUnavailable: (m) => { reason = m; },
  });

  assert.equal(surface.start(), false);
  assert.match(reason, /cannot decode/i);
});

test("degrades when there is no stream url", () => {
  let reason = null;
  const surface = new DeviceVideoSurface({
    canvas: {},
    scope: { VideoDecoder: function () {}, EncodedVideoChunk: function () {} },
    onUnavailable: (m) => { reason = m; },
  });

  assert.equal(surface.start(), false);
  assert.ok(reason);
});

test("discards frames until the first keyframe arrives", () => {
  // A decoder started mid-stream has no reference frames, so delta frames before the first
  // keyframe decode to garbage. This is why the stream takes a moment to appear.
  const decoded = [];
  const scope = {
    VideoDecoder: class {
      constructor(init) { this.init = init; this.state = "unconfigured"; }
      configure() { this.state = "configured"; }
      decode(chunk) { decoded.push(chunk.type); }
      close() { this.state = "closed"; }
    },
    EncodedVideoChunk: class {
      constructor(init) { Object.assign(this, init); }
    },
    WebSocket: class { constructor() { this.readyState = 0; } close() {} },
  };

  const surface = new DeviceVideoSurface({
    url: "ws://localhost/ws/video",
    canvas: { getContext: () => ({ drawImage() {} }) },
    scope,
  });
  surface.start();

  surface._consume(bytes(START_LONG, nal(1, 1), START_LONG, nal(1, 2), START_LONG, nal(5, 3), START_LONG, nal(1, 4)));

  // The two leading delta frames are dropped; decoding starts at the keyframe.
  assert.deepEqual(decoded, ["key"]);
});
