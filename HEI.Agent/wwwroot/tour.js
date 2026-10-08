// The tour of the review page, for someone who has just installed Heiward: what it does, the few choices that are
// theirs on day one, then the page, part by part. It's Heiward's own, so it works with or without Manor: it opens
// by itself the first time the page is opened after setup (once per browser), and again at #/tour. Manor's hire
// flow opens #/tour?from=<the role's card>, and the last step then offers Back to Manor beside Done.
// Loaded after app.js, whose state, el(), icon(), toggleSwitch() and saveSettings() it uses.
'use strict';

const TOURED = 'heiward.toured';

/** The parts of the page it walks, by their data-tour; only those showing are walked. */
const TOUR_PARTS = [
  ['controls', 'Where the AI runs, and Scan now',
    'The badge says where AI matching runs: on the NPU when this PC has one, else the graphics card or the processor. Pause holds the scheduled scans for a while; Scan now starts one at full speed.'],
  ['tiles', 'What you could free',
    'The sets of copies waiting for your review, the space they take, and what you have freed so far.'],
  ['drives', 'Devices and drives',
    'A card per drive, as File Explorer has them. Open one to walk its folders and see the copies in each. Right-click a folder to include it or leave it out, or a drive to scan it only when you ask, so an archive disk can sleep.'],
  ['dev', 'Developer area',
    'Your repositories\' open pull requests, and what development tools recreate: build outputs, finished worktrees, package caches and old temp files.'],
  ['games', 'Games',
    'Your games by launcher and drive, and what they leave behind: leftovers of uninstalled games, download caches, shader caches and crash dumps. What you remove goes to the Recycle Bin.'],
  ['hotspots', 'Where the duplicates are',
    'The folders where cleaning up frees the most. Only identical files and pixel-level copies are ticked for you: look-alikes are your call.'],
  ['history', 'History',
    'What you sent to the Recycle Bin and what you kept, so a choice can be undone.'],
];

let tourStep = null; // null: closed; else 0 (intro), 1 (your choices), 2.. (the page's parts)
let tourParts = [];
let tourFrom = null;
let tourFocus = null;

function toured() {
  try { return localStorage.getItem(TOURED) === '1'; } catch { return false; }
}

function markToured() {
  try { localStorage.setItem(TOURED, '1'); } catch { /* a private window: it may open again next time */ }
}

/** The page #/tour?from= names, only when it's on this PC (localhost, 127.0.0.1, [::1], a *.localhost name). */
function tourFromHash(hash) {
  const q = hash.indexOf('?');
  if (q < 0) return null;
  const from = new URLSearchParams(hash.slice(q + 1)).get('from');
  if (!from) return null;
  try {
    const u = new URL(from);
    const host = u.hostname.toLowerCase();
    const local = host === 'localhost' || host === '127.0.0.1' || host === '[::1]' || host.endsWith('.localhost');
    return (u.protocol === 'http:' || u.protocol === 'https:') && local ? u.href : null;
  } catch { return null; }
}

function isTourHash(hash) {
  return hash === '#/tour' || hash.startsWith('#/tour?');
}

function startTour(from) {
  if (!state || state.setup.needed) return;
  tourFrom = from;
  tourStep = 0;
  // The page under it is the home page, whose parts the tour walks.
  if (isTourHash(location.hash)) history.replaceState(null, '', '#/');
  route = parseRoute();
  renderRoute();
  drawTour();
}

function endTour(back) {
  markToured();
  tourStep = null;
  clearTourFocus();
  const box = $('tour');
  if (box) box.remove();
  if (back && tourFrom) location.href = tourFrom;
  tourFrom = null;
}

function clearTourFocus() {
  if (tourFocus) tourFocus.classList.remove('tour-focus');
  tourFocus = null;
}

/** The page's parts showing now: the Developer area only with Developer mode on, and so on. */
function partsShowing() {
  return TOUR_PARTS.filter(([name]) => {
    const e = document.querySelector('[data-tour="' + name + '"]');
    return e && !e.classList.contains('hidden') && e.offsetParent !== null;
  });
}

