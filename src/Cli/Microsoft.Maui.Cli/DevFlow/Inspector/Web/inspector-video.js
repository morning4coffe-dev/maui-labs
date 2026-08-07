// Live device video for the DevFlow Inspector.
//
// The Inspector's frame source has always been a screenshot: fetched, cached ~200ms, and re-polled.
// That is a slideshow of a thing the user is trying to interact with. Where the device layer can
// stream, this replaces it with decoded H.264 painted straight onto the stage backdrop.
//
// Three properties this module has to preserve, because the Inspector already depends on them:
//
//   1. It NEVER touches the agent. Frames come from the device host through the broker's proxy, so
//      video costs the app nothing and — critically — keeps arriving after the app has died.
//   2. It NEVER drives the tree. Frame cadence and tree cadence are separate: 50 frames a second
//      must not mean 50 visual-tree pulls a second. This module emits no refreshes at all.
//   3. It always degrades. No WebCodecs, no stream capability, an upstream that refuses — every
//      one of them falls back to the existing screenshot path rather than showing a dead panel.

const NAL_START_LONG = 4;
const NAL_START_SHORT = 3;

/** H.264 NAL unit types we care about for framing decisions. */
const NAL_IDR = 5;   // keyframe slice
const NAL_SPS = 7;
const NAL_PPS = 8;

/**
 * Splits an Annex-B byte stream into NAL units.
 *
 * The stream arrives as WebSocket messages that do not necessarily align to frame boundaries, so
 * the caller carries a remainder between calls. Returning the tail rather than assuming alignment
 * is what stops a decoder from being fed half a slice, which it reports as a corrupt stream rather
 * than as a framing bug.
 */
export function splitNalUnits(bytes) {
  const units = [];
  let payloadStart = -1;
  let startCodeStart = -1;
  let i = 0;

  while (i + 2 < bytes.length) {
    const isLong = i + 3 < bytes.length &&
      bytes[i] === 0 && bytes[i + 1] === 0 && bytes[i + 2] === 0 && bytes[i + 3] === 1;
    const isShort = bytes[i] === 0 && bytes[i + 1] === 0 && bytes[i + 2] === 1;

    if (isLong || isShort) {
      if (payloadStart >= 0) units.push(bytes.subarray(payloadStart, i));
      startCodeStart = i;
      payloadStart = i + (isLong ? NAL_START_LONG : NAL_START_SHORT);
      i = payloadStart;
      continue;
    }
    i++;
  }

  // The remainder is a RAW tail including its start code, so a caller can prepend it to the next
  // chunk and split again. Returning the payload without its start code would make the reassembled
  // buffer begin mid-unit, and the next split would silently discard it — losing exactly the
  // frames that straddle a message boundary.
  const remainder = startCodeStart >= 0 ? bytes.subarray(startCodeStart) : bytes;
  return { units, remainder };
}

/** The NAL type of a unit body (the start code is already stripped). */
export function nalType(unit) {
  return unit.length > 0 ? (unit[0] & 0x1f) : 0;
}

/**
 * Groups NAL units into access units — one decodable picture each.
 *
 * A decoder must be handed whole pictures. Parameter sets (SPS/PPS) belong with the keyframe that
 * follows them, so they are accumulated rather than emitted alone; feeding an SPS as if it were a
 * frame is a common way to produce a decoder that configures and then never outputs anything.
 */
export function groupAccessUnits(units) {
  const frames = [];
  let pending = [];
  let pendingIsKey = false;

  for (const unit of units) {
    const type = nalType(unit);

    if (type === NAL_SPS || type === NAL_PPS) {
      pending.push(unit);
      continue;
    }

    if (type === NAL_IDR || type === 1) {
      pending.push(unit);
      if (type === NAL_IDR) pendingIsKey = true;
      frames.push({ nals: pending, key: pendingIsKey });
      pending = [];
      pendingIsKey = false;
      continue;
    }

    // Anything else (SEI, AUD, filler) rides along with the next picture.
    pending.push(unit);
  }

  return { frames, pending };
}

/** Re-emits NAL units as a single Annex-B buffer with 4-byte start codes. */
export function toAnnexB(nals) {
  let size = 0;
  for (const n of nals) size += NAL_START_LONG + n.length;

  const out = new Uint8Array(size);
  let offset = 0;
  for (const n of nals) {
    out[offset] = 0; out[offset + 1] = 0; out[offset + 2] = 0; out[offset + 3] = 1;
    out.set(n, offset + NAL_START_LONG);
    offset += NAL_START_LONG + n.length;
  }
  return out;
}

/** Whether this browser can decode the stream at all. */
export function isVideoSupported(globalScope) {
  const scope = globalScope || (typeof globalThis !== 'undefined' ? globalThis : {});
  return typeof scope.VideoDecoder === 'function' && typeof scope.EncodedVideoChunk === 'function';
}

