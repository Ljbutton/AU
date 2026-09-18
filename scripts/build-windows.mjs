#!/usr/bin/env node
// scripts/build-windows.mjs
//
// Produces dist/AmongUs-TheHull-win-x64.zip: the game plus an official Node
// runtime and a double-click launcher, so a Windows player needs no install.
//
//   node scripts/build-windows.mjs
//
// No npm packages are used - downloads go through fetch and the archive is
// built by scripts/zip.js.

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { createZip, listEntries, readEntry, extractFile } from './zip.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..');
const CACHE = path.join(ROOT, '.cache');
const DIST = path.join(ROOT, 'dist');

const NODE_VERSION = 'v22.23.2';          // LTS "Jod"
const NODE_PLATFORM = 'win-x64';
const NODE_DIR = `node-${NODE_VERSION}-${NODE_PLATFORM}`;
const NODE_ZIP = `${NODE_DIR}.zip`;
const NODE_BASE = `https://nodejs.org/dist/${NODE_VERSION}`;

const PKG = JSON.parse(fs.readFileSync(path.join(ROOT, 'package.json'), 'utf8'));
const APP_NAME = 'AmongUs-TheHull';
const OUT_NAME = `${APP_NAME}-win-x64.zip`;

/**
 * A fixed timestamp for every archive entry, so two builds of the same commit
 * produce byte-identical zips and the published sha256 can be reproduced.
 * SOURCE_DATE_EPOCH wins, then the commit date, then a constant.
 */
