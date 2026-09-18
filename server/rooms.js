// server/rooms.js - lobby registry and the shared simulation loop.

import { GameRoom, sanitizeName } from './game.js';
import { TICK_MS } from '../shared/constants.js';

const CODE_ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ';   // no I/O, they read badly
const EMPTY_ROOM_TTL = 60_000;

export class RoomManager {
  constructor() {
    this.rooms = new Map();
    this.connections = new Map();     // conn -> { room, player }
    this.last = Date.now();
    this.timer = setInterval(() => this.tick(), TICK_MS);
  }

  newCode() {
    let code;
    do {
      code = Array.from({ length: 6 }, () => CODE_ALPHABET[Math.floor(Math.random() * CODE_ALPHABET.length)]).join('');
    } while (this.rooms.has(code));
    return code;
  }

  createRoom() {
    const code = this.newCode();
    const room = new GameRoom(code, this);
    this.rooms.set(code, room);
    return room;
  }

  getRoom(code) { return this.rooms.get(String(code || '').toUpperCase().trim()); }

  destroyRoom(code) {
    const room = this.rooms.get(code);
    if (!room) return;
    for (const p of room.playerList) if (p.conn) this.connections.delete(p.conn);
    this.rooms.delete(code);
  }

  /** A socket asks to create or join. Returns the joined player or an error. */
  attach(conn, msg) {
    const existing = this.connections.get(conn);
    if (existing) return { error: 'Already in a lobby.' };

    let room;
    if (msg.t === 'create') {
      room = this.createRoom();
      if (msg.settings) room.settings = { ...room.settings, ...msg.settings };
    } else {
      room = this.getRoom(msg.code);
      if (!room) return { error: 'No lobby with that code.' };
    }

    const res = room.addPlayer({ name: sanitizeName(msg.name), color: msg.color, hat: msg.hat, conn });
    if (res.error) {
      if (msg.t === 'create') this.destroyRoom(room.code);
      return res;
    }
    this.connections.set(conn, { room, player: res.player });
    conn.sendJSON({
      t: 'joined',
      you: res.player.id,
      code: room.code,
      hostId: room.hostId,
      settings: room.settings,
      players: room.playerList.map((p) => p.publicInfo()),
    });
    return { room, player: res.player };
  }

  handle(conn, raw) {
    let msg;
    try { msg = JSON.parse(raw); } catch { return; }
    if (!msg || typeof msg !== 'object') return;

    const entry = this.connections.get(conn);
    if (!entry) {
      if (msg.t === 'create' || msg.t === 'join') {
        const res = this.attach(conn, msg);
        if (res.error) conn.sendJSON({ t: 'error', msg: res.error });
      } else if (msg.t === 'ping') {
        conn.sendJSON({ t: 'pong', ts: msg.ts });
      }
      return;
    }
    entry.room.handleMessage(entry.player, msg);
  }

  detach(conn) {
    const entry = this.connections.get(conn);
    if (!entry) return;
    this.connections.delete(conn);
    entry.room.removePlayer(entry.player.id);
    if (entry.room.players.size === 0) entry.room.emptySince = Date.now();
  }

  tick() {
    const now = Date.now();
    const dt = Math.min(0.25, (now - this.last) / 1000);
    this.last = now;
    for (const room of this.rooms.values()) {
      try {
        room.tick(dt);
      } catch (err) {
        console.error(`[room ${room.code}] tick failed:`, err);
      }
      if (room.players.size === 0) {
        if (!room.emptySince) room.emptySince = now;
        else if (now - room.emptySince > EMPTY_ROOM_TTL) this.destroyRoom(room.code);
      } else {
        room.emptySince = null;
      }
    }
  }

  stats() {
    return {
      rooms: this.rooms.size,
      players: [...this.rooms.values()].reduce((n, r) => n + r.players.size, 0),
      lobbies: [...this.rooms.values()].map((r) => ({ code: r.code, phase: r.phase, players: r.players.size })),
    };
  }

  stop() { clearInterval(this.timer); }
}
