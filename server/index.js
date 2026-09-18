#!/usr/bin/env node
// server/index.js - static file host + websocket game server.

import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { WebSocketServer } from './ws.js';
import { RoomManager } from './rooms.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..');
const PUBLIC = path.join(ROOT, 'public');
const SHARED = path.join(ROOT, 'shared');
const args = process.argv.slice(2);
const flag = (name) => args.includes(`--${name}`);
const PORT = Number(process.env.PORT || 3000);
const HOST = process.env.HOST || '0.0.0.0';
const OPEN_BROWSER = flag('open') || process.env.AU_OPEN === '1';
const MAX_PORT_TRIES = 12;

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.ico': 'image/x-icon',
  '.webmanifest': 'application/manifest+json',
};

/**
 * Resolve a request path inside `base`, or null if it would escape.
 * `..` is neutralised before the join, and the result is re-checked with
 * path.relative rather than a string prefix (which would also accept a sibling
 * directory whose name merely starts with the base name).
 */
export function safeJoin(base, target) {
  const resolved = path.resolve(base, '.' + path.posix.normalize('/' + target));
  const rel = path.relative(base, resolved);
  if (rel === '') return resolved;
  return !rel.startsWith('..') && !path.isAbsolute(rel) ? resolved : null;
}

function serveFile(res, filePath, req) {
  fs.stat(filePath, (err, stat) => {
    if (err || !stat.isFile()) {
      res.writeHead(404, { 'Content-Type': 'text/plain' });
      res.end('Not found');
      return;
    }
    const ext = path.extname(filePath).toLowerCase();
    const headers = {
      'Content-Type': MIME[ext] || 'application/octet-stream',
      'Content-Length': stat.size,
      'Cache-Control': 'no-cache',
    };
    if (req.method === 'HEAD') {
      res.writeHead(200, headers);
      res.end();
      return;
    }
    res.writeHead(200, headers);
    fs.createReadStream(filePath).pipe(res);
  });
}

const manager = new RoomManager();

const server = http.createServer((req, res) => {
  const url = new URL(req.url, `http://${req.headers.host || 'localhost'}`);
  let pathname = decodeURIComponent(url.pathname);

  if (pathname === '/health') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ ok: true, ...manager.stats() }));
    return;
  }

  if (pathname.startsWith('/shared/')) {
    const file = safeJoin(SHARED, pathname.slice('/shared'.length));
    if (!file) { res.writeHead(403).end('Forbidden'); return; }
    serveFile(res, file, req);
    return;
  }

  if (pathname === '/') pathname = '/index.html';
  const file = safeJoin(PUBLIC, pathname);
  if (!file) { res.writeHead(403).end('Forbidden'); return; }
  serveFile(res, file, req);
});

const wss = new WebSocketServer(server, { path: '/ws' });
wss.on('connection', (conn) => {
  conn.on('message', (raw) => {
    try { manager.handle(conn, raw); }
    catch (err) { console.error('message handling failed:', err); }
  });
  conn.on('close', () => manager.detach(conn));
  conn.on('error', () => manager.detach(conn));
});

/** Every IPv4 address friends on the same network could use. */
function lanAddresses() {
  const out = [];
  for (const entries of Object.values(os.networkInterfaces())) {
    for (const net of entries || []) {
      if (net.family === 'IPv4' && !net.internal) out.push(net.address);
    }
  }
  return out;
}

function openBrowser(url) {
  const cmd = process.platform === 'win32' ? ['cmd', ['/c', 'start', '', url]]
    : process.platform === 'darwin' ? ['open', [url]]
      : ['xdg-open', [url]];
  try {
    const child = spawn(cmd[0], cmd[1], { detached: true, stdio: 'ignore' });
    child.on('error', () => {});
    child.unref();
  } catch { /* opening a browser is a convenience, never a failure */ }
}

/**
 * Listen on `port`, stepping to the next one when it is already taken - a
 * second copy of the game (or anything else on 3000) should not be a dead end
 * for someone who just double-clicked a launcher.
 */
function listen(port, attempt = 0) {
  // Both listeners are removed as soon as one fires: a stale 'listening'
  // handler from a failed attempt would otherwise announce the wrong port.
  const onError = (err) => {
    server.removeListener('listening', onListening);
    if (err.code === 'EADDRINUSE' && attempt < MAX_PORT_TRIES) {
      console.log(`  port ${port} is busy, trying ${port + 1}...`);
      listen(port + 1, attempt + 1);
      return;
    }
    console.error(`\n  Could not start the server: ${err.message}\n`);
    process.exit(1);
  };
  const onListening = () => {
    server.removeListener('error', onError);
    const local = `http://localhost:${port}`;
    console.log('\n  Among Us - The Hull is running.');
    console.log(`\n    Play here:        ${local}`);
    const lan = lanAddresses();
    if (lan.length) {
      console.log('    Friends can join: ' + lan.map((ip) => `http://${ip}:${port}`).join('\n                      '));
    }
    console.log('\n  Keep this window open while you play. Press Ctrl+C to stop.\n');
    if (OPEN_BROWSER) openBrowser(local);
  };
  server.once('error', onError);
  server.once('listening', onListening);
  server.listen(port, HOST);
}

listen(PORT);

function shutdown() {
  console.log('\nshutting down...');
  manager.stop();
  wss.close();
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 1500).unref();
}
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
