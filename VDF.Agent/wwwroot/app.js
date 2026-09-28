// The review page. Plain DOM, no build step. Every string from the report goes in as text, never HTML.
'use strict';

const token = document.querySelector('meta[name="agent-token"]').content;
const $ = (id) => document.getElementById(id);
const PAGE = 20;
let shown = PAGE;
let state = null;
const ticks = new Map(); // group key -> Set of ticked paths (the user's edits survive refreshes)

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
  edited: 'Edited version (colour, filter or flip)',
  variant: 'Edited, cropped, flipped, or a similar shot',
};

function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined && text !== null) e.textContent = String(text);
  return e;
}

function bytes(b) {
  if (b >= 2 ** 30) return (b / 2 ** 30).toFixed(1) + ' GB';
  if (b >= 2 ** 20) return (b / 2 ** 20).toFixed(1) + ' MB';
  if (b >= 1024) return Math.round(b / 1024) + ' KB';
  return b + ' bytes';
}

function ago(iso) {
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 90) return 'just now';
  if (s < 5400) return Math.round(s / 60) + ' min ago';
  if (s < 129600) return Math.round(s / 3600) + ' h ago';
  return Math.round(s / 86400) + ' days ago';
}

function duration(sec) {
  const m = Math.floor(sec / 60), s = Math.round(sec % 60);
  return m + ':' + String(s).padStart(2, '0');
}

async function post(url, body) {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Agent-Token': token },
    body: body ? JSON.stringify(body) : '{}',
  });
  let data = null;
  try { data = await res.json(); } catch { /* empty body */ }
  if (!res.ok) throw new Error((data && data.error) || res.status + ' ' + res.statusText);
  return data;
}

function showError(msg) {
  $('error').textContent = msg;
  $('error').classList.toggle('hidden', !msg);
}

function ticked(g) {
  if (!ticks.has(g.key)) ticks.set(g.key, new Set(g.items.filter((i) => i.suggested).map((i) => i.path)));
  return ticks.get(g.key);
}

function renderHeader(s) {
  const r = s.report;
  const parts = [];
  if (r) {
    parts.push('Last scan ' + ago(r.scannedAtUtc));
    parts.push(r.filesScanned.toLocaleString() + ' files');
    parts.push(r.device === 'off' ? 'AI matching off' : 'AI on the ' + r.device);
  } else {
    parts.push('No scan yet');
  }
  parts.push(s.schedule.next ? 'next scan ' + s.schedule.next : 'no daily scan scheduled');
  $('subtitle').textContent = parts.join(' · ');

  const running = s.scan.running;
  $('scan-now').disabled = running;
  $('scan-now').textContent = running ? 'Scanning…' : 'Scan now';
  $('progress').classList.toggle('hidden', !running);
  const st = s.scan.status;
  if (running && st && st.max > 0) {
    $('progress-bar').style.width = Math.min(100, (100 * st.position) / st.max) + '%';
    $('progress-text').textContent = st.stage + ' ' + st.position.toLocaleString() + ' / ' + st.max.toLocaleString();
  } else {
    $('progress-bar').style.width = running ? '5%' : '0';
    $('progress-text').textContent = running ? (st ? st.stage : 'Starting') + '…' : '';
  }

  const notes = $('notes');
  notes.replaceChildren(...((r && r.notes) || []).map((n) => el('li', null, n)));

  $('t-groups').textContent = s.totals.groups;
  $('t-free').textContent = bytes(s.totals.reclaimableBytes);
  $('t-freed').textContent = bytes(s.totals.recycledBytes);
}

function itemTile(g, item, index) {
  const set = ticked(g);
  const tile = el('div', 'item' + (item.keep ? ' keep' : '') + (set.has(item.path) ? ' ticked' : ''));
  const img = el('img', 'thumb');
  img.loading = 'lazy';
  img.alt = item.name;
  img.src = '/api/thumb/' + encodeURIComponent(g.key) + '/' + index;
  tile.append(img);

  const body = el('div', 'item-body');
  body.append(el('div', 'name', item.name));
  const folder = el('div', 'folder muted small', item.folder);
  folder.title = item.path;
  body.append(folder);
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
    render();
  });
  label.append(box, el('span', null, 'Move to Recycle Bin'));
  body.append(label);
  tile.append(body);
  return tile;
}

