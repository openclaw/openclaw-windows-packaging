// Reports where CommonJS resolution lands for the fixture packages: the copy a
// loaded module exports, or the root that a resolved path is under.
const Module = require("node:module");
const { join } = require("node:path");

const where = (path) => {
  const lower = String(path).toLowerCase();
  if (lower.startsWith(process.env.FIXTURE_STAGED_ROOT.toLowerCase())) return "staged";
  if (lower.startsWith(process.env.FIXTURE_APP_ROOT.toLowerCase())) return "app";
  return path;
};
const applicationFile = (...parts) => join(__dirname, "node_modules", ...parts);

const checks = {
  requireBare: () => require("cjs-pkg").copy,
  requireAbsolute: () => require(applicationFile("cjs-pkg", "index.js")).copy,
  resolveBare: () => where(require.resolve("cjs-pkg")),
  resolveAbsolute: () => where(require.resolve(applicationFile("cjs-pkg", "index.js"))),
  resolveFilename: () => where(Module._resolveFilename("cjs-pkg", module, false)),
  unstaged: () => require("unstaged-pkg").copy,
  missingStagedFile: () => require("cjs-pkg/extra.js").copy,
};

const report = {};
for (const [name, check] of Object.entries(checks)) {
  try {
    report[name] = check();
  } catch (error) {
    report[name] = `error: ${error.code ?? error.message}`;
  }
}
process.stdout.write(JSON.stringify(report));
