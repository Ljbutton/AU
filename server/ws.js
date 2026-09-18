// server/ws.js
// A small, dependency-free RFC 6455 WebSocket server. Only what this game
// needs: text frames, fragmentation, ping/pong keepalive and clean closes.

import crypto from 'node:crypto';
import { EventEmitter } from 'node:events';

const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';
const OP = { CONT: 0x0, TEXT: 0x1, BINARY: 0x2, CLOSE: 0x8, PING: 0x9, PONG: 0xa };
const MAX_MESSAGE = 1 << 20;        // 1 MiB is far more than any game message
const PING_INTERVAL = 25_000;
const PING_TIMEOUT = 60_000;

export class WebSocketConnection extends EventEmitter {
  constructor(socket, req) {
    super();
    this.socket = socket;
    this.req = req;
    this.open = true;
    this.buffer = Buffer.alloc(0);
    this.fragments = [];
    this.fragmentOp = null;
    this.lastPong = Date.now();
    this.remoteAddress = socket.remoteAddress;

    socket.on('data', (chunk) => this._onData(chunk));
    socket.on('close', () => this._finish());
    socket.on('error', (err) => { this.emit('error', err); this._finish(); });
    socket.setTimeout(0);
    socket.setNoDelay(true);
  }

  _onData(chunk) {
    this.buffer = this.buffer.length ? Buffer.concat([this.buffer, chunk]) : chunk;
    try {
      while (this._readFrame()) { /* keep draining */ }
    } catch (err) {
      this.emit('error', err);
      this.close(1002, 'protocol error');
    }
  }

  _readFrame() {
    const buf = this.buffer;
    if (buf.length < 2) return false;
    const b0 = buf[0], b1 = buf[1];
    const fin = (b0 & 0x80) !== 0;
    const opcode = b0 & 0x0f;
    const masked = (b1 & 0x80) !== 0;
    let len = b1 & 0x7f;
    let offset = 2;

    if (len === 126) {
      if (buf.length < offset + 2) return false;
      len = buf.readUInt16BE(offset);
      offset += 2;
    } else if (len === 127) {
      if (buf.length < offset + 8) return false;
      const big = buf.readBigUInt64BE(offset);
      if (big > BigInt(MAX_MESSAGE)) throw new Error('frame too large');
      len = Number(big);
      offset += 8;
    }
    if (len > MAX_MESSAGE) throw new Error('frame too large');
    if (!masked) throw new Error('client frames must be masked');
    if (buf.length < offset + 4 + len) return false;

    const mask = buf.subarray(offset, offset + 4);
    offset += 4;
    const payload = Buffer.allocUnsafe(len);
    for (let i = 0; i < len; i++) payload[i] = buf[offset + i] ^ mask[i & 3];
    offset += len;
    this.buffer = buf.subarray(offset);

    switch (opcode) {
      case OP.CONT:
      case OP.TEXT:
      case OP.BINARY: {
        if (opcode !== OP.CONT) {
          this.fragmentOp = opcode;
          this.fragments = [];
        }
        this.fragments.push(payload);
        const total = this.fragments.reduce((n, f) => n + f.length, 0);
        if (total > MAX_MESSAGE) throw new Error('message too large');
        if (fin) {
          const full = this.fragments.length === 1 ? this.fragments[0] : Buffer.concat(this.fragments);
          const op = this.fragmentOp;
          this.fragments = [];
          this.fragmentOp = null;
          if (op === OP.TEXT) this.emit('message', full.toString('utf8'));
          else this.emit('binary', full);
        }
        break;
      }
      case OP.PING:
        this._frame(OP.PONG, payload);
        break;
      case OP.PONG:
        this.lastPong = Date.now();
        break;
      case OP.CLOSE: {
        const code = payload.length >= 2 ? payload.readUInt16BE(0) : 1005;
        this.closeCode = code;
        this._frame(OP.CLOSE, payload.subarray(0, 2));
        this.socket.end();
        this._finish();
        break;
      }
      default:
        throw new Error('unknown opcode ' + opcode);
    }
    return this.buffer.length >= 2;
  }

  _frame(opcode, payload) {
    if (!this.open || this.socket.destroyed) return;
    const len = payload.length;
    let header;
    if (len < 126) {
      header = Buffer.allocUnsafe(2);
      header[1] = len;
    } else if (len < 65536) {
      header = Buffer.allocUnsafe(4);
      header[1] = 126;
      header.writeUInt16BE(len, 2);
    } else {
      header = Buffer.allocUnsafe(10);
      header[1] = 127;
      header.writeBigUInt64BE(BigInt(len), 2);
    }
    header[0] = 0x80 | opcode;
    try {
      this.socket.write(header);
      if (len) this.socket.write(payload);
    } catch { this._finish(); }
  }

  /** Send a JSON-serialisable value as a text frame. */
  sendJSON(value) {
    this.send(JSON.stringify(value));
  }

  send(text) {
    this._frame(OP.TEXT, Buffer.from(text, 'utf8'));
  }

  ping() { this._frame(OP.PING, Buffer.alloc(0)); }

  close(code = 1000, reason = '') {
    if (!this.open) return;
    const reasonBuf = Buffer.from(reason, 'utf8');
    const payload = Buffer.allocUnsafe(2 + reasonBuf.length);
    payload.writeUInt16BE(code, 0);
    reasonBuf.copy(payload, 2);
    this._frame(OP.CLOSE, payload);
    this.socket.end();
    this._finish();
  }

  terminate() {
    this.socket.destroy();
    this._finish();
  }

  _finish() {
    if (!this.open) return;
    this.open = false;
    this.emit('close', this.closeCode || 1006);
    this.removeAllListeners('message');
  }
}

export class WebSocketServer extends EventEmitter {
  constructor(httpServer, { path = '/ws' } = {}) {
    super();
    this.path = path;
    this.clients = new Set();
    httpServer.on('upgrade', (req, socket, head) => this._onUpgrade(req, socket, head));

    this.heartbeat = setInterval(() => {
      const now = Date.now();
      for (const c of this.clients) {
        if (now - c.lastPong > PING_TIMEOUT) { c.terminate(); continue; }
        c.ping();
      }
    }, PING_INTERVAL);
    this.heartbeat.unref?.();
  }

  _onUpgrade(req, socket, head) {
    const url = new URL(req.url, 'http://localhost');
    if (this.path && url.pathname !== this.path) {
      socket.destroy();
      return;
    }
    const key = req.headers['sec-websocket-key'];
    if (req.headers.upgrade?.toLowerCase() !== 'websocket' || !key) {
      socket.write('HTTP/1.1 400 Bad Request\r\n\r\n');
      socket.destroy();
      return;
    }
    const accept = crypto.createHash('sha1').update(key + GUID).digest('base64');
    socket.write(
      'HTTP/1.1 101 Switching Protocols\r\n' +
      'Upgrade: websocket\r\n' +
      'Connection: Upgrade\r\n' +
      `Sec-WebSocket-Accept: ${accept}\r\n\r\n`
    );
    const conn = new WebSocketConnection(socket, req);
    if (head && head.length) conn._onData(head);
    this.clients.add(conn);
    conn.on('close', () => this.clients.delete(conn));
    this.emit('connection', conn, req);
  }

  close() {
    clearInterval(this.heartbeat);
    for (const c of this.clients) c.close(1001, 'server shutting down');
  }
}
