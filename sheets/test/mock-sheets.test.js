// Runs TournamentSheet.gs against a small in-memory stand-in for Google Sheets.
// Usage: node sheets/test/mock-sheets.test.js <payload.json>...
const fs = require('fs');
const vm = require('vm');
const path = require('path');
const assert = require('assert');

class Range {
  constructor(sheet, r, c, nr, nc) { Object.assign(this, { sheet, r, c, nr, nc }); }
  getValues() {
    const out = [];
    for (let i = 0; i < this.nr; i++) {
      const row = [];
      for (let j = 0; j < this.nc; j++) row.push(this.sheet.get(this.r + i, this.c + j));
      out.push(row);
    }
    return out;
  }
  getValue() { return this.sheet.get(this.r, this.c); }
  setValues(v) {
    assert.strictEqual(v.length, this.nr, 'row count mismatch');
    v.forEach((row, i) => { assert.strictEqual(row.length, this.nc, 'column count mismatch'); row.forEach((x, j) => this.sheet.set(this.r + i, this.c + j, x)); });
    return this;
  }
  setValue(x) { this.sheet.set(this.r, this.c, x); return this; }
  setFormula(f) { this.sheet.formulas[`${this.r},${this.c}`] = f; this.sheet.set(this.r, this.c, ''); return this; }
  setFontWeight() { return this; }
  setNumberFormat() { return this; }
  setNote() { return this; }
}

class Sheet {
  constructor(name) { this.name = name; this.rows = []; this.formulas = {}; this.frozen = 0; }
  getName() { return this.name; }
  get(r, c) { return (this.rows[r - 1] || [])[c - 1] ?? ''; }
  set(r, c, v) { while (this.rows.length < r) this.rows.push([]); this.rows[r - 1][c - 1] = v; }
  getLastRow() {
    for (let i = this.rows.length; i > 0; i--) if ((this.rows[i - 1] || []).some(v => v !== '' && v !== undefined)) return i;
    return 0;
  }
  getLastColumn() { return Math.max(0, ...this.rows.map(r => r.length)); }
  getRange(a, b, c, d) {
    if (typeof a === 'string') {
      const m = a.match(/^([A-Z]+)(\d*)(?::([A-Z]+)(\d*))?$/);
      const col = s => [...s].reduce((n, ch) => n * 26 + ch.charCodeAt(0) - 64, 0);
      const r1 = m[2] ? +m[2] : 1, c1 = col(m[1]);
      const r2 = m[4] ? +m[4] : (m[3] ? 1000 : r1), c2 = m[3] ? col(m[3]) : c1;
      return new Range(this, r1, c1, r2 - r1 + 1, c2 - c1 + 1);
    }
    return new Range(this, a, b, c ?? 1, d ?? 1);
  }
  deleteRows(start, count) { this.rows.splice(start - 1, count); }
  setFrozenRows(n) { this.frozen = n; }
}

function makeSpreadsheet() {
  const sheets = [new Sheet('Sheet1')];
  return {
    sheets,
    getSheetByName: n => sheets.find(s => s.name === n) || null,
    insertSheet: n => { const s = new Sheet(n); sheets.push(s); return s; },
    deleteSheet: s => sheets.splice(sheets.indexOf(s), 1),
    getSheets: () => sheets,
  };
}

function load(secret) {
  const ss = makeSpreadsheet();
  const ctx = {
    SpreadsheetApp: { getActiveSpreadsheet: () => ss },
    LockService: { getScriptLock: () => ({ waitLock() {}, releaseLock() {} }) },
    ContentService: {
      MimeType: { JSON: 'json' },
      createTextOutput: t => ({ text: t, setMimeType() { return this; } }),
    },
    Date, JSON, Array, Object,
  };
  vm.createContext(ctx);
  let code = fs.readFileSync(path.join(__dirname, '..', 'TournamentSheet.gs'), 'utf8');
  if (secret) code = code.replace("const SECRET = 'change-me';", `const SECRET = '${secret}';`);
  vm.runInContext(code, ctx);
  const post = body => JSON.parse(ctx.doPost({ postData: { contents: JSON.stringify(body) } }).text);
  return { ss, ctx, post };
}

const payloads = process.argv.slice(2).map(f => JSON.parse(fs.readFileSync(f, 'utf8')));
assert(payloads.length >= 2, 'pass at least two payload files');

// The shipped script refuses to run until SECRET is changed.
assert.match(load().post({ ...payloads[0], secret: 'change-me' }).error, /Set SECRET/);

const { ss, post } = load('hunter2');
assert.match(post({ ...payloads[0], secret: 'wrong' }).error, /Wrong secret/);
assert.strictEqual(ss.getSheetByName('Games'), null, 'nothing written on a bad secret');

for (const p of payloads) assert.deepStrictEqual(post({ ...p, secret: 'hunter2' }), { ok: true, game: p.game.id });

const tabs = ss.getSheets().map(s => s.name);
assert.deepStrictEqual(tabs, ['Leaderboard', 'Player Games', 'Games', 'Points Detail', 'Players']);
const pg = ss.getSheetByName('Player Games');
const games = ss.getSheetByName('Games');
const detail = ss.getSheetByName('Points Detail');
const playerCount = payloads.reduce((n, p) => n + p.players.length, 0);
assert.strictEqual(pg.getLastRow(), 1 + playerCount);
assert.strictEqual(games.getLastRow(), 1 + payloads.length);
assert.strictEqual(pg.getLastColumn(), 29);

// A referee adjusts a score in game 1, then game 1 is sent again (e.g. !sheetsync).
const first = payloads[0];
const row = pg.rows.findIndex(r => r[0] === first.game.id && r[6] === first.players[0].key) + 1;
pg.set(row, 27, -2);
pg.set(row, 28, 'Meta call, round 3');
assert.deepStrictEqual(post({ ...first, secret: 'hunter2' }), { ok: true, game: first.game.id });
assert.strictEqual(pg.getLastRow(), 1 + playerCount, 'resend must not duplicate rows');
assert.strictEqual(games.getLastRow(), 1 + payloads.length);
const again = pg.rows.find(r => r[0] === first.game.id && r[6] === first.players[0].key);
assert.strictEqual(again[26], -2, 'referee adjustment kept');
assert.strictEqual(again[27], 'Meta call, round 3', 'referee note kept');

// Points in Player Games match the breakdown lines in Points Detail.
for (const p of payloads) {
  for (const player of p.players) {
    const r = pg.rows.find(x => x[0] === p.game.id && x[6] === player.key);
    const lines = detail.rows.filter(x => x[0] === p.game.id && x[3] === r[5]);
    const sum = lines.reduce((n, x) => n + x[6], 0);
    assert(Math.abs(sum - r[25]) < 1e-9, `${r[5]} game ${p.game.number}: detail ${sum} vs points ${r[25]}`);
  }
}

// A renamed player keeps the name the Players tab gave them.
const players = ss.getSheetByName('Players');
assert.strictEqual(players.getLastRow(), 1 + new Set(payloads.flatMap(p => p.players.map(x => x.key))).size);
players.set(2, 2, 'Renamed');
post({ ...first, secret: 'hunter2' });
assert(pg.rows.some(r => r[0] === first.game.id && r[5] === 'Renamed'));

console.log('Leaderboard formula:\n' + ss.getSheetByName('Leaderboard').formulas['3,1']);
console.log(`\nAll mock sheet checks passed (${payloads.length} games, ${playerCount} player rows).`);
