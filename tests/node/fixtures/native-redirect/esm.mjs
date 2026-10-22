// Reports where ESM resolution lands for the fixture packages: the copy a
// loaded module exports, or the root that a resolved URL is under. Runs as a
// process entry point or as a worker and reports to whichever started it.
import { createRequire } from "node:module";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { parentPort } from "node:worker_threads";

const where = (path) => {
  const lower = String(path).toLowerCase();
  if (lower.startsWith(process.env.FIXTURE_STAGED_ROOT.toLowerCase())) return "staged";
  if (lower.startsWith(process.env.FIXTURE_APP_ROOT.toLowerCase())) return "app";
  return path;
};
const applicationUrl = (...parts) =>
  pathToFileURL(join(import.meta.dirname, "node_modules", ...parts)).href;
const require = createRequire(import.meta.url);

const checks = {
  importBare: async () => (await import("esm-pkg")).copy,
  importAbsolute: async () => (await import(applicationUrl("esm-pkg", "index.js"))).copy,
  importCommonJs: async () => (await import("cjs-pkg")).default.copy,
  createRequire: () => require("cjs-pkg").copy,
  metaResolveBare: () => where(fileURLToPath(import.meta.resolve("esm-pkg"))),
  metaResolveAbsolute: () =>
    where(fileURLToPath(import.meta.resolve(applicationUrl("esm-pkg", "index.js")))),
  unstaged: async () => (await import("unstaged-pkg")).default.copy,
  missingStagedFile: async () => (await import(applicationUrl("esm-pkg", "extra.js"))).copy,
};

const report = {};
for (const [name, check] of Object.entries(checks)) {
  try {
    report[name] = await check();
  } catch (error) {
    report[name] = `error: ${error.code ?? error.message}`;
  }
}
report.execArgv = process.execArgv;
report.nodeOptions = process.env.NODE_OPTIONS ?? null;

if (parentPort) {
  parentPort.postMessage(report);
} else {
  process.stdout.write(JSON.stringify(report));
}
