// Re-captures every screenshot, then assembles, checks and builds the manual (see plan.md).
//
// Before running: start the "supportdesk-manual" and "supportdesk-mobile" launch configurations, and set
// SD_DESK_PASSWORD, SD_PORTAL_PASSWORD and SD_PLATFORM_PASSWORD to the demo passwords in docs/DEVELOPMENT.md.
//
//   node refresh.mjs                 capture all shot lists, then build
//   node refresh.mjs --no-capture    only assemble, check and build
//   node refresh.mjs 05 12           capture only shot lists whose names start with 05 or 12, then build
import { execFileSync } from 'node:child_process';
import { readdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const skill = process.env.USER_MANUAL_SKILL ?? join(homedir(), '.claude', 'skills', 'user-manual');
const args = process.argv.slice(2);
const capture = !args.includes('--no-capture');
const prefixes = args.filter((a) => !a.startsWith('--'));

const run = (script, ...scriptArgs) => execFileSync(process.execPath, [script, ...scriptArgs], { cwd: here, stdio: 'inherit' });

if (capture) {
  const missing = ['SD_DESK_PASSWORD', 'SD_PORTAL_PASSWORD', 'SD_PLATFORM_PASSWORD'].filter((v) => !process.env[v]);
  if (missing.length) {
    console.error(`Set ${missing.join(', ')} first (demo passwords in docs/DEVELOPMENT.md).`);
    process.exit(1);
  }
  const lists = readdirSync(join(here, 'shotlists'))
    .filter((f) => f.endsWith('.json'))
    .filter((f) => prefixes.length === 0 || prefixes.some((p) => f.startsWith(p)))
    .sort();
  for (const list of lists) {
    console.log(`\n=== ${list}`);
    run(join(skill, 'scripts', 'capture-web.mjs'), join('shotlists', list));
  }
}

run(join(here, 'assemble.mjs'));
run(join(skill, 'scripts', 'check-prose.mjs'), 'manual.json');
run(join(skill, 'scripts', 'build.mjs'), 'manual.json');