function buildDate() {
  const epoch = process.env.SOURCE_DATE_EPOCH;
  if (epoch && /^\d+$/.test(epoch)) return new Date(Number(epoch) * 1000);
  try {
    const iso = execFileSync('git', ['log', '-1', '--format=%cI'], { cwd: ROOT, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
    if (iso) return new Date(iso);
  } catch { /* not a git checkout - fall through */ }
  return new Date('2026-01-01T00:00:00Z');
}

const log = (...a) => console.log(' ', ...a);
const mb = (n) => (n / 1048576).toFixed(1) + ' MB';
const sha256 = (buf) => crypto.createHash('sha256').update(buf).digest('hex');

// ---------------------------------------------------------------------------
// Download the official Node runtime (cached, checksum-verified)
// ---------------------------------------------------------------------------

async function download(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${url} -> HTTP ${res.status}`);
  return Buffer.from(await res.arrayBuffer());
}

async function cached(name, url) {
  const file = path.join(CACHE, name);
  if (fs.existsSync(file)) {
    log(`using cached ${name}`);
    return fs.readFileSync(file);
  }
  log(`downloading ${url}`);
  const buf = await download(url);
  fs.mkdirSync(CACHE, { recursive: true });
  fs.writeFileSync(file, buf);
  return buf;
}

async function fetchNodeRuntime() {
  const sums = (await cached(`SHASUMS256-${NODE_VERSION}.txt`, `${NODE_BASE}/SHASUMS256.txt`)).toString('utf8');
  const expected = sums.split('\n').map((l) => l.trim().split(/\s+/))
    .find(([, name]) => name === NODE_ZIP)?.[0];
  if (!expected) throw new Error(`no checksum published for ${NODE_ZIP}`);

  const zip = await cached(NODE_ZIP, `${NODE_BASE}/${NODE_ZIP}`);
  const actual = sha256(zip);
  if (actual !== expected) {
    fs.rmSync(path.join(CACHE, NODE_ZIP), { force: true });
    throw new Error(`checksum mismatch for ${NODE_ZIP}\n  expected ${expected}\n  got      ${actual}`);
  }
  log(`${NODE_ZIP} verified (${mb(zip.length)}, sha256 ${actual.slice(0, 16)}…)`);

  const entries = listEntries(zip);
  const pick = (suffix) => {
    const entry = entries.find((e) => e.name === `${NODE_DIR}/${suffix}`);
    if (!entry) throw new Error(`${suffix} missing from ${NODE_ZIP}`);
    return readEntry(zip, entry);
  };
  return { exe: pick('node.exe'), license: pick('LICENSE') };
}

// ---------------------------------------------------------------------------
// Game files
// ---------------------------------------------------------------------------

function collect(dir, base = dir, out = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) collect(full, base, out);
    else out.push(path.relative(ROOT, full).split(path.sep).join('/'));
  }
  return out;
}

function gameFiles() {
  return [
    ...collect(path.join(ROOT, 'server')),
    ...collect(path.join(ROOT, 'shared')),
    ...collect(path.join(ROOT, 'public')),
    'package.json',
    'LICENSE',
  ].filter((f) => fs.existsSync(path.join(ROOT, f)));
}

// ---------------------------------------------------------------------------
// Launcher + notes (CRLF so Notepad is happy)
// ---------------------------------------------------------------------------

const crlf = (text) => Buffer.from(text.replace(/\r?\n/g, '\r\n'), 'utf8');

const LAUNCHER = `@echo off
setlocal
title Among Us - The Hull
cd /d "%~dp0"

if not exist "node\\node.exe" (
  echo.
  echo   Could not find node\\node.exe next to this launcher.
  echo   Extract the whole ZIP folder first, then run this file from inside it.
  echo.
  pause
  exit /b 1
)

echo.
echo   Starting the ship... your browser will open in a moment.
echo   Keep this window open while you play.
echo.

"node\\node.exe" "server\\index.js" --open

echo.
echo   The game has stopped.
pause
`;

const READTHIS = `Among Us - The Hull  (v${PKG.version})
================================================================

A fan-made recreation of Among Us that runs in your browser.
Nothing to install - this folder already contains everything.


HOW TO PLAY
----------------------------------------------------------------

1. Extract this whole folder somewhere (Desktop is fine).
   Do not run the launcher from inside the ZIP.

2. Double-click:  Play Among Us.bat

3. A console window opens and your browser goes to the game.
   Keep that console window open while you play.

4. To stop the game, close the console window.


PLAYING WITH FRIENDS
----------------------------------------------------------------

Everyone on the same Wi-Fi / network can join:

  * The console window prints an address like  http://192.168.1.42:3000
  * Send that to your friends - they open it in their browser and
    type in your 6-letter lobby code.
  * The first time you run it, Windows may ask whether to allow
    node.exe through the firewall. Allow it on Private networks,
    or friends will not be able to connect.

Playing solo? Press "+ Bot" in the lobby to fill the crew with AI
players. You need at least 4 players (bots count).


CONTROLS
----------------------------------------------------------------

  Move .................. W A S D  or arrow keys (or drag with a mouse/finger)
  Use a console ......... E  or Space
  Report a body ......... R
  Kill (Impostor) ....... Q
  Vent (Impostor) ....... F
  Map ................... Tab  or M
  Chat (in meetings) .... C
  Close a panel ......... Esc


IF SOMETHING GOES WRONG
----------------------------------------------------------------

The window flashed and vanished
  Run "Play Among Us.bat" again and read the message before it
  closes - it now pauses on errors.

The browser did not open
  Look at the console window: it prints the address, normally
  http://localhost:3000 - paste that into any browser.

"Port 3000 is busy"
  That is fine. The game moves to the next free port and prints
  the new address in the console window.

Windows SmartScreen warned about the file
  This build is not code-signed. The launcher is a plain text .bat
  file and node.exe is the official, unmodified Node.js runtime
  downloaded from nodejs.org (its checksum is verified at build
  time). You can open the .bat in Notepad to see exactly what it
  runs.

Friends cannot connect
  Check the firewall prompt was allowed, that they used the
  192.168.x.x address (not localhost), and that you are all on the
  same network.


WHAT IS IN THIS FOLDER
----------------------------------------------------------------

  Play Among Us.bat ..... the launcher
  node\\node.exe ......... official Node.js ${NODE_VERSION} runtime (Windows x64)
  node\\LICENSE-node.txt . the Node.js licence
  server\\ ............... the game server
  shared\\ ............... rules and map shared by server and browser
  public\\ ............... the browser client
  LICENSE ............... this project's licence (MIT)


NOTES
----------------------------------------------------------------

This is a fan recreation made for fun. Among Us is made by
Innersloth - no code or assets from the original game are used
here. Go buy the real thing, it is excellent.
`;

// ---------------------------------------------------------------------------
// Build
// ---------------------------------------------------------------------------

async function main() {
  log(`packaging ${APP_NAME} ${PKG.version} for Windows x64`);
  const node = await fetchNodeRuntime();

  const files = [
    { name: `${APP_NAME}/Play Among Us.bat`, data: crlf(LAUNCHER) },
    { name: `${APP_NAME}/READ THIS FIRST.txt`, data: crlf(READTHIS) },
    { name: `${APP_NAME}/node/node.exe`, data: node.exe },
    { name: `${APP_NAME}/node/LICENSE-node.txt`, data: crlf(node.license.toString('utf8')) },
  ];
  for (const rel of gameFiles()) {
    files.push({ name: `${APP_NAME}/${rel}`, data: fs.readFileSync(path.join(ROOT, rel)) });
  }

  log(`node.exe ${mb(node.exe.length)}, ${files.length - 4} game files`);

  const date = buildDate();
  log(`archive timestamp ${date.toISOString()} (reproducible)`);
  const zip = createZip(files, { date });
  fs.mkdirSync(DIST, { recursive: true });
  const out = path.join(DIST, OUT_NAME);
  fs.writeFileSync(out, zip);

  // Read the archive back and confirm every byte survived the round trip.
  const check = fs.readFileSync(out);
  for (const f of files) {
    const back = extractFile(check, f.name);
    if (!back.equals(f.data)) throw new Error(`verification failed for ${f.name}`);
  }

  const digest = sha256(zip);
  fs.writeFileSync(`${out}.sha256`, `${digest}  ${OUT_NAME}\n`);
  log(`wrote dist/${OUT_NAME} (${mb(zip.length)})`);
  log(`sha256 ${digest}`);
  log(`verified ${files.length} entries`);
}

main().catch((err) => {
  console.error('\n  build failed:', err.message, '\n');
  process.exit(1);
});
