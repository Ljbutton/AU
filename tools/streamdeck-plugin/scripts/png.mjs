// Stream Deck wants the plugin's own icon as a PNG (every other picture can be SVG). Rather than keep
// a binary file in the repository, the build draws Red Alert's logo (docs/red-alert-logo.svg) here
// with a few shapes, smooths the edges by sampling each pixel 4 x 4 times, and writes the PNG with
// Node's own zlib.

import { deflateSync } from "node:zlib";

const CRC_TABLE = (() => {
	const table = new Uint32Array(256);
	for (let n = 0; n < 256; n++) {
		let c = n;
		for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
		table[n] = c >>> 0;
	}
	return table;
})();

export function crc32(bytes) {
	let c = 0xffffffff;
	for (const b of bytes) c = CRC_TABLE[(c ^ b) & 0xff] ^ (c >>> 8);
	return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
	const out = Buffer.alloc(12 + data.length);
	out.writeUInt32BE(data.length, 0);
	out.write(type, 4, "ascii");
	data.copy(out, 8);
	out.writeUInt32BE(crc32(out.subarray(4, 8 + data.length)), 8 + data.length);
	return out;
}

/** An RGBA image (width × height × 4 bytes, row by row) as PNG file bytes. */
export function encodePng(width, height, rgba) {
	const header = Buffer.alloc(13);
	header.writeUInt32BE(width, 0);
	header.writeUInt32BE(height, 4);
	header[8] = 8; // bits per channel
	header[9] = 6; // RGBA
	const rows = Buffer.alloc((width * 4 + 1) * height);
	for (let y = 0; y < height; y++) {
		rows[y * (width * 4 + 1)] = 0; // no filter
		Buffer.from(rgba.buffer, rgba.byteOffset + y * width * 4, width * 4).copy(rows, y * (width * 4 + 1) + 1);
	}
	return Buffer.concat([
		Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
		chunk("IHDR", header),
		chunk("IDAT", deflateSync(rows, { level: 9 })),
		chunk("IEND", Buffer.alloc(0)),
	]);
}

const hex = (h) => [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)];
const mix = (a, b, t) => a.map((v, i) => v + (b[i] - v) * t);

function segmentDistance(px, py, ax, ay, bx, by) {
	const dx = bx - ax;
	const dy = by - ay;
	const t = Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)));
	return Math.hypot(px - (ax + t * dx), py - (ay + t * dy));
}

function inRoundRect(x, y, left, top, width, height, r) {
	if (x < left || y < top || x > left + width || y > top + height) return false;
	const cx = Math.max(left + r, Math.min(left + width - r, x));
	const cy = Math.max(top + r, Math.min(top + height - r, y));
	return Math.hypot(x - cx, y - cy) <= r;
}

const RAYS = [
	[32, 4.5, 32, 12],
	[10.5, 13.5, 15.8, 18.8],
	[53.5, 13.5, 48.2, 18.8],
	[3.5, 33, 11, 33],
	[60.5, 33, 53, 33],
];
const DOME = [hex("#ff5a4f"), hex("#e10600"), hex("#9e0400")];

/** The logo's colour at a point of its 64 × 64 drawing, over what is beneath (rgb, alpha 0..1). */
function logoAt(u, v, under) {
	let [r, g, b] = under;
	const over = (rgb, alpha) => {
		r += (rgb[0] - r) * alpha;
		g += (rgb[1] - g) * alpha;
		b += (rgb[2] - b) * alpha;
	};
	// The red glow behind the light.
	if (Math.hypot(u - 32, v - 38) <= 30) {
		const d = Math.hypot(u - 32, v - 45.2) / 33;
		if (d < 1) over(hex("#ff2a1f"), 0.55 * (1 - d));
	}
	// The rays.
	if (RAYS.some(([ax, ay, bx, by]) => segmentDistance(u, v, ax, ay, bx, by) <= 2.1)) over(hex("#ff3b30"), 1);
	// The dome, red at the top to dark red at the base.
	if ((u >= 16.5 && u <= 47.5 && v >= 36 && v <= 47) || (v <= 36 && Math.hypot(u - 32, v - 36) <= 15.5)) {
		const t = Math.max(0, Math.min(1, (v - 20.5) / 26.5));
		over(t < 0.55 ? mix(DOME[0], DOME[1], t / 0.55) : mix(DOME[1], DOME[2], (t - 0.55) / 0.45), 1);
		// Its shine: part of a ring, upper left.
		const ring = Math.abs(Math.hypot(u - 32, v - 36.5) - 9.8) <= 1.6;
		const angle = (Math.atan2(v - 36.5, u - 32) * 180) / Math.PI;
		const onArc = ring && (angle >= 180 - 1e-9 || angle <= -105);
		const cap = Math.hypot(u - 22.2, v - 36.5) <= 1.6 || Math.hypot(u - 29.5, v - 27.1) <= 1.6;
		if (onArc || cap) over([255, 255, 255], 0.85);
	}
	// The base.
	if (inRoundRect(u, v, 11, 47, 42, 9, 2)) over(hex("#f2f2f4"), 1);
	if (inRoundRect(u, v, 11, 53, 42, 3, 1)) over(hex("#b9b9c2"), 1);
	return [r, g, b];
}

/** Red Alert's logo on a dark rounded square, `size` pixels across, as PNG bytes. */
export function logoPng(size) {
	const rgba = new Uint8Array(size * size * 4);
	const background = hex("#08080a");
	const radius = size * 0.18;
	const scale = 64 / (size * 0.84);
	const offset = size * 0.08;
	const samples = 4;
	for (let y = 0; y < size; y++) {
		for (let x = 0; x < size; x++) {
			let r = 0;
			let g = 0;
			let b = 0;
			let a = 0;
			for (let sy = 0; sy < samples; sy++) {
				for (let sx = 0; sx < samples; sx++) {
					const px = x + (sx + 0.5) / samples;
					const py = y + (sy + 0.5) / samples;
					if (!inRoundRect(px, py, 0, 0, size, size, radius)) continue;
					const [cr, cg, cb] = logoAt((px - offset) * scale, (py - offset) * scale + 1.5, background);
					r += cr;
					g += cg;
					b += cb;
					a += 1;
				}
			}
			const i = (y * size + x) * 4;
			if (a > 0) {
				rgba[i] = Math.round(r / a);
				rgba[i + 1] = Math.round(g / a);
				rgba[i + 2] = Math.round(b / a);
			}
			rgba[i + 3] = Math.round((255 * a) / (samples * samples));
		}
	}
	return encodePng(size, size, rgba);
}