/**
 * A live device video surface.
 *
 * Owns the socket, the decoder and the canvas, and reports upward only two things: that a frame
 * arrived, or that video is not going to work. The Inspector reacts to the second by staying on
 * screenshots — it never has to know why.
 */
export class DeviceVideoSurface {
  constructor(options) {
    const opts = options || {};
    this.url = opts.url;
    this.canvas = opts.canvas;
    this.scope = opts.scope || (typeof globalThis !== 'undefined' ? globalThis : {});
    this.onFrame = typeof opts.onFrame === 'function' ? opts.onFrame : () => {};
    this.onUnavailable = typeof opts.onUnavailable === 'function' ? opts.onUnavailable : () => {};

    this._socket = null;
    this._decoder = null;
    this._remainder = new Uint8Array(0);
    this._pending = [];
    this._started = false;
    this._sawKeyFrame = false;
    this._closed = false;
    this._frameCount = 0;
  }

  get frameCount() { return this._frameCount; }

  start() {
    if (this._started || this._closed) return false;
    if (!isVideoSupported(this.scope) || !this.url || !this.canvas) {
      this.onUnavailable('This browser cannot decode the device video stream.');
      return false;
    }

    this._started = true;
    try {
      this._openSocket();
      return true;
    } catch (e) {
      this.onUnavailable('The device video stream could not be opened.');
      return false;
    }
  }

  _openSocket() {
    const socket = new this.scope.WebSocket(this.url);
    socket.binaryType = 'arraybuffer';
    this._socket = socket;

    socket.onmessage = (event) => {
      if (!(event.data instanceof ArrayBuffer)) return;
      this._consume(new Uint8Array(event.data));
    };
    // A refused or dropped stream is not an error state for the Inspector — it is the signal to
    // stay on screenshots, which is exactly what it was doing a moment ago.
    socket.onerror = () => this.onUnavailable('The device video stream is unavailable.');
    socket.onclose = () => {
      if (!this._closed) this.onUnavailable('The device video stream ended.');
    };
  }

  _consume(chunk) {
    const combined = this._concat(this._remainder, chunk);
    const { units, remainder } = splitNalUnits(combined);
    this._remainder = remainder;

    const all = this._pending.concat(units);
    const { frames, pending } = groupAccessUnits(all);
    this._pending = pending;

    for (const frame of frames) this._decode(frame);
  }

  _concat(a, b) {
    if (a.length === 0) return b;
    const out = new Uint8Array(a.length + b.length);
    out.set(a, 0);
    out.set(b, a.length);
    return out;
  }

  _decode(frame) {
    // A decoder started mid-stream has no reference frames, so anything before the first keyframe
    // decodes to garbage or an error. Waiting is both correct and briefly visible as the stream
    // taking a moment to appear.
    if (!this._sawKeyFrame && !frame.key) return;
    this._sawKeyFrame = true;

    try {
      if (!this._decoder) this._configureDecoder();

      const chunk = new this.scope.EncodedVideoChunk({
        type: frame.key ? 'key' : 'delta',
        timestamp: this._frameCount * 1000,
        data: toAnnexB(frame.nals),
      });
      this._decoder.decode(chunk);
    } catch (e) {
      this._fail('The device video stream could not be decoded.');
    }
  }

  _configureDecoder() {
    const canvas = this.canvas;
    const ctx = canvas.getContext('2d');

    this._decoder = new this.scope.VideoDecoder({
      output: (image) => {
        try {
          // Size the canvas to the frame, not to the layout: the stage already scales it, and
          // resizing here to the CSS box would resample every frame for nothing.
          if (canvas.width !== image.displayWidth || canvas.height !== image.displayHeight) {
            canvas.width = image.displayWidth;
            canvas.height = image.displayHeight;
          }
          ctx.drawImage(image, 0, 0);
          this._frameCount++;
          this.onFrame();
        } finally {
          image.close();
        }
      },
      error: () => this._fail('The device video decoder failed.'),
    });

    // Baseline profile, level 3.0 — what both platform encoders emit. No description is supplied
    // because the stream is Annex-B rather than length-prefixed.
    this._decoder.configure({ codec: 'avc1.42E01E', optimizeForLatency: true });
  }

  _fail(message) {
    this.onUnavailable(message);
    this.stop();
  }

  stop() {
    this._closed = true;
    try { if (this._socket) this._socket.close(); } catch (e) { /* already closing */ }
    try {
      if (this._decoder && this._decoder.state !== 'closed') this._decoder.close();
    } catch (e) { /* already closed */ }
    this._socket = null;
    this._decoder = null;
  }
}
