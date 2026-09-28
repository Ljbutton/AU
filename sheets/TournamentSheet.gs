/**
 * Among Us Tournament Tracker -> Google Sheets.
 *
 * Paste this into a NEW, blank Google Sheet (Extensions > Apps Script), set SECRET below,
 * run `setup` once, then deploy it as a web app (Execute as: Me, Who has access: Anyone).
 * Put the web app URL and the same secret in the mod's config under [GoogleSheets].
 *
 * Tabs it creates:
 *   Leaderboard    Totals per player, recalculated live from Player Games.
 *   Player Games   One row per player per game. Referees can use Ref Adj / Ref Note.
 *   Games          One row per game.
 *   Points Detail  Every rule each player scored on, for checking against the point sheet.
 *   Players        Friend code -> the name shown everywhere else. Rename players here.
 */

// Change this to your own password, and use the same one in the mod's config.
const SECRET = 'change-me';

const TABS = {
  leaderboard: 'Leaderboard',
  playerGames: 'Player Games',
  games: 'Games',
  detail: 'Points Detail',
  players: 'Players',
};

const PLAYER_GAME_HEADERS = [
  'Game ID', 'Tournament', 'Game #', 'Date', 'Map', 'Player', 'Friend Code', 'Color', 'Role', 'Team',
  'Won', 'Death', 'Kills', 'First Blood', 'Correct Votes', 'Wrong Votes', 'Skips', 'Missed Votes',
  'Correct Vote-outs', 'Wrong Vote-outs', 'Meetings Called', 'Bodies Reported', 'Tasks Done', 'Tasks Total',
  'Sabotages', 'Points', 'Ref Adj', 'Ref Note', 'Counted',
];
const GAME_HEADERS = ['Game ID', 'Tournament', 'Game #', 'Date', 'Lobby', 'Map', 'Winner', 'Result', 'Length', 'Impostors', 'Meetings', 'MVP', 'Counted'];
const DETAIL_HEADERS = ['Game ID', 'Tournament', 'Game #', 'Player', 'Team', 'Rule', 'Points'];
const PLAYER_HEADERS = ['Friend Code', 'Name', 'First Seen'];

// Column positions in Player Games (1-based) that other code relies on.
const PG = { player: 6, key: 7, points: 26, refAdj: 27, refNote: 28, counted: 29 };

function doGet() {
  return reply({ ok: true, message: 'Tournament sheet is ready. The mod sends games here.' });
}

function doPost(e) {
  let data;
  try {
    data = JSON.parse(e.postData.contents);
  } catch (err) {
    return reply({ ok: false, error: 'Request was not JSON' });
  }
  if (SECRET === 'change-me') return reply({ ok: false, error: 'Set SECRET at the top of the script and redeploy' });
  if (data.secret !== SECRET) return reply({ ok: false, error: 'Wrong secret: it must match GoogleSheets.Secret in the mod config' });
  if (!data.game || !data.game.id || !Array.isArray(data.players)) return reply({ ok: false, error: 'Missing game data' });

  const lock = LockService.getScriptLock();
  lock.waitLock(30000);
  try {
    setup();
    recordGame(data.game, data.players);
    return reply({ ok: true, game: data.game.id });
  } finally {
    lock.releaseLock();
  }
}

/** Creates any missing tabs, headers and the leaderboard formula. Safe to run again. */
function setup() {
  const ss = SpreadsheetApp.getActiveSpreadsheet();
  const leaderboard = tab(ss, TABS.leaderboard, null);
  tab(ss, TABS.playerGames, PLAYER_GAME_HEADERS);
  tab(ss, TABS.games, GAME_HEADERS);
  tab(ss, TABS.detail, DETAIL_HEADERS);
  tab(ss, TABS.players, PLAYER_HEADERS);

  if (leaderboard.getRange('A1').getValue() !== 'Tournament:') buildLeaderboard(leaderboard);

  // A new spreadsheet starts with an empty "Sheet1"; drop it once our tabs exist.
  const blank = ss.getSheetByName('Sheet1');
  if (blank && blank.getLastRow() === 0 && ss.getSheets().length > 1) ss.deleteSheet(blank);
}

function tab(ss, name, headers) {
  let sheet = ss.getSheetByName(name);
  if (!sheet) sheet = ss.insertSheet(name);
  if (headers && sheet.getLastRow() === 0) {
    sheet.getRange(1, 1, 1, headers.length).setValues([headers]).setFontWeight('bold');
    sheet.setFrozenRows(1);
  }
  return sheet;
}