function groupCard(g) {
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
  if (g.kind === 'similar')
    card.append(el('p', 'muted small', 'These look alike but may be edits or different shots (a burst, a retake). Nothing is ticked; pick what you don\'t need.'));
  else if (g.items.some((i) => !i.keep && !i.suggested))
    card.append(el('p', 'muted small', 'Plain copies are ticked. Edited, cropped or look-alike versions are not: tick them only if you don\'t want them.'));

  const items = el('div', 'items');
  g.items.forEach((item, i) => items.append(itemTile(g, item, i)));
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
      for (const f of r.failed) lines.push('Not moved: ' + f.path.split('\\').pop() + ' (' + f.reason + ')');
      result.textContent = lines.join(' ');
      result.className = 'result small ' + (r.failed.length ? 'bad' : 'ok');
      ticks.delete(g.key);
      setTimeout(refresh, r.failed.length ? 4000 : 800);
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
      refresh();
    } catch (e) { showError(e.message); }
  });
  actions.append(recycle, keep);
  card.append(actions, result);
  return card;
}

function renderGroups(s) {
  const box = $('groups');
  const pending = s.pending;
  if (!s.report) {
    box.replaceChildren(el('p', 'card muted', 'No scan yet. Press "Scan now"; the first scan of a large library takes a while, later ones only look at new files.'));
  } else if (pending.length === 0) {
    box.replaceChildren(el('p', 'card muted', 'Nothing to review. The next scan will check new files.'));
  } else {
    box.replaceChildren(...pending.slice(0, shown).map(groupCard));
  }
  $('more').classList.toggle('hidden', pending.length <= shown);
  $('more').textContent = 'Show more (' + (pending.length - shown) + ' left)';
}

function renderDone(s) {
  const box = $('done');
  if (!s.done.length) {
    box.replaceChildren(el('p', 'muted small', 'Nothing yet.'));
    return;
  }
  box.replaceChildren(...s.done.map((d) => {
    const row = el('div', 'done-row');
    row.append(el('span', 'muted small', new Date(d.atUtc).toLocaleString()));
    if (d.action === 'recycled') {
      row.append(el('span', null, 'Moved ' + d.recycled + ' file(s), ' + bytes(d.recycledBytes) + ', to the Recycle Bin' + (d.keepName ? ', kept ' + d.keepName : '')));
    } else {
      row.append(el('span', null, 'Kept all' + (d.keepName ? ' (' + d.keepName + ' and its look-alikes)' : '')));
      if (d.inReport) {
        const undo = el('button', 'link', 'review again');
        undo.addEventListener('click', async () => {
          try { await post('/api/groups/' + encodeURIComponent(d.key) + '/reopen'); refresh(); } catch (e) { showError(e.message); }
        });
        row.append(undo);
      }
    }
    return row;
  }));
}

function renderFooter(s) {
  const f = $('footer');
  f.replaceChildren();
  f.append(el('div', null, 'Scanned: ' + s.config.folders.join('; ') + (s.config.allDrives ? ' (every fixed drive, minus system, app and game folders)' : '')));
  if (s.config.excludeExtensions.length) f.append(el('div', null, 'Skipped file types: ' + s.config.excludeExtensions.join(' ')));
  const p = el('div', null, 'Settings: ');
  p.append(el('code', null, s.config.path));
  f.append(p);
}

function render() {
  if (!state) return;
  // Keep the user's place: rebuilding the cards must not jump the page.
  const y = window.scrollY;
  renderHeader(state);
  renderGroups(state);
  renderDone(state);
  renderFooter(state);
  window.scrollTo(0, y);
}

let timer = null;
async function refresh() {
  clearTimeout(timer);
  try {
    const res = await fetch('/api/state');
    if (!res.ok) throw new Error(res.status + ' ' + res.statusText);
    const s = await res.json();
    const firstOrChanged = !state || JSON.stringify(s.pending.map((g) => g.key)) !== JSON.stringify(state.pending.map((g) => g.key)) ||
      s.done.length !== state.done.length || s.scan.running !== state.scan.running;
    state = s;
    showError('');
    if (firstOrChanged) render(); else renderHeader(s);
    timer = setTimeout(refresh, s.scan.running ? 2000 : 15000);
  } catch (e) {
    showError('The agent is not responding (' + e.message + '). Run "vdf-agent open" to start it again.');
    timer = setTimeout(refresh, 5000);
  }
}

$('scan-now').addEventListener('click', async () => {
  try { await post('/api/scan'); refresh(); } catch (e) { showError(e.message); }
});
$('more').addEventListener('click', () => { shown += PAGE; render(); });
refresh();
