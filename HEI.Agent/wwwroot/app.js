// The review page, laid out like File Explorer: drives, a folder tree, and the duplicates of the
// folder you're in. Plain DOM, no build step. Every string from the agent goes in as text, never HTML.
'use strict';

const token = document.querySelector('meta[name="agent-token"]').content;
const $ = (id) => document.getElementById(id);
const SEP = '\\';
const PAGE = 8;

let state = null;
let route = parseRoute();
let groupsShown = PAGE;
let groupFilter = 'copies';
let sortBy = { key: 'reclaim', desc: true };
const ticks = new Map();      // group key -> Set of ticked paths (the user's edits survive refreshes)
const listings = new Map();   // folder path (lower case, + '|all') -> /api/tree answer
const expanded = new Set();   // lower-case folder paths open in the tree
const showAll = new Set();    // lower-case folder paths showing their empty subfolders too
const exemptOpen = new Set(); // lower-case folder paths whose exempt subfolders are unfolded
const FOLD_EXEMPT = 5;        // more exempt subfolders than this fold into one row
const showKeptHere = new Set(); // lower-case folder paths showing the sets that only keep their original there

const KIND = {
  identical: ['identical', 'Identical copies'],
  copies: ['same', 'Copies of one picture'],
  similar: ['similar', 'Look alike: your call'],
};

// What each file is to the kept one (decided by code, see ReportBuilder.Relation).
const RELATION = {
  identical: 'Identical copy',
  smaller: 'Smaller copy',
  compressed: 'More compressed copy',
  resaved: 'Saved again (same picture)',
  edited: 'Edited version',
  variant: 'Edited, cropped, flipped, or a similar shot',
};

// ---------------------------------------------------------------- icons

const ICONS = {
  back: [['path', 'M10 3 5 8l5 5', 'stroke']],
  fwd: [['path', 'M6 3l5 5-5 5', 'stroke']],
  up: [['path', 'M8 13V3M3.5 7.5 8 3l4.5 4.5', 'stroke']],
  chevron: [['path', 'M6 3l5 5-5 5', 'stroke']],
  sep: [['path', 'M6 4l4 4-4 4', 'stroke']],
  check: [['path', 'M3 8.5 6.5 12 13 4.5', 'stroke']],
  info: [['circle', '8,8,6.5', 'stroke'], ['path', 'M8 7.2V11.5M8 4.6v.2', 'stroke']],
  code: [['path', 'M5.5 4.5 2 8l3.5 3.5M10.5 4.5 14 8l-3.5 3.5', 'stroke']],
  layers: [['path', 'M8 2 2 5l6 3 6-3-6-3zM2 8l6 3 6-3M2 11l6 3 6-3', 'stroke']],
  branch: [['circle', '4.5,3.5,1.5', 'stroke'], ['circle', '4.5,12.5,1.5', 'stroke'], ['circle', '11.5,5,1.5', 'stroke'],
    ['path', 'M4.5 5v6M11.5 6.5c0 3-3.5 3.2-7 4.6', 'stroke']],
  db: [['path', 'M3 4c0-1.1 2.2-2 5-2s5 .9 5 2-2.2 2-5 2-5-.9-5-2zM3 4v8c0 1.1 2.2 2 5 2s5-.9 5-2V4M3 8c0 1.1 2.2 2 5 2s5-.9 5-2', 'stroke']],
  phone: [['rect', '4.5,1.5,7,13,1.6', 'stroke'], ['path', 'M7 12.2h2', 'stroke']],
  clock: [['circle', '8,8,6', 'stroke'], ['path', 'M8 4.8V8l2.3 1.4', 'stroke']],
  gear: [['circle', '8,8,2', 'stroke'],
    ['path', 'M6.9 1.8h2.2l.4 1.8 1.2.7 1.7-.6 1.1 1.9-1.3 1.2v1.4l1.3 1.2-1.1 1.9-1.7-.6-1.2.7-.4 1.8H6.9l-.4-1.8-1.2-.7-1.7.6-1.1-1.9 1.3-1.2V7.3L2.5 6.1l1.1-1.9 1.7.6 1.2-.7z', 'stroke']],
  stack: [['rect', '2,5.5,9,8,1.5', 'stroke'], ['path', 'M5 3.5h7.5a1.5 1.5 0 0 1 1.5 1.5v6', 'stroke']],
  merge: [['circle', '4.5,3.5,1.5', 'stroke'], ['circle', '4.5,12.5,1.5', 'stroke'], ['circle', '11.5,12.5,1.5', 'stroke'],
    ['path', 'M4.5 5v6M4.5 5c0 4.5 3 7.5 5.5 7.5', 'stroke']],
  pull: [['circle', '4.5,3.5,1.5', 'stroke'], ['circle', '4.5,12.5,1.5', 'stroke'], ['circle', '11.5,12.5,1.5', 'stroke'],
    ['path', 'M4.5 5v6M11.5 11V6.5a2 2 0 0 0-2-2H7M8.6 3 7 4.5 8.6 6', 'stroke']],
  pad: [['path', 'M4.8 4.5h6.4a3 3 0 0 1 2.9 3.7l-.6 2.6a1.5 1.5 0 0 1-2.7.6L9.7 10H6.3l-1.1 1.4a1.5 1.5 0 0 1-2.7-.6l-.6-2.6a3 3 0 0 1 2.9-3.7zM5.2 6.4v2.2M4.1 7.5h2.2', 'stroke'],
    ['circle', '10.6,6.9,0.6', 'fill'], ['circle', '11.8,8.1,0.6', 'fill']],
  box: [['path', 'M2.5 5 8 2.5 13.5 5v6L8 13.5 2.5 11zM2.5 5 8 7.5 13.5 5M8 7.5v6', 'stroke']],
  spark: [['path', 'M8 1.8v3M8 11.2v3M1.8 8h3M11.2 8h3M3.6 3.6l2.1 2.1M10.3 10.3l2.1 2.1M3.6 12.4l2.1-2.1M10.3 5.7l2.1-2.1', 'stroke']],
  alert: [['path', 'M8 2 14.5 13.5h-13zM8 6.5v3.2M8 11.6v.2', 'stroke']],
  pause: [['path', 'M6 3.5v9M10 3.5v9', 'stroke']],
  play: [['path', 'M5.5 3.2v9.6l7.5-4.8z', 'stroke']],
  chip: [['rect', '4,4,8,8,1.5', 'stroke'], ['rect', '6.5,6.5,3,3,0.5', 'fill'],
    ['path', 'M6 1.5V4M10 1.5V4M6 12v2.5M10 12v2.5M1.5 6H4M1.5 10H4M12 6h2.5M12 10h2.5', 'stroke']],
  palette: [['path', 'M8 1.8a6.2 6.2 0 1 0 0 12.4c.9 0 1.5-.6 1.5-1.4 0-.9-.8-1.3-.8-2.1 0-.8.6-1.3 1.4-1.3h1.6a2.5 2.5 0 0 0 2.5-2.5C14.2 4.2 11.5 1.8 8 1.8z', 'stroke'],
    ['circle', '5,7.2,1', 'fill'], ['circle', '7.4,4.6,1', 'fill'], ['circle', '10.6,5.3,1', 'fill'], ['circle', '5.3,10.5,1', 'fill']],
};

// The Developer area's mark: code brackets on an accent tile.
function devIcon(cls) {
  return svg('0 0 40 40', [
    ['rect', '4,6,32,28,6', 'fill', 'var(--accent)'],
    ['path', 'M16 14l-6 6 6 6M24 14l6 6-6 6', 'stroke'],
  ], cls);
}

function svg(viewBox, parts, cls) {
  const s = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  s.setAttribute('viewBox', viewBox);
  s.setAttribute('aria-hidden', 'true');
  if (cls) s.setAttribute('class', cls);
  for (const [tag, data, mode, fill] of parts) {
    const e = document.createElementNS('http://www.w3.org/2000/svg', tag);
    if (tag === 'path') e.setAttribute('d', data);
    else if (tag === 'circle') { const [cx, cy, r] = data.split(','); e.setAttribute('cx', cx); e.setAttribute('cy', cy); e.setAttribute('r', r); }
    else if (tag === 'rect') { const [x, y, w, h, rx] = data.split(','); e.setAttribute('x', x); e.setAttribute('y', y); e.setAttribute('width', w); e.setAttribute('height', h); e.setAttribute('rx', rx || 0); }
    if (mode === 'stroke') {
      e.setAttribute('fill', 'none'); e.setAttribute('stroke', 'currentColor');
      e.setAttribute('stroke-width', '1.5'); e.setAttribute('stroke-linecap', 'round'); e.setAttribute('stroke-linejoin', 'round');
    } else {
      // Theme colours are CSS variables, which a fill attribute doesn't take everywhere; the style does.
      if (fill && fill.startsWith('var(')) e.style.fill = fill;
      else e.setAttribute('fill', fill || 'currentColor');
    }
    s.append(e);
  }
  return s;
}

const icon = (name, cls) => svg('0 0 16 16', ICONS[name], cls);

// A Windows 11 style folder; exempt folders are grey with a "no entry" mark.
function folderIcon(exempt, cls) {
  const back = exempt ? 'var(--exempt)' : 'var(--folder-dark)';
  const front = exempt ? 'var(--exempt)' : 'var(--folder)';
  const parts = [
    ['path', 'M2 5.5A1.5 1.5 0 0 1 3.5 4h5.2l2 2H20.5A1.5 1.5 0 0 1 22 7.5V18a1.5 1.5 0 0 1-1.5 1.5h-17A1.5 1.5 0 0 1 2 18z', 'fill', back],
    ['path', 'M2 9a1.5 1.5 0 0 1 1.5-1.5h17A1.5 1.5 0 0 1 22 9v9a1.5 1.5 0 0 1-1.5 1.5h-17A1.5 1.5 0 0 1 2 18z', 'fill', front],
  ];
  if (exempt) {
    parts.push(['circle', '17.5,16.5,4.2', 'fill', 'var(--surface)']);
    parts.push(['path', 'M14.8 19.2l5.4-5.4M17.5 13.3a3.2 3.2 0 1 1 0 6.4 3.2 3.2 0 0 1 0-6.4z', 'stroke']);
  }
  const s = svg('0 0 24 24', parts, cls);
  if (exempt) s.style.color = 'var(--muted)';
  return s;
}

function driveIcon(type, cls) {
  if (type === 'folder') return folderIcon(false, cls);
  const body = type === 'removable' ? 'var(--muted)' : 'var(--faint)';
  return svg('0 0 40 40', [
    ['rect', '4,13,32,16,3', 'fill', body],
    ['rect', '4,13,32,11,3', 'fill', 'var(--line)'],
    ['rect', '8,25,6,2,1', 'fill', type === 'network' ? 'var(--accent)' : 'var(--ok)'],
    ['rect', '26,25,6,2,1', 'fill', 'var(--surface)'],
  ], cls);
}

function pcIcon(cls) {
  return svg('0 0 16 16', [
    ['rect', '1.5,2.5,13,9,1.2', 'stroke'],
    ['path', 'M5.5 14h5M8 11.5V14', 'stroke'],
  ], cls);
}

// ---------------------------------------------------------------- helpers

function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined && text !== null) e.textContent = String(text);
  return e;
}

function bytes(b) {
  if (b >= 2 ** 40) return (b / 2 ** 40).toFixed(1) + ' TB';
  if (b >= 2 ** 30) return (b / 2 ** 30).toFixed(1) + ' GB';
  if (b >= 2 ** 20) return (b / 2 ** 20).toFixed(1) + ' MB';
  if (b >= 1024) return Math.round(b / 1024) + ' KB';
  return b + ' bytes';
}

const count = (n, one, many) => n.toLocaleString() + ' ' + (n === 1 ? one : many);

function ago(iso) {
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 90) return 'just now';
  if (s < 5400) return Math.round(s / 60) + ' min ago';
  if (s < 129600) return Math.round(s / 3600) + ' h ago';
  return Math.round(s / 86400) + ' days ago';
}

function took(sec) {
  if (sec < 1) return 'under a second';
  if (sec < 60) return Math.round(sec) + ' s';
  const m = Math.floor(sec / 60), s = Math.round(sec % 60);
  if (m < 60) return m + ' min' + (s ? ' ' + s + ' s' : '');
  return Math.floor(m / 60) + ' h ' + (m % 60) + ' min';
}

function duration(sec) {
  const m = Math.floor(sec / 60), s = Math.round(sec % 60);
  return m + ':' + String(s).padStart(2, '0');
}

const key = (p) => trimSep(p).toLowerCase();
function trimSep(p) { return p.length > 3 && p.endsWith(SEP) ? p.slice(0, -1) : p; }
function isUnder(file, folder) {
  const f = trimSep(folder);
  return file.toLowerCase().startsWith((f.endsWith(SEP) ? f : f + SEP).toLowerCase());
}
function sameFolder(a, b) { return key(a) === key(b); }

/** The drive (or extra scanned folder) a path lives on: the card whose root is its longest prefix. */
function cardFor(path) {
  if (!state) return null;
  let best = null;
  for (const d of state.drives) {
    if ((sameFolder(d.root, path) || isUnder(path, d.root)) && (!best || d.root.length > best.root.length)) best = d;
  }
  return best;
}

function parentOf(path) {
  const d = cardFor(path);
  if (!d || sameFolder(d.root, path)) return null;
  const t = trimSep(path);
  const parent = t.slice(0, t.lastIndexOf(SEP));
  return parent.length <= trimSep(d.root).length ? d.root : parent;
}

function nameOf(path) {
  const d = cardFor(path);
  if (d && sameFolder(d.root, path)) return d.name;
  const t = trimSep(path);
  return t.slice(t.lastIndexOf(SEP) + 1);
}

async function post(url, body) {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Agent-Token': token },
    body: body ? JSON.stringify(body) : '{}',
  });
  let data = null;
  try { data = await res.json(); } catch { /* empty body */ }
  if (!res.ok) {
    const err = new Error((data && data.error) || res.status + ' ' + res.statusText);
    err.data = data;
    throw err;
  }
  return data;
}

function showError(msg) {
  $('error').textContent = msg;
  $('error').classList.toggle('hidden', !msg);
}

let noticeTimer = null;
/** A short green line under the command bar after an action, with an optional button; it goes after a while. */
function showNotice(msg, action) {
  const n = $('notice');
  clearTimeout(noticeTimer);
  n.replaceChildren(el('span', null, msg));
  if (action) {
    const b = el('button', 'btn secondary', action.label);
    b.addEventListener('click', () => { n.classList.add('hidden'); action.run(); });
    n.append(b);
  }
  n.classList.remove('hidden');
  noticeTimer = setTimeout(() => n.classList.add('hidden'), 12000);
}

/** One action on a whole folder: its sets share this id, so History lists them as one row. */
function batchId() {
  return [...crypto.getRandomValues(new Uint8Array(8))].map((b) => b.toString(16).padStart(2, '0')).join('');
}

/** The title bar's button: Scan now, or Stop scan while one runs. */
async function scanNow() {
  const running = state && state.scan.running;
  try { await post(running ? '/api/scan/stop' : '/api/scan'); refresh(true); } catch (e) { showError(e.message); }
}

// ---------------------------------------------------------------- the agent: pause, resume, start

// Whether Heiward scans on its own. Paused (scheduled scans skip themselves for a while), its scan task
// gone or disabled in Task Scheduler, or Heiward not running at all (this page then can't reach it): a
// banner says so, with the button that fixes it.
async function pauseScans(minutes) {
  try { await post('/api/agent/pause', { minutes }); refresh(true); } catch (e) { showError(e.message); }
}

async function resumeScans() {
  try { await post('/api/agent/resume'); refresh(true); } catch (e) { showError(e.message); }
}

async function startSchedule() {
  try { await post('/api/agent/schedule'); refresh(true); } catch (e) { showError(e.message); }
}

/** One banner at a time; rebuilt only when it says something else, so a poll doesn't steal the focus. */
function showAgentBanner(text, action, down) {
  const banner = $('agent-banner');
  banner.classList.toggle('hidden', !text);
  banner.classList.toggle('down', !!down);
  const key = text ? text + '|' + (action ? action[0] : '') : '';
  if (banner.dataset.key === key) return;
  banner.dataset.key = key;
  if (!text) { banner.replaceChildren(); return; }
  const parts = [el('span', null, text)];
  if (action) {
    const [label, run] = action;
    const b = typeof run === 'string' ? el('a', 'btn', label) : el('button', 'btn', label);
    if (typeof run === 'string') b.href = run;
    else b.addEventListener('click', run);
    parts.push(b);
  }
  banner.replaceChildren(...parts);
}

function renderAgent(s) {
  const a = s.agent;
  const btn = $('pause-btn');
  const label = a.paused ? 'Resume scans' : 'Pause scans';
  btn.replaceChildren(icon(a.paused ? 'play' : 'pause'));
  btn.title = a.paused ? 'Resume scans (paused ' + a.pausedText + ')' : label;
  btn.setAttribute('aria-label', label);
  btn.setAttribute('aria-haspopup', a.paused ? 'false' : 'menu');
  btn.setAttribute('aria-pressed', String(a.paused));
  btn.disabled = s.setup.needed;
  if (s.setup.needed) showAgentBanner(null);
  else if (a.paused) {
    showAgentBanner('Scans are paused ' + a.pausedText + ': scheduled scans skip themselves. Scan now still works.', ['Resume scans', resumeScans]);
  } else if (a.scheduleMissing) {
    showAgentBanner('Scheduled scans aren\'t running: Heiward\'s scan task is missing, or turned off in Task Scheduler.', ['Turn them back on', startSchedule]);
  } else if (a.dev) {
    showAgentBanner('A development build: it doesn\'t scan on a schedule, and keeps apart from the installed Heiward. Scan now works.');
  } else showAgentBanner(null);
  renderSettling(a.firstRound);
}

/** The manor's door, footsteps walking up to it: the settling-in drawing, as the Steward's kit draws it. */
const SETTLING_SVG = '<svg class="settling-mark" viewBox="0 0 44 28" width="44" height="28" aria-hidden="true"><path class="st-house" d="M24 26V11l9-7 9 7v15z"/><path class="st-door" d="M30 26v-7.5a3 3 0 0 1 6 0V26"/><circle class="st-step st-s1" cx="4" cy="23.5" r="1.4"/><circle class="st-step st-s2" cx="10" cy="21.5" r="1.4"/><circle class="st-step st-s3" cx="16" cy="23.5" r="1.4"/><circle class="st-step st-s4" cx="22" cy="21.5" r="1.4"/><path class="st-ground" d="M1 26.5h42"/></svg>';

/**
 * Settling into the manor: while its first scan waits its turn among the agents' first rounds, or runs (FirstRound),
 * the words the Steward's kit gives every agent (settlingText), Heiward's first round being a scan of every drive.
 */
function renderSettling(first) {
  const banner = $('settling-banner');
  const text = first === 'waiting'
    ? 'Heiward is new to the manor and waits its turn: agents settling in do their first rounds one at a time, so this PC is never swamped. It starts as soon as the one before it has finished.'
    : first === 'running'
      ? "Heiward's first scan is under way: scanning every drive for the first time, at full speed while it has its turn. It takes longer than the scans after it, which only look at what changed."
      : null;
  banner.classList.toggle('hidden', !text);
  if (banner.dataset.key === (text || '')) return;
  banner.dataset.key = text || '';
  if (!text) { banner.replaceChildren(); return; }
  banner.innerHTML = SETTLING_SVG;
  const words = el('span');
  words.append(el('strong', null, 'Settling into the manor'), ': ' + text);
  banner.append(words);
}

