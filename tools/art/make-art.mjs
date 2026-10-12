// Heiward's slides and its setup window's banner, drawn here so they can be drawn again.
//
//   node tools/art/make-art.mjs          writes HEI.Agent/wwwroot/setup/*.svg (the setup window shows them)
//   node tools/art/make-art.mjs --png    also renders docs/art/*.png at twice the size, through Edge
//
// Each slide is 1200 x 628, the size social sites show a link's picture at, so each one is also a picture to post.
// They share Castellan's look (its setup's slides): the night sky, the dotted grid, Segoe UI. Heiward's colour is its
// green; Castellan's indigo marks Castellan.
//
// The Castellan mark (castellan.svg) and its agents' icons (agents/) are Castellan's, used here to show Heiward's
// place among its staff. They aren't part of Heiward's code, nor covered by its license.

import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { execFileSync } from 'node:child_process';

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, '..', '..');
const out = join(repo, 'HEI.Agent', 'wwwroot', 'setup');
const pngOut = join(repo, 'docs', 'art');
const W = 1200, H = 628;
const SITE = 'castellan-software.com';
const FONT = `'Segoe UI Variable Display','Segoe UI',system-ui,-apple-system,Helvetica,Arial,sans-serif`;

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const r2 = (n) => Math.round(n * 100) / 100;

/** Text; `parts` may mix plain strings and [text, fill] pairs for a coloured word. */
function text(x, y, parts, { size = 16, weight = 500, fill = '#cbd5e1', anchor = 'start', ls = 0, opacity } = {}) {
  const body = (Array.isArray(parts) ? parts : [parts]).map((p) => (Array.isArray(p) ? `<tspan fill="${p[1]}">${esc(p[0])}</tspan>` : esc(p))).join('');
  return `<text x="${r2(x)}" y="${r2(y)}" font-family="${FONT}" font-size="${size}" font-weight="${weight}" fill="${fill}" text-anchor="${anchor}"${ls ? ` letter-spacing="${ls}"` : ''}${opacity != null ? ` opacity="${opacity}"` : ''}>${body}</text>`;
}

/** An icon from its file, in its dark colours (the icons follow Windows' theme; the slides are always night). */
function darkIcon(file) {
  let svg = readFileSync(file, 'utf8').replace(/<!--[\s\S]*?-->/g, '');
  const dark = svg.match(/@media \(prefers-color-scheme: dark\) \{([\s\S]*?)\}\s*<\/style>/);
  if (dark) svg = svg.replace(/<style>[\s\S]*?<\/style>/, `<style>${dark[1]}</style>`);
  return 'data:image/svg+xml;base64,' + Buffer.from(svg.replace(/\s+/g, ' ')).toString('base64');
}

/** Heiward's mark, the shield and leaf, in a 24-unit box. */
const MARK = {
  back: 'M12 2.5 20 5.4v6.1c0 4.9-3.3 8.6-8 10-4.7-1.4-8-5.1-8-10V5.4z',
  front: 'M8.2 16.2c-.4-4.9 2.6-8.2 7.6-8.4.3 5-2.9 8.2-7.6 8.4z',
  vein: 'M8.2 16.2 13 11.4',
};
function mark(x, y, size, { back = '#173323', line = '#6ccb8f', leaf = '#6ccb8f', vein = '#173323' } = {}) {
  const s = size / 24;
  return `<g transform="translate(${r2(x)} ${r2(y)}) scale(${r2(s)})"><path d="${MARK.back}" fill="${back}" stroke="${line}" stroke-width="1.6" stroke-linejoin="round"/><path d="${MARK.front}" fill="${leaf}"/><path d="${MARK.vein}" fill="none" stroke="${vein}" stroke-width="1.2" stroke-linecap="round"/></g>`;
}

/** Heiward's app tile: the mark on a deep green square. */
function tile(x, y, size, glow = false) {
  return `${glow ? `<circle cx="${x + size / 2}" cy="${y + size / 2}" r="${size * 1.15}" fill="url(#greenhalo)"/>` : ''}<rect x="${x}" y="${y}" width="${size}" height="${size}" rx="${size * 0.24}" fill="url(#tilebg)" stroke="#34d399" stroke-opacity="0.35"/>${mark(x + size * 0.14, y + size * 0.12, size * 0.72)}`;
}

