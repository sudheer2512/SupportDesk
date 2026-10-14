// `ng build <project>` with a safety net: @angular/build 21.2 sometimes leaves its esbuild service process running
// after "Application bundle generation complete" (seen with the desk app on Windows), so the CLI never exits and
// `npm run build` stalls. Once the output location has been printed, give the CLI a few seconds to exit by itself,
// then stop it. Errors still fail the script.
// Usage: node scripts/ng-build.mjs <project> [extra ng build args…]
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';

const [project, ...extra] = process.argv.slice(2);
if (!project) {
  console.error('usage: node scripts/ng-build.mjs <project> [ng build args…]');
  process.exit(2);
}

const ng = createRequire(import.meta.url).resolve('@angular/cli/bin/ng.js');
const child = spawn(process.execPath, [ng, 'build', project, ...extra], {
  stdio: ['inherit', 'pipe', 'pipe'],
  env: { ...process.env, NG_CLI_ANALYTICS: 'false', FORCE_COLOR: process.env.FORCE_COLOR ?? '1' },
});

let failed = false;
let finished = false;
let grace = null;
const watch = (data, sink) => {
  sink.write(data);
  const text = data.toString();
  if (/✘ \[ERROR\]|\bERROR\b|bundle generation failed/i.test(text)) failed = true;
  if (!finished && /Output location:/.test(text)) {
    finished = true;
    grace = setTimeout(() => {
      console.log(`[ng-build] ${project}: output written; stopping the lingering Angular CLI process.`);
      child.kill();
    }, 8000);
  }
};
child.stdout.on('data', (d) => watch(d, process.stdout));
child.stderr.on('data', (d) => watch(d, process.stderr));
child.on('exit', (code) => {
  if (grace) clearTimeout(grace);
  const ok = finished && !failed && (code === 0 || code === null);
  if (!ok) console.error(`[ng-build] ${project}: build failed (exit ${code}).`);
  process.exit(ok ? 0 : code || 1);
});