/** The pause button's menu: how long to pause for. While paused, the button resumes at once. */
function setupPauseMenu() {
  const btn = $('pause-btn');
  const menu = $('pause-menu');
  const show = (open) => {
    menu.classList.toggle('hidden', !open);
    btn.setAttribute('aria-expanded', String(open));
    if (open) menu.querySelector('.theme-item')?.focus();
  };
  const tomorrowMorning = () => {
    const at = new Date();
    at.setDate(at.getDate() + 1);
    at.setHours(8, 0, 0, 0);
    return Math.round((at - new Date()) / 60000);
  };
  menu.append(el('div', 'menu-label', 'Pause scans, and stop the one running'));
  for (const [label, minutes] of [['For 1 hour', () => 60], ['For 4 hours', () => 240], ['Until tomorrow morning', tomorrowMorning], ['Until I resume', () => null]]) {
    const item = el('button', 'theme-item');
    item.type = 'button';
    item.setAttribute('role', 'menuitem');
    item.append(el('span', 'ti-label', label));
    item.addEventListener('click', () => { show(false); pauseScans(minutes()); });
    menu.append(item);
  }
  btn.append(icon('pause'));
  btn.addEventListener('click', () => { if (state && state.agent.paused) resumeScans(); else show(menu.classList.contains('hidden')); });
  document.addEventListener('click', (e) => {
    if (!menu.classList.contains('hidden') && !menu.contains(e.target) && !btn.contains(e.target)) show(false);
  });
  menu.addEventListener('keydown', (e) => {
    const items = [...menu.querySelectorAll('.theme-item')];
    const at = items.indexOf(document.activeElement);
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      items[(at + (e.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length].focus();
    } else if (e.key === 'Escape') {
      show(false);
      btn.focus();
    } else if (e.key === 'Tab') show(false);
  });
}

function ticked(g) {
  if (!ticks.has(g.key)) ticks.set(g.key, new Set(g.items.filter((i) => i.suggested).map((i) => i.path)));
  return ticks.get(g.key);
}

// ---------------------------------------------------------------- routing

function parseRoute() {
  const h = location.hash;
  if (h === '#/settings') return { view: 'settings' };
  if (h === '#/dev') return { view: 'dev', cat: null };
  if (h.startsWith('#/dev/g/')) return { view: 'dev', group: decodeURIComponent(h.slice(8)) };
  if (h.startsWith('#/dev/s/')) return { view: 'dev', cat: decodeURIComponent(h.slice(8)) };
  // The Games area: the overview, a kind of thing games leave (k), a drive (d), a game (g).
  if (h === '#/games') return { view: 'games' };
  if (h.startsWith('#/games/k/')) return { view: 'games', cat: decodeURIComponent(h.slice(10)) };
  if (h.startsWith('#/games/d/')) return { view: 'games', drive: decodeURIComponent(h.slice(10)) };
  if (h.startsWith('#/games/g/')) return { view: 'games', game: decodeURIComponent(h.slice(10)) };
  if (h.startsWith('#/f/')) {
    try { return { view: 'folder', path: decodeURIComponent(h.slice(4)) }; } catch { /* fall through */ }
  }
  return { view: 'home' };
}

function go(path) {
  const next = path ? '#/f/' + encodeURIComponent(path) : '#/';
  if (location.hash === next) renderRoute();
  else location.hash = next;
}

window.addEventListener('hashchange', () => {
  route = parseRoute();
  groupsShown = PAGE;
  if (route.view === 'folder') expanded.add(key(route.path)); // opening a folder opens it in the tree too
  renderRoute();
  $('content').scrollTop = 0;
});

// ---------------------------------------------------------------- header and command bar

// Where AI matching runs, from ai-status.json: the install and "hei setup" write it, then every scan,
// with the device it actually used. Green only when the NPU is set up and (after a scan) really ran. The commands, the
// settings file and the log are named only with developer mode on (dev); otherwise it says the same in plain words.
function renderAiBadge(ai, dev) {
  const badge = $('ai-badge');
  if (!ai) { badge.classList.add('hidden'); return; }
  const npu = ai.npuDisplayName || ai.npuName || 'The NPU';
  let text, tone, tip;
  if (ai.device === 'NPU') {
    tone = 'good';
    text = ai.source === 'scan' ? 'Running on the NPU' : 'NPU ready';
    tip = ai.source === 'scan' ? npu + ' ran the last scan.' : npu + ' is set up; scans run on it.';
  } else if (ai.device === 'off') {
    tone = 'warn';
    text = 'AI matching off';
    tip = dev ? 'The AI components are not installed. Run "hei setup".' : 'The AI components aren\'t installed, so AI matching is off. Reinstalling Heiward adds them.';
  } else {
    const on = ai.device;
    if (ai.npuVendor === 'None') {
      tone = 'quiet'; text = 'No NPU available · ' + on;
      tip = 'This PC has no NPU, so AI matching runs on the ' + on + '.';
    } else if (!ai.npuSupported) {
      tone = 'warn'; text = 'Unsupported NPU · ' + on;
      tip = (ai.npuName || 'This NPU') + ' isn\'t supported by this version yet, so AI matching runs on the ' + on + '.';
    } else if (!ai.npuInstalled) {
      tone = 'warn'; text = 'NPU not set up · ' + on;
      tip = dev ? 'The ' + npu + ' pack is not installed. Run "hei setup" to download it.'
        : 'What the ' + npu + ' needs isn\'t installed, so AI matching runs on the ' + on + '. Reinstalling Heiward adds it.';
    } else if (ai.setting === 'gpu' || ai.setting === 'cpu') {
      tone = 'quiet'; text = 'On the ' + on;
      tip = dev ? 'settings.json asks for the ' + ai.setting.toUpperCase() + ' (aiDevice), so the ' + npu + ' is not used.'
        : 'Heiward\'s settings ask for the ' + (ai.setting === 'gpu' ? 'graphics card' : 'processor') + ', so the ' + npu + ' is not used.';
    } else {
      tone = 'warn'; text = 'NPU fell back · ' + on;
      tip = 'The ' + npu + ' is set up, but it could not run AI matching, so it ran on the ' + on + '.' + (dev ? ' heiward.log has the reason.' : '');
    }
    if (ai.card) tip += ' The graphics card: ' + ai.card + '.';
  }
  // Why the last run didn't run where it was meant to (ai-status.json's fallback, also in heiward.log): technical, so only for developers.
  if (ai.fallback && dev) tip += ' It fell back: ' + ai.fallback + '.';
  badge.textContent = text;
  badge.title = tip;
  badge.className = 'ai-badge ' + tone;
}

/**
 * When the next scan is due by the last one and the interval ("in 20 min"), as the page starting checks
 * before it scans; null when scans run only when asked or there's been none.
 */
function scanDueIn(schedule) {
  if (!schedule || !schedule.dueUtc) return null;
  const min = Math.round((Date.parse(schedule.dueUtc) - Date.now()) / 60000);
  return min <= 1 ? 'now' : min < 90 ? 'in ' + min + ' min' : 'in about ' + Math.round(min / 60) + ' hours';
}

function renderHeader(s) {
  const r = s.report;
  const parts = [];
  if (s.scan.running) parts.push('Scanning now');
  if (r) {
    parts.push((s.scan.running ? 'last finished ' : 'Last scan ') + ago(r.scannedAtUtc));
    parts.push(r.device === 'off' ? 'AI matching off' : 'AI on the ' + r.device);
  } else {
    // A new build set the last report aside: its sets were judged by the old rules. The next due scan
    // (or Scan now) finds them again; opening or reloading the page doesn't start one before it's due.
    parts.push(!s.updated ? 'No scan yet' : s.scan.running ? 'Heiward was updated: finding the sets again with this version'
      : 'Heiward was updated: the next scan finds the sets again');
  }
  if (s.agent.paused) parts.push('scans paused ' + s.agent.pausedText);
  else if (s.scan.running) { /* the next is counted from this one's end */ }
  else if (s.schedule.next) parts.push('next ' + s.schedule.next);
  else if (s.schedule.everyMinutes === 0) parts.push('scans when you press Scan now');
  else if (scanDueIn(s.schedule)) parts.push('next scan ' + scanDueIn(s.schedule));
  $('subtitle').textContent = parts.join(' · ');
  renderAiBadge(s.ai, s.dev.enabled);

  const running = s.scan.running;
  // Just started: running from the click, though it can't be stopped until its process has begun.
  const starting = !!s.scan.starting;
  const scanBtn = $('scan-now');
  scanBtn.disabled = s.setup.needed || s.agent.stopping || starting;
  scanBtn.textContent = starting ? 'Starting…' : running ? (s.agent.stopping ? 'Stopping…' : 'Stop scan') : 'Scan now';
  scanBtn.classList.toggle('secondary', running);
  renderAgent(s);
  // The scan's step and progress at the top of the page; each drive's card shows how far it has got on it.
  renderScanStatus(s);
  renderDriveProgress(s);
  $('notes').replaceChildren(...((r && r.notes) || []).map((n) => el('li', null, n)));
}

function renderCrumbs() {
  const crumbs = $('crumbs');
  const items = [];
  const crumb = (label, glyph, path) => {
    const li = el('li');
    if (items.length) li.append(icon('sep', 'sep'));
    const b = el('button', 'crumb');
    b.title = path || 'This PC';
    if (glyph) b.append(glyph);
    b.append(el('span', null, label));
    b.addEventListener('click', () => go(path));
    li.append(b);
    items.push(li);
  };
  crumb('This PC', pcIcon(), null);
  if (route.view === 'settings') {
    const li = el('li');
    li.append(icon('sep', 'sep'));
    const b = el('button', 'crumb');
    b.append(icon('gear'), el('span', null, 'Settings'));
    b.addEventListener('click', () => { location.hash = '#/settings'; });
    li.append(b);
    items.push(li);
  }
  if (route.view === 'dev') {
    const li = el('li');
    li.append(icon('sep', 'sep'));
    const b = el('button', 'crumb');
    b.append(icon('code'), el('span', null, 'Developer area'));
    b.addEventListener('click', () => { location.hash = '#/dev'; });
    li.append(b);
    items.push(li);
    const g = route.group ? devGroupById(route.group) : null;
    const label = g ? g.name : route.cat && DEV_META[route.cat] ? DEV_META[route.cat].short : null;
    if (label) {
      const li2 = el('li');
      li2.append(icon('sep', 'sep'));
      const c = el('button', 'crumb');
      c.append(g ? (g.project ? icon('stack') : folderIcon(false)) : icon(DEV_META[route.cat].icon), el('span', null, label));
      c.addEventListener('click', () => { location.hash = g ? '#/dev/g/' + encodeURIComponent(g.id) : '#/dev/s/' + route.cat; });
      li2.append(c);
      items.push(li2);
    }
  }
  if (route.view === 'games') {
    const li = el('li');
    li.append(icon('sep', 'sep'));
    const b = el('button', 'crumb');
    b.append(icon('pad'), el('span', null, 'Games'));
    b.addEventListener('click', () => { location.hash = '#/games'; });
    li.append(b);
    items.push(li);
    const game = route.game ? gameById(route.game) : null;
    const label = game ? game.name : route.cat && GAME_META[route.cat] ? GAME_META[route.cat].short : route.drive || null;
    if (label) {
      const li2 = el('li');
      li2.append(icon('sep', 'sep'));
      const c = el('button', 'crumb');
      c.append(game ? icon('pad') : route.cat ? icon(GAME_META[route.cat].icon) : driveIcon('fixed'), el('span', null, label));
      const hash = game ? '#/games/g/' + encodeURIComponent(game.id) : route.cat ? '#/games/k/' + route.cat : '#/games/d/' + encodeURIComponent(route.drive);
      c.addEventListener('click', () => { if (location.hash === hash) renderRoute(); else location.hash = hash; });
      li2.append(c);
      items.push(li2);
    }
  }
  if (route.view === 'folder') {
    const d = cardFor(route.path);
    if (d) {
      crumb(d.name, driveIcon(d.type), d.root);
      const rel = trimSep(route.path).slice(trimSep(d.root).length).split(SEP).filter(Boolean);
      let p = trimSep(d.root);
      for (const part of rel) {
        p = p.endsWith(SEP) ? p + part : p + SEP + part;
        crumb(part, null, p);
      }
    }
  }
  crumbs.replaceChildren(...items);
  crumbs.scrollLeft = crumbs.scrollWidth;
  $('nav-up').disabled = route.view === 'home';
}

// ---------------------------------------------------------------- home: This PC

function renderHome(s) {
  $('t-groups').textContent = s.totals.groups - s.totals.similar;
  const autoCopies = s.auto.settings.duplicates ? s.auto.upcoming.groups.count : 0;
  $('t-groups-label').textContent = autoCopies ? 'sets of copies · ' + autoCopies + (autoCopies === 1 ? ' goes' : ' go') + ' automatically' : 'sets of copies to review';
  $('t-free').textContent = bytes(s.totals.reclaimableBytes);
  $('t-freed').textContent = bytes(s.totals.recycledBytes);

  renderGlance(s.glance);
  const drives = $('drives');
  drives.replaceChildren(...s.drives.map(driveCard));
  if (!s.drives.length) drives.append(el('p', 'muted', 'No drives found.'));

  const spots = s.hotspots;
  $('hotspots-section').classList.toggle('hidden', !spots.length);
  const max = Math.max(1, ...spots.map((h) => h.reclaim));
  $('hotspots').replaceChildren(...spots.map((h) => {
    const row = el('button', 'hotspot');
    row.append(folderIcon(false));
    const where = el('div', 'where');
    const name = el('div', null, h.folder.slice(h.folder.lastIndexOf(SEP) + 1));
    name.style.fontWeight = '600';
    where.append(name);
    const path = el('div', 'muted small', h.folder);
    where.append(path);
    row.append(where);
    const amount = el('div', 'amount');
    amount.append(el('div', null, bytes(h.reclaim)), el('div', 'muted small', count(h.copies, 'set of copies', 'sets of copies')));
    row.append(amount);
    const meter = el('div', 'meter');
    const fill = el('div', 'fill');
    fill.style.width = Math.max(2, (100 * h.reclaim) / max) + '%';
    meter.append(fill);
    row.append(meter);
    row.title = h.folder;
    row.addEventListener('click', () => go(h.folder));
    row.addEventListener('contextmenu', (e) => openFolderMenu(e, { name: h.folder.slice(h.folder.lastIndexOf(SEP) + 1), path: h.folder }));
    return row;
  }));

  renderDevCard(s.dev);
  renderGamesCard(s.games);
  renderDone(s);
  renderManorCard(s.manorCard);
  renderFooter(s);
}

// ---------------------------------------------------------------- home: at a glance

/** A share as a percentage that never rounds a real part away: "<1%" rather than "0%". */
function share(part, whole) {
  if (!whole || part <= 0) return '0%';
  const p = (100 * part) / whole;
  return p < 1 ? '<1%' : p >= 99.5 && part < whole ? '>99%' : Math.round(p) + '%';
}

const GLANCE_TIPS = {
  videos: 'Videos the last scan found, in the folders it scans (not Windows, programs, games, app data or code).',
  photos: 'Photos the last scan found, in the folders it scans (not Windows, programs, games, app data or code).',
  developer: 'What development tools recreate: build outputs, worktrees, package caches, emulators (Developer area).',
  games: 'Your installed games, and what they and their launchers leave behind (Games area).',
  bin: 'Files in the Recycle Bin take their space until it\'s emptied, the copies Heiward recycled too.',
  other: 'Windows, apps, games, documents and everything else Heiward doesn\'t sort.',
};

const kindClass = (kind) => (kind === 'video' ? 'videos' : kind === 'photo' ? 'photos' : 'other');

/**
 * Every drive of this PC in one card, above the drive cards: how big, how full, what the used space is
 * (photos, videos, developer files, the Recycle Bin, everything else), the photos and videos by type,
 * the drives nearly full, and what could be freed now. Network drives have their cards but aren't counted.
 */
function renderGlance(g) {
  const box = $('glance');
  box.classList.toggle('hidden', !g || !g.drives);
  if (!g || !g.drives) { box.replaceChildren(); return; }

  const head = el('div', 'glance-head');
  head.append(el('div', 'glance-title', 'At a glance'), el('div', 'muted small', count(g.drives, 'drive', 'drives') + ' on this PC' +
    (g.networkDrives ? ' · ' + count(g.networkDrives, 'network drive', 'network drives') + ' not counted' : '')));

  const stats = el('div', 'glance-stats');
  const stat = (value, label, cls) => {
    const s = el('div', 'glance-stat' + (cls ? ' ' + cls : ''));
    s.append(el('div', 'glance-value', value), el('div', 'muted small', label));
    return s;
  };
  stats.append(
    stat(bytes(g.totalBytes), 'total space'),
    stat(bytes(g.usedBytes), share(g.usedBytes, g.totalBytes) + ' used', g.usedBytes / g.totalBytes >= 0.9 ? 'low' : ''),
    stat(bytes(g.freeBytes), share(g.freeBytes, g.totalBytes) + ' free'),
    stat(g.reclaim.total ? bytes(g.reclaim.total) : '–', 'you could free', g.reclaim.total ? 'good' : ''));

  // The capacity as one bar: each kind of used space in its colour, the rest free.
  const bar = el('div', 'glance-bar');
  bar.setAttribute('role', 'img');
  bar.setAttribute('aria-label', g.kinds.map((k) => k.label + ' ' + bytes(k.bytes)).join(', ') + ', free ' + bytes(g.freeBytes));
  const legend = el('div', 'glance-legend');
  const item = (key, label, amount, pct, tip) => {
    const it = el('div', 'glance-item');
    it.title = tip || '';
    const text = el('span', null, label + ' ');
    text.append(el('b', null, bytes(amount)));
    it.append(el('span', 'swatch k-' + key), text, el('span', 'muted', pct));
    return it;
  };
  for (const k of g.kinds) {
    if (k.bytes <= 0) continue;
    const seg = el('div', 'seg k-' + k.key);
    seg.style.width = (100 * k.bytes) / g.totalBytes + '%';
    seg.title = k.label + ': ' + bytes(k.bytes) + (k.files ? ', ' + count(k.files, 'file', 'files') : '');
    bar.append(seg);
    legend.append(item(k.key, k.label, k.bytes, share(k.bytes, g.usedBytes), GLANCE_TIPS[k.key]));
  }
  legend.append(item('free', 'Free', g.freeBytes, share(g.freeBytes, g.totalBytes), 'Free space on these drives.'));
  const note = el('div', 'muted small', 'Each kind\'s share is of the used space; free is of the total.');

  // The photos and videos, by file type.
  const types = el('div', 'glance-types');
  types.append(el('div', 'glance-sub', 'Photos and videos by type'));
  const media = g.types.reduce((n, t) => n + t.bytes, 0) + (g.otherTypes ? g.otherTypes.bytes : 0);
  if (!g.typed) {
    types.append(el('div', 'muted small', g.scannedAtUtc ? 'After the next scan: the last one was made before Heiward sorted files by type.' : 'After the first scan.'));
  } else if (!media) {
    types.append(el('div', 'muted small', 'The last scan found no photos or videos on these drives.'));
  } else {
    const rows = el('div', 'type-rows');
    for (const t of g.otherTypes ? [...g.types, g.otherTypes] : g.types) {
      const mixed = t.kind === 'mixed';
      const row = el('div', 'type-row');
      row.title = (mixed ? t.type + ' types: ' : '') + count(t.files, 'file', 'files') + ', ' + bytes(t.bytes) + (mixed ? '' : ' (' + kindClass(t.kind) + ')');
      const meter = el('span', 'type-meter');
      const fill = el('span', 'fill k-' + (mixed ? 'other' : kindClass(t.kind)));
      fill.style.width = Math.max(1.5, (100 * t.bytes) / media) + '%';
      meter.append(fill);
      row.append(el('span', 'type-name' + (mixed ? ' muted' : ''), mixed ? 'Others' : t.type.toUpperCase() || '(none)'),
        meter, el('span', 'type-pct', share(t.bytes, media)), el('span', 'muted small type-bytes', bytes(t.bytes)));
      rows.append(row);
    }
    types.append(rows);
  }

  // What needs a look: drives nearly full, and what can be freed now.
  const notes = el('div', 'glance-notes');
  notes.append(el('div', 'glance-sub', 'Worth knowing'));
  const list = el('ul', 'glance-list');
  for (const d of g.low) {
    const li = el('li', 'warn-line');
    li.append(el('b', null, d.name), document.createTextNode(' is ' + Math.round(100 * d.usedShare) + '% full: ' + bytes(d.freeBytes) + ' left.'));
    list.append(li);
  }
  const r = g.reclaim;
  if (r.duplicates) list.append(el('li', null, bytes(r.duplicates) + ' in copies to review (Where the duplicates are, below).'));
  if (r.developer) list.append(el('li', null, bytes(r.developer) + ' of developer files ticked to clean (Developer area).'));
  if (r.games) list.append(el('li', null, bytes(r.games) + ' that games left behind, ticked to remove (Games area).'));
  if (r.recycleBin) list.append(el('li', null, bytes(r.recycleBin) + ' in the Recycle Bin: empty it to get that space back.'));
  if (!list.childElementCount) list.append(el('li', 'muted', 'No drive is nearly full, and there\'s nothing to free right now.'));
  notes.append(list);

  const lower = el('div', 'glance-lower');
  lower.append(types, notes);
  box.replaceChildren(head, stats, bar, legend, note, lower);
}

// ---------------------------------------------------------------- settings

// Every switch in one place: how hard scans work, automatic cleanup, the history, developer mode. The
// rest of settings.json (folders, file types, the AI device) is listed with where to change it.
function renderSettings(s) {
  renderScanCard(s);
  renderWhereCard(s);
  renderAutoCard(s);
  renderHistoryCard(s);
  renderGameModeCard(s);
  renderDevModeCard(s);
  renderMoreCard(s);
  renderAboutCard(s);
}

const outLink = (text, href) => {
  const a = el('a', null, text);
  a.href = href;
  a.target = '_blank';
  a.rel = 'noopener noreferrer';
  return a;
};

/** This build of Heiward and where it came from, and the licence's "appropriate legal notices". */
function renderAboutCard(s) {
  const [version, commit] = s.about.build.split('+');
  const card = el('div', 'auto-card about');
  const row = el('div', 'auto-row');
  const text = el('div', 'auto-text');
  text.append(el('div', 'auto-title', 'Version ' + version));
  // The commit it was built from is for developers: only with developer mode on.
  if (s.dev.enabled) {
    const built = el('div', 'muted small', commit ? 'Built from commit ' : 'Built from an unknown commit.');
    if (commit) built.append(outLink(commit.slice(0, 7), 'https://github.com/Jcollier0120/Heiward/commit/' + commit), '.');
    text.append(built);
  }
  const from = el('div', 'muted small');
  if (s.about.store) from.append('From the Microsoft Store, which keeps it up to date.');
  else if (s.about.dev) from.append('A development build, kept apart from the installed Heiward.');
  else from.append('From GitHub: new versions are on its ', outLink('releases page', 'https://github.com/Jcollier0120/Heiward/releases'), '.');
  text.append(from);
  row.append(text);
  card.append(row);
  // Without Manor: the home page's "In a manor" card, on until its Not now; this brings it back.
  if (s.manorCard) {
    const manorRow = el('div', 'auto-row');
    manorRow.append(toggleSwitch(s.manorCard.show, 'The "In a manor" card', settingsBusy, (on) => saveSettings({ manorCard: on })));
    const manorText = el('div', 'auto-text');
    manorText.append(el('div', 'auto-title', 'The "In a manor" card'),
      el('div', 'muted small', 'Last on the home page: what Manor adds to Heiward, with a link to its site. It\'s text in the page: nothing is sent anywhere for it.'));
    manorRow.append(manorText);
    card.append(manorRow);
  }
  // The licence notice the AGPL asks for, with the copyright of the code Heiward includes.
  const foot = el('div', 'auto-foot');
  const licence = el('div', 'muted small', 'Heiward is free software: you can share and change it under the ');
  licence.append(outLink('GNU AGPL v3', 'https://www.gnu.org/licenses/agpl-3.0.html'),
    '. It comes with no warranty. Its ', outLink('source code', 'https://github.com/Jcollier0120/Heiward'), ' is on GitHub. It includes code from ',
    outLink('Video Duplicate Finder', 'https://github.com/0x90d/videoduplicatefinder'),
    ' (© 0x90d and contributors), under the same licence.');
  foot.append(licence);
  card.append(foot);
  $('about-card').replaceChildren(card);
}

/**
 * Developer mode: the daily developer check, the Developer area (from the home page), and its automatic cleanup, or none of it.
 * With Manor installed and its Developer options set, they decide, for every agent in the manor: the card says which way, with a
 * link to change it in Manor, in place of the switch, as the Theme menu does for Manor's theme.
 */
function renderDevModeCard(s) {
  const on = s.dev.enabled;
  const manor = s.dev.manor;
  // Under Manor with its Developer options off, nothing of developer mode shows: no switch, and no pitch to developers.
  const hidden = !!manor && !on;
  $('devmode-section').classList.toggle('hidden', hidden);
  if (hidden) { $('devmode-card').replaceChildren(); return; }
  const card = el('div', 'auto-card');
  const row = el('div', 'auto-row');
  if (!manor) row.append(toggleSwitch(on, 'Developer mode', settingsBusy, (next) => saveSettings({ developerMode: next })));
  // Where Reeve keeps git state, worktrees are only measured here, and merged branches are its.
  const what = reeveRole() ? 'build outputs of projects you\'ve left, package caches, emulator images, old temp files and crash dumps, and the space worktrees take'
    : 'build outputs and worktrees of projects you\'ve left, package caches, emulator images, old temp files and crash dumps, and branches already merged';
  const text = el('div', 'auto-text');
  text.append(el('div', 'auto-title', 'Developer mode'));
  if (manor) {
    const note = el('div', 'manor-decides small', manor.note + '. ');
    const link = el('a', null, `Change it in ${manor.name}`);
    link.href = manor.url;
    note.append(link);
    text.append(note);
  }
  text.append(el('div', 'muted small', on
    ? 'Once a day Heiward also looks for what development tools recreate: ' + what + '. The Developer area, from the home page, shows them' +
      (stewardRole() ? '' : ' beside your repositories\' open pull requests') + ', and automatic cleanup can take what it ticks.'
    : 'Off: nothing of it is checked or shown. For developers: a Developer area with your repositories\' open pull requests, and what development tools recreate: ' + what + '.'));
  // At a manor, who looks after the rest.
  if (on) for (const role of [reeveRole(), stewardRole()]) if (role) text.append(roleNote(role));
  row.append(text);
  card.append(row);
  $('devmode-card').replaceChildren(card);
}

/**
 * Game mode: the daily look at the games, the Games area (from the home page), and its automatic cleanup, or none of it. Heiward's
 * own switch; once Manor passes it down (its settings' "gameMode"), the card says which way, with a link, in place of the switch.
 */
function renderGameModeCard(s) {
  const on = s.games.enabled;
  const manor = s.games.manor;
  const card = el('div', 'auto-card');
  const row = el('div', 'auto-row');
  if (!manor) row.append(toggleSwitch(on, 'Game mode', settingsBusy, (next) => saveSettings({ gameMode: next })));
  const text = el('div', 'auto-text');
  text.append(el('div', 'auto-title', 'Game mode'));
  if (manor) {
    const note = el('div', 'manor-decides small', manor.note + '. ');
    const link = el('a', null, `Change it in ${manor.name}`);
    link.href = manor.url;
    note.append(link);
    text.append(note);
  }
  text.append(el('div', 'muted small', on
    ? 'Once a day Heiward also looks at your games: what each launcher has installed and where, leftovers of games you\'ve uninstalled, ' +
      'launchers\' download caches, shader caches, crash dumps, a game installed twice, and games you haven\'t played in months. ' +
      'The Games area, from the home page, shows them. What you remove goes to the Recycle Bin; your games\' own files and your saves are never touched.'
    : 'Off: nothing of it is checked or shown. For gamers: a Games area with your games by launcher and drive, and what they leave behind, to clean up.'));
  row.append(text);
  card.append(row);
  $('gamemode-card').replaceChildren(card);
}

function renderHistoryCard(s) {
  const card = el('div', 'auto-card');
  card.append(historyBar(s, true));
  $('history-card').replaceChildren(card);
}

/**
 * Where AI matching last ran, as the manor's programs name it (npu, cpu, gpu-…: they take turns on each through
 * its lock), why it fell back, and the devices that failed for Heiward lately, which its Auto leaves alone for
 * 10 minutes (Heiward's own marks: other programs' failures, of their model servers, don't count here).
 */
function acceleratorNotes(a, dev) {
  const notes = [];
  if (!a) return notes;
  if (a.inUse) {
    const where = a.card ? 'the ' + a.card : a.inUse === 'npu' ? 'the NPU' : a.inUse === 'cpu' ? 'the processor' : dev ? a.inUse : 'the graphics card';
    // The manor's ids for its accelerators (npu, gpu-…) and why one failed are for developers.
    notes.push(el('div', 'muted small', 'Last ran on ' + where + (dev ? ' (' + a.inUse + ')' : '') + '.'));
  }
  if (a.fallback && dev) notes.push(el('div', 'small gpu-note', 'It fell back: ' + a.fallback + '.'));
  const until = (iso) => new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  for (const f of a.failed || []) {
    notes.push(el('div', 'small gpu-note', (f.name.startsWith('the ') ? 'The ' + f.name.slice(4) : f.name) + (dev ? ' (' + f.id + ')' : '') + ' failed at ' +
      until(f.sinceUtc) + ', so Heiward leaves it until ' + until(f.untilUtc) + (dev ? ': ' + f.reason : '.')));
  }
  return notes;
}

/** Where AI matching runs on this PC, in a sentence, from ai-status.json as the title bar's badge reads it. */
function modelHere(ai, dev) {
  if (!ai) return '';
  if (ai.device === 'off') return 'On this PC it\'s off: its components aren\'t installed' + (dev ? ' ("hei setup" installs them).' : '.');
  const where = ai.device === 'NPU' ? 'the ' + (ai.npuDisplayName || ai.npuName || 'NPU')
    : ai.device === 'GPU' ? 'the graphics card' + (ai.card ? ', the ' + ai.card : '') : 'the processor';
  const why = ai.device === 'NPU' ? ''
    : ai.npuVendor === 'None' ? ', as this PC has no NPU'
      : !ai.npuSupported ? ', as this version doesn\'t support its NPU yet'
        : !ai.npuInstalled ? ', as its NPU isn\'t set up' + (dev ? ' ("hei setup")' : '')
          : ai.setting === 'gpu' || ai.setting === 'cpu' ? ', as Heiward\'s settings ask' + (dev ? ' (aiDevice)' : '')
            : ', as the NPU couldn\'t run it' + (dev ? ' (heiward.log says why)' : '');
  return 'On this PC it ' + (ai.source === 'scan' ? 'ran' : 'runs') + ' on ' + where + why + '.';
}

/**
 * Where its work runs, in plain words, so processor and graphics use that comes and goes needs no explaining: the model
 * (on this PC, as the badge says), video frames, files, when scans run and how hard, the turns on the NPU, and why the
 * meters move. Every agent at the manor has this section; its facts are Heiward's own (OnnxEmbedder, HardwareVideoDecode,
 * ReportBuilder.ContentHashes, Scheduler, ScanPace, NpuLock).
 */
function renderWhereCard(s) {
  const c = s.config;
  const speed = c.scanSpeed === 'background' || c.scanSpeed === 'full' ? c.scanSpeed : 'auto';
  const background = 'in Windows\' efficiency mode, at low priority and at most ' + c.backgroundCpuPercent + '% of the processor';
  const fast = 'at full speed, on ' + count(c.fullSpeedCores, 'core', 'cores');
  const when = !s.schedule.everyMinutes
    ? (s.about.dev ? 'A development build has no scheduled scans: it scans when you press Scan now.' : 'It scans only when you press Scan now.')
    : 'Heiward ' + (s.schedule.text || 'scans every ' + s.schedule.everyMinutes + ' min') + '; a scan with nothing new is a quick look at the folders.';
  const pace = {
    background: 'Every scan runs ' + background + '.',
    auto: s.schedule.everyMinutes ? 'Scheduled scans run ' + background + '. Scan now, and any scan while this page is open, runs ' + fast + '.'
      : 'Scan now runs ' + fast + '.',
    full: 'Every scan runs ' + fast + (s.schedule.everyMinutes ? ', scheduled ones too.' : '.'),
  }[speed];
  // The model's and its runtimes' names are for developers; otherwise the same in plain words.
  const dev = s.dev.enabled;
  const rows = [
    ['The model', (dev
      ? 'AI matching compares pictures and video frames with DINOv2, a vision model. It runs on the NPU, through its maker\'s runtime ' +
        '(Qualcomm\'s QNN, Intel\'s OpenVINO or AMD\'s Vitis AI, with Windows ML); without an NPU that works, on a graphics card through DirectML; otherwise on the processor. '
      : 'AI matching compares pictures and video frames with a vision model, here on this PC. It runs on the NPU when there\'s one that works, ' +
        'otherwise on the graphics card, otherwise on the processor. ') + modelHere(s.ai, dev)],
    ['Video frames', (dev ? 'FFmpeg decodes the frames Heiward samples' : 'Heiward decodes the frames it samples') + ' from each video. A few videos at a time go to the graphics chip\'s video decoder, ' +
      'which costs the processor little; the rest, and any it can\'t decode, are decoded on the processor. iPhone photos use that decoder too, beside the processor.'],
    ['Files', 'Listing folders, reading files and hashing them is processor and disk work. A rescan reads only new and changed files. ' +
      'Files of the same size are hashed whole, once, to find exact copies; photos are decoded, and the sound of videos fingerprinted, on the processor.'],
    ['When it runs', when + ' ' + pace + ' While a game or anything full screen runs, scans make way.'],
    ['Taking turns', 'On the NPU, the model takes its turn in the queue the manor\'s agents on this PC share: Heiward holds the NPU for at most 2 seconds, ' +
      'then joins the back of the line, and work someone is waiting on goes first. On a graphics card it takes that card\'s turn the same way.'],
    ['Why the processor and graphics come and go', 'A scan comes in bursts: the folders, then decoding and comparing what\'s new, then hashing, and between ' +
      'scans Heiward does nothing. The graphics chip is busy only while it decodes videos or runs the model, the NPU in short turns. ' +
      'A scan in the background stays under its cap; one at full speed can fill the processor until it finishes.'],
  ];
  const card = el('div', 'auto-card');
  for (const [title, value] of rows) {
    const r = el('div', 'auto-row');
    const text = el('div', 'auto-text');
    text.append(el('div', 'auto-title', title), el('div', 'muted small', value));
    r.append(text);
    card.append(r);
  }
  $('where-card').replaceChildren(card);
}

/** What the page has no switch for: where it's set, and what it's set to now. */
function renderMoreCard(s) {
  const c = s.config;
  // Where each is set (the settings file, its keys) only with developer mode on; plain words otherwise.
  const dev = s.dev.enabled;
  const card = el('div', 'auto-card');
  const extra = c.folders.filter((f) => trimSep(f).length > 3); // not a drive's root
  const device = { npu: 'NPU', gpu: 'graphics card', cpu: 'processor' }[c.aiDevice] || c.aiDevice.toUpperCase();
  const rows = [
    ['What\'s scanned', c.allDrives
      ? 'Every fixed drive, minus Windows, programs, games, app data and code.' + (extra.length ? ' Also: ' + extra.join('; ') : '')
      : c.folders.join('; ') || (dev ? 'Nothing: add folders in the settings file.' : 'Nothing yet: right-click a folder to include it.'),
      'Right-click a folder in a folder\'s view to include it in scans or leave it out.'],
    ['Skipped file types', c.excludeExtensions.length ? c.excludeExtensions.join(' ') : 'None.', dev ? 'excludeExtensions in the settings file.' : null],
    ['Where AI matching runs', c.aiDevice === 'auto'
      ? 'On the NPU when there is one that works, otherwise on the graphics card once Heiward has checked it, otherwise on the processor.'
      : 'On the ' + (dev ? c.aiDevice.toUpperCase() : device) + (c.aiDevice === 'gpu' && s.gpu && (s.gpu.chosen || s.gpu.windowsDefault)
        ? ': the ' + (s.gpu.missing ? s.gpu.windowsDefault : s.gpu.chosen || s.gpu.windowsDefault) : '') + '.',
      dev ? 'aiDevice in the settings file: auto, npu, gpu or cpu.' : null, acceleratorNotes(s.accelerators, dev)],
  ];
  for (const [title, value, how, notes] of rows) {
    const r = el('div', 'auto-row');
    const text = el('div', 'auto-text');
    text.append(el('div', 'auto-title', title), el('div', null, value), ...(notes || []));
    if (how) text.append(el('div', 'muted small', how));
    r.append(text);
    card.append(r);
  }
  const foot = el('div', 'auto-foot');
  if (dev && c.path) {
    const file = el('div', 'muted small', 'Settings file: ');
    file.append(el('code', null, c.path));
    foot.append(file, el('div', 'muted small', 'Changes to the file apply at the next scan. The page\'s own switches above save to it too.'));
  } else {
    foot.append(el('div', 'muted small', 'The switches on this page keep your choices.'));
  }
  card.append(foot);
  $('more-card').replaceChildren(card);
}

// ---------------------------------------------------------------- scanning and history settings

let settingsBusy = false;

async function saveSettings(next) {
  settingsBusy = true;
  try { await post('/api/settings', next); } catch (e) { showError(e.message); }
  settingsBusy = false;
  refresh(true);
}

/**
 * How hard scans work (ScanPace): always in the background, at full speed while someone waits for them,
 * or always at full speed.
 */
function renderScanCard(s) {
  const c = s.config;
  const speed = c.scanSpeed === 'background' || c.scanSpeed === 'full' ? c.scanSpeed : 'auto';
  const card = el('div', 'auto-card');
  const row = el('div', 'auto-row');
  const text = el('div', 'auto-text');
  const head = el('div', 'auto-wait');
  const label = el('label', 'auto-title', 'Scans run');
  label.htmlFor = 'scan-speed';
  const pick = el('select');
  pick.id = 'scan-speed';
  for (const [value, name] of [['background', 'in the background'], ['auto', 'at full speed when you\'re here'], ['full', 'always at full speed']]) {
    const o = el('option', null, name);
    o.value = value;
    o.selected = value === speed;
    pick.append(o);
  }
  pick.disabled = settingsBusy;
  pick.addEventListener('change', () => saveSettings({ scanSpeed: pick.value }));
  head.append(label, pick);
  const background = 'Windows\' efficiency mode, low priority, and at most ' + c.backgroundCpuPercent + '% of the processor';
  const fast = count(c.fullSpeedCores, 'core', 'cores') + ' at normal priority';
  text.append(head, el('div', 'muted small', {
    background: 'Every scan runs in the background: ' + background + '. Slower, and light on the battery and the fans.',
    auto: 'Scan now, and a scheduled scan while this page is open, use ' + fast + '. With the page closed, scheduled scans run in the background: ' + background + '.',
    full: 'Every scan, scheduled ones too, uses ' + fast + ', to finish as fast as it can.',
  }[speed]));
  row.append(text);
  card.append(row);
  // More memory, faster scans (HardwareVideoDecode): how many videos the graphics chip decodes at once.
  const memory = el('div', 'auto-row');
  memory.append(toggleSwitch(c.moreMemory, 'Use more memory to scan faster', settingsBusy, (on) => saveSettings({ moreMemory: on })));
  const memoryText = el('div', 'auto-text');
  memoryText.append(el('div', 'auto-title', 'Use more memory to scan faster'), el('div', 'muted small', c.moreMemory
    ? 'In the background, the graphics chip decodes more videos at once, taking up to ' + bytes(c.moreMemoryBytes) + ' of memory while a scan runs, so it finishes sooner on less of the processor.'
    : 'The graphics chip decodes two videos at once, up to about 1 GB of memory. Scans take longer.'),
    el('div', 'muted small', 'While a game or another 3D program runs, or anything full screen, scans make way: they run in the background with less memory until it\'s closed.'));
  memory.append(memoryText);
  card.append(memory);
  const gpu = gpuSetting(s);
  if (gpu) card.append(gpu);
  const foot = el('div', 'auto-foot');
  foot.append(el('div', 'muted small', s.schedule.next ? 'Next scheduled scan: ' + s.schedule.next + '.'
    : s.schedule.everyMinutes === 0 ? 'No scheduled scans: scans run when you press Scan now.'
      : s.dev.enabled ? 'Scheduled scans aren\'t set up on this PC: run "hei install".' : 'Scheduled scans aren\'t set up on this PC: reinstall Heiward to set them up.'));
  card.append(foot);
  $('scan-card').replaceChildren(card);
}

/** "12 GB of its own memory", or "shares the PC's memory" for graphics built into the processor. */
function gpuMemory(memory) {
  return memory >= 512 * 1024 * 1024 ? bytes(memory) + ' of its own memory' : 'shares the PC\'s memory';
}

/**
 * Which graphics card does the GPU work (GpuAdapters): decoding videos and iPhone photos, and AI matching when
 * it runs on the GPU. Only on a PC with more than one, or when the settings name one that's gone. A scan keeps
 * the card it started on, so the choice is locked while one runs, and a change applies from the next scan.
 */
function gpuSetting(s) {
  const g = s.gpu;
  if (!g || (g.cards.length < 2 && !g.chosen)) return null;
  const row = el('div', 'auto-row');
  const text = el('div', 'auto-text');
  const head = el('div', 'auto-wait');
  const label = el('label', 'auto-title', 'Graphics card');
  label.htmlFor = 'gpu-pick';
  const pick = el('select');
  pick.id = 'gpu-pick';
  const options = [['', 'Windows\' default' + (g.windowsDefault ? ' (' + g.windowsDefault + ')' : '')]];
  for (const c of g.cards) {
    options.push([c.key, c.key + ', ' + (c.memory >= 512 * 1024 * 1024 ? bytes(c.memory) : 'shared memory') +
      (c.key === g.recommended ? ' (recommended)' : '')]);
  }
  if (g.missing) options.push([g.chosen, g.chosen + ' (not on this PC now)']);
  for (const [value, name] of options) {
    const o = el('option', null, name);
    o.value = value;
    o.selected = value === g.chosen;
    pick.append(o);
  }
  pick.disabled = settingsBusy || g.locked;
  pick.addEventListener('change', () => saveSettings({ gpu: pick.value }));
  head.append(label, pick);
  const ai = s.ai && ['NPU', 'GPU', 'CPU'].includes(s.ai.device) ? s.ai.device : null;
  text.append(head, el('div', 'muted small', s.config.aiDevice === 'gpu' || ai === 'GPU'
    ? 'It runs AI matching, and decodes videos and iPhone photos.'
    : 'It decodes videos and iPhone photos' + (ai ? '; AI matching runs on the ' + ai + '.' : '.')));
  if (g.locked) text.append(el('div', 'small gpu-note', g.lockedText));
  else if (g.missing) text.append(el('div', 'small gpu-note', g.chosen + ' isn\'t on this PC now, so scans use Windows\' default until it\'s back, or you choose another.'));
  else text.append(el('div', 'muted small', 'A scan keeps the card it started on: a change applies from the next scan.'));
  row.append(text);
  return row;
}

// ---------------------------------------------------------------- the Store version's first run

// From the Store, Heiward starts without a console, so its first run asks here what the GitHub exe asks
// in its window (StoreSetup): where the AI runs (the NPU it found, named, unless the user overrides it,
// against the page's advice), when to scan (on the GPU or CPU), how hard scans work, and whether to
// remove a copy installed from GitHub. The answers survive the page's polls; the install's output shows
// while it runs.
const setupAnswers = { device: null, onDemand: false, scanSpeed: 'background', removeGitHubCopy: true, gpu: null };
let setupRetry = false;
let setupShown = null; // 'form' or 'progress': the form is only built once, so a poll doesn't reset it

/** The NPU setup found, named as Windows lists it: the AI runs on it unless the user picks otherwise. */
function npuFound(setup) {
  const card = el('div', 'setup-found');
  const text = el('div', 'setup-found-text');
  text.append(el('span', 'setup-chip', 'NPU detected'), el('div', 'setup-found-name', setup.npuName));
  if (setup.npuHardware) text.append(el('div', 'muted small', setup.npuHardware));
  text.append(el('div', 'muted small', 'Heiward runs its AI matching on it: the NPU is built for this work, uses far less power than ' +
    'the graphics card or the processor, and leaves them free for you.'));
  card.append(icon('chip', 'setup-found-icon'), text);
  return card;
}

function setupChoice(title, name, options, current, onPick) {
  const box = el('fieldset', 'setup-question');
  box.append(el('legend', 'auto-title', title));
  for (const [value, label, detail] of options) {
    const row = el('label', 'setup-option');
    const radio = el('input');
    radio.type = 'radio';
    radio.name = name;
    radio.value = String(value);
    radio.checked = value === current;
    radio.addEventListener('change', () => { if (radio.checked) onPick(value); });
    const text = el('span');
    text.append(el('span', 'setup-label', label), el('span', 'muted small', detail));
    row.append(radio, text);
    box.append(row);
  }
  return box;
}

async function startSetup(button) {
  button.disabled = true;
  try {
    await post('/api/setup', {
      device: setupAnswers.device,
      onDemand: setupAnswers.device !== 'npu' && setupAnswers.onDemand,
      scanSpeed: setupAnswers.scanSpeed,
      removeGitHubCopy: !!state.setup.gitHubCopy && setupAnswers.removeGitHubCopy,
      gpu: state.setup.gpus && state.setup.gpus.length > 1 ? setupAnswers.gpu : null,
    });
    setupRetry = false;
    refresh(true);
  } catch (e) {
    showError(e.message);
    button.disabled = false;
  }
}

function renderSetup(s) {
  for (const id of ['home', 'settingsview', 'devview', 'gamesview', 'folder']) $(id).classList.add('hidden');
  $('setup').classList.remove('hidden');
  const setup = s.setup;
  const card = $('setup-card');
  if (setup.running || (setup.failed && !setupRetry)) {
    setupShown = 'progress';
    const log = el('pre', 'setup-log');
    log.textContent = setup.output.join('\n') || 'Starting…';
    const parts = [el('h2', null, setup.failed ? 'Setup stopped' : 'Setting up Heiward…')];
    parts.push(el('p', 'muted', setup.failed
      ? 'Something went wrong; the last lines below say what.' + (s.dev.enabled ? ' heiward.log has the rest.' : '')
      : 'Downloading what the AI needs and checking where it runs. The first scan starts right after.'));
    parts.push(log);
    if (setup.failed) {
      const again = el('button', 'btn', 'Try again');
      again.addEventListener('click', () => { setupRetry = true; setupShown = null; renderSetup(state); });
      parts.push(again);
    }
    card.replaceChildren(...parts);
    log.scrollTop = log.scrollHeight;
    return;
  }
  if (setupShown === 'form') return;
  setupShown = 'form';
  if (!setupAnswers.device) setupAnswers.device = setup.npu ? 'npu' : 'gpu';
  const parts = [el('h2', null, 'Set up Heiward')];
  if (setup.npu) parts.push(npuFound(setup));
  else parts.push(el('p', 'muted', (setup.unsupportedNpu
    ? 'This version of Heiward can\'t use this PC\'s NPU yet (' + setup.unsupportedNpu + '), so'
    : 'This PC has no NPU, so') + ' AI matching runs on the graphics card or the processor.'));

  // Where the AI runs. With an NPU, the GPU and the CPU stay available, with advice against them.
  const devices = [];
  if (setup.npu) devices.push(['npu', 'On the NPU (recommended)', 'The ' + setup.npuName + '. Scans run every hour, and step aside on a low battery.']);
  devices.push(['gpu', 'On the graphics card' + (setup.npu ? '' : ' (recommended)'), 'Any DirectX 12 GPU: fast, and lighter on power than the processor. It downloads about 215 MB more.']);
  devices.push(['cpu', 'On the processor', 'Works on every PC, and uses the most power.']);
  const advice = el('p', 'setup-advice', 'Not recommended on this PC. The ' + setup.npuName + ' does this work on far less power. ' +
    'On the graphics card or the processor, scans draw more power and run warmer, so scheduled ones run only every 6 hours ' +
    'on AC power, and the NPU sits idle.');
  const when = setupChoice('When should it look for new duplicates?', 'setup-when', [
    [false, 'Every 6 hours, on AC power (recommended)', 'Scans skip themselves on battery.'],
    [true, 'Only when I press Scan now', 'Nothing runs on a schedule.'],
  ], setupAnswers.onDemand, (v) => { setupAnswers.onDemand = v; });
  const sync = () => {
    advice.classList.toggle('hidden', !setup.npu || setupAnswers.device === 'npu');
    when.classList.toggle('hidden', setupAnswers.device === 'npu');
  };
  const where = setupChoice('Where should the AI run?', 'setup-device', devices, setupAnswers.device, (v) => { setupAnswers.device = v; sync(); });
  where.append(advice);
  sync();
  parts.push(where, when);

  // More than one graphics card: which one does the GPU work (the GitHub exe asks the same, Installer.AskGpu).
  if (setup.gpus && setup.gpus.length > 1) {
    if (setupAnswers.gpu == null) setupAnswers.gpu = setup.recommendedGpu;
    const cards = setupChoice('Which graphics card should Heiward use?', 'setup-gpu', setup.gpus.map((c) => {
      const memory = gpuMemory(c.memory);
      return [c.key, c.key + (c.key === setup.recommendedGpu ? ' (recommended)' : ''), memory[0].toUpperCase() + memory.slice(1) + '.'];
    }), setupAnswers.gpu, (v) => { setupAnswers.gpu = v; });
    cards.append(el('p', 'muted small', 'It decodes videos and iPhone photos, and runs the AI when that\'s on the graphics card. ' +
      'You can choose another one later, in Settings.'));
    parts.push(cards);
  }

  parts.push(setupChoice('How hard should scans work?', 'setup-speed', [
    ['background', 'In the background (recommended)', 'Low power: Windows\' efficiency mode and low priority. Scans take longer.'],
    ['full', 'At full speed', 'As many cores as it takes, at normal priority, to finish as fast as possible.'],
  ], setupAnswers.scanSpeed, (v) => { setupAnswers.scanSpeed = v; }));

  // A copy installed from GitHub: this one replaces it, and the Store keeps it up to date.
  if (setup.gitHubCopy != null) {
    const box = el('fieldset', 'setup-question');
    box.append(el('legend', 'auto-title', 'Heiward from GitHub'));
    const row = el('label', 'setup-option');
    const tick = el('input');
    tick.type = 'checkbox';
    tick.checked = setupAnswers.removeGitHubCopy;
    tick.addEventListener('change', () => { setupAnswers.removeGitHubCopy = tick.checked; });
    const text = el('span');
    text.append(el('span', 'setup-label', 'Remove the copy installed from GitHub' + (setup.gitHubCopy ? ' (' + setup.gitHubCopy + ')' : '') + ' (recommended)'),
      el('span', 'muted small', 'The Microsoft Store keeps this version up to date by itself: new versions arrive automatically, with ' +
        'nothing to download from GitHub. Removing the old copy takes away its shortcuts, its entry in Installed apps and its folder. ' +
        'Your settings, the report and the history stay: this version uses them.'));
    row.append(tick, text);
    box.append(row);
    parts.push(box);
  }

  const go = el('button', 'btn', 'Set up and scan');
  go.addEventListener('click', () => startSetup(go));
  parts.push(el('p', 'muted small', 'The Microsoft Store keeps Heiward up to date. How hard scans work can be changed on the Settings page later. ' +
    'Your photos, videos and files are never deleted unless you choose them on this page.'), go);
  card.replaceChildren(...parts);
}

// ---------------------------------------------------------------- automatic cleanup

// Automatic cleanup (AutoCleaner): after each scan Heiward cleans what this page would tick, once it has
// been listed for a few days. The card turns it on and off; each set and developer item says when it goes,
// with a "Leave it" button.
let autoBusy = false;

/** The developer kinds it can take (AutoCleaner.DeveloperKinds), safest first. */
function autoKinds(a) {
  return [
    ['branches', 'Merged branches', 'Their commits are already in the remote\'s main branch.'],
    ['temp', 'Temp files and crash dumps', 'Untouched for ' + a.tempOlderThanDays + ' days.'],
    ['buildOutputs', 'Build outputs', 'Of projects untouched for ' + a.staleProjectDays + ' days.'],
    ['worktrees', 'Worktrees', 'Untouched for ' + a.staleProjectDays + ' days, with everything committed and pushed.'],
    ['systemImages', 'Emulator system images', 'That no emulator uses.'],
  ];
}

async function saveAuto(next) {
  autoBusy = true;
  renderAutoCard(state);
  try { await post('/api/auto/settings', next); } catch (e) { showError(e.message); }
  autoBusy = false;
  refresh(true);
}

async function autoHold(target, hold) {
  try { await post('/api/auto/hold', { target, hold }); } catch (e) { showError(e.message); }
  refresh(true);
}

async function autoAllow(pair) {
  if (!confirm('Let automatic cleanup take the copies in these two folders?\n\n' +
      'It held them back because many sets have copies in both, which is what a backup looks like. ' +
      'Allow it only if the second copies aren\'t something you keep on purpose.')) return;
  try { await post('/api/auto/allow', { pair, allow: true }); } catch (e) { showError(e.message); }
  refresh(true);
}

/** When something due goes: "at the next scan", "from 14:05 today", "from tomorrow", "from Wed 1 Oct". */
function dueText(iso, dev) {
  const d = new Date(iso);
  const now = new Date();
  if (d <= now) return dev === 'games' ? 'after the next daily look at your games' : dev ? 'after the next daily developer check' : 'at the next scan';
  const day = (x) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  const days = Math.round((day(d) - day(now)) / 86400000);
  if (days === 0) return 'from ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) + ' today';
  if (days === 1) return 'from tomorrow';
  return 'from ' + d.toLocaleDateString([], { weekday: 'short', day: 'numeric', month: 'short' });
}

function autoSwitch(on, label, onChange) {
  return toggleSwitch(on, label, autoBusy, onChange);
}

/** A Windows-style on/off switch. */
function toggleSwitch(on, label, disabled, onChange) {
  const wrap = el('label', 'switch');
  const box = el('input');
  box.type = 'checkbox';
  box.setAttribute('role', 'switch');
  box.setAttribute('aria-label', label);
  box.checked = on;
  box.disabled = disabled;
  box.addEventListener('change', () => onChange(box.checked, box));
  wrap.append(box, el('span', 'slider'));
  return wrap;
}

/** "12 sets of copies (340 MB) go, the first at the next scan." */
function upcomingText(u, one, many, dev, what) {
  if (!u.count) return 'Nothing is due yet.';
  return count(what === 'files' ? u.files : u.count, one, many) + (u.bytes ? ' (' + bytes(u.bytes) + ')' : '') + ' will go, the first ' + dueText(u.firstDueUtc, dev) + '.';
}

function renderAutoCard(s) {
  const a = s.auto;
  const set = a.settings;
  const days = set.afterDays;
  const waitText = days === 0 ? 'at the next scan after they\'re listed' : count(days, 'day', 'days') + ' after they\'re listed';
  const card = el('div', 'auto-card');

  const row = (on, title, about, onChange, extra) => {
    const r = el('div', 'auto-row');
    r.append(autoSwitch(on, title, onChange));
    const text = el('div', 'auto-text');
    text.append(el('div', 'auto-title', title), el('div', 'muted small', about));
    for (const x of extra) if (x) text.append(x);
    r.append(text);
    return r;
  };

  const dupStatus = set.duplicates ? el('div', 'auto-status small', upcomingText(a.upcoming.groups, 'set of copies', 'sets of copies', false)) : null;
  card.append(row(set.duplicates, 'Duplicate photos and videos',
    'Plain copies go to the Recycle Bin by themselves, ' + waitText + '. Edits and look-alikes, copies in cloud-synced folders, and what looks like a backup of a whole folder always wait for you.',
    async (on, box) => {
      if (on && !confirm('Clean up duplicates automatically?\n\n' +
          'After each scan, Heiward moves plain copies of photos, and byte-for-byte identical videos, to the Recycle Bin ' + waitText +
          ', keeping the best copy of each. You can restore them from the Recycle Bin.\n\n' +
          'Edits, look-alikes, copies in cloud-synced folders and what looks like a backup of a whole folder always wait for you. ' +
          'Each set shows when it will go, with a "Leave it" button.')) { box.checked = false; return; }
      await saveAuto({ ...set, duplicates: on });
    }, [dupStatus]));

  if (s.dev.enabled) {
    let kinds = null, devStatus = null, reeveNote = null;
    if (set.developer) {
      kinds = el('div', 'auto-kinds');
      // Where Reeve keeps git state, merged branches are its to delete and worktrees its to remove: what a removal that
      // stopped partway left (a folder git no longer knows) is all of worktrees that's left here.
      const reeve = reeveRole();
      for (const [k, label, hint] of autoKinds(a)) {
        const reeves = !!reeve && k === 'branches', leftovers = !!reeve && k === 'worktrees';
        const l = el('label');
        l.title = reeves ? reeve.note : leftovers ? 'Folders a worktree removal left behind, which git no longer knows. ' + reeve.note + '.' : hint;
        const box = el('input');
        box.type = 'checkbox';
        box.checked = !reeves && set.developerKinds.includes(k);
        box.disabled = autoBusy || reeves;
        box.addEventListener('change', () => saveAuto({ ...set, developerKinds: box.checked ? [...set.developerKinds, k] : set.developerKinds.filter((x) => x !== k) }));
        l.append(box, el('span', null, reeves ? label + ' (' + reeve.name + '\'s)' : leftovers ? 'Worktree leftovers' : label));
        kinds.append(l);
      }
      if (reeve) reeveNote = roleNote(reeve);
      const parts = [];
      if (a.upcoming.dev.count) parts.push(upcomingText(a.upcoming.dev, 'item', 'items', true));
      if (a.upcoming.branches.count) parts.push(upcomingText(a.upcoming.branches, 'merged branch', 'merged branches', true, 'files'));
      devStatus = el('div', 'auto-status small', parts.join(' ') || 'Nothing is due yet.');
    }
    card.append(row(set.developer, 'Developer leftovers',
      'The kinds you tick below are deleted by themselves right after the daily developer check, ' + waitText +
      '. They\'re deleted permanently, not to the Recycle Bin: tools recreate them. Package caches and emulators always wait for you.',
      async (on, box) => {
        if (on && !confirm('Clean up developer leftovers automatically?\n\n' +
            'Right after the daily developer check, Heiward deletes what this page ticks for you (of the kinds you pick) ' + waitText + '. ' +
            'They\'re deleted permanently, not to the Recycle Bin: tools rebuild or download them again, so the next build takes longer. ' +
            'Merged branches go only when every commit is in the remote\'s main branch; worktrees only when everything is committed and pushed.\n\n' +
            'Package caches and emulators always wait for you.')) { box.checked = false; return; }
        await saveAuto({ ...set, developer: on });
      }, [kinds, reeveNote, devStatus]));
  }

  // Game mode: what games leave behind, to the Recycle Bin, of the kinds ticked (AutoCleaner.GameKinds).
  if (s.games.enabled) {
    let kinds = null, gameStatus = null;
    if (set.games) {
      kinds = el('div', 'auto-kinds');
      for (const [k, label, hint] of [
        ['dumps', 'Crash dumps and reports', 'What games and Windows wrote when a game crashed.'],
        ['caches', 'Launchers\' download caches', 'Left alone while their launcher runs.'],
        ['leftovers', 'Leftovers of uninstalled games', 'Never a folder with saved games in it.'],
        ['shaders', 'Shader caches of uninstalled games', 'Those of games you have always wait for you.'],
      ]) {
        const l = el('label');
        l.title = hint;
        const box = el('input');
        box.type = 'checkbox';
        box.checked = (set.gameKinds || []).includes(k);
        box.disabled = autoBusy;
        box.addEventListener('change', () => saveAuto({ ...set, gameKinds: box.checked ? [...(set.gameKinds || []), k] : (set.gameKinds || []).filter((x) => x !== k) }));
        l.append(box, el('span', null, label));
        kinds.append(l);
      }
      gameStatus = el('div', 'auto-status small', a.upcoming.games && a.upcoming.games.count ? upcomingText(a.upcoming.games, 'item', 'items', 'games') : 'Nothing is due yet.');
    }
    card.append(row(!!set.games, 'What games leave behind',
      'The kinds you tick below go to the Recycle Bin by themselves right after the daily look at your games, ' + waitText +
      '. Paused downloads, and shader caches of games you have, always wait for you.',
      async (on, box) => {
        if (on && !confirm('Clean up what games leave behind automatically?\n\n' +
            'Right after the daily look at your games, Heiward moves what the Games area ticks for you (of the kinds you pick) to the Recycle Bin ' + waitText + '. ' +
            'You can restore it from there.\n\n' +
            'Installed games\' own files and saved games are never touched, and nothing of a launcher or game that\'s running.')) { box.checked = false; return; }
        await saveAuto({ ...set, games: on });
      }, [kinds, gameStatus]));
  }

  const foot = el('div', 'auto-foot');
  const wait = el('label', 'auto-wait');
  const select = el('select');
  select.disabled = autoBusy;
  for (const d of [0, 1, 3, 7, 14, 30]) {
    const o = el('option', null, d === 0 ? 'no wait' : count(d, 'day', 'days'));
    o.value = String(d);
    select.append(o);
  }
  if (![0, 1, 3, 7, 14, 30].includes(days)) {
    const o = el('option', null, count(days, 'day', 'days'));
    o.value = String(days);
    select.append(o);
  }
  select.value = String(days);
  select.addEventListener('change', () => saveAuto({ ...set, afterDays: Number(select.value) }));
  wait.append(el('span', null, 'Wait'), select, el('span', 'muted', 'after something is first listed, so you can see it coming.'));
  foot.append(wait);
  const last = a.lastRun;
  if (last) {
    const parts = [];
    if (last.files) parts.push(count(last.files, 'copy', 'copies') + ' (' + bytes(last.fileBytes) + ') to the Recycle Bin');
    if (last.devItems) parts.push(bytes(last.devBytes) + ' of developer leftovers');
    if (last.branches) parts.push(count(last.branches, 'merged branch', 'merged branches'));
    if (last.gameItems) parts.push(bytes(last.gameBytes) + ' of game leftovers to the Recycle Bin');
    const line = el('div', 'muted small', 'Last run ' + ago(last.atUtc) + ': ' + (parts.join(' · ') || 'nothing cleaned') + '.');
    if (last.problems.length) {
      const more = el('details', 'auto-problems');
      more.append(el('summary', null, count(last.problems.length, 'thing was', 'things were') + ' left alone'));
      const list = el('ul');
      for (const p of last.problems.slice(0, 20)) list.append(el('li', null, p));
      more.append(list);
      line.append(more);
    }
    foot.append(line);
  }
  card.append(foot);
  $('auto-card').replaceChildren(card);
}

/** Under a set or developer item: when automatic cleanup takes it (with "Leave it"), or why it won't. */
function autoLine(e, target, dueLabel) {
  const line = el('div', 'auto-line small');
  line.append(icon('clock'));
  const act = (label, fn) => {
    const b = el('button', 'link small', label);
    b.addEventListener('click', (ev) => { ev.preventDefault(); ev.stopPropagation(); fn(); });
    return b;
  };
  if (e.held) {
    line.append(el('span', null, 'You\'ll decide: automatic cleanup leaves it.'), act('Let it go automatically', () => autoHold(target, false)));
  } else if (e.dueUtc) {
    line.classList.add('due');
    line.append(el('span', null, dueLabel), act('Leave it', () => autoHold(target, true)));
  } else {
    line.append(el('span', null, 'Not automatic: ' + e.reason + '.'));
    if (e.folderPair) line.append(act('Allow for these folders', () => autoAllow(e.folderPair)));
  }
  return line;
}

// ---------------------------------------------------------------- developer area

let devReport = null;         // /api/dev
const devTicks = new Set();   // ticked item ids
const devSeen = new Set();    // ids whose default tick was applied (the user's edits survive refreshes)
let devBusy = false;

async function startDevCheck() {
  try { await post('/api/dev/scan'); } catch (e) { showError(e.message); }
  refresh(true);
}

function renderDevCard(dev) {
  const section = $('dev-section');
  section.classList.toggle('hidden', !dev.enabled || (dev.scannedAtUtc && !(dev.categories || []).length && !dev.running));
  const card = el('div', 'dev-card');
  const top = el('div', 'drive-top');
  top.append(devIcon());
  const text = el('div');
  text.style.flex = '1';
  text.style.minWidth = '0';
  if (!dev.scannedAtUtc) {
    text.append(el('div', 'drive-name', 'Developer leftovers'),
      el('div', 'muted small', dev.running ? 'Checking build outputs, worktrees and caches…' : 'Build outputs, git worktrees, package caches and emulators that tools recreate. Not checked yet.'));
  } else {
    text.append(el('div', 'drive-name', bytes(dev.totalBytes) + ' that tools can recreate'),
      el('div', 'muted small', (dev.suggestedBytes ? bytes(dev.suggestedBytes) + ' ticked for cleaning · ' : 'Nothing ticked for you · ') +
        (dev.running ? 'checking again…' : 'checked ' + ago(dev.scannedAtUtc))));
    const chips = el('div', 'chips');
    chips.style.marginTop = '6px';
    for (const c of dev.categories || []) {
      // Each chip opens its category.
      const chip = el('button', 'chip quiet chip-link', ((DEV_META[c.key] || {}).short || c.title) + ' ' + bytes(c.bytes));
      chip.addEventListener('click', () => { location.hash = SHARED.includes(c.key) ? '#/dev/s/' + c.key : '#/dev'; });
      chips.append(chip);
    }
    text.append(chips);
  }
  top.append(text);
  const actions = el('div', 'actions');
  actions.style.marginTop = '0';
  if (dev.scannedAtUtc) {
    const open = el('button', 'btn', 'Review');
    open.addEventListener('click', () => { location.hash = '#/dev'; });
    actions.append(open);
  } else {
    const check = el('button', 'btn', dev.running ? 'Checking…' : 'Check now');
    check.disabled = dev.running;
    check.addEventListener('click', startDevCheck);
    actions.append(check);
  }
  top.append(actions);
  card.append(top);
  $('dev-card').replaceChildren(card);
}

async function loadDevReport() {
  const res = await fetch('/api/dev');
  const data = res.ok ? await res.json() : null;
  devReport = data ? data.report : null;
  devProjects = data ? data.projects || [] : [];
  for (const c of (devReport && devReport.categories) || [])
    for (const i of c.items)
      if (!devSeen.has(i.id)) {
        devSeen.add(i.id);
        if (i.suggested && !i.blocked) devTicks.add(i.id);
      }
}

// Open pull requests, asked of each repository's host (GitHub, Azure DevOps, GitLab …) while this view is open:
// the agent keeps an answer two minutes, and Refresh asks again.
let devPulls = null;          // /api/dev/pulls: { repos: { [repository folder]: { host, web, total, pulls, error } }, fetchedUtc }
let devPullsAsked = 0;        // when they were last asked for (ms)
let devPullsLoading = false;

// At a manor, who looks after what in Heiward's place (ManorRoles): Reeve the worktrees and merged branches, the Steward
// the pull requests, each { name, url, note } or null. Heiward then shows worktrees read-only, and no branches or pull requests.
const reeveRole = () => (state && state.dev.roles && state.dev.roles.worktrees) || null;
const stewardRole = () => (state && state.dev.roles && state.dev.roles.pullRequests) || null;

/** "Reeve's worktree-tidy job removes merged worktrees and branches. Open Reeve", with a link to its page. */
function roleNote(role) {
  const note = el('div', 'manor-decides small', role.note + '. ');
  const link = el('a', null, 'Open ' + role.name);
  link.href = role.url;
  note.append(link);
  return note;
}

async function loadDevPulls(again) {
  if (devPullsLoading) return;
  // The Steward merges them: no host is asked.
  if (stewardRole()) { devPulls = null; return; }
  devPullsLoading = true;
  devPullsAsked = Date.now();
  if (route.view === 'dev') renderDev();
  try {
    const res = await fetch('/api/dev/pulls' + (again ? '?again=true' : ''), { cache: 'no-store' });
    if (res.ok) devPulls = await res.json();
  } catch (e) { /* the section says nothing new; Refresh asks again */ }
  devPullsLoading = false;
  if (route.view === 'dev') renderDev();
}

const pullsOf = (r) => (!stewardRole() && devPulls && devPulls.repos[r.key]) || null;

const VCS_NAMES = {
  git: 'Git', hg: 'Mercurial', svn: 'Subversion', tfvc: 'TFVC', plastic: 'Unity Version Control', bzr: 'Bazaar',
  fossil: 'Fossil', jj: 'Jujutsu', darcs: 'Darcs', pijul: 'Pijul', perforce: 'Perforce',
};

// What a pull request waits for: its tag, and its tone. Ready to merge and waiting for a review come first.
const WAITS = {
  merge: ['Ready to merge', 'good'],
  review: ['Needs review', 'accent'],
  changes: ['Changes requested', 'warn'],
  checks: ['Checks failing', 'bad'],
  conflicts: ['Has conflicts', 'bad'],
  behind: ['Behind its base branch', 'warn'],
  blocked: ['Blocked by branch rules', 'warn'],
  running: ['Checks running', 'quiet'],
  draft: ['Draft', 'quiet'],
};
const WAIT_ORDER = Object.keys(WAITS);
const waitsOnYou = (p) => p.waits === 'merge' || p.waits === 'review';

function devItems() {
  return ((devReport && devReport.categories) || []).flatMap((c) => c.items);
}

function lastUsed(iso) {
  if (!iso) return '';
  const days = (Date.now() - new Date(iso).getTime()) / 86400000;
  if (days < 1) return 'used today';
  if (days < 2) return 'used yesterday';
  if (days < 60) return 'untouched ' + Math.floor(days) + ' days';
  return 'untouched ' + Math.round(days / 30) + ' months';
}

// The developer view, project first: each repository (or a project the user bundled several into)
// has its own page with a section per cleanup area; machine-wide things (package caches, emulators,
// temp files) sit under Shared. Laid out like the folder view: navigation pane, one page at a time,
// and a selection bar that appears once something is ticked.
const DEV_META = {
  projects: { short: 'Build outputs', icon: 'layers', blurb: 'node_modules, bin and obj, Gradle and Cargo build folders, Python environments. The next install or build recreates them.' },
  orphans: { short: 'Build outputs without a project', icon: 'layers', blurb: 'bin and obj folders whose project file is gone (a project renamed or moved, usually), with nothing in them that git keeps. Nothing proves a build made them, so they\'re never ticked and never cleaned automatically: look at each first.' },
  worktrees: { short: 'Worktrees', icon: 'branch', blurb: 'Extra working folders; git removes one only when it has no uncommitted changes, and the branch stays. One a program or a Claude Code session works in waits.' },
  repos: { short: 'Merged branches', icon: 'merge', blurb: 'Local branches already merged into the remote\'s main or master.' },
  caches: { short: 'Package caches', icon: 'db' },
  android: { short: 'Emulators', icon: 'phone' },
  temp: { short: 'Temp & dumps', icon: 'clock' },
};
const SHARED = ['caches', 'android', 'temp'];
let devProjects = [];         // [{ name, repos: [path] }] from settings.json
let devResult = null;         // [ok, message, what wasn't cleaned] after the last clean, shown until the next
let devProgress = '';         // while cleaning: what, and how far along (the page re-renders meanwhile)
let groupMode = false;        // picking repositories to bundle into a project
const groupPick = new Set();  // repository keys picked

const rkey = (p) => (p || '').replace(/\\+$/, '').toLowerCase();

function catIcon(k, big) {
  const box = el('span', 'cat-icon' + (big ? ' big' : ''));
  box.append(icon((DEV_META[k] || {}).icon || 'code'));
  return box;
}

const devSize = (items) => items.reduce((a, i) => a + i.bytes, 0);
const devPicked = () => devItems().filter((i) => devTicks.has(i.id) && !i.blocked);

/**
 * Repositories with their open pull requests, build outputs, worktrees and branches, bundled as the user asked.
 * Those with pull requests waiting to be merged or reviewed come first.
 */
function devGroups() {
  if (!devReport || !devReport.scannedAtUtc) return [];
  const sources = new Map((devReport.sources || []).map((s) => [rkey(s.path), s]));
  const repos = new Map();
  const repoOf = (path) => {
    const k = rkey(path);
    if (!repos.has(k)) repos.set(k, { key: k, name: path.replace(/\\+$/, '').split(SEP).pop(), path, outputs: [], orphans: [], worktrees: [], branches: null, source: sources.get(k) || null });
    return repos.get(k);
  };
  for (const c of devReport.categories) {
    if (c.key === 'projects') for (const i of c.items) repoOf(i.location).outputs.push(i);
    if (c.key === 'orphans') for (const i of c.items) repoOf(i.repo || i.location).orphans.push(i);
    if (c.key === 'worktrees') for (const i of c.items) repoOf(i.repo || i.location).worktrees.push(i);
  }
  for (const r of devReport.repositories || []) repoOf(r.path).branches = r;
  const groups = [];
  const used = new Set();
  for (const p of devProjects) {
    const members = p.repos.map(rkey).filter((k) => repos.has(k) && !used.has(k)).map((k) => repos.get(k));
    if (!members.length) continue;
    members.forEach((m) => used.add(m.key));
    groups.push({ id: 'p:' + p.name.toLowerCase(), name: p.name, project: p, repos: members });
  }
  for (const r of repos.values()) if (!used.has(r.key)) groups.push({ id: 'r:' + r.key, name: r.name, project: null, repos: [r] });
  for (const g of groups) {
    g.outputs = g.repos.flatMap((r) => r.outputs);
    g.orphans = g.repos.flatMap((r) => r.orphans);
    g.worktrees = g.repos.flatMap((r) => r.worktrees);
    g.branches = g.repos.map((r) => r.branches).filter(Boolean);
    g.items = [...g.outputs, ...g.orphans, ...g.worktrees];
    g.bytes = devSize(g.items);
    g.merged = g.branches.reduce((a, b) => a + b.merged.length, 0);
    g.pulls = g.repos.flatMap((r) => (pullsOf(r) || { pulls: [] }).pulls);
    g.open = g.repos.reduce((a, r) => a + ((pullsOf(r) || {}).total || 0), 0);
    g.waiting = g.pulls.filter(waitsOnYou).length;
  }
  return groups.sort((a, b) => b.waiting - a.waiting || b.bytes - a.bytes || b.merged - a.merged || a.name.localeCompare(b.name));
}

function devGroupById(id) {
  return devGroups().find((g) => g.id === id) || null;
}

function renderDev() {
  renderDevNav();
  renderDevContent();
}

function renderDevNav() {
  const rows = [];
  const row = (label, glyph, hash, badge, selected, dim, pulls) => {
    const b = el('button', 'tree-row' + (dim ? ' empty' : ''));
    b.style.paddingLeft = '8px';
    glyph.classList.add('glyph');
    b.append(glyph, el('span', 'label', label));
    if (pulls) {
      const n = el('span', 'count pulls', String(pulls));
      n.title = count(pulls, 'pull request', 'pull requests') + ' ready to merge or waiting for a review';
      b.append(n);
    } else if (badge) b.append(el('span', 'count', badge));
    if (selected) { b.classList.add('selected'); b.setAttribute('aria-current', 'page'); }
    b.addEventListener('click', () => { location.hash = hash; $('dev-nav').classList.remove('open'); });
    rows.push(b);
  };
  row('Overview', icon('code'), '#/dev', null, !route.group && !route.cat);
  if (devReport && devReport.scannedAtUtc) {
    const groups = devGroups();
    if (groups.length) rows.push(el('div', 'tree-section', 'Repositories'));
    for (const g of groups)
      row(g.name, g.project ? icon('stack') : folderIcon(false), '#/dev/g/' + encodeURIComponent(g.id),
        g.bytes >= 1 << 20 ? bytes(g.bytes) : g.merged ? count(g.merged, 'branch', 'branches') : '', route.group === g.id, !g.bytes && !g.merged && !g.open, g.waiting);
    const shared = devReport.categories.filter((c) => SHARED.includes(c.key));
    if (shared.length) rows.push(el('div', 'tree-section', 'Shared'));
    for (const c of shared) row(DEV_META[c.key].short, catIcon(c.key), '#/dev/s/' + c.key, bytes(devSize(c.items)), route.cat === c.key);
  }
  $('dev-tree').replaceChildren(...rows);
}

function devHeader(glyph, title, intro, chips) {
  const head = el('div', 'folder-head');
  head.append(glyph);
  const text = el('div');
  text.style.flex = '1';
  text.style.minWidth = '0';
  text.append(el('h1', null, title));
  if (intro) text.append(typeof intro === 'string' ? el('div', 'dev-intro', intro) : intro);
  if (chips.length) {
    const box = el('div', 'chips');
    box.append(...chips);
    text.append(box);
  }
  head.append(text);
  return head;
}

function renderDevContent() {
  const content = $('dev-content');
  const y = content.scrollTop;
  const frag = document.createDocumentFragment();
  const dev = state.dev;
  if (!devReport || !devReport.scannedAtUtc) {
    frag.append(devHeader(devIcon(), 'Developer area', 'Your repositories\' open pull requests, and the build outputs, git worktrees, merged branches, package caches and emulators that your tools recreate when needed.', []));
    const empty = el('div', 'empty-state');
    empty.append(el('div', null, dev.running ? 'Checking… this takes a minute or so.' : 'Not checked yet.'));
    if (!dev.running) {
      const b = el('button', 'btn more', 'Check now');
      b.addEventListener('click', startDevCheck);
      empty.append(b);
    }
    frag.append(empty);
  } else if (route.group) {
    const g = devGroupById(route.group);
    frag.append(...(g ? groupPage(g) : [el('div', 'empty-state', 'That project isn\'t in the last check.')]));
  } else if (route.cat) {
    const c = devReport.categories.find((x) => x.key === route.cat);
    frag.append(...(c ? categoryPage(c) : [el('div', 'empty-state', 'Nothing here in the last check.')]));
  } else {
    frag.append(...devOverview(dev));
  }
  if (devResult) {
    const [ok, msg, failed] = devResult;
    const banner = el('div', 'dev-banner ' + (ok ? 'ok' : 'bad'), msg);
    if (failed && failed.length) {
      const list = el('ul');
      for (const f of failed) list.append(el('li', null, f));
      banner.append(list);
    }
    frag.append(banner);
  }
  frag.append(groupMode && !route.group && !route.cat ? groupBar() : selectionBar());
  content.replaceChildren(frag);
  content.scrollTop = y;
}

// ---- overview

// What the overview shows of each repository; its own page shows the rest.
const OVERVIEW_PULLS = 5, OVERVIEW_ITEMS = 4;

/**
 * Every repository at once: a panel each, its open pull requests beside its cleanup, those waiting to be merged or
 * reviewed first. Repositories with nothing open and nothing to clean share one line; machine-wide things come last.
 */
function devOverview(dev) {
  const total = devSize(devItems());
  const groups = devGroups();
  const again = el('button', 'link small', dev.running ? 'Checking…' : 'Check again');
  again.disabled = dev.running;
  again.addEventListener('click', startDevCheck);
  const chips = [];
  const steward = stewardRole(), reeve = reeveRole();
  if (devPulls && !steward) {
    const pulls = groups.flatMap((g) => g.pulls);
    const merge = pulls.filter((p) => p.waits === 'merge').length, review = pulls.filter((p) => p.waits === 'review').length;
    chips.push(el('span', 'chip', count(groups.reduce((a, g) => a + g.open, 0), 'open pull request', 'open pull requests')));
    if (merge) chips.push(el('span', 'chip good', merge + ' ready to merge'));
    if (review) chips.push(el('span', 'chip accent', review + ' to review'));
  }
  chips.push(el('span', 'chip', bytes(total) + ' tools can recreate'), el('span', 'chip quiet', 'checked ' + ago(devReport.scannedAtUtc) + ' in ' + took(devReport.durationSec)), again);
  if (!steward) {
    const pullsAgain = el('button', 'link small', devPullsLoading ? 'Asking for pull requests…' : 'Refresh pull requests');
    pullsAgain.disabled = devPullsLoading;
    pullsAgain.addEventListener('click', () => loadDevPulls(true));
    chips.push(pullsAgain);
  }
  const cleanNote = 'Cleaning deletes permanently, not to the Recycle Bin: tools rebuild or download it again, so the next build takes longer.';
  const intro = el('div');
  intro.append(el('div', 'dev-intro', steward
    ? 'All your repositories in one place, with what your development tools recreate when they need them. ' + cleanNote
    : 'All your repositories in one place: their open pull requests, and what your development tools recreate when they need them. Pull requests open on their host, to merge or review them. ' + cleanNote));
  // At a manor, who looks after the rest: Heiward still measures the worktrees, read-only.
  for (const role of [reeve, steward]) if (role) intro.append(roleNote(role));
  const out = [devHeader(devIcon(), 'Developer area', intro, chips)];

  // Why a host's pull requests couldn't be read (a sign-in, mostly), once each.
  const problems = [...new Set(Object.values((devPulls && devPulls.repos) || {}).map((r) => r.error).filter(Boolean))];
  for (const p of problems.slice(0, 3)) out.push(el('div', 'dev-banner warn', 'Pull requests: ' + p));
  if (groups.length) {
    const head = el('div', 'list-head');
    head.append(el('h2', null, 'Repositories'));
    const toggle = el('button', 'link small', groupMode ? 'Cancel grouping' : 'Group repositories into a project');
    toggle.addEventListener('click', () => { groupMode = !groupMode; groupPick.clear(); renderDev(); });
    head.append(toggle);
    out.push(head);
    if (groupMode) {
      // Picking is easier on small cards.
      out.push(el('p', 'muted small', 'Tick the repositories that belong together (an app and its backend, a monorepo split in two), then name the project.'));
      const grid = el('div', 'cat-grid');
      const biggest = Math.max(1, ...groups.map((g) => g.bytes));
      for (const g of groups) grid.append(projectCard(g, biggest));
      out.push(grid);
    } else {
      const quiet = groups.filter(isQuiet);
      for (const g of groups) if (!quiet.includes(g)) out.push(repoPanel(g));
      if (quiet.length) {
        const line = el('div', 'quiet-repos');
        line.append(el('span', 'muted small', 'No open pull requests and nothing to clean:'));
        for (const g of quiet) {
          const a = el('a', 'chip quiet chip-link', g.name);
          a.href = '#/dev/g/' + encodeURIComponent(g.id);
          line.append(a);
        }
        out.push(line);
      }
    }
  }
  const shared = devReport.categories.filter((c) => SHARED.includes(c.key));
  if (shared.length) {
    out.push(el('h2', null, 'Shared by all projects'));
    const grid = el('div', 'cat-grid');
    for (const c of shared) {
      const blocked = c.items.filter((i) => i.blocked).length;
      const picked = c.items.filter((i) => devTicks.has(i.id) && !i.blocked);
      grid.append(sharedCard(c, blocked, picked));
    }
    out.push(grid);
  }
  if (!groups.length && !shared.length) out.push(el('div', 'empty-state', 'Nothing to clean up.'));
  return out;
}

// A repository whose host may have pull requests: git, with a remote.
const hostedRepo = (r) => !!(r.source && r.source.vcs === 'git' && r.source.remote);

/** Nothing to clean, and no pull requests open, failed or still being asked for. */
function isQuiet(g) {
  if (g.items.length || g.merged || g.open) return false;
  if (stewardRole()) return true;
  return g.repos.every((r) => pullsOf(r) ? !pullsOf(r).error : !(hostedRepo(r) && (devPullsLoading || !devPulls)));
}

function repoPanel(g) {
  const multi = g.repos.length > 1;
  const box = el('section', 'dev-section repo-panel');
  const head = el('div', 'dev-section-head');
  const glyph = el('span', 'cat-icon');
  glyph.append(icon(g.project ? 'stack' : 'branch'));
  head.append(glyph);
  const title = el('div', 'dev-section-title');
  const name = el('a', 'repo-name', g.name);
  name.href = '#/dev/g/' + encodeURIComponent(g.id);
  const src = g.repos[0].source;
  title.append(name, el('span', 'muted', g.project ? ' · ' + count(g.repos.length, 'repository', 'repositories')
    : src ? ' · ' + (src.remote ? shortRemote(src.remote) : VCS_NAMES[src.vcs] || src.vcs) : ''));
  head.append(title);
  box.append(head);
  name.title = g.repos.map((r) => r.path).join('\n');
  const cols = el('div', 'repo-cols');
  // Where the Steward merges pull requests, the panel is its cleanup alone.
  if (!stewardRole()) cols.append(pullColumn(g, multi));
  cols.append(cleanColumn(g, multi));
  box.append(cols);
  return box;
}

function colHead(glyph, title, extra) {
  const head = el('div', 'repo-col-head');
  head.append(icon(glyph), el('span', null, title));
  if (extra) head.append(el('span', 'muted', '· ' + extra));
  return head;
}

/** The overview's pull requests of a repository: the first few, waiting on you first; its page has them all. */
function pullColumn(g, multi) {
  const col = el('div', 'repo-col');
  const known = g.repos.map((r) => [r, pullsOf(r)]).filter(([, p]) => p);
  col.append(colHead('pull', 'Pull requests', known.length ? known.reduce((a, [, p]) => a + p.total, 0) + ' open' : ''));
  if (known.length) col.append(...pullList(known, multi, OVERVIEW_PULLS, g));
  else if (!devPulls) col.append(el('div', 'muted small', !g.repos.some(hostedRepo) ? 'No remote to ask.' : devPullsLoading ? 'Asking its host…' : 'Not asked yet: Refresh pull requests asks.'));
  else {
    const remote = g.repos.map((r) => r.source && r.source.remote).find(Boolean);
    col.append(el('div', 'muted small', remote ? shortRemote(remote) + ' isn\'t a host Heiward reads pull requests from.' : 'No remote to ask.'));
  }
  return col;
}

/** The overview's cleanup of a repository: ticked and biggest first, then its merged branches with their Prune button. */
function cleanColumn(g, multi) {
  const col = el('div', 'repo-col');
  col.append(colHead('layers', 'Cleanup', g.bytes ? bytes(g.bytes) : ''));
  const on = (i) => devTicks.has(i.id) && !i.blocked;
  const items = [...g.items].sort((a, b) => on(b) - on(a) || !!a.blocked - !!b.blocked || b.bytes - a.bytes);
  const branches = g.branches.filter((r) => r.merged.length);
  if (!items.length && !branches.length) {
    col.append(el('div', 'muted small', 'Nothing to clean.'));
    return col;
  }
  const list = el('div', 'dev-list');
  for (const i of items.slice(0, OVERVIEW_ITEMS)) list.append(devRow(i, multi, true));
  for (const r of branches) list.append(branchRow(r, true, multi));
  col.append(list);
  const rest = items.slice(OVERVIEW_ITEMS);
  if (rest.length) col.append(pageLink(g, 'And ' + count(rest.length, 'more to clean', 'more to clean') + ', ' + bytes(devSize(rest))));
  return col;
}

/** A link to a repository's (or a project's) own page. */
function pageLink(g, text) {
  const a = el('a', 'link small', text);
  a.href = '#/dev/g/' + encodeURIComponent(g.id);
  return a;
}

function projectCard(g, biggest) {
  const card = el(groupMode ? 'label' : 'button', 'cat-card' + (groupMode && g.repos.every((r) => groupPick.has(r.key)) ? ' picked' : ''));
  const top = el('div', 'cat-top');
  if (groupMode) {
    const box = el('input');
    box.type = 'checkbox';
    box.checked = g.repos.every((r) => groupPick.has(r.key));
    box.addEventListener('change', () => {
      for (const r of g.repos) if (box.checked) groupPick.add(r.key); else groupPick.delete(r.key);
      renderDev();
    });
    top.append(box);
  }
  const glyph = el('span', 'cat-icon big');
  glyph.append(g.project ? icon('stack') : icon('branch'));
  top.append(glyph);
  const text = el('div');
  text.style.minWidth = '0';
  text.append(el('div', 'cat-title', g.name), el('div', 'cat-amount', g.bytes >= 1 << 20 ? bytes(g.bytes) : '–'));
  top.append(text);
  card.append(top);
  const bar = el('div', 'usage');
  const fill = el('div', 'fill');
  fill.style.width = Math.max(1.5, (100 * g.bytes) / biggest) + '%';
  bar.append(fill);
  card.append(bar);
  // Its pull requests first: those waiting to be merged, then reviewed, then the rest.
  if (g.open) {
    const pulls = el('div', 'chips');
    const merge = g.pulls.filter((p) => p.waits === 'merge').length, review = g.pulls.filter((p) => p.waits === 'review').length;
    if (merge) pulls.append(el('span', 'chip good', merge + ' ready to merge'));
    if (review) pulls.append(el('span', 'chip accent', review + ' to review'));
    if (g.open - merge - review > 0) pulls.append(el('span', 'chip quiet', count(g.open - merge - review, 'other pull request', 'other pull requests')));
    card.append(pulls);
  }
  const parts = [];
  if (g.project) parts.push(count(g.repos.length, 'repository', 'repositories'));
  else if (g.repos[0].source && g.repos[0].source.vcs !== 'git') parts.push(VCS_NAMES[g.repos[0].source.vcs] || g.repos[0].source.vcs);
  if (g.outputs.length) parts.push('build outputs');
  if (g.orphans.length) parts.push('build outputs without a project');
  if (g.worktrees.length) parts.push(count(g.worktrees.length, 'worktree', 'worktrees'));
  if (g.merged) parts.push(count(g.merged, 'merged branch', 'merged branches'));
  card.append(el('div', 'muted small', parts.join(' · ') || 'Nothing to clean'));
  const picked = g.items.filter((i) => devTicks.has(i.id) && !i.blocked);
  if (picked.length) card.append(el('span', 'chip good', bytes(devSize(picked)) + ' selected'));
  if (!groupMode) card.addEventListener('click', () => { location.hash = '#/dev/g/' + encodeURIComponent(g.id); });
  return card;
}

function sharedCard(c, blocked, picked) {
  const card = el('button', 'cat-card');
  const top = el('div', 'cat-top');
  top.append(catIcon(c.key, true));
  const text = el('div');
  text.style.minWidth = '0';
  text.append(el('div', 'cat-title', DEV_META[c.key].short), el('div', 'cat-amount', bytes(devSize(c.items))));
  top.append(text);
  card.append(top);
  card.append(el('div', 'muted small', count(c.items.length, 'item', 'items') + (blocked ? ' · ' + blocked + ' in use or kept' : '')));
  if (picked.length) card.append(el('span', 'chip good', bytes(devSize(picked)) + ' selected'));
  card.addEventListener('click', () => { location.hash = '#/dev/s/' + c.key; });
  return card;
}

/** While grouping: how many repositories are picked, and the button that bundles them. */
function groupBar() {
  const bar = el('div', 'sel-bar show');
  const text = el('div', 'sel-text');
  text.append(el('div', 'sel-count', groupPick.size ? count(groupPick.size, 'repository', 'repositories') + ' picked' : 'Pick two or more repositories'));
  text.append(el('div', 'muted small', 'A repository belongs to one project; picking a project\'s card picks all of its repositories.'));
  bar.append(text);
  const cancel = el('button', 'btn secondary', 'Cancel');
  cancel.addEventListener('click', () => { groupMode = false; groupPick.clear(); renderDev(); });
  const make = el('button', 'btn', 'Group as one project');
  make.disabled = groupPick.size < 2;
  make.addEventListener('click', groupPicked);
  bar.append(cancel, make);
  return bar;
}

async function groupPicked() {
  const groups = devGroups();
  const pickedRepos = groups.flatMap((g) => g.repos).filter((r) => groupPick.has(r.key));
  const fromProject = groups.find((g) => g.project && g.repos.some((r) => groupPick.has(r.key)));
  const name = (prompt('Name for the project (' + pickedRepos.map((r) => r.name).join(', ') + '):', fromProject ? fromProject.name : pickedRepos[0].name) || '').trim();
  if (!name) return;
  // Picked repositories leave the projects they were in; the new project takes them all.
  const next = devProjects
    .map((p) => ({ name: p.name, repos: p.repos.filter((r) => !groupPick.has(rkey(r))) }))
    .filter((p) => p.name.toLowerCase() !== name.toLowerCase());
  next.push({ name, repos: pickedRepos.map((r) => r.path) });
  await saveProjects(next);
  groupMode = false;
  groupPick.clear();
  location.hash = '#/dev/g/' + encodeURIComponent('p:' + name.toLowerCase());
  renderDev();
}

async function saveProjects(next) {
  try {
    devProjects = await post('/api/dev/projects', next);
  } catch (e) { showError(e.message); }
}

// ---- a project's page

function groupPage(g) {
  const chips = g.bytes ? [el('span', 'chip', bytes(g.bytes))] : [];
  if (g.merged) chips.push(el('span', 'chip quiet', count(g.merged, 'merged branch', 'merged branches')));
  const picked = g.items.filter((i) => devTicks.has(i.id) && !i.blocked);
  if (picked.length) chips.push(el('span', 'chip good', bytes(devSize(picked)) + ' selected'));
  let intro;
  if (g.project) {
    intro = el('div', 'project-repos');
    intro.append(el('span', 'dev-intro', 'A project of ' + count(g.repos.length, 'repository', 'repositories') + ':'));
    for (const r of g.repos) {
      const chip = el('span', 'chip quiet repo-chip');
      chip.append(el('span', null, r.name));
      chip.title = r.path;
      chip.addEventListener('contextmenu', (e) => openPathMenu(e, { name: r.name, path: r.path, folder: true }));
      const x = el('button', 'chip-x', '×');
      x.title = 'Take ' + r.name + ' out of ' + g.name;
      x.setAttribute('aria-label', x.title);
      x.addEventListener('click', async () => {
        await saveProjects(devProjects.map((p) => p === g.project ? { name: p.name, repos: p.repos.filter((q) => rkey(q) !== r.key) } : p));
        if (!devProjects.some((p) => p.name === g.name)) location.hash = '#/dev';
        renderDev();
      });
      chip.append(x);
      intro.append(chip);
    }
    const ungroup = el('button', 'link small', 'Ungroup');
    ungroup.addEventListener('click', async () => {
      await saveProjects(devProjects.filter((p) => p !== g.project));
      location.hash = '#/dev';
      renderDev();
    });
    intro.append(ungroup);
  } else {
    intro = g.repos[0].path;
    // Its version control, and where it's hosted.
    const src = g.repos[0].source;
    if (src) chips.unshift(el('span', 'chip quiet', (VCS_NAMES[src.vcs] || src.vcs) + (src.remote ? ' · ' + shortRemote(src.remote) : '')));
  }
  const out = [devHeader(g.project ? bigIcon('stack') : bigFolder(), g.name, intro, chips)];
  const multi = g.repos.length > 1;
  // Its open pull requests, then its cleanup.
  const pulls = pullSection(g, multi);
  if (pulls) out.push(pulls);
  if (g.outputs.length) out.push(devSection('projects', g.outputs, multi));
  if (g.orphans.length) out.push(devSection('orphans', g.orphans, multi));
  if (g.worktrees.length) out.push(devSection('worktrees', g.worktrees, multi));
  if (g.branches.length) out.push(branchSection(g.branches));
  if (!g.items.length && !g.branches.length) out.push(el('div', 'empty-state', 'Nothing to clean in this ' + (g.project ? 'project.' : 'repository.')));
  return out;
}

/** "github.com/owner/repo" from https://github.com/owner/repo.git or git@github.com:owner/repo.git. */
function shortRemote(url) {
  return url.replace(/^[a-z][a-z0-9+.-]*:\/\//i, '').replace(/^[^@/]+@/, '').replace(/^([^:/]+):(?!\d)/, '$1/').replace(/\.git$/i, '');
}

/**
 * The open pull requests of a repository, or of a project's repositories: those ready to merge and those waiting for
 * a review first. Each opens on its host, where it's merged or reviewed. Null for a repository without a known host.
 */
function pullSection(g, multi) {
  if (stewardRole()) return null;
  const known = g.repos.map((r) => [r, pullsOf(r)]).filter(([, p]) => p);
  if (!known.length && !(devPullsLoading && g.repos.some(hostedRepo))) return null;
  const box = el('section', 'dev-section');
  const head = el('div', 'dev-section-head');
  const glyph = el('span', 'cat-icon');
  glyph.append(icon('pull'));
  head.append(glyph);
  const title = el('div', 'dev-section-title');
  const open = known.reduce((a, [, p]) => a + p.total, 0);
  title.append(el('span', null, 'Pull requests'), el('span', 'muted', known.length ? ' · ' + open + ' open' : ''));
  head.append(title);
  const again = el('button', 'link small', devPullsLoading ? 'Asking…' : 'Refresh');
  again.disabled = devPullsLoading;
  again.addEventListener('click', () => loadDevPulls(true));
  head.append(again);
  box.append(head);
  const hosts = [...new Set(known.map(([, p]) => p.host))];
  box.append(el('p', 'muted small', !known.length ? 'Asking where it\'s hosted…'
    : 'On ' + hosts.join(' and ') + (devPulls ? ', as of ' + ago(devPulls.fetchedUtc) : '') + '. Each opens there, to merge or review it.'));
  box.append(...pullList(known, multi, Infinity, null));
  return box;
}

/**
 * Why a host couldn't be asked, then the pull requests (those ready to merge or waiting for a review first) up to
 * limit, then where the rest are: on the page of g past the limit, on the host past what it sent.
 */
function pullList(known, multi, limit, g) {
  const out = [];
  for (const [r, p] of known) if (p.error) out.push(el('div', 'result small bad', (multi ? r.name + ': ' : '') + p.error));
  const rows = known.flatMap(([r, p]) => p.pulls.map((pr) => [r, p, pr]))
    .sort((a, b) => WAIT_ORDER.indexOf(a[2].waits) - WAIT_ORDER.indexOf(b[2].waits) || (b[2].asksYou - a[2].asksYou) || String(b[2].updatedUtc).localeCompare(String(a[2].updatedUtc)));
  if (rows.length) {
    const list = el('div', 'dev-list');
    for (const [r, p, pr] of rows.slice(0, limit)) list.append(pullRow(pr, p.host, multi ? r.name : null));
    out.push(list);
  } else if (known.some(([, p]) => !p.error)) out.push(el('div', 'muted small', 'No open pull requests.'));
  if (rows.length > limit) out.push(pageLink(g, 'And ' + (rows.length - limit) + ' more'));
  for (const [r, p] of known)
    if (p.total > p.pulls.length) {
      const more = el('a', 'link small', 'And ' + (p.total - p.pulls.length) + ' more on ' + p.host + (multi ? ' (' + r.name + ')' : ''));
      more.href = p.web;
      more.target = '_blank';
      more.rel = 'noopener noreferrer';
      out.push(more);
    }
  return out;
}

function pullRow(p, host, repoName) {
  const row = el('div', 'dev-item pull-item');
  const main = el('div', 'dev-item-main');
  const name = el('div', 'dev-item-name');
  const link = el('a', 'pull-title', p.title || p.ref);
  link.href = p.url;
  link.target = '_blank';
  link.rel = 'noopener noreferrer';
  const [label, tone] = WAITS[p.waits] || [p.waits, 'quiet'];
  name.append(link, el('span', 'tag pull-' + tone, p.waits === 'review' && p.asksYou ? 'Your review is asked for' : label));
  main.append(name);
  main.append(el('div', 'muted small', [repoName, p.ref, p.head && p.base ? p.head + ' → ' + p.base : '', p.yours ? 'yours' : p.author ? 'by ' + p.author : '',
    p.updatedUtc ? 'updated ' + ago(p.updatedUtc) : ''].filter(Boolean).join(' · ')));
  row.append(main);
  const side = el('div', 'dev-item-side');
  const go = el('a', waitsOnYou(p) ? 'btn' : 'btn secondary', (p.waits === 'merge' ? 'Merge' : p.waits === 'review' ? 'Review' : 'Open') + ' ↗');
  go.href = p.url;
  go.target = '_blank';
  go.rel = 'noopener noreferrer';
  go.title = 'Opens it on ' + host;
  side.append(go);
  row.append(side);
  return row;
}

function bigIcon(name) {
  const box = el('span', 'cat-icon big hero');
  box.append(icon(name));
  return box;
}

function bigFolder() {
  const f = folderIcon(false);
  f.classList.add('hero-folder');
  return f;
}

/** One cleanup area of a project: its items, ticked ones first to clean, the blocked ones folded. */
function devSection(k, items, showRepo) {
  const box = el('section', 'dev-section');
  const head = el('div', 'dev-section-head');
  head.append(catIcon(k));
  const title = el('div', 'dev-section-title');
  title.append(el('span', null, DEV_META[k].short), el('span', 'muted', ' · ' + bytes(devSize(items))));
  head.append(title);
  const open = items.filter((i) => !i.blocked);
  if (open.length > 1) {
    const allOn = open.every((i) => devTicks.has(i.id));
    const toggle = el('button', 'link small', allOn ? 'Select none' : 'Select all');
    toggle.addEventListener('click', () => { for (const i of open) if (allOn) devTicks.delete(i.id); else devTicks.add(i.id); renderDev(); });
    head.append(toggle);
  }
  box.append(head);
  // Where Reeve keeps git state, its worktrees are listed for the space they take, read-only, with who removes them.
  const reeve = k === 'worktrees' ? reeveRole() : null;
  if (reeve) box.append(el('p', 'muted small', 'Extra working folders, and the space they take.'), roleNote(reeve));
  else box.append(el('p', 'muted small', DEV_META[k].blurb));
  if (open.length) {
    const list = el('div', 'dev-list');
    for (const i of open) list.append(devRow(i, showRepo));
    box.append(list);
  }
  const blocked = items.filter((i) => i.blocked);
  if (reeve && blocked.length) {
    const list = el('div', 'dev-list');
    for (const i of blocked) list.append(devRow(i, showRepo));
    box.append(list);
    return box;
  }
  if (blocked.length) {
    const group = el('details', 'blocked-group');
    group.append(el('summary', null, count(blocked.length, 'item', 'items') + ' in use or kept, ' + bytes(devSize(blocked))));
    const list = el('div', 'dev-list');
    for (const i of blocked) list.append(devRow(i, showRepo));
    group.append(list);
    box.append(group);
  }
  return box;
}

function branchSection(branches) {
  const box = el('section', 'dev-section');
  const head = el('div', 'dev-section-head');
  head.append(catIcon('repos'));
  const merged = branches.reduce((a, b) => a + b.merged.length, 0);
  const title = el('div', 'dev-section-title');
  title.append(el('span', null, DEV_META.repos.short), el('span', 'muted', ' · ' + count(merged, 'branch', 'branches')));
  head.append(title);
  box.append(head);
  box.append(el('p', 'muted small', DEV_META.repos.blurb + ' Prune fetches first; hover it to see why it\'s safe.'));
  const list = el('div', 'dev-list');
  for (const r of branches) list.append(branchRow(r));
  box.append(list);
  return box;
}

/**
 * A repository's merged branches, and the Prune button that deletes them. On the overview, among build outputs and
 * worktrees, it says what they are (and in which repository, for a project) and names fewer.
 */
function branchRow(r, overview, multi) {
  const row = el('div', 'dev-item repo-item');
  const main = el('div', 'dev-item-main');
  const names = overview ? 6 : 12;
  main.append(el('div', 'dev-item-name', !overview ? r.name : count(r.merged.length, 'merged branch', 'merged branches') + (multi ? ' in ' + r.name : '')));
  main.append(el('div', 'muted small', r.default
    ? count(r.localBranches, 'local branch', 'local branches') + ' · compared with ' + r.default
    : r.note || ''));
  if (r.merged.length) {
    const chips = el('div', 'chips');
    for (const b of r.merged.slice(0, names)) chips.append(el('span', 'chip quiet', b));
    if (r.merged.length > names) chips.append(el('span', 'chip quiet', '+' + (r.merged.length - names) + ' more'));
    main.append(chips);
  }
  if (r.checkedOut.length) main.append(el('div', 'muted small', 'Kept, checked out in a worktree: ' + r.checkedOut.join(', ')));
  const auto = state.auto.repos[r.id];
  if (auto && (auto.dueUtc || auto.held)) main.append(autoLine(auto, 'b:' + r.id, 'Merged branches are deleted automatically, the first ' + dueText(auto.dueUtc, true) + '.'));
  if (pruneResults.has(r.id)) main.append(pruneResult(pruneResults.get(r.id)));
  row.append(main);
  const side = el('div', 'dev-item-side');
  const btn = el('button', r.merged.length ? 'btn' : 'btn secondary', r.merged.length ? 'Prune ' + r.merged.length : 'Nothing to prune');
  btn.disabled = !r.merged.length || devBusy;
  btn.addEventListener('click', () => pruneRepo(r));
  side.append(pruneTip(r, btn));
  row.append(side);
  return row;
}

// ---- a shared category's page

function categoryPage(c) {
  const items = c.items;
  const picked = items.filter((i) => devTicks.has(i.id) && !i.blocked);
  const chips = [el('span', 'chip', bytes(devSize(items))), el('span', 'chip quiet', count(items.length, 'item', 'items'))];
  if (picked.length) chips.push(el('span', 'chip good', bytes(devSize(picked)) + ' selected'));
  return [devHeader(bigIcon(DEV_META[c.key].icon), c.title, c.explain, chips), devSection(c.key, items, false)];
}

/** One thing to clean, with its tick box. Compact (the overview): its facts on one line, its folder on hover. */
function devRow(i, showRepo, compact) {
  const row = el('label', 'dev-item' + (i.blocked ? ' blocked' : '') + (devTicks.has(i.id) && !i.blocked ? ' on' : ''));
  const box = el('input');
  box.type = 'checkbox';
  box.checked = devTicks.has(i.id) && !i.blocked;
  box.disabled = !!i.blocked || devBusy;
  box.addEventListener('change', () => { if (box.checked) devTicks.add(i.id); else devTicks.delete(i.id); renderDev(); });
  row.append(box);
  const main = el('div', 'dev-item-main');
  const name = el('div', 'dev-item-name');
  // A project's build outputs are one row per repository: name it by what's in it, not the repository twice.
  name.append(el('span', null, i.kind === 'projects' && !showRepo ? 'Build outputs' : i.name));
  if (i.blocked) name.append(el('span', 'tag', i.blocked));
  else if (i.suggested) name.append(el('span', 'tag suggested', 'Suggested'));
  main.append(name);
  const auto = state.auto.devItems[i.id];
  if (auto && (auto.dueUtc || auto.held)) main.append(autoLine(auto, 'd:' + i.id, 'Deleted automatically ' + dueText(auto.dueUtc, true) + '.'));
  if (compact) row.title = i.location;
  else {
    const where = el('div', 'folder muted small', i.location);
    where.title = i.location;
    main.append(where);
  }
  const places = i.paths || [];
  if (places.length) row.addEventListener('contextmenu', (e) => openPathMenu(e, { name: i.name, path: places[0], folder: true, more: places.length - 1 }));
  // On a single repository's page, 'worktree of <it>' says nothing new.
  const facts = (i.detail || '').split(/, | · /).filter((f) => f && !(i.kind === 'worktrees' && !showRepo && f.startsWith('worktree of ')));
  if (facts.length && compact) main.append(el('div', 'muted small', facts.join(' · ')));
  else if (facts.length) {
    const chips = el('div', 'chips');
    for (const f of facts) chips.append(el('span', 'chip quiet', f));
    main.append(chips);
  }
  row.append(main);
  const side = el('div', 'dev-item-side');
  side.append(el('div', 'dev-size', bytes(i.bytes)));
  if (i.lastUsedUtc) side.append(el('div', 'muted small', lastUsed(i.lastUsedUtc)));
  row.append(side);
  return row;
}

const pruneResults = new Map(); // repository id -> the prune's answer ({ deleted, kept, fetched } or { error })

/** What a Prune did, one branch per line: those deleted, then any kept and why. */
function pruneResult(res) {
  const box = el('div', 'result small ' + (res.error ? 'bad' : 'ok'));
  if (res.error) {
    box.textContent = res.error;
    return box;
  }
  const list = (title, lines) => {
    const ul = el('ul');
    for (const line of lines) ul.append(el('li', null, line));
    box.append(el('div', null, title), ul);
  };
  if (res.deleted.length) list('Deleted ' + count(res.deleted.length, 'branch', 'branches') + ':', res.deleted);
  else box.append(el('div', null, 'No merged branches to delete.'));
  if (res.kept.length) list('Kept ' + count(res.kept.length, 'branch', 'branches') + ':', res.kept.map((k) => k.branch + ' (' + k.reason + ')'));
  if (!res.fetched) box.append(el('div', null, 'Couldn\'t fetch, so the last fetched state was used.'));
  return box;
}

/** The Prune button with a tooltip (hover or keyboard focus) saying what it does and why nothing is lost. */
function pruneTip(r, btn) {
  const wrap = el('span', 'tip');
  const body = el('div', 'tip-body');
  body.id = 'prune-tip-' + r.id;
  body.setAttribute('role', 'tooltip');
  btn.setAttribute('aria-describedby', body.id);
  const target = r.default || 'the remote\'s main branch';
  body.append(el('div', 'tip-title', r.merged.length
    ? 'Deletes ' + count(r.merged.length, 'local branch', 'local branches') + ' in ' + r.name
    : 'Nothing to prune in ' + r.name));
  body.append(el('p', null, 'Branches whose work is already in ' + target + ': they were merged, so they only clutter your branch list.'));
  body.append(el('div', 'tip-title', 'Why it\'s safe'));
  const why = el('ul');
  for (const line of [
    'It fetches first, so "merged" is judged against the remote as it is now.',
    'Every commit on these branches is already in ' + target + '. Nothing is lost, and you can recreate any of them from there.',
    'main, master, develop and branches checked out in a worktree are never deleted.',
    'Only your local copies go: nothing on the remote changes.',
    'git does the deleting (git branch -d), which refuses anything unmerged.',
  ]) why.append(el('li', null, line));
  body.append(why);
  wrap.append(btn, body);
  // Above the button when it fits in the scrolling view, otherwise below: the view scrolls down to
  // whatever doesn't fit there, while a tooltip clipped at the top can't be read.
  const place = () => {
    const view = wrap.closest('.content, .view');
    if (!view) return;
    const above = wrap.getBoundingClientRect().top - view.getBoundingClientRect().top;
    wrap.classList.toggle('below', above < body.offsetHeight + 12);
  };
  wrap.addEventListener('mouseenter', place);
  wrap.addEventListener('focusin', place);
  return wrap;
}

async function pruneRepo(r) {
  // The branches are on the page beside the button: the count is enough here.
  if (!confirm('Delete ' + count(r.merged.length, 'local branch', 'local branches') + ' in ' + r.name + ', merged into ' + r.default + '?\n\n' +
      'It fetches first, so the count can change. The commits stay in ' + r.default + '; branches on the remote are not touched.')) return;
  devBusy = true;
  devProgress = 'Merged branches in ' + r.name;
  renderDev();
  try {
    pruneResults.set(r.id, await post('/api/dev/repos/' + encodeURIComponent(r.id) + '/prune'));
  } catch (e) {
    pruneResults.set(r.id, { error: e.message });
  }
  devBusy = false;
  devProgress = '';
  await loadDevReport();
  renderDev();
  refresh(true);
}

/** Appears once something is ticked, on any page: the one place to clean from. */
function selectionBar() {
  const picked = devPicked();
  const bar = el('div', 'sel-bar' + (picked.length || devBusy ? ' show' : ''));
  if (!picked.length && !devBusy) return bar;
  const text = el('div', 'sel-text');
  const where = [...new Set(picked.map((i) => {
    if (i.kind === 'projects') return i.name;
    if (i.kind === 'worktrees' || i.kind === 'orphans') return (i.repo || '').split(SEP).pop();
    const c = devReport.categories.find((x) => x.items.includes(i));
    return c ? DEV_META[c.key].short : '';
  }))].filter(Boolean);
  text.append(el('div', 'sel-count', devBusy ? 'Cleaning…' : count(picked.length, 'item', 'items') + ' selected · ' + bytes(devSize(picked))));
  const status = el('div', 'muted small');
  status.id = 'dev-status';
  status.textContent = devBusy ? devProgress : 'in ' + where.slice(0, 4).join(', ') + (where.length > 4 ? ' and ' + (where.length - 4) + ' more' : '');
  text.append(status);
  bar.append(text);
  const clear = el('button', 'btn secondary', 'Clear');
  clear.disabled = devBusy;
  clear.addEventListener('click', () => { for (const i of picked) devTicks.delete(i.id); renderDev(); });
  const clean = el('button', 'btn', devBusy ? 'Cleaning…' : 'Clean ' + bytes(devSize(picked)));
  clean.disabled = devBusy;
  clean.addEventListener('click', () => cleanDev(picked));
  bar.append(clear, clean);
  return bar;
}

async function cleanDev(picked) {
  const size = devSize(picked);
  const lines = ['Delete ' + count(picked.length, 'item', 'items') + ' (' + bytes(size) + ') permanently?',
    'Tools recreate them when needed; the next build or install takes longer.'];
  const worktrees = picked.filter((i) => i.kind === 'worktrees').length;
  if (worktrees) lines.push(count(worktrees, 'worktree folder is', 'worktree folders are') + ' removed by git; the branches stay.');
  if (picked.some((i) => i.kind === 'avd')) lines.push('Deleting an emulator deletes the apps and data inside it.');
  const orphans = picked.filter((i) => i.kind === 'orphans').length;
  if (orphans) lines.push(count(orphans, 'folder has', 'folders have') + ' bin and obj with no project beside them: be sure nothing in them is yours to keep.');
  if (!confirm(lines.join('\n\n'))) return;
  devBusy = true;
  devResult = null;
  renderDev();
  let freed = 0, left = 0;
  const failed = [], held = [];
  for (const [n, i] of picked.entries()) {
    devProgress = i.name + ' (' + (n + 1) + ' of ' + picked.length + ')';
    const status = $('dev-status');
    if (status) status.textContent = devProgress;
    try {
      const r = await post('/api/dev/items/' + encodeURIComponent(i.id) + '/clean');
      freed += r.freedBytes;
      left += r.leftInUse;
      if (r.error) failed.push(i.name + ' (' + r.error + ')');
      // What was left, and what holds it: a program working in the folder, one with a file open, or Windows' reason.
      if (r.leftInUse) held.push(i.name + ': ' + count(r.leftInUse, 'item', 'items') + ' left in use' +
        (r.leftPath ? ', ' + r.leftPath : '') + (r.heldBy ? ' (' + r.heldBy + ')' : ''));
      devTicks.delete(i.id);
    } catch (e) {
      failed.push(i.name + ' (' + e.message + ')');
    }
  }
  devBusy = false;
  devProgress = '';
  devResult = [!failed.length, 'Freed ' + bytes(freed) + '.' + (left ? ' ' + count(left, 'file or folder was', 'files or folders were') + ' in use and left alone.' : '') +
    (failed.length ? ' Not cleaned:' : ''), [...failed, ...held]];
  await loadDevReport();
  renderDev();
  refresh(true);
}

// ---------------------------------------------------------------- games area

// Game mode: the games every launcher has installed, by game and by drive, and what games and launchers leave behind.
// Laid out like the Developer area: a navigation pane, a page at a time, and a selection bar once something is ticked.
// Everything removed goes to the Recycle Bin; an installed game's files and anyone's saves are never touched (GameSafety).
let gameReport = null;         // /api/games
const gameTicks = new Set();   // ticked item ids
const gameSeen = new Set();    // ids whose default tick was applied (the user's edits survive refreshes)
let gameBusy = false;
let gameResult = null;         // [ok, message, what wasn't removed] after the last removal, shown until the next
let gameProgress = '';         // while removing: what, and how far along

const GAME_META = {
  leftovers: { short: 'Leftovers', icon: 'box' },
  caches: { short: 'Download caches', icon: 'db' },
  shaders: { short: 'Shader caches', icon: 'spark' },
  dumps: { short: 'Crash dumps', icon: 'alert' },
  twice: { short: 'Installed twice', icon: 'stack' },
  idle: { short: 'Not played in months', icon: 'clock' },
};
const LAUNCHERS = {
  steam: 'Steam', epic: 'Epic Games', gog: 'GOG GALAXY', ea: 'EA app', ubisoft: 'Ubisoft Connect', battlenet: 'Battle.net', xbox: 'Xbox',
};

// The Games area's mark: a game controller on an accent tile.
function gamesIcon(cls) {
  return svg('0 0 40 40', [
    ['rect', '4,6,32,28,6', 'fill', 'var(--accent)'],
    ['path', 'M14 15h12a4.5 4.5 0 0 1 4.4 5.5l-1 4.2a2.3 2.3 0 0 1-4 .9L23.5 23h-7l-1.9 2.6a2.3 2.3 0 0 1-4-.9l-1-4.2A4.5 4.5 0 0 1 14 15zM14.5 18v4M12.5 20h4', 'stroke'],
    ['circle', '24.5,19.5,0.9', 'fill'], ['circle', '26.8,21.5,0.9', 'fill'],
  ], cls);
}

function gameCatIcon(k, big) {
  const box = el('span', 'cat-icon' + (big ? ' big' : ''));
  box.append(icon((GAME_META[k] || {}).icon || 'pad'));
  return box;
}

async function startGamesCheck() {
  try { await post('/api/games/scan'); } catch (e) { showError(e.message); }
  refresh(true);
}

/** The home page's games card: what's installed, and what the games left that could go. */
function renderGamesCard(g) {
  const section = $('games-section');
  section.classList.toggle('hidden', !g || !g.enabled);
  if (!g || !g.enabled) { $('games-card').replaceChildren(); return; }
  const card = el('div', 'dev-card');
  const top = el('div', 'drive-top');
  top.append(gamesIcon());
  const text = el('div');
  text.style.flex = '1';
  text.style.minWidth = '0';
  if (!g.scannedAtUtc) {
    text.append(el('div', 'drive-name', 'Your games'),
      el('div', 'muted small', g.running ? 'Looking at your games and launchers…' : 'Your games by launcher and drive, and what they leave behind. Not checked yet.'));
  } else {
    text.append(el('div', 'drive-name', count(g.installed, 'game', 'games') + ' installed, ' + bytes(g.installedBytes)),
      el('div', 'muted small', (g.totalBytes ? bytes(g.totalBytes) + ' left behind · ' : 'Nothing left behind · ') +
        (g.suggestedBytes ? bytes(g.suggestedBytes) + ' ticked to remove · ' : '') + (g.running ? 'checking again…' : 'checked ' + ago(g.scannedAtUtc))));
    const chips = el('div', 'chips');
    chips.style.marginTop = '6px';
    for (const c of g.categories || []) {
      const chip = el('button', 'chip quiet chip-link', ((GAME_META[c.key] || {}).short || c.title) + (c.bytes ? ' ' + bytes(c.bytes) : ' · ' + c.count));
      chip.addEventListener('click', () => { location.hash = '#/games/k/' + c.key; });
      chips.append(chip);
    }
    text.append(chips);
  }
  top.append(text);
  const actions = el('div', 'actions');
  actions.style.marginTop = '0';
  const b = el('button', 'btn', g.scannedAtUtc ? 'Review' : g.running ? 'Checking…' : 'Check now');
  b.disabled = !g.scannedAtUtc && g.running;
  b.addEventListener('click', () => { if (g.scannedAtUtc) location.hash = '#/games'; else startGamesCheck(); });
  actions.append(b);
  top.append(actions);
  card.append(top);
  $('games-card').replaceChildren(card);
}

async function loadGameReport() {
  const res = await fetch('/api/games');
  const data = res.ok ? await res.json() : null;
  gameReport = data ? data.report : null;
  for (const i of gameItems())
    if (!gameSeen.has(i.id)) {
      gameSeen.add(i.id);
      if (i.suggested && !i.blocked && !i.info) gameTicks.add(i.id);
    }
}

const gameItems = () => ((gameReport && gameReport.categories) || []).flatMap((c) => c.items);
const gameSize = (items) => items.reduce((a, i) => a + i.bytes, 0);
const gamePicked = () => gameItems().filter((i) => gameTicks.has(i.id) && !i.blocked && !i.info);
const driveOf = (p) => (p || '').slice(0, 3).toUpperCase();
const gameById = (id) => ((gameReport && gameReport.games) || []).find((g) => g.id === id) || null;
const launcherName = (id) => LAUNCHERS[id] || id || '';

function gameCategoryOf(i) {
  const c = ((gameReport && gameReport.categories) || []).find((x) => x.items.includes(i));
  return c ? c.key : null;
}

/** Each drive with games or leftovers on it: its games, and what's left on it, biggest first. */
function gameDrives() {
  const drives = new Map();
  const of = (root) => {
    if (!drives.has(root)) drives.set(root, { root, games: [], items: [] });
    return drives.get(root);
  };
  for (const g of (gameReport && gameReport.games) || []) of(driveOf(g.folder)).games.push(g);
  for (const i of gameItems()) if (!i.info) of(driveOf(i.location)).items.push(i);
  const total = (d) => d.games.reduce((a, g) => a + g.bytes, 0) + gameSize(d.items);
  return [...drives.values()].sort((a, b) => total(b) - total(a) || a.root.localeCompare(b.root));
}

function renderGames() {
  renderGamesNav();
  renderGamesContent();
}

function renderGamesNav() {
  const rows = [];
  const row = (label, glyph, hash, badge, selected, dim) => {
    const b = el('button', 'tree-row' + (dim ? ' empty' : ''));
    b.style.paddingLeft = '8px';
    glyph.classList.add('glyph');
    b.append(glyph, el('span', 'label', label));
    if (badge) b.append(el('span', 'count', badge));
    if (selected) { b.classList.add('selected'); b.setAttribute('aria-current', 'page'); }
    b.addEventListener('click', () => { location.hash = hash; $('games-nav').classList.remove('open'); });
    rows.push(b);
  };
  row('Overview', icon('pad'), '#/games', null, !route.game && !route.drive && !route.cat);
  if (gameReport && gameReport.scannedAtUtc) {
    if (gameReport.categories.length) rows.push(el('div', 'tree-section', 'What games leave'));
    for (const c of gameReport.categories)
      row(GAME_META[c.key].short, gameCatIcon(c.key), '#/games/k/' + c.key, gameSize(c.items) ? bytes(gameSize(c.items)) : String(c.items.length), route.cat === c.key);
    const drives = gameDrives();
    if (drives.length) rows.push(el('div', 'tree-section', 'Drives'));
    for (const d of drives)
      row(d.root, driveIcon('fixed'), '#/games/d/' + encodeURIComponent(d.root), bytes(d.games.reduce((a, g) => a + g.bytes, 0) + gameSize(d.items)), route.drive === d.root);
    if (gameReport.games.length) rows.push(el('div', 'tree-section', 'Games'));
    for (const g of gameReport.games) {
      const has = gameItems().some((i) => i.game === g.id);
      row(g.name, icon('pad'), '#/games/g/' + encodeURIComponent(g.id), g.bytes ? bytes(g.bytes) : '', route.game === g.id, !has);
    }
  }
  $('games-tree').replaceChildren(...rows);
}

function renderGamesContent() {
  const content = $('games-content');
  const y = content.scrollTop;
  const frag = document.createDocumentFragment();
  const g = state.games;
  if (!gameReport || !gameReport.scannedAtUtc) {
    frag.append(devHeader(gamesIcon(), 'Games', 'Your games by launcher and drive, and what they and their launchers leave behind: leftovers of uninstalled games, download caches, shader caches and crash dumps.', []));
    const empty = el('div', 'empty-state');
    empty.append(el('div', null, g.running ? 'Looking at your games… this takes a minute or so.' : 'Not checked yet.'));
    if (!g.running) {
      const b = el('button', 'btn more', 'Check now');
      b.addEventListener('click', startGamesCheck);
      empty.append(b);
    }
    frag.append(empty);
  } else if (route.cat) {
    const c = gameReport.categories.find((x) => x.key === route.cat);
    frag.append(...(c ? gameCategoryPage(c) : [el('div', 'empty-state', 'Nothing here in the last check.')]));
  } else if (route.drive) {
    const d = gameDrives().find((x) => x.root === route.drive);
    frag.append(...(d ? gameDrivePage(d) : [el('div', 'empty-state', 'No games or leftovers on that drive in the last check.')]));
  } else if (route.game) {
    const game = gameById(route.game);
    frag.append(...(game ? gamePage(game) : [el('div', 'empty-state', 'That game isn\'t in the last check.')]));
  } else {
    frag.append(...gamesOverview(g));
  }
  if (gameResult) {
    const [ok, msg, failed] = gameResult;
    const banner = el('div', 'dev-banner ' + (ok ? 'ok' : 'bad'), msg);
    if (failed && failed.length) {
      const list = el('ul');
      for (const f of failed) list.append(el('li', null, f));
      banner.append(list);
    }
    frag.append(banner);
  }
  frag.append(gameSelectionBar());
  content.replaceChildren(frag);
  content.scrollTop = y;
}

const GAMES_SAFE = 'Nothing is removed until you tick it, and what you remove goes to the Recycle Bin. Heiward never touches an installed game\'s own files or your saved games.';

function gamesOverview(g) {
  const again = el('button', 'link small', g.running ? 'Checking…' : 'Check again');
  again.disabled = g.running;
  again.addEventListener('click', startGamesCheck);
  const left = gameSize(gameItems().filter((i) => !i.info));
  const chips = [el('span', 'chip', count(gameReport.games.length, 'game', 'games') + ' installed · ' + bytes(gameReport.games.reduce((a, x) => a + x.bytes, 0))),
    el('span', 'chip', bytes(left) + ' left behind'),
    el('span', 'chip quiet', 'checked ' + ago(gameReport.scannedAtUtc) + ' in ' + took(gameReport.durationSec)), again];
  const launchers = (gameReport.launchers || []).map(launcherName);
  const intro = el('div');
  intro.append(el('div', 'dev-intro', (launchers.length ? 'Found ' + launchers.join(', ') + '. ' : 'No game launcher found on this PC. ') + GAMES_SAFE));
  const out = [devHeader(gamesIcon(), 'Games', intro, chips)];

  if (gameReport.categories.length) {
    out.push(el('h2', null, 'What your games leave behind'));
    const grid = el('div', 'cat-grid');
    for (const c of gameReport.categories) grid.append(gameKindCard(c));
    out.push(grid);
  }
  const drives = gameDrives();
  if (drives.length) {
    out.push(el('h2', null, 'By drive'));
    const grid = el('div', 'cat-grid');
    for (const d of drives) {
      const card = el('button', 'cat-card');
      const top = el('div', 'cat-top');
      const glyph = el('span', 'cat-icon big');
      glyph.append(driveIcon('fixed'));
      const text = el('div');
      text.style.minWidth = '0';
      text.append(el('div', 'cat-title', d.root), el('div', 'cat-amount', bytes(d.games.reduce((a, x) => a + x.bytes, 0) + gameSize(d.items))));
      top.append(glyph, text);
      card.append(top, el('div', 'muted small', count(d.games.length, 'game', 'games') + (d.items.length ? ' · ' + bytes(gameSize(d.items)) + ' left behind' : '')));
      card.addEventListener('click', () => { location.hash = '#/games/d/' + encodeURIComponent(d.root); });
      grid.append(card);
    }
    out.push(grid);
  }
  if (gameReport.games.length) {
    out.push(el('h2', null, 'Biggest games'));
    const list = el('div', 'dev-list');
    for (const game of gameReport.games.slice(0, 8)) list.append(gameInstallRow(game));
    out.push(list);
    if (gameReport.games.length > 8) out.push(el('p', 'muted small', 'The rest are under Games, in the pane on the left.'));
  }
  if (!gameReport.games.length && !gameReport.categories.length) out.push(el('div', 'empty-state', 'No games, and nothing they left behind.'));
  return out;
}

function gameKindCard(c) {
  const card = el('button', 'cat-card');
  const top = el('div', 'cat-top');
  top.append(gameCatIcon(c.key, true));
  const text = el('div');
  text.style.minWidth = '0';
  text.append(el('div', 'cat-title', GAME_META[c.key].short), el('div', 'cat-amount', bytes(gameSize(c.items))));
  top.append(text);
  card.append(top);
  const blocked = c.items.filter((i) => i.blocked).length;
  card.append(el('div', 'muted small', count(c.items.length, c.key === 'twice' || c.key === 'idle' ? 'game' : 'item', c.key === 'twice' || c.key === 'idle' ? 'games' : 'items') +
    (blocked ? ' · ' + blocked + ' waiting' : '')));
  const picked = c.items.filter((i) => gameTicks.has(i.id) && !i.blocked && !i.info);
  if (picked.length) card.append(el('span', 'chip good', bytes(gameSize(picked)) + ' selected'));
  card.addEventListener('click', () => { location.hash = '#/games/k/' + c.key; });
  return card;
}

/** An installed game: its launcher, size and when it was last played; it opens the game's page. */
function gameInstallRow(game) {
  const row = el('button', 'dev-item game-install');
  const main = el('div', 'dev-item-main');
  main.append(el('div', 'dev-item-name', game.name));
  const where = el('div', 'folder muted small', launcherName(game.launcher) + ' · ' + game.folder);
  where.title = game.folder;
  main.append(where);
  row.append(main);
  const side = el('div', 'dev-item-side');
  side.append(el('div', 'dev-size', bytes(game.bytes)));
  if (game.lastPlayedUtc) side.append(el('div', 'muted small', playedText(game.lastPlayedUtc)));
  row.append(side);
  row.addEventListener('click', () => { location.hash = '#/games/g/' + encodeURIComponent(game.id); });
  row.addEventListener('contextmenu', (e) => openPathMenu(e, { name: game.name, path: game.folder, folder: true }));
  return row;
}

function playedText(iso) {
  const days = (Date.now() - new Date(iso).getTime()) / 86400000;
  if (days < 1) return 'played today';
  if (days < 2) return 'played yesterday';
  if (days < 60) return 'played ' + Math.floor(days) + ' days ago';
  return 'played ' + Math.round(days / 30) + ' months ago';
}

/** A kind of thing games leave, or one game's, or one drive's: its items, ticked ones first, those waiting folded. */
function gameSection(k, items, showWhere) {
  const box = el('section', 'dev-section');
  const head = el('div', 'dev-section-head');
  head.append(gameCatIcon(k));
  const title = el('div', 'dev-section-title');
  const info = items.every((i) => i.info);
  title.append(el('span', null, GAME_META[k].short), el('span', 'muted', ' · ' + (info ? count(items.length, 'game', 'games') : bytes(gameSize(items)))));
  head.append(title);
  const open = items.filter((i) => !i.blocked && !i.info);
  if (open.length > 1) {
    const allOn = open.every((i) => gameTicks.has(i.id));
    const toggle = el('button', 'link small', allOn ? 'Select none' : 'Select all');
    toggle.addEventListener('click', () => { for (const i of open) if (allOn) gameTicks.delete(i.id); else gameTicks.add(i.id); renderGames(); });
    head.append(toggle);
  }
  box.append(head);
  const listed = items.filter((i) => !i.blocked);
  if (listed.length) {
    const list = el('div', 'dev-list');
    for (const i of listed) list.append(gameRow(i, showWhere));
    box.append(list);
  }
  const blocked = items.filter((i) => i.blocked);
  if (blocked.length) {
    const group = el('details', 'blocked-group');
    group.append(el('summary', null, count(blocked.length, 'item waits', 'items wait') + ', ' + bytes(gameSize(blocked))));
    const list = el('div', 'dev-list');
    for (const i of blocked) list.append(gameRow(i, showWhere));
    group.append(list);
    box.append(group);
  }
  return box;
}

/** One thing with its tick box (none for what Heiward only points to): what it is, what removing it means, its size. */
function gameRow(i, showWhere) {
  const on = gameTicks.has(i.id) && !i.blocked && !i.info;
  const row = el(i.info ? 'div' : 'label', 'dev-item' + (i.blocked ? ' blocked' : '') + (on ? ' on' : ''));
  if (!i.info) {
    const box = el('input');
    box.type = 'checkbox';
    box.checked = on;
    box.disabled = !!i.blocked || gameBusy;
    box.addEventListener('change', () => { if (box.checked) gameTicks.add(i.id); else gameTicks.delete(i.id); renderGames(); });
    row.append(box);
  }
  const main = el('div', 'dev-item-main');
  const name = el('div', 'dev-item-name');
  name.append(el('span', null, i.name));
  if (i.blocked) name.append(el('span', 'tag', i.blocked));
  else if (i.suggested) name.append(el('span', 'tag suggested', 'Suggested'));
  else if (i.kind === 'shader' && i.game || i.kind === 'gpu-shader') name.append(el('span', 'tag', 'First launch may stutter'));
  main.append(name);
  main.append(el('div', 'muted small', i.detail + '.'));
  main.append(el('div', 'small game-removing', (i.info ? '' : 'Removing it: ') + i.removing));
  const auto = state.auto.gameItems && state.auto.gameItems[i.id];
  if (auto && (auto.dueUtc || auto.held)) main.append(autoLine(auto, 'm:' + i.id, 'Goes to the Recycle Bin automatically ' + dueText(auto.dueUtc, 'games') + '.'));
  if (showWhere !== false && !(i.kind === 'twice')) {
    const where = el('div', 'folder muted small', i.location);
    where.title = i.location;
    main.append(where);
  }
  const places = i.paths || [];
  if (places.length) row.addEventListener('contextmenu', (e) => openPathMenu(e, { name: i.name, path: places[0], folder: i.kind !== 'dump', more: places.length - 1 }));
  row.append(main);
  const side = el('div', 'dev-item-side');
  side.append(el('div', 'dev-size', bytes(i.bytes)));
  if (i.kind === 'idle' && i.lastUsedUtc) side.append(el('div', 'muted small', playedText(i.lastUsedUtc)));
  else if (i.lastUsedUtc) side.append(el('div', 'muted small', lastUsed(i.lastUsedUtc)));
  row.append(side);
  return row;
}

function gameCategoryPage(c) {
  const picked = c.items.filter((i) => gameTicks.has(i.id) && !i.blocked && !i.info);
  const chips = [el('span', 'chip', bytes(gameSize(c.items))), el('span', 'chip quiet', count(c.items.length, 'item', 'items'))];
  if (picked.length) chips.push(el('span', 'chip good', bytes(gameSize(picked)) + ' selected'));
  return [devHeader(bigGameIcon(GAME_META[c.key].icon), c.title, c.explain, chips), gameSection(c.key, c.items)];
}

function bigGameIcon(name) {
  const box = el('span', 'cat-icon big hero');
  box.append(icon(name));
  return box;
}

/** A drive: the games on it, and what games left on it, kind by kind. */
function gameDrivePage(d) {
  const gamesBytes = d.games.reduce((a, g) => a + g.bytes, 0);
  const glyph = el('span', 'cat-icon big hero');
  glyph.append(driveIcon('fixed'));
  const card = (state.drives || []).find((x) => x.root.toUpperCase() === d.root);
  const chips = [el('span', 'chip', count(d.games.length, 'game', 'games') + ' · ' + bytes(gamesBytes))];
  if (d.items.length) chips.push(el('span', 'chip', bytes(gameSize(d.items)) + ' left behind'));
  if (card) chips.push(el('span', 'chip quiet', bytes(card.freeBytes) + ' free of ' + bytes(card.totalBytes)));
  const out = [devHeader(glyph, (card && card.name) || d.root, 'The games on this drive, and what games and their launchers left on it. ' + GAMES_SAFE, chips)];
  if (d.games.length) {
    const box = el('section', 'dev-section');
    const head = el('div', 'dev-section-head');
    const glyphBox = el('span', 'cat-icon');
    glyphBox.append(icon('pad'));
    head.append(glyphBox);
    const title = el('div', 'dev-section-title');
    title.append(el('span', null, 'Games on it'), el('span', 'muted', ' · ' + bytes(gamesBytes)));
    head.append(title);
    box.append(head, el('p', 'muted small', 'To move a game to another drive, use its launcher: each game\'s page says how.'));
    const list = el('div', 'dev-list');
    for (const g of d.games) list.append(gameInstallRow(g));
    box.append(list);
    out.push(box);
  }
  for (const c of gameReport.categories) {
    const here = d.items.filter((i) => c.items.includes(i));
    if (here.length) out.push(gameSection(c.key, here));
  }
  return out;
}

/** One game: where it is, how big, when last played, how to move it, and everything Heiward found of it. */
function gamePage(game) {
  const chips = [el('span', 'chip', bytes(game.bytes)), el('span', 'chip quiet', launcherName(game.launcher))];
  if (game.lastPlayedUtc) chips.push(el('span', 'chip quiet', playedText(game.lastPlayedUtc)));
  const intro = el('div');
  const where = el('div', 'folder muted small', game.folder);
  where.title = game.folder;
  where.addEventListener('contextmenu', (e) => openPathMenu(e, { name: game.name, path: game.folder, folder: true }));
  intro.append(where, el('div', 'dev-intro', game.advice + ' Heiward never touches the game\'s own files or its saves.'));
  const glyph = el('span', 'cat-icon big hero');
  glyph.append(icon('pad'));
  const out = [devHeader(glyph, game.name, intro, chips)];
  const mine = gameItems().filter((i) => i.game === game.id || (i.kind === 'twice' && (i.paths || []).some((p) => p.toLowerCase() === game.folder.toLowerCase())));
  for (const c of gameReport.categories) {
    const here = mine.filter((i) => c.items.includes(i));
    if (here.length) out.push(gameSection(c.key, here));
  }
  if (!mine.length) out.push(el('p', 'muted small', 'Nothing else of it to look at: no shader cache, crash dumps or second copy found.'));
  return out;
}

/** Appears once something is ticked, on any page: the one place to remove from. */
function gameSelectionBar() {
  const picked = gamePicked();
  const bar = el('div', 'sel-bar' + (picked.length || gameBusy ? ' show' : ''));
  if (!picked.length && !gameBusy) return bar;
  const text = el('div', 'sel-text');
  text.append(el('div', 'sel-count', gameBusy ? 'Moving to the Recycle Bin…' : count(picked.length, 'item', 'items') + ' selected · ' + bytes(gameSize(picked))));
  const status = el('div', 'muted small');
  status.id = 'games-status';
  const kinds = [...new Set(picked.map((i) => (GAME_META[gameCategoryOf(i)] || {}).short).filter(Boolean))];
  status.textContent = gameBusy ? gameProgress : kinds.join(', ');
  text.append(status);
  bar.append(text);
  const clear = el('button', 'btn secondary', 'Clear');
  clear.disabled = gameBusy;
  clear.addEventListener('click', () => { for (const i of picked) gameTicks.delete(i.id); renderGames(); });
  const go = el('button', 'btn', gameBusy ? 'Moving…' : 'Move ' + bytes(gameSize(picked)) + ' to the Recycle Bin');
  go.disabled = gameBusy;
  go.addEventListener('click', () => removeGames(picked));
  bar.append(clear, go);
  return bar;
}

async function removeGames(picked) {
  const lines = ['Move ' + count(picked.length, 'item', 'items') + ' (' + bytes(gameSize(picked)) + ') to the Recycle Bin?',
    'You can restore them from there until it\'s emptied. Installed games\' files and saved games are never touched.'];
  const stutter = picked.filter((i) => (i.kind === 'shader' && i.game) || i.kind === 'gpu-shader').length;
  if (stutter) lines.push(count(stutter, 'shader cache is', 'shader caches are') + ' of games you have: they rebuild, but the first launch of those games may stutter for a while.');
  if (picked.some((i) => i.kind === 'paused')) lines.push('A paused download starts again from the beginning.');
  if (!confirm(lines.join('\n\n'))) return;
  gameBusy = true;
  gameResult = null;
  renderGames();
  let freed = 0;
  const failed = [];
  for (const [n, i] of picked.entries()) {
    gameProgress = i.name + ' (' + (n + 1) + ' of ' + picked.length + ')';
    const status = $('games-status');
    if (status) status.textContent = gameProgress;
    try {
      const r = await post('/api/games/items/' + encodeURIComponent(i.id) + '/remove');
      freed += r.freedBytes;
      if (r.error) failed.push(i.name + ' (' + r.error + ')');
      else if (r.leftInUse) failed.push(i.name + ': ' + count(r.leftInUse, 'part', 'parts') + ' left' + (r.heldBy ? ' (' + r.heldBy + ')' : ''));
      gameTicks.delete(i.id);
    } catch (e) {
      failed.push(i.name + ' (' + e.message + ')');
    }
  }
  gameBusy = false;
  gameProgress = '';
  gameResult = [!failed.length, 'Moved ' + bytes(freed) + ' to the Recycle Bin.' + (failed.length ? ' Not moved:' : ''), failed];
  await loadGameReport();
  renderGames();
  refresh(true);
}

/**
 * The running scan reads this drive (or folder): its card waits for the scan's end. Until the scan says which
 * it reads, every drive but those scanned only when asked.
 */
const normRoot = (p) => p.toLowerCase().replace(/\\+$/, '');

/** The same folder, or one inside the other: a card's drive or folder, and what a scan reads on it. */
function overlaps(a, b) {
  a = normRoot(a);
  b = normRoot(b);
  return a === b || a.startsWith(b + '\\') || b.startsWith(a + '\\');
}

function scanReads(d) {
  if (!state.scan.running) return false;
  // A scan still starting has no status yet; the server says what it will read.
  const roots = (state.scan.status && state.scan.status.roots) || state.scan.roots;
  if (!roots) return !d.onRequest;
  return roots.some((r) => overlaps(d.root, r));
}

/**
 * How far the scan has got on this card's drive: [what it's doing, fraction done], the fraction null
 * while there's nothing to count (the bar then sweeps). A scan finds the files on every drive at once,
 * checks each drive's new and changed files at that drive's own pace, then compares them all together.
 */
function driveProgress(d, st) {
  if (!st) return ['Starting…', null];
  if (st.phase === 'listing') {
    const listed = new Set((st.listed || []).map(normRoot));
    const roots = (st.roots || []).filter((r) => overlaps(d.root, r));
    return roots.length && roots.every((r) => listed.has(normRoot(r))) ? ['Files found · waiting for the other drives', 0] : ['Finding photos and videos…', null];
  }
  if (st.phase === 'checking') {
    if (!st.drives) return ['Getting ready to check the files…', null];
    const mine = st.drives.filter((x) => overlaps(d.root, x.root));
    const done = mine.reduce((n, x) => n + x.done, 0), total = mine.reduce((n, x) => n + x.total, 0);
    const othersBusy = st.drives.some((x) => !mine.includes(x) && x.done < x.total);
    const waiting = othersBusy ? ' · waiting for the other drives' : st.stage && !/^checking files$/i.test(st.stage) ? ' · ' + st.stage : '';
    if (!total) return ['No photos or videos to check' + waiting, 1];
    // Folders on one drive share its count: their cards say whose it is.
    const shared = mine.length === 1 && state.drives.filter((c) => c.scanned && overlaps(c.root, mine[0].root)).length > 1;
    const where = shared ? ' on ' + mine[0].root : '';
    if (done < total) return ['Checking files' + where + ' · ' + done.toLocaleString() + ' of ' + total.toLocaleString(), done / total];
    return ['Checked ' + count(total, 'file', 'files') + where + waiting, 1];
  }
  // Comparing and the report are every drive's together: the status at the top shows how far, not each card.
  const several = state.drives.filter((c) => c.scanned && scanReads(c)).length > 1;
  return ['Files checked · ' + (several ? 'being compared with the other drives' : 'looking for copies among them'), 'done'];
}

/** A scan's three steps, as the status at the top of the page names them (renderScanStatus). */
const SCAN_STEPS = [
  { phases: ['listing'], label: 'Find photos and videos' },
  { phases: ['checking'], label: 'Check new and changed files' },
  { phases: ['comparing', 'finishing'], label: 'Compare and make the report' },
];

/** The whole scan's step now (0 to 2), what it's doing in words, and how far (0 to 1, or null while it can't count). */
function scanProgress(st) {
  if (!st) return { step: 0, text: 'Starting the scan…', fraction: null };
  const step = Math.max(0, SCAN_STEPS.findIndex((x) => x.phases.includes(st.phase)));
  if (st.phase === 'listing') {
    const roots = st.roots || [];
    const listed = new Set((st.listed || []).map(normRoot));
    const done = roots.filter((r) => listed.has(normRoot(r))).length;
    return { step, text: roots.length > 1 ? 'Finding photos and videos · ' + done + ' of ' + roots.length + ' places done' : 'Finding photos and videos…', fraction: null };
  }
  if (st.phase === 'checking') {
    const drives = st.drives || [];
    const done = drives.reduce((n, x) => n + x.done, 0), total = drives.reduce((n, x) => n + x.total, 0);
    if (!total) return { step, text: 'Getting ready to check the files…', fraction: null };
    return { step, text: 'Checking files · ' + done.toLocaleString() + ' of ' + total.toLocaleString(), fraction: done / total };
  }
  if (st.phase === 'comparing') {
    const what = st.stage ? st.stage[0].toUpperCase() + st.stage.slice(1) : 'Comparing the drives together';
    return { step, text: what, fraction: st.max > 0 ? st.position / st.max : null };
  }
  return { step, text: 'Finishing the report: checking the copies byte for byte', fraction: null };
}

/**
 * While a scan runs, at the top of the page: that it's scanning, since when and at what pace; what it's doing now, in
 * words; and one bar in three parts, a part a step, each named under it: done, under way (filling, or sweeping while it
 * can't count), or still to come. Updated in place each poll, so the bar glides.
 */
function renderScanStatus(s) {
  const box = $('scan-status');
  const running = s.scan.running;
  box.classList.toggle('hidden', !running);
  if (!running) return;
  const st = s.scan.status;
  if (!box.firstChild) {
    const head = el('div', 'ss-head');
    const who = el('div', 'ss-who');
    who.append(el('span', 'ss-dot'), el('strong', 'ss-title', 'Scanning'), el('span', 'ss-when muted small'));
    head.append(who, el('span', 'ss-pct'));
    const steps = el('ol', 'ss-steps');
    SCAN_STEPS.forEach((x, i) => {
      const li = el('li', 'ss-step');
      const bar = el('div', 'dp-bar');
      bar.append(el('div', 'dp-fill'));
      const label = el('div', 'ss-label small');
      label.append(el('span', 'ss-num', String(i + 1)), el('span', null, x.label));
      li.append(bar, label);
      steps.append(li);
    });
    box.append(head, el('div', 'ss-text'), steps);
  }
  const p = scanProgress(s.scan.starting ? null : st);
  const when = box.querySelector('.ss-when');
  when.textContent = st ? 'started ' + ago(st.startedUtc) + ' · ' + (st.fullSpeed ? 'at full speed' : 'in the background') : '';
  when.title = st ? (st.fullSpeed
    ? 'Full speed: every core but one, at normal priority.'
    : "In the background: Windows' efficiency mode, low priority, and a cap on how much of the processor it uses. Open this page and it speeds up, unless the Scanning setting keeps every scan in the background.") : '';
  const sweep = p.fraction === null;
  box.querySelector('.ss-pct').textContent = sweep ? '' : Math.floor(100 * Math.min(1, p.fraction)) + '%';
  box.querySelector('.ss-text').textContent = p.text;
  box.querySelectorAll('.ss-step').forEach((li, i) => {
    const now = i === p.step;
    li.classList.toggle('done', i < p.step);
    li.classList.toggle('now', now);
    li.querySelector('.ss-num').textContent = i < p.step ? '✓' : String(i + 1);
    li.title = now && st ? PHASE_TIPS[st.phase] || '' : '';
    if (now) li.setAttribute('aria-current', 'step');
    else li.removeAttribute('aria-current');
    const bar = li.querySelector('.dp-bar');
    bar.classList.toggle('sweep', now && sweep);
    bar.querySelector('.dp-fill').style.width = i < p.step ? '100%' : now && !sweep ? 100 * Math.min(1, p.fraction) + '%' : now ? '' : '0';
  });
}

const PHASE_TIPS = {
  listing: 'Step 1 of 3: finding the photos and videos. Where the drive\'s change journal says what changed, only those folders are listed again.',
  checking: 'Step 2 of 3: reading new and changed files, each drive at its own pace. Files checked before go by quickly.',
  comparing: 'Step 3 of 3: comparing the files of every drive together, so this bar is the same on each.',
  finishing: 'Step 3 of 3: making the report, with byte-for-byte checks of the copies.',
};

/** A card's progress: fills its box, or updates the one there in place so the bar glides. */
function fillDriveProgress(box, d, st) {
  const [text, fraction] = driveProgress(d, st);
  if (!box.firstChild) {
    const line = el('div', 'dp-line small');
    line.append(el('span', 'dp-text'), el('span', 'dp-pct'));
    const bar = el('div', 'dp-bar');
    bar.append(el('div', 'dp-fill'));
    box.append(line, bar);
  }
  const done = fraction === 'done';
  const sweep = fraction === null;
  box.title = (st && PHASE_TIPS[st.phase]) || '';
  box.classList.toggle('checked', done);
  box.querySelector('.dp-text').textContent = text;
  box.querySelector('.dp-bar').classList.toggle('hidden', done);
  if (done) {
    box.querySelector('.dp-pct').textContent = '';
    return;
  }
  box.querySelector('.dp-pct').textContent = sweep || fraction === 0 ? '' : Math.floor(100 * Math.min(1, fraction)) + '%';
  box.querySelector('.dp-bar').classList.toggle('sweep', sweep);
  box.querySelector('.dp-fill').style.width = sweep ? '' : 100 * Math.min(1, fraction) + '%';
}

/** Every poll while a scan runs: the drive cards' progress, without rebuilding the cards. */
function renderDriveProgress(s) {
  for (const box of $('drives').querySelectorAll('.drive-progress')) {
    const d = s.drives.find((x) => x.root === box.dataset.root);
    if (d && s.scan.running) fillDriveProgress(box, d, s.scan.status);
  }
}

function driveCard(d) {
  const card = el('button', 'drive-card');
  card.disabled = !d.scanned;
  const top = el('div', 'drive-top');
  top.append(driveIcon(d.type));
  const id = el('div');
  id.style.minWidth = '0';
  id.style.flex = '1';
  id.append(el('div', 'drive-name', d.name));
  if (d.totalBytes > 0) {
    const used = d.totalBytes - d.freeBytes;
    const usage = el('div', 'usage');
    const fill = el('div', 'fill' + (d.freeBytes / d.totalBytes < 0.1 ? ' low' : ''));
    fill.style.width = (100 * used) / d.totalBytes + '%';
    usage.append(fill);
    id.append(usage, el('div', 'muted small', bytes(d.freeBytes) + ' free of ' + bytes(d.totalBytes)));
  } else {
    id.append(el('div', 'muted small', d.root));
  }
  top.append(id);
  card.append(top);

  const scan = el('div', 'drive-scan');
  if (d.scanned && scanReads(d)) {
    // Its details wait for the scan's end; meanwhile, how far the scan has got on it.
    const box = el('div', 'drive-progress');
    box.dataset.root = d.root;
    fillDriveProgress(box, d, state.scan.status);
    scan.append(box);
  } else if (d.scanned && d.scan) {
    const line = el('div', 'scan-line small');
    line.append(icon('check'));
    line.append(el('span', null, count(d.scan.files, 'photo or video', 'photos and videos') + ' · last scan took ' + took(d.scan.listingSec + d.scan.analysisSec)));
    line.title = 'Last scan: listing folders ' + took(d.scan.listingSec) + ', checking new and changed files ' + took(d.scan.analysisSec) +
      '. Files already checked are skipped, so a rescan is much faster than the first scan.';
    scan.append(line);
    const chips = el('div', 'chips');
    const dup = d.duplicates;
    if (dup.copies) chips.append(el('span', 'chip accent', count(dup.copies, 'set of copies', 'sets of copies')));
    if (dup.reclaim) chips.append(el('span', 'chip good', bytes(dup.reclaim) + ' to free'));
    if (dup.lookalikes) chips.append(el('span', 'chip quiet', count(dup.lookalikes, 'look-alike set', 'look-alike sets')));
    if (!dup.copies && !dup.lookalikes) chips.append(el('span', 'chip good', 'No duplicates'));
    if (d.onRequest) chips.append(el('span', 'chip quiet', 'Scanned when you ask'));
    scan.append(chips);
  } else if (d.scanned) {
    scan.append(el('div', 'muted small', d.onRequest ? 'Scanned when you ask: open it to scan it.' : 'Details appear after the next scan.'));
  } else {
    scan.append(el('div', 'muted small', d.type === 'removable'
      ? 'Not scanned. USB drives come and go: add it under "folders" in the settings to include it.'
      : 'Not scanned.'));
  }
  card.append(scan);
  card.addEventListener('click', () => go(d.root));
  // Right-click: scan the drive only when asked, or automatically again.
  if (d.scanned) card.addEventListener('contextmenu', (e) => openFolderMenu(e, { name: d.name, path: d.root, drive: d }));
  return card;
}

/**
 * History's controls. In Settings (withSwitch): keep one or not, and clear it. Above the list on the
 * home page: clear it, and a pointer to Settings while it's off.
 */
function historyBar(s, withSwitch) {
  const bar = el('div', 'history-bar');
  const keep = s.config.keepHistory;
  const text = el('div', 'auto-text');
  if (withSwitch) {
    bar.style.borderBottom = '0';
    bar.append(toggleSwitch(keep, 'Keep a history', settingsBusy, (on) => saveSettings({ keepHistory: on })));
    text.append(el('div', 'auto-title', 'Keep a history'),
      el('div', 'muted small', keep
        ? 'What you and automatic cleanup clean up or keep is listed under History on the home page, with file names.'
        : 'Off: nothing new is listed, and no file names are kept. Sets you kept still stay hidden.'));
  } else if (!keep) {
    const off = el('div', 'muted small', 'History is off: nothing new is listed. ');
    const link = el('button', 'link small', 'Settings');
    link.addEventListener('click', () => { location.hash = '#/settings'; });
    off.append(link);
    text.append(off);
  }
  bar.append(text);
  const clear = el('button', 'btn secondary', 'Clear history');
  clear.disabled = !s.done.length;
  clear.addEventListener('click', async () => {
    if (!confirm('Clear the history?\n\nThe list empties and the file names in it are forgotten. Nothing on disk changes: sets you kept stay hidden, and "freed so far" stays.')) return;
    try {
      await post('/api/history/clear');
      showNotice('History cleared.');
      refresh(true);
    } catch (e) { showError(e.message); }
  });
  bar.append(clear);
  return bar;
}

function renderDone(s) {
  const box = $('done');
  $('history').querySelector('summary').textContent = 'History' + (s.done.length ? ' (' + s.done.length + ')' : '');
  if (!s.done.length) {
    box.replaceChildren(historyBar(s), el('p', 'muted small', s.config.keepHistory ? 'Nothing yet.' : 'Nothing listed.'));
    return;
  }
  const where = (folder) => {
    const w = el('span', 'where', nameOf(folder));
    w.title = folder;
    w.addEventListener('contextmenu', (e) => openPathMenu(e, { name: nameOf(folder), path: folder, folder: true }));
    return w;
  };
  box.replaceChildren(historyBar(s), ...s.done.map((d) => {
    const row = el('div', 'done-row');
    row.append(el('span', 'muted small', new Date(d.atUtc).toLocaleString()));
    if (d.auto) row.append(el('span', 'tag auto', 'Automatic'));
    if (d.batch && d.action === 'kept') {
      // "Skip all" on a folder's look-alikes.
      row.append(el('span', null, 'Skipped ' + count(d.sets, 'look-alike set', 'look-alike sets') + (d.folder ? ' in' : '')));
      if (d.folder) row.append(where(d.folder));
      if (d.inReport) {
        const undo = el('button', 'link', 'review again');
        undo.addEventListener('click', async () => {
          try { await post('/api/history/reopen', { batch: d.batch }); refresh(true); } catch (e) { showError(e.message); }
        });
        row.append(undo);
      }
    } else if (d.batch && d.action === 'recycled') {
      // A folder's "Move to Recycle Bin", set by set.
      row.append(el('span', null, 'Moved ' + count(d.recycled, 'file', 'files') + ', ' + bytes(d.recycledBytes) + ', from ' + count(d.sets, 'set', 'sets') +
        ' to the Recycle Bin' + (d.folder ? ', in' : '')));
      if (d.folder) row.append(where(d.folder));
    } else if (d.action === 'branches-pruned') {
      row.append(el('span', null, 'Deleted ' + count(d.recycled - 1, 'merged branch', 'merged branches') + ' in ' + (d.label || 'a repository')));
    } else if (d.action === 'game-recycled') {
      row.append(el('span', null, 'Moved ' + (d.label || 'what a game left behind') + ', ' + bytes(d.recycledBytes) + ', to the Recycle Bin'));
    } else if (d.action === 'dev-cleaned') {
      row.append(el('span', null, 'Cleaned ' + (d.label || 'developer files') + ', freed ' + bytes(d.recycledBytes) + ' (deleted permanently)'));
    } else if (d.action === 'recycled') {
      row.append(el('span', null, 'Moved ' + d.recycled + ' file(s), ' + bytes(d.recycledBytes) + ', to the Recycle Bin' + (d.keepName ? ', kept ' + d.keepName : '')));
    } else {
      row.append(el('span', null, 'Kept all' + (d.keepName ? ' (' + d.keepName + ' and its look-alikes)' : '')));
      if (d.inReport) {
        const undo = el('button', 'link', 'review again');
        undo.addEventListener('click', async () => {
          try { await post('/api/groups/' + encodeURIComponent(d.key) + '/reopen'); refresh(true); } catch (e) { showError(e.message); }
        });
        row.append(undo);
      }
    }
    return row;
  }));
}

// ---------------------------------------------------------------- home: in a manor

/** A few of the manor's staff, a line each, for the "In a manor" card. */
const MANOR_STAFF = [
  ['Porter', 'what\'s new on this PC, and who signed it.'],
  ['Clerk', 'your screenshots and scans, made searchable.'],
  ['Warrener', 'a name and a home for what piles up in Downloads.'],
  ['Lamplighter', 'rolls a graphics driver back when a new one leaves the screen dark.'],
  ['Miller', 'big videos and photos, made small.'],
];

/**
 * Without Manor, the last thing on the home page: what Manor adds, in a few lines, with a link to its site and Not now, which
 * hides it for good (settings.json's manorCard; Settings shows it again). It's text in the page: nothing is fetched or sent for
 * it. With Manor installed, card is null and there's nothing here: the title bar's "Back to <manor>" says where Heiward works.
 */
function renderManorCard(card) {
  const show = !!(card && card.show);
  $('manor-card-section').classList.toggle('hidden', !show);
  if (!show) { $('manor-card').replaceChildren(); return; }
  const box = el('div', 'auto-card manor-card');
  box.append(el('p', 'manor-card-lead',
    'Heiward is one of a household of agents that put this PC\'s NPU to work: all private, all on this PC. Manor is the house they share.'));
  const staff = el('ul', 'manor-staff');
  for (const [name, does] of MANOR_STAFF) {
    const li = el('li');
    li.append(el('strong', null, name), ': ' + does);
    staff.append(li);
  }
  box.append(staff);
  box.append(el('p', null, 'Try every agent free for 30 days, with no card. After that, from $4 a month. ' +
    'Stop paying and keep what you have: it all keeps working, just without new versions.'));
  box.append(el('p', 'muted small', 'Heiward stays free, and works the same without it.'));
  const actions = el('div', 'manor-card-actions');
  const see = outLink('See the manor', card.url);
  see.className = 'btn';
  const later = el('button', 'btn secondary', 'Not now');
  later.addEventListener('click', async () => {
    later.disabled = true;
    try { await post('/api/settings', { manorCard: false }); } catch (e) { showError(e.message); later.disabled = false; return; }
    showNotice('The "In a manor" card is hidden for good. Settings can show it again.', { label: 'Undo', run: () => saveSettings({ manorCard: true }) });
    refresh(true);
  });
  actions.append(see, later);
  box.append(actions);
  $('manor-card').replaceChildren(box);
}

function renderFooter(s) {
  const f = $('footer');
  f.replaceChildren();
  f.append(el('div', null, s.config.allDrives
    ? 'Every fixed drive is scanned, except folders that belong to Windows, installed programs, games and code projects (they show greyed out as exempt).'
    : 'Scanned: ' + s.config.folders.join('; ')));
  if (s.config.excludeExtensions.length) f.append(el('div', null, 'Skipped file types: ' + s.config.excludeExtensions.join(' ')));
  const p = el('div', null, 'Settings: ');
  p.append(el('code', null, s.config.path));
  f.append(p);
}

// ---------------------------------------------------------------- folder tree

async function listing(path, all) {
  const k = key(path) + (all ? '|all' : '');
  if (listings.has(k)) return listings.get(k);
  const res = await fetch('/api/tree?path=' + encodeURIComponent(path) + (all ? '&all=true' : ''));
  const data = res.ok ? await res.json() : null;
  listings.set(k, data);
  return data;
}

/** Opens every folder from the drive down to the one shown, loading what isn't loaded yet. */
async function revealInTree(path) {
  const d = cardFor(path);
  if (!d) return;
  const chain = [];
  for (let p = path; p; p = parentOf(p)) chain.unshift(p);
  for (const p of chain.slice(0, -1)) {
    expanded.add(key(p));
    await listing(p, showAll.has(key(p)));
  }
}

async function renderTree() {
  const tree = $('tree');
  const frag = document.createDocumentFragment();
  const pc = el('button', 'tree-row');
  pc.style.paddingLeft = '6px';
  pc.append(el('span', 'twisty'), pcIcon('glyph'), el('span', 'label', 'This PC'));
  pc.addEventListener('click', () => go(null));
  frag.append(pc);
  const list = el('ul');
  list.setAttribute('role', 'group');
  for (const d of state.drives.filter((x) => x.scanned)) {
    list.append(await treeItem({
      name: d.name, path: d.root, exempt: null, files: d.scan ? d.scan.files : 0,
      duplicates: d.duplicates, expandable: true, drive: d,
    }, 1));
  }
  frag.append(list);
  tree.replaceChildren(frag);
  const sel = tree.querySelector('.tree-row.selected');
  if (sel) sel.scrollIntoView({ block: 'nearest' });
}

async function treeItem(node, depth) {
  const li = el('li');
  li.setAttribute('role', 'none');
  const row = el('button', 'tree-row' + (node.exempt ? ' exempt' : '') + (!node.exempt && !node.files ? ' empty' : ''));
  row.setAttribute('role', 'treeitem');
  row.dataset.path = node.path;
  row.style.paddingLeft = (6 + depth * 14) + 'px';
  const open = expanded.has(key(node.path));
  if (node.expandable) row.setAttribute('aria-expanded', String(open));
  if (route.view === 'folder' && sameFolder(route.path, node.path)) {
    row.classList.add('selected');
    row.setAttribute('aria-selected', 'true');
  }
  const twisty = el('span', 'twisty');
  if (node.expandable) twisty.append(icon('chevron'));
  row.append(twisty);
  row.append(node.drive ? driveIcon(node.drive.type, 'glyph') : folderIcon(!!node.exempt, 'glyph'));
  row.append(el('span', 'label', node.name));
  if (node.exempt) {
    row.append(el('span', 'count', 'Exempt'));
    row.title = node.name + ': not scanned (' + node.exempt + ')';
  } else if (node.duplicates.copies) {
    row.append(el('span', 'count', node.duplicates.copies.toLocaleString()));
    row.title = node.name + ': ' + count(node.duplicates.copies, 'set of copies', 'sets of copies') + ', ' + bytes(node.duplicates.reclaim) + ' to free';
  } else {
    row.title = node.name;
  }
  twisty.addEventListener('click', (e) => {
    if (!node.expandable) return;
    e.stopPropagation();
    toggle(node.path);
  });
  row.addEventListener('click', () => go(node.path));
  row.addEventListener('keydown', (e) => treeKeys(e, node));
  row.addEventListener('contextmenu', (e) => openFolderMenu(e, node));
  li.append(row);

  if (node.expandable && open) {
    const data = await listing(node.path, showAll.has(key(node.path)));
    const ul = el('ul');
    ul.setAttribute('role', 'group');
    if (data) {
      const scanned = data.children.filter((c) => !c.exempt);
      const exempt = data.children.filter((c) => c.exempt);
      for (const c of scanned) ul.append(await treeItem(c, depth + 1));
      if (exempt.length > FOLD_EXEMPT) {
        // Many exempt folders (a profile's dot-folders, a drive's system folders): one greyed row that opens.
        const k = key(node.path);
        const li = el('li');
        const row = el('button', 'tree-row exempt');
        row.style.paddingLeft = (6 + (depth + 1) * 14) + 'px';
        row.setAttribute('aria-expanded', String(exemptOpen.has(k)));
        const tw = el('span', 'twisty');
        tw.append(icon('chevron'));
        row.append(tw, folderIcon(true, 'glyph'), el('span', 'label', count(exempt.length, 'exempt folder', 'exempt folders')));
        row.title = 'Not scanned: ' + [...new Set(exempt.map((c) => c.exempt))].join(', ').toLowerCase();
        row.addEventListener('click', () => { if (exemptOpen.has(k)) exemptOpen.delete(k); else exemptOpen.add(k); renderTree(); });
        li.append(row);
        if (exemptOpen.has(k)) {
          const inner = el('ul');
          for (const c of exempt) inner.append(await treeItem(c, depth + 2));
          li.append(inner);
        }
        ul.append(li);
      } else {
        for (const c of exempt) ul.append(await treeItem(c, depth + 1));
      }
      if (data.hiddenEmpty) {
        const more = el('li', 'tree-note');
        more.style.paddingLeft = (26 + (depth + 1) * 14) + 'px';
        const b = el('button', 'link', count(data.hiddenEmpty, 'folder', 'folders') + ' without photos or videos');
        b.addEventListener('click', () => { showAll.add(key(node.path)); renderTree(); if (route.view === 'folder' && sameFolder(route.path, node.path)) renderContent(); });
        more.append(b);
        ul.append(more);
      }
    }
    li.append(ul);
  }
  return li;
}

async function toggle(path) {
  const k = key(path);
  if (expanded.has(k)) expanded.delete(k); else expanded.add(k);
  await renderTree();
  const row = [...$('tree').querySelectorAll('.tree-row')].find((r) => r.dataset.path && sameFolder(r.dataset.path, path));
  if (row) row.focus();
}

function treeKeys(e, node) {
  const rows = [...$('tree').querySelectorAll('.tree-row')];
  const i = rows.indexOf(e.currentTarget);
  if (e.key === 'ArrowDown' && rows[i + 1]) { rows[i + 1].focus(); e.preventDefault(); }
  else if (e.key === 'ArrowUp' && rows[i - 1]) { rows[i - 1].focus(); e.preventDefault(); }
  else if (e.key === 'ArrowRight' && node.expandable && !expanded.has(key(node.path))) { toggle(node.path); e.preventDefault(); }
  else if (e.key === 'ArrowLeft' && expanded.has(key(node.path))) { toggle(node.path); e.preventDefault(); }
}

// ---------------------------------------------------------------- folder menu (right-click)

// Right-click a folder (or press the menu key on it) to include it in scans or leave it out, whichever it
// isn't now. Saved to "folders" / "excludeFolders" in the settings; the next scan follows. Right-click
// any file or folder the page lists to show it in File Explorer, selected in its folder, or copy its path.
let menuReturn = null;

function closeFolderMenu(focusBack) {
  const m = $('folder-menu');
  if (m.classList.contains('hidden')) return;
  m.classList.add('hidden');
  if (focusBack && menuReturn && document.contains(menuReturn)) menuReturn.focus();
  menuReturn = null;
}

/** A right-click menu's items, in order: item(label, run) adds one; a reason instead of run greys it out. */
function menuItems() {
  const items = [];
  const item = (label, run, why) => {
    const b = el('button', 'menu-item', label);
    b.type = 'button';
    b.setAttribute('role', 'menuitem');
    if (why) { b.disabled = true; b.title = why; }
    else b.addEventListener('click', () => { closeFolderMenu(false); run(); });
    items.push(b);
    return b;
  };
  return { items, item };
}

/** Shows the right-click menu: at the pointer, or from the keyboard under the row, its first item focused. */
function showMenu(e, label, items, hint) {
  e.preventDefault();
  e.stopPropagation();
  const m = $('folder-menu');
  m.setAttribute('aria-label', label);
  m.replaceChildren(el('div', 'menu-label', label), ...items);
  if (hint) m.append(el('div', 'menu-hint', hint));
  m.classList.remove('hidden');
  const r = e.currentTarget.getBoundingClientRect();
  const fromKeys = !e.clientX && !e.clientY;
  const x = Math.max(8, Math.min(fromKeys ? r.left + 28 : e.clientX, window.innerWidth - m.offsetWidth - 8));
  const y = Math.max(8, Math.min(fromKeys ? r.bottom : e.clientY, window.innerHeight - m.offsetHeight - 8));
  m.style.left = x + 'px';
  m.style.top = y + 'px';
  menuReturn = e.currentTarget;
  (items.find((b) => !b.disabled) || items[0]).focus();
}

/**
 * File Explorer, from this PC's Heiward: shows the file or folder selected in the folder it's in, or with
 * `open`, opens a folder itself. The server checks it's still there first.
 */
async function revealPath(path, open) {
  try { await post('/api/reveal', { path, open: !!open }); }
  catch (e) { showError(e.message); }
}

async function copyPath(path) {
  try {
    await navigator.clipboard.writeText(path);
    showNotice('Copied ' + path);
  } catch { showError('The path couldn\'t be copied: the browser didn\'t allow it.'); }
}

/**
 * The right-click menu of a file or folder listed on the page (a copy in a set, a developer item, a
 * folder in the history): show it in File Explorer, or copy its path. `more`: further places it covers.
 */
function openPathMenu(e, { name, path, folder, more }) {
  const { items, item } = menuItems();
  item('Show in File Explorer', () => revealPath(path, false));
  if (folder) item('Open in File Explorer', () => revealPath(path, true));
  item('Copy path', () => copyPath(path));
  showMenu(e, name, items, more ? 'Also in ' + count(more, 'other place', 'other places') + '; File Explorer shows the first.' : null);
}

/** node: { name, path, exempt, drive } as the tree and the folder table have them. */
function openFolderMenu(e, node) {
  const { items, item } = menuItems();
  item('Open', () => go(node.path));
  const card = node.drive || state.drives.find((d) => sameFolder(d.root, node.path));
  let hint = null;
  const isDrive = card && sameFolder(card.root, node.path) && card.type !== 'folder';
  // A drive opens in File Explorer; a folder can also be shown selected in the folder it's in.
  item('Open in File Explorer', () => revealPath(node.path, true));
  if (!isDrive) item('Show in File Explorer', () => revealPath(node.path, false));
  if (node.exempt) {
    item('Include in scans', () => overrideFolder(node.path, true));
    hint = 'Not scanned now: ' + node.exempt.toLowerCase() + '.';
  } else if (isDrive && card.onRequest) {
    item('Scan this drive now', () => scanDrive(card.root));
    item('Scan automatically again', () => setOnRequest(card.root, false));
    hint = 'Scanned only when you ask: scheduled scans and Scan now leave it alone.';
  } else if (isDrive) {
    item('Scan only when I ask', () => setOnRequest(card.root, true));
    hint = 'Scheduled scans and Scan now would leave this drive alone, so a disk that sleeps stays asleep. Its sets stay listed; scan it from its page.';
  } else if (card && card.type !== 'folder') {
    item('Leave out of scans', null, 'Every fixed drive is scanned; leave out folders on it instead.');
  } else {
    item('Leave out of scans', () => overrideFolder(node.path, false));
  }
  item('Copy path', () => copyPath(node.path));
  showMenu(e, node.name, items, hint);
}

function setupFolderMenu() {
  const m = $('folder-menu');
  document.addEventListener('mousedown', (e) => { if (!m.contains(e.target)) closeFolderMenu(false); });
  window.addEventListener('blur', () => closeFolderMenu(false));
  window.addEventListener('resize', () => closeFolderMenu(false));
  window.addEventListener('hashchange', () => closeFolderMenu(false));
  document.addEventListener('scroll', () => closeFolderMenu(false), true);
  m.addEventListener('keydown', (e) => {
    const list = [...m.querySelectorAll('.menu-item:not(:disabled)')];
    const at = list.indexOf(document.activeElement);
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (list.length) list[(at + (e.key === 'ArrowDown' ? 1 : list.length - 1)) % list.length].focus();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      closeFolderMenu(true);
    } else if (e.key === 'Tab') {
      closeFolderMenu(false);
    }
  });
}

/** A drive scanned only when asked (onRequestDrives), or automatically again. */
async function setOnRequest(root, onRequest) {
  try {
    const r = await post('/api/drives/on-request', { root, onRequest });
    await refresh(true);
    showNotice(nameOf(root) + ': ' + r.message, onRequest ? null : { label: 'Scan now', run: scanNow });
  } catch (e) { showError(e.message); }
}

/** Scans a drive that's scanned only when asked, now (and everything else as usual). */
async function scanDrive(root) {
  try { await post('/api/scan/drive', { root }); refresh(true); } catch (e) { showError(e.message); }
}

/** Includes the folder in scans, or leaves it out; asks first when that means removing a wider rule of the user's. */
async function overrideFolder(path, include, removeRule) {
  try {
    const r = await post('/api/folders/override', { path, include, removeRule: removeRule || null });
    listings.clear();
    await refresh(true);
    showNotice(nameOf(path) + ': ' + (r.message || 'saved.'),
      include && r.scanned && !state.scan.running ? { label: 'Scan now', run: scanNow } : null);
  } catch (e) {
    const rule = e.data && e.data.rule;
    if (rule && !removeRule) {
      if (confirm(e.message + '\n\nRemove the rule "' + rule + '" from the settings? Everything it leaves out is scanned again.'))
        await overrideFolder(path, include, rule);
      return;
    }
    showError(e.message);
  }
}

// ---------------------------------------------------------------- folder content

async function renderContent() {
  const path = route.path;
  const content = $('content');
  const data = await listing(path, showAll.has(key(path)));
  if (route.view !== 'folder' || !sameFolder(route.path, path)) return; // navigated on meanwhile
  if (!data) {
    content.replaceChildren(el('div', 'empty-state', 'This folder isn\'t part of what Heiward scans, or it no longer exists.'));
    return;
  }
  const frag = document.createDocumentFragment();
  const d = cardFor(path);
  const isDrive = d && sameFolder(d.root, path);

  const head = el('div', 'folder-head');
  head.append(isDrive ? driveIcon(d.type) : folderIcon(!!data.exempt));
  const title = el('div');
  title.style.minWidth = '0';
  title.append(el('h1', null, nameOf(path)), el('div', 'folder-path', data.path));
  const chips = el('div', 'chips');
  if (data.exempt) {
    chips.append(el('span', 'chip quiet', 'Exempt · ' + data.exempt));
  } else {
    chips.append(el('span', 'chip', count(data.files, 'photo or video', 'photos and videos') + (data.bytes ? ' · ' + bytes(data.bytes) : '')));
    if (data.duplicates.copies) chips.append(el('span', 'chip accent', count(data.duplicates.copies, 'set of copies', 'sets of copies')));
    if (data.duplicates.reclaim) chips.append(el('span', 'chip good', bytes(data.duplicates.reclaim) + ' to free here'));
    if (data.duplicates.lookalikes) chips.append(el('span', 'chip quiet', count(data.duplicates.lookalikes, 'look-alike set', 'look-alike sets')));
    if (isDrive && d.scan) chips.append(el('span', 'chip quiet', 'Last scan took ' + took(d.scan.listingSec + d.scan.analysisSec)));
  }
  title.append(chips);
  head.append(title);
  frag.append(head);
  // A drive scanned only when asked: its scan is here, on its own page.
  if (d && d.onRequest) {
    const strip = el('div', 'rest-strip');
    strip.append(el('span', null, (d.type === 'folder' ? 'This folder\'s drive' : 'This drive') +
      ' is scanned only when you ask: scheduled scans and Scan now leave it alone, and its sets are from its last scan.'));
    const now = el('button', 'btn', state.scan.running ? 'Scanning…' : 'Scan this drive now');
    now.disabled = state.scan.running;
    now.addEventListener('click', () => scanDrive(d.root));
    const auto = el('button', 'btn secondary', 'Scan automatically again');
    auto.addEventListener('click', () => setOnRequest(d.root, false));
    strip.append(now, auto);
    frag.append(strip);
  }

  if (data.exempt) {
    const panel = el('div', 'exempt-panel');
    panel.append(icon('info'));
    const text = el('div');
    text.append(el('div', null, 'Not scanned: ' + data.exempt.toLowerCase() + '.'));
    text.append(el('div', 'muted small', data.exempt === 'Excluded in settings'
      ? 'You left it out (excludeFolders in the settings).'
      : 'Pictures and videos here usually belong to Windows, a program, a game or a project, which could break if a "duplicate" went. ' +
        'Include it only if the pictures in it are yours.'));
    const include = el('button', 'btn secondary', 'Include in scans');
    include.addEventListener('click', () => overrideFolder(data.path, true));
    text.append(include);
    panel.append(text);
    frag.append(panel);
    content.replaceChildren(frag);
    return;
  }

  if (data.children.length || data.hiddenEmpty) frag.append(subfolderTable(path, data));
  frag.append(duplicatesSection(path));
  content.replaceChildren(frag);
}

function subfolderTable(path, data) {
  const box = el('div');
  const head = el('div', 'section-head');
  head.append(el('h2', null, 'Folders'), el('span', 'muted small', 'Right-click a folder to include it in scans or leave it out.'));
  box.append(head);
  const table = el('table', 'details');
  const thead = el('thead');
  const tr = el('tr');
  const columns = [
    ['name', 'Name', ''],
    ['files', 'Photos & videos', 'num col-files'],
    ['copies', 'Sets of copies', 'num'],
    ['reclaim', 'To free', 'num'],
  ];
  for (const [k, label, cls] of columns) {
    const th = el('th', cls);
    const b = el('button', null, label + (sortBy.key === k ? (sortBy.desc ? ' ↓' : ' ↑') : ''));
    b.addEventListener('click', () => {
      sortBy = { key: k, desc: sortBy.key === k ? !sortBy.desc : k !== 'name' };
      renderContent();
    });
    th.append(b);
    tr.append(th);
  }
  thead.append(tr);
  table.append(thead);

  const value = (c) => ({ name: c.name.toLowerCase(), files: c.files, copies: c.duplicates.copies, reclaim: c.duplicates.reclaim })[sortBy.key];
  const rows = [...data.children].sort((a, b) => {
    if (!!a.exempt !== !!b.exempt) return a.exempt ? 1 : -1; // exempt folders last, like a footnote
    const va = value(a), vb = value(b);
    const c = va < vb ? -1 : va > vb ? 1 : 0;
    return (sortBy.desc ? -c : c) || a.name.localeCompare(b.name);
  });
  const tbody = el('tbody');
  const exemptRows = rows.filter((c) => c.exempt);
  const folded = exemptRows.length > FOLD_EXEMPT && !exemptOpen.has(key(path));
  for (const c of rows) {
    if (c.exempt && folded) continue;
    const row = el('tr', 'row' + (c.exempt ? ' exempt' : !c.files ? ' none' : ''));
    row.tabIndex = 0;
    const name = el('td');
    const cell = el('div', 'name-cell');
    cell.append(folderIcon(!!c.exempt), el('span', null, c.name));
    if (c.exempt) cell.append(el('span', 'tag', 'Exempt · ' + c.exempt));
    name.append(cell);
    row.append(name);
    row.append(el('td', 'num col-files', c.exempt ? '' : c.files ? c.files.toLocaleString() : '–'));
    row.append(el('td', 'num', c.exempt ? '' : c.duplicates.copies ? c.duplicates.copies.toLocaleString() : '–'));
    row.append(el('td', 'num', c.exempt ? '' : c.duplicates.reclaim ? bytes(c.duplicates.reclaim) : '–'));
    row.title = c.path + (c.exempt ? ' (not scanned: ' + c.exempt + ')' : '');
    const open = () => { expanded.add(key(path)); go(c.path); };
    row.addEventListener('click', open);
    row.addEventListener('keydown', (e) => { if (e.key === 'Enter') open(); });
    row.addEventListener('contextmenu', (e) => openFolderMenu(e, c));
    tbody.append(row);
  }
  if (folded) {
    const row = el('tr', 'row exempt');
    row.tabIndex = 0;
    const td = el('td');
    td.colSpan = 4;
    const cell = el('div', 'name-cell');
    const reasons = [...new Set(exemptRows.map((c) => c.exempt))];
    cell.append(folderIcon(true), el('span', null, count(exemptRows.length, 'exempt folder', 'exempt folders')),
      el('span', 'tag', reasons.slice(0, 3).join(', ') + (reasons.length > 3 ? '…' : '')));
    td.append(cell);
    row.append(td);
    row.title = 'Not scanned: ' + exemptRows.map((c) => c.name).join(', ');
    const unfold = () => { exemptOpen.add(key(path)); renderRoute(); };
    row.addEventListener('click', unfold);
    row.addEventListener('keydown', (e) => { if (e.key === 'Enter') unfold(); });
    tbody.append(row);
  }
  table.append(tbody);
  box.append(table);
  if (data.hiddenEmpty) {
    const b = el('button', 'link small show-empty', 'Show ' + count(data.hiddenEmpty, 'folder', 'folders') + ' without photos or videos');
    b.addEventListener('click', () => { showAll.add(key(path)); renderRoute(); });
    box.append(b);
  }
  return box;
}

function duplicatesSection(path) {
  const box = el('div');
  const here = state.pending.filter((g) => g.items.some((i) => isUnder(i.path, path)));
  // Sets with a copy to clean up here; sets that only keep their original here come after, folded.
  const copies = here.filter((g) => g.kind !== 'similar' && g.items.some((i) => i.suggested && isUnder(i.path, path)));
  const keptHere = here.filter((g) => g.kind !== 'similar' && !copies.includes(g));
  const similar = here.filter((g) => g.kind === 'similar');
  if (groupFilter === 'copies' && !copies.length && similar.length) groupFilter = 'similar';
  const shownGroups = groupFilter === 'copies' ? copies : similar;

  const head = el('div', 'section-head');
  head.append(el('h2', null, 'Duplicates in this folder'));
  const seg = el('div', 'segmented');
  seg.setAttribute('role', 'group');
  for (const [k, label, n] of [['copies', 'Copies to clean up', copies.length], ['similar', 'Look-alikes', similar.length]]) {
    const b = el('button', null, label + ' (' + n + ')');
    b.setAttribute('aria-pressed', String(groupFilter === k));
    b.addEventListener('click', () => { groupFilter = k; groupsShown = PAGE; renderContent(); });
    seg.append(b);
  }
  head.append(seg);
  box.append(head);

  if (!here.length) {
    box.append(el('div', 'empty-state', state.report ? 'No duplicates in this folder.'
      : state.updated ? 'Heiward was updated. Its next scan finds the sets again with this version\'s rules' + (state.scan.running ? ': it\'s running now.'
        : scanDueIn(state.schedule) ? ' (due ' + scanDueIn(state.schedule) + '), or press "Scan now".' : ': press "Scan now".')
      : 'No scan yet: press "Scan now".'));
    return box;
  }
  if (groupFilter === 'copies' && copies.length) box.append(cleanupBar(path, copies));
  if (groupFilter === 'similar') {
    box.append(el('p', 'muted small', 'These look alike but may be edits or different shots. Nothing is ticked; pick what you don\'t need, set by set. ' +
      'Burst shots (photos numbered in a row, like IMG_1234 and IMG_1235) and pictures less than 75% alike aren\'t listed: they\'re different photos.'));
    if (similar.length) box.append(skipBar(path, similar));
  }
  if (groupFilter === 'copies' && !copies.length)
    box.append(el('div', 'empty-state', 'Nothing to clean up in this folder.'));
  for (const g of shownGroups.slice(0, groupsShown)) box.append(groupCard(g, path));
  if (shownGroups.length > groupsShown) {
    const more = el('button', 'btn secondary more', 'Show more (' + (shownGroups.length - groupsShown) + ' left)');
    more.addEventListener('click', () => { groupsShown += PAGE * 2; renderContent(); });
    box.append(more);
  }
  if (groupFilter === 'copies' && keptHere.length && shownGroups.length <= groupsShown) {
    const k = key(path);
    const note = el('p', 'muted small');
    note.append(el('span', null, count(keptHere.length, 'more set keeps its', 'more sets keep their') + ' original here; the copies are in other folders. '));
    const b = el('button', 'link small', showKeptHere.has(k) ? 'Hide them' : 'Show them');
    b.addEventListener('click', () => { if (showKeptHere.has(k)) showKeptHere.delete(k); else showKeptHere.add(k); renderContent(); });
    note.append(b);
    box.append(note);
    if (showKeptHere.has(k)) for (const g of keptHere) box.append(groupCard(g, path));
  }
  return box;
}

/** "Skip all": every look-alike set with a file in this folder is kept as it is, and leaves the list. */
function skipBar(path, groups) {
  const bar = el('div', 'cleanup');
  const text = el('div');
  text.append(el('div', null, count(groups.length, 'look-alike set', 'look-alike sets') + ' with a file in this folder.'));
  text.append(el('div', 'muted small', 'Nothing worth a look? Skip them all: nothing is deleted, they leave the list, and a set only comes back if its files change.'));
  bar.append(text);
  const btn = el('button', 'btn secondary', 'Skip all ' + groups.length);
  btn.addEventListener('click', async () => {
    if (!confirm('Skip all ' + count(groups.length, 'look-alike set', 'look-alike sets') + ' in ' + nameOf(path) + '?\n\nNothing is deleted: they leave the list' +
        (state.config.keepHistory ? ', and History can bring them back.' : '. History is off, so they won\'t be listed there.'))) return;
    btn.disabled = true;
    try {
      const r = await post('/api/groups/skip', { keys: groups.map((g) => g.key), batch: batchId(), folder: trimSep(path) });
      for (const g of groups) ticks.delete(g.key);
      groupsShown = PAGE;
      showNotice('Skipped ' + count(r.skipped, 'look-alike set', 'look-alike sets') + '.');
      refresh(true);
    } catch (e) {
      showError(e.message);
      btn.disabled = false;
    }
  });
  bar.append(btn);
  return bar;
}

/** One button for every ticked copy inside this folder, set by set, each keeping at least one copy. */
function cleanupBar(path, groups) {
  const plan = [];
  for (const g of groups) {
    const set = ticked(g);
    const paths = g.items.filter((i) => set.has(i.path) && isUnder(i.path, path)).map((i) => i.path);
    if (paths.length && paths.length < g.items.length) plan.push({ g, paths });
  }
  const files = plan.reduce((a, p) => a + p.paths.length, 0);
  const size = plan.reduce((a, p) => a + p.g.items.filter((i) => p.paths.includes(i.path)).reduce((s, i) => s + i.size, 0), 0);
  const synced = plan.reduce((a, p) => a + p.g.items.filter((i) => p.paths.includes(i.path) && i.synced).length, 0);

  const bar = el('div', 'cleanup');
  const text = el('div');
  text.append(el('div', null, files
    ? 'Ticked in this folder: ' + count(files, 'file', 'files') + ', ' + bytes(size) + ', in ' + count(plan.length, 'set', 'sets') + '.'
    : 'Nothing ticked in this folder.'));
  text.append(el('div', 'muted small', 'Each set keeps its kept file. Files go to the Recycle Bin, where you can restore them.'));
  const result = el('div', 'result small');
  text.append(result);
  bar.append(text);
  const btn = el('button', 'btn', files ? 'Move ' + count(files, 'file', 'files') + ' to Recycle Bin' : 'Move to Recycle Bin');
  btn.disabled = !files;
  btn.addEventListener('click', async () => {
    let msg = 'Move ' + count(files, 'ticked file', 'ticked files') + ' (' + bytes(size) + ') from ' + count(plan.length, 'set', 'sets') + ' to the Recycle Bin?';
    if (synced) msg += '\n\n' + count(synced, 'of them is', 'of them are') +
      ' in a cloud-synced folder (iCloud Photos, OneDrive…): the Recycle Bin move also deletes ' + (synced === 1 ? 'it' : 'them') +
      ' from the cloud and your other devices (the cloud keeps deleted items for about 30 days).';
    if (!confirm(msg)) return;
    btn.disabled = true;
    let moved = 0, freed = 0;
    const failed = [];
    const batch = batchId(); // one History row for the whole folder
    for (const [i, p] of plan.entries()) {
      result.textContent = 'Moving… set ' + (i + 1) + ' of ' + plan.length;
      try {
        const r = await post('/api/groups/' + encodeURIComponent(p.g.key) + '/recycle', { paths: p.paths, batch, folder: trimSep(path) });
        moved += r.recycled.length;
        freed += r.recycledBytes;
        for (const f of r.failed) failed.push(f.path.split(SEP).pop() + ' (' + f.reason + ')');
        ticks.delete(p.g.key);
      } catch (e) {
        failed.push(p.g.items[0].name + ' (' + e.message + ')');
      }
    }
    result.textContent = 'Moved ' + count(moved, 'file', 'files') + ', ' + bytes(freed) + ', to the Recycle Bin.' + (failed.length ? ' Not moved: ' + failed.join('; ') : '');
    result.className = 'result small ' + (failed.length ? 'bad' : 'ok');
    setTimeout(() => refresh(true), failed.length ? 4000 : 1200);
  });
  bar.append(btn);
  return bar;
}

function itemTile(g, item, index, folder) {
  const set = ticked(g);
  const outside = folder && !isUnder(item.path, folder);
  const tile = el('div', 'item' + (item.keep ? ' keep' : '') + (set.has(item.path) ? ' ticked' : '') + (outside ? ' outside' : ''));
  const img = el('img', 'thumb');
  img.loading = 'lazy';
  img.alt = item.name;
  img.src = '/api/thumb/' + encodeURIComponent(g.key) + '/' + index;
  tile.append(img);

  const body = el('div', 'item-body');
  body.append(el('div', 'name', item.name));
  const where = el('div', 'folder muted small', item.folder);
  where.title = item.path;
  body.append(where);
  tile.addEventListener('contextmenu', (e) => openPathMenu(e, { name: item.name, path: item.path }));
  if (outside) body.append(el('div', 'elsewhere', 'In another folder'));
  const facts = [];
  if (item.width && item.height) facts.push(item.width + ' × ' + item.height);
  if (g.media === 'video' && item.durationSec) facts.push(duration(item.durationSec));
  facts.push(bytes(item.size));
  facts.push(new Date(item.modifiedUtc).toLocaleDateString());
  body.append(el('div', 'muted small', facts.join(' · ')));
  if (item.keep) body.append(el('span', 'badge keep', 'Keep: ' + g.keepReason));
  else body.append(el('div', 'relation small', (RELATION[item.relation] || item.relation) + (item.aiMatched ? ' · ' + Math.round(item.similarity) + '% alike' : '')));
  if (item.synced) body.append(el('div', 'synced small', 'Synced to the cloud: deleting it here deletes it everywhere'));

  const label = el('label', 'tick');
  const box = el('input');
  box.type = 'checkbox';
  box.checked = set.has(item.path);
  box.addEventListener('change', () => {
    if (box.checked) set.add(item.path); else set.delete(item.path);
    renderContent();
  });
  label.append(box, el('span', null, 'Move to Recycle Bin'));
  body.append(label);
  tile.append(body);
  return tile;
}

function groupCard(g, folder) {
  const card = el('div', 'card');
  const head = el('div', 'card-head');
  const left = el('div', 'left');
  const [cls, label] = KIND[g.kind] || ['same', g.kind];
  left.append(el('span', 'badge ' + cls, label));
  left.append(el('span', 'muted small', g.items.length + ' files' + (g.media === 'video' ? ' (video)' : '')));
  head.append(left);
  const set = ticked(g);
  const selectedBytes = g.items.filter((i) => set.has(i.path)).reduce((a, i) => a + i.size, 0);
  head.append(el('span', 'muted small', set.size ? 'frees ' + bytes(selectedBytes) : ''));
  card.append(head);
  const auto = state.auto.groups[g.key];
  if (auto && g.kind !== 'similar')
    card.append(autoLine(auto, 'g:' + g.key, count(auto.count, 'copy goes', 'copies go') + ' to the Recycle Bin automatically ' + dueText(auto.dueUtc, false) + '.'));
  if (g.kind !== 'similar' && g.items.some((i) => !i.keep && !i.suggested))
    card.append(el('p', 'muted small', 'Plain copies are ticked. Edited, cropped or look-alike versions are not: tick them only if you don\'t want them.'));

  const items = el('div', 'items');
  g.items.forEach((item, i) => items.append(itemTile(g, item, i, folder)));
  card.append(items);

  const actions = el('div', 'actions');
  const recycle = el('button', 'btn', set.size ? 'Move ' + set.size + ' to Recycle Bin' : 'Tick the files to remove');
  recycle.disabled = set.size === 0 || set.size >= g.items.length;
  if (set.size >= g.items.length) recycle.textContent = 'Keep at least one copy';
  const keep = el('button', 'btn secondary', g.kind === 'similar' ? 'Keep them all' : 'Not duplicates, keep all');
  const result = el('div', 'result small');
  recycle.addEventListener('click', async () => {
    const synced = g.items.filter((i) => set.has(i.path) && i.synced).length;
    if (synced && !confirm(synced + ' of these files ' + (synced === 1 ? 'is' : 'are') + ' in a cloud-synced folder (iCloud Photos, OneDrive…). ' +
        'Moving ' + (synced === 1 ? 'it' : 'them') + ' to the Recycle Bin also deletes ' + (synced === 1 ? 'it' : 'them') +
        ' from the cloud and your other devices (the cloud keeps deleted items for about 30 days). Continue?')) return;
    recycle.disabled = keep.disabled = true;
    try {
      const r = await post('/api/groups/' + encodeURIComponent(g.key) + '/recycle', { paths: [...set] });
      const lines = [];
      if (r.recycled.length) lines.push('Moved ' + r.recycled.length + ' file(s), ' + bytes(r.recycledBytes) + ', to the Recycle Bin.');
      for (const f of r.failed) lines.push('Not moved: ' + f.path.split(SEP).pop() + ' (' + f.reason + ')');
      result.textContent = lines.join(' ');
      result.className = 'result small ' + (r.failed.length ? 'bad' : 'ok');
      ticks.delete(g.key);
      setTimeout(() => refresh(true), r.failed.length ? 4000 : 800);
    } catch (e) {
      result.textContent = e.message;
      result.className = 'result small bad';
      recycle.disabled = keep.disabled = false;
    }
  });
  keep.addEventListener('click', async () => {
    try {
      await post('/api/groups/' + encodeURIComponent(g.key) + '/keep');
      ticks.delete(g.key);
      refresh(true);
    } catch (e) { showError(e.message); }
  });
  actions.append(recycle, keep);
  card.append(actions, result);
  return card;
}

// ---------------------------------------------------------------- render loop

async function renderRoute() {
  if (!state) return;
  if (state.setup.needed) { renderSetup(state); return; }
  // Developer mode is off (Settings): its pages aren't there, so a link to one lands home.
  // Game mode the same way: off, the Games area isn't there.
  if ((route.view === 'dev' && !state.dev.enabled) || (route.view === 'games' && !state.games.enabled)) {
    history.replaceState(null, '', '#/');
    route = parseRoute();
  }
  renderCrumbs();
  const folder = route.view === 'folder';
  $('setup').classList.add('hidden');
  $('home').classList.toggle('hidden', route.view !== 'home');
  $('settingsview').classList.toggle('hidden', route.view !== 'settings');
  $('devview').classList.toggle('hidden', route.view !== 'dev');
  $('gamesview').classList.toggle('hidden', route.view !== 'games');
  $('folder').classList.toggle('hidden', !folder);
  $('settings-btn').setAttribute('aria-pressed', String(route.view === 'settings'));
  if (route.view === 'settings') {
    renderSettings(state);
    return;
  }
  if (route.view === 'dev') {
    if (!devReport || (state.dev.scannedAtUtc && devReport.scannedAtUtc !== state.dev.scannedAtUtc)) await loadDevReport();
    // Pull requests come later, from the hosts: the page shows the rest meanwhile.
    if (devReport && devReport.scannedAtUtc && Date.now() - devPullsAsked > 120000) loadDevPulls(false);
    renderCrumbs(); // a project's name comes with the report
    renderDev();
    return;
  }
  if (route.view === 'games') {
    if (!gameReport || (state.games.scannedAtUtc && gameReport.scannedAtUtc !== state.games.scannedAtUtc)) await loadGameReport();
    renderCrumbs(); // a game's name comes with the report
    renderGames();
    return;
  }
  if (!folder) {
    renderHome(state);
    return;
  }
  if (!cardFor(route.path)) {
    $('content').replaceChildren(el('div', 'empty-state', 'That folder isn\'t on a drive Heiward scans.'));
    return;
  }
  const y = $('content').scrollTop;
  await revealInTree(route.path);
  await Promise.all([renderTree(), renderContent()]);
  $('content').scrollTop = y;
}

let timer = null;
let serverLost = false;
async function refresh(force) {
  clearTimeout(timer);
  try {
    // "seen": this page is showing, so a scan runs at full speed; a hidden tab doesn't say so.
    const res = await fetch('/api/state' + (document.visibilityState === 'visible' ? '?seen=true' : ''));
    if (!res.ok) throw new Error(res.status + ' ' + res.statusText);
    const s = await res.json();
    // Heiward is back, as a new process: its buttons need the token of a page it served.
    if (serverLost) { location.reload(); return; }
    // Reeve or the Steward taking over (or handing back) worktrees, branches or pull requests: the report is read again.
    const rolesChanged = !!state && JSON.stringify(s.dev.roles) !== JSON.stringify(state.dev.roles);
    if (rolesChanged) devReport = null;
    const changed = force || !state || s.setup.needed || state.setup.needed ||
      JSON.stringify(s.pending.map((g) => g.key)) !== JSON.stringify(state.pending.map((g) => g.key)) ||
      s.done.length !== state.done.length || s.totals.decisions !== state.totals.decisions || s.scan.running !== state.scan.running ||
      JSON.stringify(s.scan.status && s.scan.status.roots) !== JSON.stringify(state.scan.status && state.scan.status.roots) ||
      s.dev.running !== state.dev.running || s.dev.scannedAtUtc !== state.dev.scannedAtUtc || s.updated !== state.updated ||
      // Developer mode turned on or off, here or in Manor's Developer options.
      s.dev.enabled !== state.dev.enabled || JSON.stringify(s.dev.manor) !== JSON.stringify(state.dev.manor) ||
      // Game mode turned on or off, its check running or done.
      s.games.enabled !== state.games.enabled || JSON.stringify(s.games.manor) !== JSON.stringify(state.games.manor) ||
      s.games.running !== state.games.running || s.games.scannedAtUtc !== state.games.scannedAtUtc ||
      rolesChanged ||
      (s.report && state.report && s.report.scannedAtUtc !== state.report.scannedAtUtc);
    state = s;
    showError('');
    renderHeader(s);
    if (changed) {
      listings.clear(); // counts in the tree follow the report
      await renderRoute();
    }
    // Quickly while something is starting, so its progress shows as soon as it has some.
    timer = setTimeout(refresh, s.setup.running || s.scan.starting ? 1000 : s.scan.running || s.dev.running || s.games.running ? 2000 : 15000);
  } catch (e) {
    // The page stays as it was, and says what's wrong: Heiward isn't running (it stopped, or the PC slept).
    serverLost = true;
    // heiward://start starts the installed Heiward, which isn't a development build's page.
    if (state && state.agent.dev) {
      showAgentBanner('This development build isn\'t running, so this page can\'t update or clean up. Start it again (hei serve), and the page carries on.', null, true);
    } else {
      showAgentBanner('Heiward isn\'t running, so this page can\'t update or clean up. Start it, and the page carries on.',
        ['Start Heiward', 'heiward://start'], true);
    }
    $('scan-now').disabled = true;
    timer = setTimeout(refresh, 5000);
  }
}

// ---------------------------------------------------------------- theme menu

// The themes and the saved choice come from theme.js, which has already applied it.
const THEME_KEY = 'heiward:theme';
const currentTheme = () => document.documentElement.dataset.theme || 'system';

function setTheme(name) {
  if (name === 'system') delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme = name;
  try { localStorage.setItem(THEME_KEY, name); } catch (e) { /* storage blocked: it holds for this visit */ }
  for (const item of $('theme-menu').querySelectorAll('.theme-item'))
    item.setAttribute('aria-checked', String(item.dataset.theme === name));
}

function swatch(colors) {
  const s = el('span', 'swatch');
  ['sw-bg', 'sw-accent', 'sw-fg'].forEach((cls, i) => {
    const part = el('span', cls);
    part.style.background = colors[i];
    s.append(part);
  });
  return s;
}

// With Manor installed, Manor chooses the theme for every page in the manor: Heiward served the page with it
// on <html> (data-theme, unless it's Match Windows), with Manor's name and page (data-manor, data-manor-url).
function manorTheme() {
  const root = document.documentElement;
  if (!root.hasAttribute('data-manor')) return null;
  return { name: root.dataset.manor || 'Manor', url: root.dataset.manorUrl || '' };
}

function themeItem(t, tag) {
  const item = el(tag, 'theme-item');
  item.setAttribute('role', 'menuitemradio');
  item.setAttribute('aria-checked', String(t.name === currentTheme()));
  item.dataset.theme = t.name;
  const text = el('span', 'ti-text');
  text.append(el('span', 'ti-label', t.label), el('span', 'ti-desc', t.description));
  item.append(swatch(t.swatch), text, icon('check', 'ti-check'));
  return item;
}

function setupThemeMenu() {
  const btn = $('theme-btn');
  const menu = $('theme-menu');
  btn.append(icon('palette'));
  const manor = manorTheme();
  if (manor) {
    // Manor's theme, shown, not chosen here: the menu says so and links to Manor, where it's changed.
    const label = `Theme (${manor.name}'s)`;
    btn.title = label;
    btn.setAttribute('aria-label', label);
    menu.append(el('div', 'menu-label', 'Theme'));
    const themes = window.heiwardThemes || [];
    const current = themes.find((t) => t.name === currentTheme()) || themes[0];
    if (current) {
      const item = themeItem(current, 'div');
      item.setAttribute('aria-disabled', 'true');
      menu.append(item);
    }
    menu.append(el('p', 'menu-note', `${manor.name} chooses the theme, for every page in the manor.`));
    const link = el('a', 'menu-link', `Change it in ${manor.name}`);
    link.href = manor.url;
    link.setAttribute('role', 'menuitem');
    menu.append(link);
  } else {
    const labels = { windows: 'Windows', colour: 'Colour themes' };
    let group = null;
    for (const t of window.heiwardThemes || []) {
      if (t.group !== group) {
        group = t.group;
        menu.append(el('div', 'menu-label', labels[group] || ''));
      }
      const item = themeItem(t, 'button');
      item.type = 'button';
      // The menu stays open, so themes can be tried one after another.
      item.addEventListener('click', () => setTheme(t.name));
      menu.append(item);
    }
  }
  // What the keyboard moves between: the themes, or with Manor's, the link to Manor.
  const items = () => [...menu.querySelectorAll(manor ? '.menu-link' : 'button.theme-item')];
  const show = (open) => {
    menu.classList.toggle('hidden', !open);
    btn.setAttribute('aria-expanded', String(open));
    if (open) (items().find((i) => i.getAttribute('aria-checked') === 'true') || items()[0])?.focus();
  };
  btn.addEventListener('click', () => show(menu.classList.contains('hidden')));
  document.addEventListener('click', (e) => {
    if (!menu.classList.contains('hidden') && !menu.contains(e.target) && !btn.contains(e.target)) show(false);
  });
  menu.addEventListener('keydown', (e) => {
    const list = items();
    const at = list.indexOf(document.activeElement);
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      list[(at + (e.key === 'ArrowDown' ? 1 : list.length - 1)) % list.length].focus();
    } else if (e.key === 'Home' || e.key === 'End') {
      e.preventDefault();
      list[e.key === 'Home' ? 0 : list.length - 1].focus();
    } else if (e.key === 'Escape') {
      show(false);
      btn.focus();
    } else if (e.key === 'Tab') {
      show(false);
    }
  });
}