const defs = `<defs>
  <linearGradient id="hl" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#6ee7b7"/><stop offset="1" stop-color="#22d3ee"/></linearGradient>
  <linearGradient id="hlc" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#a5b4fc"/><stop offset="1" stop-color="#22d3ee"/></linearGradient>
  <linearGradient id="tilebg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#064e3b"/><stop offset="1" stop-color="#022c22"/></linearGradient>
  <linearGradient id="panel" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#0f172a"/><stop offset="1" stop-color="#0b1222"/></linearGradient>
  <linearGradient id="chipbody" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#064e3b"/><stop offset="1" stop-color="#022c22"/></linearGradient>
  <linearGradient id="chipdie" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#10b981"/><stop offset="1" stop-color="#047857"/></linearGradient>
  <linearGradient id="chipbodyc" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#312e81"/><stop offset="1" stop-color="#1e1b4b"/></linearGradient>
  <linearGradient id="chipdiec" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#6366f1"/><stop offset="1" stop-color="#4f46e5"/></linearGradient>
  <linearGradient id="sky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#38bdf8"/><stop offset="1" stop-color="#fde68a"/></linearGradient>
  <linearGradient id="sky2" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fb923c"/><stop offset="1" stop-color="#fde68a"/></linearGradient>
  <linearGradient id="sky3" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#818cf8"/><stop offset="1" stop-color="#f9a8d4"/></linearGradient>
  <radialGradient id="greenhalo"><stop offset="0" stop-color="#10b981" stop-opacity="0.45"/><stop offset="0.5" stop-color="#10b981" stop-opacity="0.12"/><stop offset="1" stop-color="#10b981" stop-opacity="0"/></radialGradient>
  <radialGradient id="indigohalo"><stop offset="0" stop-color="#4f46e5" stop-opacity="0.5"/><stop offset="0.5" stop-color="#4f46e5" stop-opacity="0.14"/><stop offset="1" stop-color="#4f46e5" stop-opacity="0"/></radialGradient>
  <radialGradient id="wash"><stop offset="0" stop-color="#10b981" stop-opacity="0.2"/><stop offset="1" stop-color="#10b981" stop-opacity="0"/></radialGradient>
  <radialGradient id="washc"><stop offset="0" stop-color="#4f46e5" stop-opacity="0.26"/><stop offset="1" stop-color="#4f46e5" stop-opacity="0"/></radialGradient>
  <radialGradient id="wash2"><stop offset="0" stop-color="#22d3ee" stop-opacity="0.09"/><stop offset="1" stop-color="#22d3ee" stop-opacity="0"/></radialGradient>
  <pattern id="dots" width="28" height="28" patternUnits="userSpaceOnUse"><circle cx="2" cy="2" r="1.3" fill="#f1f5f9" fill-opacity="0.07"/></pattern>
  <clipPath id="thumb"><rect width="44" height="30" rx="5"/></clipPath>
</defs>`;

/** A photo's thumbnail: sky, sun and a hill; `kind` crops, tints or shrinks it, as the copies Heiward finds are. */
function photo(x, y, kind = 'original', w = 44, h = 30) {
  const sky = kind === 'edited' ? 'url(#sky3)' : kind === 'video' ? 'url(#sky2)' : 'url(#sky)';
  const zoom = kind === 'crop' ? 'translate(-14 -10) scale(1.6)' : '';
  const sx = w / 44, sy = h / 30;
  return `<g transform="translate(${x} ${y}) scale(${r2(sx)} ${r2(sy)})"><g clip-path="url(#thumb)"><g transform="${zoom}"><rect width="44" height="30" fill="${sky}"/><circle cx="31" cy="10" r="4.6" fill="#fef3c7"/><path d="M0 24c8-7 15-9 22-5s14 3 22-3v14H0z" fill="#15803d"/><path d="M0 27c10-4 20-5 30-2s10 1 14 0v5H0z" fill="#166534"/></g>${kind === 'video' ? '<path d="M18 10.5v9l8-4.5z" fill="#fff" fill-opacity="0.9"/>' : ''}</g><rect width="44" height="30" rx="5" fill="none" stroke="#f1f5f9" stroke-opacity="0.18"/></g>`;
}

/** A window: title bar dots and a title, on the panel colour. */
function windowBox(x, y, w, h, title, { accent = '#334155', glow = false } = {}) {
  return `${glow ? `<rect x="${x - 6}" y="${y - 6}" width="${w + 12}" height="${h + 12}" rx="22" fill="#10b981" fill-opacity="0.06"/>` : ''}<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="16" fill="url(#panel)" stroke="${accent}" stroke-width="1.5"/>
  <circle cx="${x + 20}" cy="${y + 22}" r="4" fill="#475569"/><circle cx="${x + 34}" cy="${y + 22}" r="4" fill="#475569"/><circle cx="${x + 48}" cy="${y + 22}" r="4" fill="#475569"/>
  ${text(x + 64, y + 27.5, title, { size: 15, weight: 600, fill: '#cbd5e1' })}
  <line x1="${x}" y1="${y + 44}" x2="${x + w}" y2="${y + 44}" stroke="#1e293b"/>`;
}

