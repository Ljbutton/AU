#!/usr/bin/env node
// server/index.js - static file host + websocket game server.

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { WebSocketServer } from './ws.js';
import { RoomManager } from './rooms.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..');
const PUBLIC = path.join(ROOT, 'public');
const SHARED = path.join(ROOT, 'shared');
const PORT = Number(process.env.PORT || 3000);
const HOST = process.env.HOST || '0.0.0.0';

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

function safeJoin(base, target) {
  const resolved = path.resolve(base, '.' + path.posix.normalize('/' + target));
  return resolved.startsWith(base) ? resolved : null;
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

server.listen(PORT, HOST, () => {
  console.log(`\n  Among Us recreation running`);
  console.log(`  -> http://localhost:${PORT}\n`);
});

function shutdown() {
  console.log('\nshutting down...');
  manager.stop();
  wss.close();
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 1500).unref();
}
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