setupThemeMenu();
setupPauseMenu();
$('nav-back').append(icon('back'));
$('nav-fwd').append(icon('fwd'));
$('nav-up').append(icon('up'));
$('nav-back').addEventListener('click', () => history.back());
$('nav-fwd').addEventListener('click', () => history.forward());
$('nav-up').addEventListener('click', () => {
  if (route.view === 'folder') go(parentOf(route.path));
  else if (route.view === 'dev') { if (route.cat || route.group) location.hash = '#/dev'; else go(null); }
  else if (route.view === 'games') { if (route.cat || route.drive || route.game) location.hash = '#/games'; else go(null); }
  else if (route.view === 'settings') go(null);
});
// Heiward's name in the title bar goes home, wherever the page is.
$('brand').addEventListener('click', (e) => { e.preventDefault(); go(null); $('home').scrollTop = 0; });
$('settings-btn').append(icon('gear'));
$('settings-btn').addEventListener('click', () => { location.hash = route.view === 'settings' ? '#/' : '#/settings'; });
$('dev-nav-toggle').addEventListener('click', () => {
  const open = $('dev-nav').classList.toggle('open');
  $('dev-nav-toggle').setAttribute('aria-expanded', String(open));
});
$('games-nav-toggle').addEventListener('click', () => {
  const open = $('games-nav').classList.toggle('open');
  $('games-nav-toggle').setAttribute('aria-expanded', String(open));
});
$('navpane-toggle').addEventListener('click', () => {
  const open = $('navpane').classList.toggle('open');
  $('navpane-toggle').setAttribute('aria-expanded', String(open));
});
$('scan-now').addEventListener('click', scanNow);
// A tab coming back into view tells the agent at once, so a scan running in the background speeds up.
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') refresh(); });
setupFolderMenu();
if (route.view === 'folder') expanded.add(key(route.path));
refresh(true);