function checkbox(x, y, on) {
  return on
    ? `<rect x="${x}" y="${y}" width="18" height="18" rx="5" fill="#10b981"/><path d="M${x + 4.5} ${y + 9.5}l3.2 3.2 6-6.6" fill="none" stroke="#022c22" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/>`
    : `<rect x="${x + 0.75}" y="${y + 0.75}" width="16.5" height="16.5" rx="4.5" fill="none" stroke="#475569" stroke-width="1.5"/>`;
}

function pill(x, y, label, { fill = '#0f172a', stroke = '#334155', color = '#cbd5e1', size = 13, weight = 600, anchor = 'start', pad = 14, icon = '' } = {}) {
  const w = label.length * size * 0.56 + pad * 2 + (icon ? 22 : 0);
  const left = anchor === 'end' ? x - w : x;
  return `<rect x="${r2(left)}" y="${y}" width="${r2(w)}" height="${size * 2.2}" rx="${size * 1.1}" fill="${fill}" stroke="${stroke}"/>${icon ? icon(left + pad, y + size * 1.1) : ''}${text(left + pad + (icon ? 22 : 0), y + size * 1.48, label, { size, weight, fill: color })}`;
}

/** A chip with pins on every side, its die saying `label`; green for Heiward, indigo for Castellan. */
function chip(cx, cy, size, label, castellan = false) {
  const half = size / 2, pins = [];
  for (let i = 0; i < 7; i++) {
    const t = -half + size * 0.16 + (size * 0.68 * i) / 6;
    pins.push(`M${cx + t} ${cy - half - 16}v12`, `M${cx + t} ${cy + half + 4}v12`, `M${cx - half - 16} ${cy + t}h12`, `M${cx + half + 4} ${cy + t}h12`);
  }
  const die = size * 0.62;
  return `<circle cx="${cx}" cy="${cy}" r="${size * 1.25}" fill="url(#${castellan ? 'indigohalo' : 'greenhalo'})"/>
  <path d="${pins.join('')}" stroke="${castellan ? '#818cf8' : '#34d399'}" stroke-width="5" stroke-linecap="round"/>
  <rect x="${cx - half}" y="${cy - half}" width="${size}" height="${size}" rx="${size * 0.13}" fill="url(#${castellan ? 'chipbodyc' : 'chipbody'})" stroke="${castellan ? '#818cf8' : '#34d399'}" stroke-width="2.5"/>
  <circle cx="${cx - half + 14}" cy="${cy - half + 14}" r="4" fill="${castellan ? '#22d3ee' : '#6ee7b7'}"/>
  <rect x="${cx - die / 2}" y="${cy - die / 2}" width="${die}" height="${die}" rx="${die * 0.12}" fill="url(#${castellan ? 'chipdiec' : 'chipdie'})"/>
  <path d="M${cx - die / 6} ${cy - die / 2}v${die}M${cx + die / 6} ${cy - die / 2}v${die}M${cx - die / 2} ${cy - die / 6}h${die}M${cx - die / 2} ${cy + die / 6}h${die}" stroke="#fff" stroke-opacity="0.12"/>
  ${text(cx, cy + size * 0.11, label, { size: size * 0.27, weight: 800, fill: '#fff', anchor: 'middle', ls: 1 })}`;
}

