// Builds manual.json from manual.base.json and the chapter files in parts/ (one file per chapter, ordered by name).
// Each part file: { "chapters": [...], "troubleshooting": [...], "faq": [...], "glossary": [...] } (all but chapters optional).
//
//   node assemble.mjs                         -> manual.json (the whole manual)
//   node assemble.mjs --only 04,05 --out _check.json   -> just those chapters, for check-prose while writing
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const option = (name) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };
const only = option('--only')?.split(',').map((s) => s.trim()).filter(Boolean);
const out = option('--out') ?? 'manual.json';

const readJson = (file) => {
  try { return JSON.parse(readFileSync(file, 'utf8')); }
  catch (e) { throw new Error(`${file}: ${e.message}`); }
};

const manual = { ...readJson(join(here, 'manual.base.json')), chapters: [], troubleshooting: [], faq: [], glossary: [] };
const files = readdirSync(join(here, 'parts'))
  .filter((f) => f.endsWith('.json'))
  .filter((f) => !only || only.some((prefix) => f.startsWith(prefix)))
  .sort();

for (const file of files) {
  const part = readJson(join(here, 'parts', file));
  if (!Array.isArray(part.chapters) || part.chapters.length === 0) throw new Error(`parts/${file}: "chapters" must be a non-empty array.`);
  manual.chapters.push(...part.chapters);
  manual.troubleshooting.push(...(part.troubleshooting ?? []));
  manual.faq.push(...(part.faq ?? []));
  manual.glossary.push(...(part.glossary ?? []));
}

// One definition per term: the first chapter that defines it wins.
const seen = new Set();
manual.glossary = manual.glossary.filter((g) => {
  const key = g.term.toLowerCase();
  if (seen.has(key)) return false;
  seen.add(key);
  return true;
});
for (const key of ['troubleshooting', 'faq', 'glossary']) if (manual[key].length === 0) delete manual[key];

writeFileSync(join(here, out), `${JSON.stringify(manual, null, 2)}\n`);
console.log(`Wrote ${out}: ${files.length} part file(s), ${manual.chapters.length} chapter(s), ` +
  `${manual.chapters.reduce((n, c) => n + (c.sections?.length ?? 0), 0)} section(s).`);
