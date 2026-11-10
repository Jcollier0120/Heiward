// Scores the look-alike probe's result against the set's truth (docs/LOOKALIKE-BAR.md).
// usage: node tools/lookalike-score.ts <truth.json> <result.json> [--split tune|held-out|all] [--pairs]
//   prints, per setting the probe ran: different-shot pairs marked duplicates (the review's sets, then the engine's
//   groups), and edits found per kind. --pairs also lists the different-shot pairs the review put in one set.
import { readFileSync } from 'node:fs';

type Truth = { file: string; series: string; shot: number; split: string; kind: string };
const args = process.argv.slice(2);
const truth: Truth[] = JSON.parse(readFileSync(args[0], 'utf8'));
const result = JSON.parse(readFileSync(args[1], 'utf8'));
const splitArg = args.includes('--split') ? args[args.indexOf('--split') + 1] : 'all';
const listPairs = args.includes('--pairs');

const byFile = new Map(truth.map((t) => [t.file, t]));
const shotOf = (t: Truth) => `${t.series}-${t.shot}`;
const originalOf = new Map(truth.filter((t) => t.kind === 'original').map((t) => [shotOf(t), t.file]));
const inSplit = (t: Truth) => splitArg === 'all' || t.split === splitArg;
const kinds = [...new Set(truth.filter((t) => t.kind !== 'original').map((t) => t.kind))];
const edits = truth.filter((t) => t.kind !== 'original' && inSplit(t));

type Marked = { sameScene: number; other: number; originals: number; preChecked: number; list: string[] };
function score(groups: { files: string[]; keep?: string; suggested?: Set<string> }[]) {
  const marked: Marked = { sameScene: 0, other: 0, originals: 0, preChecked: 0, list: [] };
  const together = new Set<string>();
  for (const g of groups) {
    const files = g.files.filter((f) => byFile.has(f));
    for (let i = 0; i < files.length; i++)
      for (let j = i + 1; j < files.length; j++) {
        const a = byFile.get(files[i])!, b = byFile.get(files[j])!;
        together.add(files[i] + '|' + files[j]);
        together.add(files[j] + '|' + files[i]);
        if (shotOf(a) === shotOf(b) || !inSplit(a) || !inSplit(b)) continue;
        if (a.series === b.series) marked.sameScene++; else marked.other++;
        if (a.kind === 'original' && b.kind === 'original') marked.originals++;
        if (listPairs) marked.list.push(`${a.series}-${a.shot} ${a.kind} ~ ${b.series}-${b.shot} ${b.kind} (${files[i]} ${files[j]})`);
      }
    // Pre-checked for the Recycle Bin while another shot is the one kept: the worst case.
    if (g.keep && g.suggested)
      for (const f of g.suggested) {
        const k = byFile.get(g.keep), t = byFile.get(f);
        if (k && t && shotOf(k) !== shotOf(t) && inSplit(k) && inSplit(t)) marked.preChecked++;
      }
  }
  const found: Record<string, [number, number]> = {};
  for (const e of edits) {
    const o = originalOf.get(shotOf(e))!;
    const f = (found[e.kind] ??= [0, 0]);
    f[1]++;
    if (together.has(e.file + '|' + o)) f[0]++;
  }
  return { marked, found };
}

const pct = (a: number, b: number) => (b ? `${a}/${b} (${((100 * a) / b).toFixed(0)}%)` : '-');
console.log(`device ${result.device} (${result.deviceUsed}), split ${splitArg}, ${result.files.length} files, scan ${(result.scanMs / 1000).toFixed(0)} s`);
for (const run of result.runs) {
  const review = score(run.sets.map((s: any) => ({
    files: s.items.map((i: any) => i.file), keep: s.keep,
    suggested: new Set(s.items.filter((i: any) => i.suggested).map((i: any) => i.file)),
  })));
  const engine = score(run.engineGroups.map((g: string[]) => ({ files: g })));
  console.log(`\n## gray ${run.gray}% / AI ${run.ai}%: compare ${run.compareMs} ms, review ${(run.reportMs / 1000).toFixed(1)} s`);
  for (const [name, s] of [['review', review], ['engine', engine]] as const) {
    const m = s.marked;
    const all = Object.values(s.found).reduce((a, f) => [a[0] + f[0], a[1] + f[1]], [0, 0]);
    console.log(`${name}: different shots marked ${m.sameScene + m.other} (same scene ${m.sameScene}, other ${m.other}; originals both ${m.originals}${name === 'review' ? `; pre-checked ${m.preChecked}` : ''}), edits found ${pct(all[0], all[1])}`);
  }
  console.log('| kind | review | engine |\n|---|---|---|');
  for (const k of kinds) console.log(`| ${k} | ${pct(...(review.found[k] ?? [0, 0]))} | ${pct(...(engine.found[k] ?? [0, 0]))} |`);
  if (listPairs) for (const p of review.marked.list) console.log('  ' + p);
}