/** The slide's frame: the night, Heiward's name, "Part of Castellan", and the site at the foot. */
function slide(name, { eyebrow, title, body, art, foot = 'Free and open source · AGPL v3', washAt = [820, 300], castellan = false }) {
  const titleLines = title.map((line, i) => text(56, 196 + i * 50, line, { size: 42, weight: 700, fill: '#f1f5f9' })).join('\n');
  const bodyTop = 196 + title.length * 50 + 14;
  const bodyLines = body.map((line, i) => text(56, bodyTop + i * 25, line, { size: 17, weight: 500, fill: '#cbd5e1' })).join('\n');
  const castle = `<image href="${darkIcon(join(here, 'castellan.svg'))}" x="${W - 56 - 164}" y="46" width="22" height="22"/>`;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="${esc(title.map((l) => (Array.isArray(l) ? l.map((p) => (Array.isArray(p) ? p[0] : p)).join('') : l)).join(' '))}">
<!-- Drawn by tools/art/make-art.mjs: change it there. -->
${defs}
<rect width="${W}" height="${H}" fill="#020617"/>
<rect width="${W}" height="${H}" fill="url(#dots)"/>
<circle cx="${washAt[0]}" cy="${washAt[1]}" r="440" fill="url(#${castellan ? 'washc' : 'wash'})"/>
<circle cx="80" cy="600" r="560" fill="url(#wash2)"/>
${tile(56, 44, 46)}
${text(116, 74.5, 'HEIWARD', { size: 18, weight: 700, fill: '#6ee7b7', ls: 4.4 })}
<rect x="${W - 56 - 176}" y="42" width="176" height="30" rx="15" fill="#1e1b4b" fill-opacity="0.6" stroke="#4f46e5" stroke-opacity="0.6"/>
${castle}
${text(W - 56 - 134, 62, 'Part of Castellan', { size: 14, weight: 600, fill: '#c7d2fe' })}
${text(56, 142, eyebrow, { size: 15, weight: 700, fill: '#fbbf24', ls: 2.2 })}
${titleLines}
${bodyLines}
${art}
<line x1="56" y1="572" x2="${W - 56}" y2="572" stroke="#1e293b"/>
${text(56, 600, foot, { size: 14, weight: 500, fill: '#64748b' })}
${text(W - 56, 600, SITE, { size: 15, weight: 700, fill: '#a5b4fc', anchor: 'end', ls: 0.3 })}
</svg>
`;
}

// The slides.

const found = slide('found', {
  eyebrow: 'FREE FOR WINDOWS',
  title: ['Look-alike photos', [['and videos, '], ['found.', 'url(#hl)']]],
  body: ['AI matching finds the resized, recompressed,', 'cropped and edited copies on your drives,', 'and lists them for you to review. Nothing is', 'removed until you say so.'],
  art: (() => {
    const left = 506, top = 146;
    const files = [['IMG_4821.jpg', 'original'], ['IMG_4821-small.jpg', 'original'], ['beach-crop.jpg', 'crop'], ['IMG_4821 (edited).jpg', 'edited'], ['VID_0193.mp4', 'video']];
    const rows = files.map(([n, k], i) => `${photo(left + 18, top + 62 + i * 54, k)}${text(left + 74, top + 82 + i * 54, n, { size: 15, weight: 500, fill: '#cbd5e1' })}`).join('');
    const rx = 866, found = [
      ['IMG_4821.jpg', 'The original · kept', 'original', null],
      ['IMG_4821-small.jpg', 'Resized copy · 0.4 MB', 'original', true],
      ['beach-crop.jpg', 'Cropped copy · 2.1 MB', 'crop', true],
      ['IMG_4821 (edited).jpg', 'Edited copy · yours to decide', 'edited', false],
    ];
    const frows = found.map(([n, d, k, on], i) => {
      const y = top + 60 + i * 58;
      return `${on == null ? `<circle cx="${rx + 27}" cy="${y + 15}" r="6" fill="#34d399"/>` : checkbox(rx + 18, y + 6, on)}${photo(rx + 46, y, k)}${text(rx + 102, y + 14, n, { size: 15, weight: 600, fill: '#f1f5f9' })}${text(rx + 102, y + 33, d, { size: 13, weight: 500, fill: on == null ? '#6ee7b7' : '#94a3b8' })}`;
    }).join('');
    return `${windowBox(left, top, 274, 344, 'Pictures')}${rows}
    <path d="M${left + 274} ${top + 172}H${rx}" stroke="#34d399" stroke-width="2" stroke-dasharray="3 6" stroke-linecap="round"/>
    ${tile(left + 274 + 18, top + 147, 50, true)}
    ${windowBox(rx, top, 278, 344, 'Heiward found', { accent: '#10b981', glow: true })}${frows}
    <rect x="${rx + 18}" y="${top + 292}" width="242" height="36" rx="10" fill="#10b981"/>
    ${text(rx + 139, top + 315.5, 'Move 2 to the Recycle Bin', { size: 14, weight: 700, fill: '#022c22', anchor: 'middle' })}
    ${text(rx, top + 372, 'An example', { size: 13, fill: '#64748b' })}`;
  })(),
});

const npu = slide('npu', {
  eyebrow: 'NPU FIRST',
  title: [[['AI on your '], ['NPU,', 'url(#hl)']], 'not in the cloud.'],
  body: ['Heiward runs its AI on the PC\u2019s NPU, the chip', 'made for AI: quick, cool and light on the', 'battery, so scans can run every hour. With no', 'NPU, it uses the graphics card or processor.'],
  washAt: [900, 280],
  art: (() => {
    const cx = 900, cy = 268;
    const stream = [0, 1, 2, 3].map((i) => photo(560 + i * 62, cy - 15 + (i % 2 ? -26 : 22), ['original', 'crop', 'edited', 'video'][i])).join('');
    const stats = [
      ['~3 ms', 'a picture, on a', 'Snapdragon X NPU'],
      ['Hourly', 'scans, on a sip', 'of power'],
      ['0 bytes', 'of your files', 'sent online'],
    ].map(([big, a, b], i) => {
      const x = 548 + i * 204, y = 436;
      return `<rect x="${x}" y="${y}" width="190" height="100" rx="14" fill="#0f172a" stroke="#1e293b"/>${text(x + 18, y + 40, big, { size: 28, weight: 800, fill: 'url(#hl)' })}${text(x + 18, y + 66, a, { size: 13, weight: 500, fill: '#94a3b8' })}${text(x + 18, y + 84, b, { size: 13, weight: 500, fill: '#94a3b8' })}`;
    }).join('');
    return `${stream}<path d="M820 ${cy}H${cx - 120}" stroke="#34d399" stroke-width="2.5" stroke-dasharray="3 7" stroke-linecap="round"/>
    ${chip(cx, cy, 170, 'NPU')}
    ${text(cx, cy + 150, 'Qualcomm Hexagon · Intel AI Boost · AMD Ryzen AI', { size: 13.5, weight: 600, fill: '#94a3b8', anchor: 'middle' })}
    ${stats}`;
  })(),
  foot: 'Free and open source · Intel and AMD NPU support is new',
});

const safe = slide('safe', {
  eyebrow: 'YOU DECIDE',
  title: ['Nothing goes until', [['you '], ['say so.', 'url(#hl)']]],
  body: ['Heiward lists what it finds on a review page.', 'What you tick goes to the Recycle Bin, so it', 'can come back. The originals stay, and', 'cloud-only files are never downloaded.'],
  art: (() => {
    const x = 540, y = 140, w = 470;
    const sets = [
      ['Beach day', '3 copies of IMG_4821.jpg', 2, 'crop'],
      ['Birthday video', 'VID_0193.mp4, saved twice', 1, 'video'],
      ['Sunset', 'An edit beside its original', 0, 'edited'],
    ];
    const rows = sets.map(([t, d, ticked, k], i) => {
      const ry = y + 62 + i * 92;
      return `<rect x="${x + 16}" y="${ry}" width="${w - 32}" height="80" rx="12" fill="#0b1222" stroke="#1e293b"/>
      ${photo(x + 30, ry + 14, k, 66, 52)}${text(x + 112, ry + 33, t, { size: 16, weight: 700, fill: '#f1f5f9' })}${text(x + 112, ry + 56, d, { size: 13, fill: '#94a3b8' })}
      ${ticked ? `${checkbox(x + w - 58, ry + 31, true)}${text(x + w - 66, ry + 45, `${ticked} ticked`, { size: 13, weight: 600, fill: '#6ee7b7', anchor: 'end' })}` : `${checkbox(x + w - 58, ry + 31, false)}${text(x + w - 66, ry + 45, 'kept', { size: 13, weight: 600, fill: '#94a3b8', anchor: 'end' })}`}`;
    }).join('');
    const bx = 1060, by = 300;
    return `${windowBox(x, y, w, 360, 'Review · 3 sets')}${rows}
    <path d="M${x + w + 8} ${by + 10}C${x + w + 30} ${by - 30} ${bx - 30} ${by - 30} ${bx - 6} ${by}" fill="none" stroke="#34d399" stroke-width="2.5" stroke-dasharray="3 7" stroke-linecap="round"/>
    <g transform="translate(${bx} ${by})"><circle r="62" fill="url(#greenhalo)"/><path d="M-26 -20h52l-5 52a6 6 0 0 1-6 5h-30a6 6 0 0 1-6-5z" fill="#0f172a" stroke="#34d399" stroke-width="2.5" stroke-linejoin="round"/><rect x="-32" y="-30" width="64" height="10" rx="4" fill="#34d399"/><path d="M-9 -30v-6h18v6" fill="none" stroke="#34d399" stroke-width="2.5"/><path d="M-11 -6v28M0 -6v28M11 -6v28" stroke="#34d399" stroke-opacity="0.5" stroke-width="2.5" stroke-linecap="round"/></g>
    ${text(bx, by + 92, 'Recycle Bin', { size: 14, weight: 600, fill: '#cbd5e1', anchor: 'middle' })}
    ${text(bx, by + 112, 'restore any time', { size: 12.5, fill: '#64748b', anchor: 'middle' })}`;
  })(),
});

/** Small line icons for the tiles, 28 units, in Heiward's green. */
const ICONS = {
  gauge: '<path d="M4 20a10 10 0 1 1 20 0" fill="none" stroke="#6ee7b7" stroke-width="2.4" stroke-linecap="round"/><path d="M14 20l-4.5-5" stroke="#fbbf24" stroke-width="2.6" stroke-linecap="round"/><circle cx="14" cy="20" r="2.4" fill="#fbbf24"/>',
  leaf: '<path d="M6 22c-1-9 5-15 16-16 1 11-5 17-16 16z" fill="#065f46" stroke="#6ee7b7" stroke-width="2.2" stroke-linejoin="round"/><path d="M6 22l9-9" stroke="#6ee7b7" stroke-width="2.2" stroke-linecap="round"/>',
  pad: '<path d="M8 9h12a6 6 0 0 1 5.8 7.6l-1.3 4.7a3 3 0 0 1-5 1.3L16.6 20h-5.2l-2.9 2.6a3 3 0 0 1-5-1.3l-1.3-4.7A6 6 0 0 1 8 9z" fill="#065f46" stroke="#6ee7b7" stroke-width="2.2" stroke-linejoin="round"/><path d="M9 13v4M7 15h4" stroke="#6ee7b7" stroke-width="2" stroke-linecap="round"/><circle cx="19" cy="14" r="1.4" fill="#fbbf24"/><circle cx="21.5" cy="16.5" r="1.4" fill="#fbbf24"/>',
  disk: '<rect x="4" y="7" width="20" height="15" rx="3" fill="#065f46" stroke="#6ee7b7" stroke-width="2.2"/><path d="M8 17h8" stroke="#6ee7b7" stroke-width="2.2" stroke-linecap="round"/><circle cx="20" cy="17" r="1.6" fill="#6ee7b7"/><path d="M17 3.5h4l-4 4h4" fill="none" stroke="#a5b4fc" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>',
  game: '<rect x="4" y="5" width="20" height="18" rx="4" fill="#065f46" stroke="#6ee7b7" stroke-width="2.2"/><path d="M11 10l7 4-7 4z" fill="#6ee7b7"/>',
  code: '<rect x="3" y="5" width="22" height="18" rx="4" fill="#065f46" stroke="#6ee7b7" stroke-width="2.2"/><path d="M11 11l-3.5 3 3.5 3M17 11l3.5 3-3.5 3" fill="none" stroke="#6ee7b7" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/>',
};
const icon = (name, x, y, scale = 1) => `<g transform="translate(${x} ${y}) scale(${scale})">${ICONS[name]}</g>`;

const gentle = slide('gentle', {
  eyebrow: 'GENTLE BY DEFAULT',
  title: ['Gentle in the', [['background.', 'url(#hl)']]],
  body: ['Scheduled scans run at a low priority, on', 'efficient cores, under a hard cap on the', 'processor. A drive with nothing new isn\u2019t', 'read at all, so a sleeping disk stays asleep.'],
  art: (() => {
    const tiles = [
      ['gauge', '\u00bc of the processor', 'at most, and never more than', 'two cores\u2019 worth'],
      ['leaf', 'Efficiency mode', 'Windows\u2019 own: efficient cores,', 'low clocks, low priority'],
      ['pad', 'Makes way for games', 'Full screen or 3D running?', 'Scans step back until it closes'],
      ['disk', 'Reads only what changed', 'The drive\u2019s change journal', 'says where to look'],
    ];
    return tiles.map(([ic, t, a, b], i) => {
      const x = 548 + (i % 2) * 304, y = 150 + Math.floor(i / 2) * 182;
      return `<rect x="${x}" y="${y}" width="290" height="166" rx="18" fill="#0f172a" stroke="#1e293b"/><rect x="${x + 22}" y="${y + 22}" width="52" height="52" rx="14" fill="#022c22" stroke="#10b981" stroke-opacity="0.4"/>${icon(ic, x + 34, y + 34, 1)}
      ${text(x + 22, y + 108, t, { size: 19, weight: 700, fill: '#f1f5f9' })}${text(x + 22, y + 132, a, { size: 13.5, fill: '#94a3b8' })}${text(x + 22, y + 151, b, { size: 13.5, fill: '#94a3b8' })}`;
    }).join('');
  })(),
});

const more = slide('more', {
  eyebrow: 'GAME MODE · DEVELOPER MODE',
  title: ['Games and code,', [['tidied ', ], ['too.', 'url(#hl)']]],
  body: ['In game mode: what games leave behind, from', 'shader caches to leftovers of uninstalled', 'games, never your saves. In developer mode:', 'build outputs, worktrees and merged branches.'],
  art: (() => {
    const panel = (x, ic, title, items) => `<rect x="${x}" y="150" width="290" height="330" rx="18" fill="#0f172a" stroke="#1e293b"/>
      <rect x="${x + 20}" y="170" width="44" height="44" rx="12" fill="#022c22" stroke="#10b981" stroke-opacity="0.4"/>${icon(ic, x + 28, x ? 178 : 178, 1)}
      ${text(x + 78, 199, title, { size: 18, weight: 700, fill: '#f1f5f9' })}
      ${items.map(([n, s], i) => `<line x1="${x + 20}" y1="${238 + i * 56}" x2="${x + 270}" y2="${238 + i * 56}" stroke="#1e293b"/>${text(x + 20, 268 + i * 56, n, { size: 14.5, weight: 500, fill: '#cbd5e1' })}${text(x + 270, 268 + i * 56, s, { size: 14.5, weight: 700, fill: '#6ee7b7', anchor: 'end' })}`).join('')}`;
    return `${panel(548, 'game', 'Games', [['Leftovers of old games', '12.8 GB'], ['Shader caches', '4.1 GB'], ['Launcher downloads', '2.3 GB'], ['Crash dumps', '640 MB']])}
    ${panel(852, 'code', 'Developer', [['Build outputs', '18.4 GB'], ['Package caches', '6.7 GB'], ['Finished worktrees', '3.2 GB'], ['Merged branches', '27']])}
    ${text(852, 512, 'Each is off until you turn it on in Settings. An example.', { size: 13, fill: '#64748b', anchor: 'middle' })}`;
  })(),
});

const castellan = slide('castellan', {
  eyebrow: 'ONE OF A STAFF',
  title: ['Heiward works for', [['Castellan.', 'url(#hlc)']]],
  body: ['Castellan\u2019s staff keep your PC tidy and up', 'to date, with AI that runs on the PC, on', 'its NPU when it has one. Private, offline.', 'Heiward stays free, on its own or among them.'],
  castellan: true,
  washAt: [880, 300],
  art: (() => {
    const cx = 870, cy = 318;
    const staff = [
      // Only staff that use the local AI (the site's lib/npu-staff.ts): this picture wires each to the NPU.
      ['warrener', 'Warrener', 'Names your downloads'],
      ['thatcher', 'Thatcher', 'Explains app upgrades'],
      ['lamplighter', 'Lamplighter', 'Puts back good drivers'],
      ['herald', 'Herald', 'Sums up patch notes'],
      ['surveyor', 'Surveyor', 'Checks your PC'],
      ['clerk', 'Clerk', 'Makes scans searchable'],
      ['chamberlain', 'Chamberlain', 'Answers from your docs'],
    ];
    const ring = [];
    const all = [['heiward', 'Heiward', 'Look-alike photos'], ...staff];
    const n = all.length;
    all.forEach(([id, name, job], i) => {
      const a = -Math.PI / 2 + (i * 2 * Math.PI) / n;
      const rx = 236, ry = 170;
      const x = cx + Math.cos(a) * rx, y = cy + Math.sin(a) * ry;
      const me = id === 'heiward';
      const s = me ? 58 : 46;
      ring.push(`<line x1="${cx}" y1="${cy}" x2="${r2(x)}" y2="${r2(y)}" stroke="${me ? '#34d399' : '#6366f1'}" stroke-opacity="${me ? 0.9 : 0.45}" stroke-width="${me ? 2.5 : 1.6}" stroke-dasharray="${me ? '0' : '3 6'}"/>`);
      const tileSvg = me ? tile(x - s / 2, y - s / 2 - 10, s, true)
        : `<rect x="${r2(x - s / 2)}" y="${r2(y - s / 2 - 10)}" width="${s}" height="${s}" rx="${s * 0.26}" fill="#0f172a" stroke="#4f46e5" stroke-opacity="0.7"/><image href="${darkIcon(join(here, 'agents', id + '.svg'))}" x="${r2(x - s * 0.34)}" y="${r2(y - s * 0.34 - 10)}" width="${r2(s * 0.68)}" height="${r2(s * 0.68)}"/>`;
      const above = Math.sin(a) < -0.2;
      const ny = above ? y - s / 2 - 34 : y + s / 2 + 8, jy = above ? y - s / 2 - 17 : y + s / 2 + 25;
      ring.push(`<g>${tileSvg}${text(x, ny, name, { size: me ? 15 : 13.5, weight: 700, fill: me ? '#6ee7b7' : '#e2e8f0', anchor: 'middle' })}${text(x, jy, job, { size: 11.5, fill: '#94a3b8', anchor: 'middle' })}</g>`);
    });
    const [lines, tiles] = [ring.filter((s) => s.startsWith('<line')), ring.filter((s) => !s.startsWith('<line'))];
    return `${lines.join('')}${chip(cx, cy, 96, 'NPU', true)}${tiles.join('')}
    <rect x="56" y="${196 + 2 * 50 + 14 + 4 * 25 + 8}" width="292" height="40" rx="20" fill="#4f46e5"/>
    ${text(56 + 146, 196 + 2 * 50 + 14 + 4 * 25 + 33, 'Meet the staff at ' + SITE, { size: 14, weight: 700, fill: '#fff', anchor: 'middle' })}`;
  })(),
  foot: 'Heiward is free and open source · Castellan is sold with its staff',
});

/**
 * The setup window's banner: a hedgerow at night, a field gate in it and fireflies over it (a heiward kept the
 * hedges). Its foot fades into the page (setup.css). Fireflies twinkle unless the person asked for less motion.
 */
function banner() {
  const w = 1200, h = 220;
  let seed = 7;
  const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647);
  const stars = Array.from({ length: 70 }, () => `<circle cx="${r2(rnd() * w)}" cy="${r2(rnd() * 120)}" r="${r2(0.5 + rnd() * 1.1)}" fill="#f8fafc" fill-opacity="${r2(0.25 + rnd() * 0.6)}"/>`).join('');
  // The hedge: overlapping rounded clumps along a gentle line, darker at the back.
  const clumps = (y0, amp, rMin, rMax, fill, step) => {
    const out = [];
    for (let x = -30; x < w + 40; x += step) {
      const y = y0 + Math.sin(x / 140) * amp + rnd() * 8;
      out.push(`<circle cx="${r2(x)}" cy="${r2(y)}" r="${r2(rMin + rnd() * (rMax - rMin))}" fill="${fill}"/>`);
    }
    return out.join('');
  };
  const gateX = 560, gateY = 168;
  const gate = `<g stroke="#8b6b45" stroke-width="5" stroke-linecap="round"><path d="M${gateX} ${gateY - 44}v58M${gateX + 112} ${gateY - 44}v58" stroke="#6b4f30" stroke-width="9"/>${[0, 1, 2, 3, 4].map((i) => `<path d="M${gateX + 6} ${gateY - 38 + i * 11}h100"/>`).join('')}<path d="M${gateX + 8} ${gateY + 6}L${gateX + 104} ${gateY - 38}"/></g>`;
  const flies = Array.from({ length: 16 }, (_, i) => {
    const x = r2(40 + rnd() * (w - 80)), y = r2(70 + rnd() * 90), d = r2(1.8 + rnd() * 2.6), dl = r2(rnd() * 3);
    return `<g class="fly" style="animation-duration:${d}s;animation-delay:-${dl}s"><circle cx="${x}" cy="${y}" r="7" fill="url(#flyglow)"/><circle cx="${x}" cy="${y}" r="1.7" fill="#ecfccb"/></g>`;
  }).join('');
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${w} ${h}" preserveAspectRatio="xMidYMax slice" role="img" aria-label="A hedgerow at night, a field gate in it, fireflies over it">
<!-- Drawn by tools/art/make-art.mjs: change it there. -->
<style>@media (prefers-reduced-motion: no-preference) { .fly { animation: twinkle 3s ease-in-out infinite alternate; } } @keyframes twinkle { from { opacity: .25; } to { opacity: 1; } }</style>
<defs>
  <linearGradient id="bsky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#020617"/><stop offset="0.6" stop-color="#0b1a2e"/><stop offset="1" stop-color="#12312b"/></linearGradient>
  <radialGradient id="moonglow"><stop offset="0" stop-color="#ecfdf5" stop-opacity="0.35"/><stop offset="1" stop-color="#ecfdf5" stop-opacity="0"/></radialGradient>
  <radialGradient id="flyglow"><stop offset="0" stop-color="#d9f99d" stop-opacity="0.75"/><stop offset="1" stop-color="#d9f99d" stop-opacity="0"/></radialGradient>
</defs>
<rect width="${w}" height="${h}" fill="url(#bsky)"/>
${stars}
<circle cx="980" cy="52" r="70" fill="url(#moonglow)"/><circle cx="980" cy="52" r="20" fill="#f1f5f9"/><circle cx="989" cy="46" r="18" fill="#0b1a2e" fill-opacity="0.92"/>
<path d="M0 150C180 112 330 128 470 140S800 104 960 122S1140 138 1200 128V220H0z" fill="#0c2a24"/>
${clumps(170, 6, 26, 40, '#0b3326', 34)}
${clumps(184, 5, 22, 34, '#0e4a33', 30)}
<rect x="${gateX - 4}" y="${gateY - 60}" width="124" height="80" fill="#0c2a24"/>
${gate}
${clumps(206, 4, 18, 28, '#14532d', 26)}
<path d="M0 200H${w}V${h}H0z" fill="#0f3b2a"/>
${flies}
</svg>
`;
}

mkdirSync(out, { recursive: true });
const slides = { found, npu, safe, gentle, more, castellan };
for (const [n, svg] of Object.entries(slides)) writeFileSync(join(out, n + '.svg'), svg);
writeFileSync(join(out, 'banner.svg'), banner());
console.log(`wrote ${Object.keys(slides).length} slides and the banner to ${out}`);

if (process.argv.includes('--png')) {
  const edge = ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Microsoft/Edge/Application/msedge.exe'].find(existsSync);
  if (!edge) throw new Error('Edge renders the pictures, and it isn\'t installed here.');
  mkdirSync(pngOut, { recursive: true });
  for (const n of Object.keys(slides)) {
    const png = join(pngOut, `heiward-${n}.png`);
    execFileSync(edge, ['--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=2', `--window-size=${W},${H}`, `--screenshot=${png}`, pathToFileURL(join(out, n + '.svg')).href], { stdio: 'ignore' });
    console.log('rendered', png);
  }
}
