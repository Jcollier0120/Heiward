// Back and Forward by page, not by folder (the owner, 2026-11-09). In the folder view every folder you open is a step
// in the browser's history, so Back used to climb the folders one by one, as Up does. Now Back goes to the last page
// you were on (This PC, Settings, About, the Developer or Games area, or the folder view as a whole), and Up is for the
// folders (and an area's parts). Forward does the same the other way: to the next page, and there to the last folder
// (or part) you had open.
//
// The browser's own history keeps every step, as it must (its own Back button still steps through them). This keeps
// the list of this tab's steps (sessionStorage) and each history entry's place in it (history.state.trail), so the
// page's Back and Forward can jump a whole page's run of steps at once with history.go. Loaded before app.js.
'use strict';

/**
 * The page a step is on: the folder view is one page, whichever folder, and so is each area (Developer, Games),
 * whichever of its parts, as Up climbs them; anything else is its own page.
 */
function trailPage(hash, at) {
  if (typeof hash !== 'string') return '?' + at; // a step from before the list was kept: a page of its own
  const h = hash === '' || hash === '#' ? '#/' : hash;
  if (h.startsWith('#/f/')) return 'folder';
  for (const area of ['#/dev', '#/games']) if (h === area || h.startsWith(area + '/')) return area;
  return h;
}

/** Where Back goes from step at: the last step of the page before this one, as you left it; -1 when there's none. */
function trailBack(steps, at) {
  const page = trailPage(steps[at], at);
  let first = at;
  while (first > 0 && trailPage(steps[first - 1], first - 1) === page) first--;
  return first - 1;
}

/** Where Forward goes from step at: the last step of the page after this one; -1 when there's none. */
function trailForward(steps, at) {
  const page = trailPage(steps[at], at);
  let last = at;
  while (last + 1 < steps.length && trailPage(steps[last + 1], last + 1) === page) last++;
  if (last + 1 >= steps.length) return -1;
  const next = trailPage(steps[last + 1], last + 1);
  let end = last + 1;
  while (end + 1 < steps.length && trailPage(steps[end + 1], end + 1) === next) end++;
  return end;
}

/**
 * The trail for a window (win: its history, location and sessionStorage). sync() after every hashchange; replace(hash)
 * instead of history.replaceState, so the step keeps its place; back() and forward() for the command bar's buttons.
 */
function makeTrail(win) {
  const KEY = 'heiward.trail';
  const placeOf = () => {
    const s = win.history.state;
    return s && typeof s.trail === 'number' && s.trail >= 0 ? s.trail : null;
  };
  const tag = (n, hash) => {
    const state = Object.assign({}, win.history.state, { trail: n });
    if (hash === undefined) win.history.replaceState(state, '');
    else win.history.replaceState(state, '', hash);
  };
  const save = () => { try { win.sessionStorage.setItem(KEY, JSON.stringify(steps)); } catch { /* kept for this visit only */ } };
  let steps = (() => {
    try {
      const v = JSON.parse(win.sessionStorage.getItem(KEY));
      return Array.isArray(v) ? v : [];
    } catch { return []; }
  })();
  let at = placeOf();
  if (at === null || at >= steps.length) {
    // A new visit in this tab, or one whose list is gone: the trail starts here.
    steps = [win.location.hash];
    at = 0;
    tag(0);
  } else steps[at] = win.location.hash; // a reload: the same place
  save();
  // Back or Forward asked for and not there yet: a second click waits for it, rather than going twice as far.
  let going = false;

  return {
    /** After a hashchange: a step the trail knows (Back, Forward, the browser's own) or a new one, which ends it there. */
    sync() {
      going = false;
      const n = placeOf();
      if (n !== null && n < steps.length) at = n;
      else {
        at += 1;
        steps.length = at;
        tag(at);
      }
      steps[at] = win.location.hash;
      save();
    },
    /** The page shows another hash in place of this step's (history.replaceState), keeping its place in the trail. */
    replace(hash) {
      tag(at, hash);
      steps[at] = win.location.hash;
      save();
    },
    canBack: () => trailBack(steps, at) >= 0,
    canForward: () => trailForward(steps, at) >= 0,
    back() {
      const to = trailBack(steps, at);
      if (to < 0 || going) return;
      going = true;
      win.history.go(to - at);
    },
    forward() {
      const to = trailForward(steps, at);
      if (to < 0 || going) return;
      going = true;
      win.history.go(to - at);
    },
  };
}