function drawTour() {
  clearTourFocus();
  let box = $('tour');
  if (!box) {
    box = el('div', 'tour');
    box.id = 'tour';
    box.setAttribute('role', 'dialog');
    box.setAttribute('aria-modal', 'false');
    box.setAttribute('aria-labelledby', 'tour-title');
    document.body.append(box);
  }
  // Counted from the start, so step 2 knows it isn't the last; again after Developer mode changes what shows.
  if (!tourParts.length) tourParts = partsShowing();
  const total = 2 + tourParts.length;
  const card = el('div', 'tour-card');
  card.tabIndex = -1;
  const step = el('div', 'muted small tour-count', 'Step ' + (Math.min(tourStep, 2) + 1) + ' of 3' +
    (tourStep >= 2 && tourParts.length > 1 ? ' · ' + (tourStep - 1) + ' of ' + tourParts.length : ''));
  const title = el('h2');
  title.id = 'tour-title';
  const body = el('div', 'tour-body');

  if (tourStep === 0) {
    title.textContent = 'Heiward tends your drives';
    body.append(
      el('p', null, 'It finds duplicate photos and videos, including resized, recompressed, cropped and edited copies, with a vision model on this PC: on its NPU when it has one. It scans in the background, lightly, and keeps everything here: nothing about you or your files leaves this PC.'),
      el('p', null, 'It lists what it finds for you to review. Nothing is removed until you say so, and what you tick goes to the Recycle Bin.'));
  } else if (tourStep === 1) {
    title.textContent = 'Your choices';
    body.append(el('p', null, 'It works as it is: every fixed drive, leaving out Windows, programs, games, app data and code. A few things are yours to decide, now or any time in Settings:'));
    const list = el('div', 'tour-choices');
    // Game mode: its switch here, unless Manor decides it.
    const game = el('div', 'auto-row');
    const gameText = el('div', 'auto-text');
    gameText.append(el('div', 'auto-title', 'Game mode'));
    if (state.games.manor) gameText.append(el('div', 'muted small', state.games.manor.note + '.'));
    else game.append(toggleSwitch(state.games.enabled, 'Game mode', settingsBusy, async (on) => { await saveSettings({ gameMode: on }); tourParts = []; drawTour(); }));
    gameText.append(el('div', 'muted small', 'For gamers: a Games area with your games by launcher and drive, and what they leave behind, to clean up.'));
    game.append(gameText);
    list.append(game);
    // Developer mode: its switch here, unless Manor's Developer options decide it for every agent. While they turn it off,
    // nothing of it shows.
    if (!(state.dev.manor && !state.dev.enabled)) {
      const dev = el('div', 'auto-row');
      const devText = el('div', 'auto-text');
      devText.append(el('div', 'auto-title', 'Developer mode'));
      if (state.dev.manor) devText.append(el('div', 'muted small', state.dev.manor.note + '.'));
      else dev.append(toggleSwitch(state.dev.enabled, 'Developer mode', settingsBusy, async (on) => { await saveSettings({ developerMode: on }); tourParts = []; drawTour(); }));
      devText.append(el('div', 'muted small', 'For developers: a Developer area with your repositories\' pull requests, and what development tools recreate, to clean up.'));
      dev.append(devText);
      list.append(dev);
    }
    const scope = el('div', 'auto-row');
    const scopeText = el('div', 'auto-text');
    scopeText.append(el('div', 'auto-title', 'What it scans'), el('div', 'muted small', 'Right-click a folder on the page to include it or leave it out, or a drive to scan it only when you ask.'));
    scope.append(scopeText);
    const auto = el('div', 'auto-row');
    const autoText = el('div', 'auto-text');
    autoText.append(el('div', 'auto-title', 'Automatic cleanup'), el('div', 'muted small', 'Off until you turn it on in Settings, once you trust what it suggests: then plain copies go by themselves a few days after they are listed.'));
    auto.append(autoText);
    list.append(scope, auto);
    body.append(list);
  } else {
    const part = tourParts[tourStep - 2];
    if (part) {
      title.textContent = part[1];
      body.append(el('p', null, part[2]));
      const target = document.querySelector('[data-tour="' + part[0] + '"]');
      if (target) {
        target.classList.add('tour-focus');
        tourFocus = target;
        target.scrollIntoView({ block: 'center', behavior: 'smooth' });
      }
    } else {
      title.textContent = 'That\'s the page';
      body.append(el('p', null, 'Its first scan lists what it finds here.'));
    }
  }

  const last = tourStep >= total - 1;
  const buttons = el('div', 'tour-buttons');
  if (!last) {
    const skip = el('button', 'btn secondary', 'Skip');
    skip.addEventListener('click', () => endTour(false));
    buttons.append(skip);
  }
  if (tourStep > 0) {
    const back = el('button', 'btn secondary', 'Back');
    back.addEventListener('click', () => { tourStep--; drawTour(); });
    buttons.append(back);
  }
  if (last && tourFrom) {
    const manor = el('button', 'btn secondary', 'Back to Manor');
    manor.addEventListener('click', () => endTour(true));
    buttons.append(manor);
  }
  const next = el('button', 'btn', last ? 'Done' : 'Next');
  next.addEventListener('click', () => { if (last) endTour(false); else { tourStep++; drawTour(); } });
  buttons.append(next);

  card.append(step, title, body, buttons);
  box.replaceChildren(card);
  next.focus();
}

document.addEventListener('keydown', (e) => {
  if (tourStep !== null && e.key === 'Escape') endTour(false);
});

window.addEventListener('hashchange', () => {
  if (isTourHash(location.hash)) startTour(tourFromHash(location.hash));
});

// The first time the page is opened after setup, and at #/tour: once the page has its state.
const tourWait = setInterval(() => {
  if (!state || state.setup.needed) return;
  clearInterval(tourWait);
  if (isTourHash(location.hash)) startTour(tourFromHash(location.hash));
  else if (!toured()) startTour(null);
}, 300);
