// scripts/zip.js
// A minimal ZIP reader/writer built on node:zlib, so packaging the Windows
// build needs no external tools and no npm packages. Handles exactly what this
// project needs: deflate/store entries, no ZIP64, no encryption.

import zlib from 'node:zlib';

const LOCAL_SIG = 0x04034b50;
const CENTRAL_SIG = 0x02014b50;
const EOCD_SIG = 0x06054b50;

// ---------------------------------------------------------------------------
// CRC32
// ---------------------------------------------------------------------------

const CRC_TABLE = (() => {
  const table = new Int32Array(256);
  for (let i = 0; i < 256; i++) {
    let c = i;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[i] = c;
  }
  return table;
})();

export function crc32(buf) {
  let c = 0 ^ -1;
  for (let i = 0; i < buf.length; i++) c = (c >>> 8) ^ CRC_TABLE[(c ^ buf[i]) & 0xff];
  return (c ^ -1) >>> 0;
}

// ---------------------------------------------------------------------------
// Reading
// ---------------------------------------------------------------------------

function findEndOfCentralDirectory(buf) {
  const minOffset = Math.max(0, buf.length - 0xffff - 22);
  for (let i = buf.length - 22; i >= minOffset; i--) {
    if (buf.readUInt32LE(i) === EOCD_SIG) return i;
  }
  throw new Error('not a zip file (no end-of-central-directory record)');
}

/** List every entry in a zip buffer: { name, method, compressedSize, size, offset }. */
export function listEntries(buf) {
  const eocd = findEndOfCentralDirectory(buf);
  const count = buf.readUInt16LE(eocd + 10);
  let p = buf.readUInt32LE(eocd + 16);
  const entries = [];
  for (let i = 0; i < count; i++) {
    if (buf.readUInt32LE(p) !== CENTRAL_SIG) throw new Error('corrupt central directory');
    const method = buf.readUInt16LE(p + 10);
    const compressedSize = buf.readUInt32LE(p + 20);
    const size = buf.readUInt32LE(p + 24);
    const nameLen = buf.readUInt16LE(p + 28);
    const extraLen = buf.readUInt16LE(p + 30);
    const commentLen = buf.readUInt16LE(p + 32);
    const offset = buf.readUInt32LE(p + 42);
    const name = buf.toString('utf8', p + 46, p + 46 + nameLen);
    entries.push({ name, method, compressedSize, size, offset });
    p += 46 + nameLen + extraLen + commentLen;
  }
  return entries;
}

/** Decompress one entry's bytes. */
export function readEntry(buf, entry) {
  if (buf.readUInt32LE(entry.offset) !== LOCAL_SIG) throw new Error(`corrupt local header for ${entry.name}`);
  const nameLen = buf.readUInt16LE(entry.offset + 26);
  const extraLen = buf.readUInt16LE(entry.offset + 28);
  const start = entry.offset + 30 + nameLen + extraLen;
  const raw = buf.subarray(start, start + entry.compressedSize);
  const data = entry.method === 0 ? Buffer.from(raw) : zlib.inflateRawSync(raw);
  if (data.length !== entry.size) throw new Error(`size mismatch for ${entry.name}`);
  if (crc32(data) !== readEntryCrc(buf, entry)) throw new Error(`crc mismatch for ${entry.name}`);
  return data;
}

function readEntryCrc(buf, entry) {
  return buf.readUInt32LE(entry.offset + 14);
}

/** Pull a single named file out of a zip buffer. */
export function extractFile(buf, name) {
  const entry = listEntries(buf).find((e) => e.name === name);
  if (!entry) throw new Error(`${name} is not in the archive`);
  return readEntry(buf, entry);
}

// ---------------------------------------------------------------------------
// Writing
// ---------------------------------------------------------------------------

/**
 * DOS timestamps are conventionally local time, but using UTC keeps archives
 * byte-identical no matter which timezone the build machine is in.
 */
function dosDateTime(date) {
  const year = Math.max(1980, date.getUTCFullYear());
  return {
    time: (date.getUTCHours() << 11) | (date.getUTCMinutes() << 5) | (date.getUTCSeconds() >> 1),
    date: ((year - 1980) << 9) | ((date.getUTCMonth() + 1) << 5) | date.getUTCDate(),
  };
}

/**
 * Build a zip from [{ name, data }] entries. Paths use forward slashes; folders
 * are implied, which every Windows unpacker understands.
 */
export function createZip(files, { date = new Date(), level = 9 } = {}) {
  const { time, date: dosDate } = dosDateTime(date);
  const chunks = [];
  const central = [];
  let offset = 0;

  for (const file of files) {
    const name = Buffer.from(file.name.replace(/\\/g, '/'), 'utf8');
    const data = file.data;
    const deflated = zlib.deflateRawSync(data, { level });
    const useDeflate = deflated.length < data.length;
    const payload = useDeflate ? deflated : data;
    const method = useDeflate ? 8 : 0;
    const crc = crc32(data);

    const local = Buffer.alloc(30);
    local.writeUInt32LE(LOCAL_SIG, 0);
    local.writeUInt16LE(20, 4);            // version needed
    local.writeUInt16LE(0, 6);             // flags
    local.writeUInt16LE(method, 8);
    local.writeUInt16LE(time, 10);
    local.writeUInt16LE(dosDate, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(payload.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(name.length, 26);
    local.writeUInt16LE(0, 28);
    chunks.push(local, name, payload);

    const entry = Buffer.alloc(46);
    entry.writeUInt32LE(CENTRAL_SIG, 0);
    entry.writeUInt16LE(20, 4);            // version made by (MS-DOS)
    entry.writeUInt16LE(20, 6);            // version needed
    entry.writeUInt16LE(0, 8);
    entry.writeUInt16LE(method, 10);
    entry.writeUInt16LE(time, 12);
    entry.writeUInt16LE(dosDate, 14);
    entry.writeUInt32LE(crc, 16);
    entry.writeUInt32LE(payload.length, 20);
    entry.writeUInt32LE(data.length, 24);
    entry.writeUInt16LE(name.length, 28);
    entry.writeUInt16LE(0, 30);            // extra
    entry.writeUInt16LE(0, 32);            // comment
    entry.writeUInt16LE(0, 34);            // disk
    entry.writeUInt16LE(0, 36);            // internal attrs
    entry.writeUInt32LE(0, 38);            // external attrs
    entry.writeUInt32LE(offset, 42);
    central.push(entry, name);

    offset += local.length + name.length + payload.length;
  }

  const centralBuf = Buffer.concat(central);
  const eocd = Buffer.alloc(22);
  eocd.writeUInt32LE(EOCD_SIG, 0);
  eocd.writeUInt16LE(0, 4);
  eocd.writeUInt16LE(0, 6);
  eocd.writeUInt16LE(files.length, 8);
  eocd.writeUInt16LE(files.length, 10);
  eocd.writeUInt32LE(centralBuf.length, 12);
  eocd.writeUInt32LE(offset, 16);
  eocd.writeUInt16LE(0, 20);

  return Buffer.concat([...chunks, centralBuf, eocd]);
}