function buildLeaderboard(sheet) {
  sheet.getRange('A1').setValue('Tournament:').setFontWeight('bold');
  sheet.getRange('B1').setNote('Type a tournament name to show only that tournament. Leave empty for everything.');
  sheet.getRange('D1').setValue('Points include referee adjustments. Only counted games are included.');

  const where = '"where AC = true" & IF(B1 = "", "", " and B = \'" & SUBSTITUTE(B1, "\'", "") & "\'")';
  const query =
    '=IFERROR(QUERY(\'' + TABS.playerGames + '\'!A1:AC, ' +
    '"select F, sum(Z) + sum(AA), count(A), sum(K), sum(M), sum(O), sum(P), sum(W), sum(X) " & ' + where + ' & ' +
    '" group by F order by sum(Z) + sum(AA) desc ' +
    "label F 'Player', sum(Z) + sum(AA) 'Points', count(A) 'Games', sum(K) 'Wins', sum(M) 'Kills', " +
    "sum(O) 'Correct Votes', sum(P) 'Wrong Votes', sum(W) 'Tasks Done', sum(X) 'Tasks Total'\", 1), \"No games yet\")";
  sheet.getRange('A3').setFormula(query);

  sheet.getRange('J3').setValue('Vote %').setFontWeight('bold');
  sheet.getRange('K3').setValue('Task %').setFontWeight('bold');
  sheet.getRange('J4').setFormula('=ARRAYFORMULA(IF(A4:A = "", "", IFERROR(F4:F / (F4:F + G4:G), "")))');
  sheet.getRange('K4').setFormula('=ARRAYFORMULA(IF(A4:A = "", "", IFERROR(H4:H / I4:I, "")))');
  sheet.getRange('J4:K').setNumberFormat('0%');
  sheet.getRange('A3:K3').setFontWeight('bold');
  sheet.setFrozenRows(3);
}

function recordGame(game, players) {
  const ss = SpreadsheetApp.getActiveSpreadsheet();
  const playerGames = ss.getSheetByName(TABS.playerGames);
  const games = ss.getSheetByName(TABS.games);
  const detail = ss.getSheetByName(TABS.detail);
  const names = playerNames(ss.getSheetByName(TABS.players), players, game);
  const date = new Date(game.startedUtc);

  // Resending a game replaces its rows but keeps what referees typed in.
  const kept = removeGame(playerGames, game.id, function (row) {
    return { key: row[PG.key - 1], adj: row[PG.refAdj - 1], note: row[PG.refNote - 1] };
  });
  removeGame(games, game.id);
  removeGame(detail, game.id);
  const refs = {};
  kept.forEach(function (k) { refs[k.key] = k; });

  append(games, [[
    game.id, game.tournament, game.number, date, game.lobby, game.map, game.winner, game.result,
    game.length, game.impostors, game.meetings, game.mvp ? names[keyOf(players, game.mvp)] || game.mvp : '', game.counted,
  ]]);

  append(playerGames, players.map(function (p) {
    const ref = refs[p.key] || {};
    return [
      game.id, game.tournament, game.number, date, game.map, names[p.key], p.key, p.color, p.role, p.team,
      game.counted ? (p.result === 'Won' ? 1 : 0) : '', p.death, p.kills, p.firstBlood ? 1 : 0,
      p.correctVotes, p.wrongVotes, p.skips, p.missed, p.correctVoteOuts, p.wrongVoteOuts,
      p.meetingsCalled, p.bodiesReported, p.tasksDone, p.tasksTotal, p.sabotages,
      p.points, ref.adj === undefined || ref.adj === '' ? 0 : ref.adj, ref.note || '', game.counted,
    ];
  }));

  const lines = [];
  players.forEach(function (p) {
    (p.breakdown || []).forEach(function (l) {
      lines.push([game.id, game.tournament, game.number, names[p.key], p.team, l.rule, l.points]);
    });
  });
  append(detail, lines);
}

/** The name used for each friend code: the first one ever seen, unless a referee renamed it in Players. */
function playerNames(sheet, players, game) {
  const names = {};
  const last = sheet.getLastRow();
  if (last > 1) {
    sheet.getRange(2, 1, last - 1, 2).getValues().forEach(function (r) { names[r[0]] = r[1]; });
  }
  const added = [];
  players.forEach(function (p) {
    if (!(p.key in names)) {
      names[p.key] = p.name;
      added.push([p.key, p.name, game.tournament + ' game ' + game.number]);
    }
  });
  append(sheet, added);
  return names;
}

function keyOf(players, name) {
  const p = players.filter(function (x) { return x.name === name; })[0];
  return p ? p.key : '';
}

/** Deletes every row whose column A is gameId. Returns what `pick` extracted from each. */
function removeGame(sheet, gameId, pick) {
  const picked = [];
  const last = sheet.getLastRow();
  if (last < 2) return picked;
  const width = sheet.getLastColumn();
  const rows = sheet.getRange(2, 1, last - 1, width).getValues();
  // Delete from the bottom up, in contiguous runs, so row numbers stay valid.
  let end = -1;
  for (let i = rows.length - 1; i >= -1; i--) {
    const match = i >= 0 && rows[i][0] === gameId;
    if (match) {
      if (pick) picked.push(pick(rows[i]));
      if (end < 0) end = i;
    } else if (end >= 0) {
      sheet.deleteRows(i + 3, end - i);
      end = -1;
    }
  }
  return picked;
}

function append(sheet, rows) {
  if (rows.length === 0) return;
  sheet.getRange(sheet.getLastRow() + 1, 1, rows.length, rows[0].length).setValues(rows);
}

function reply(obj) {
  return ContentService.createTextOutput(JSON.stringify(obj)).setMimeType(ContentService.MimeType.JSON);
}
