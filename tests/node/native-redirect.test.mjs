// Black-box tests for the packaged native dependency redirect,
// src/OpenClaw.Launcher/node/native-redirect.mjs. Each test starts Node.js with
// the real preload, the way the launcher does, against temporary application
// and staged roots, and reads back where resolution landed.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  cpSync,
  mkdirSync,
  mkdtempSync,
  realpathSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, before, test } from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

const packagedScripts = fileURLToPath(new URL("../../src/OpenClaw.Launcher/node/", import.meta.url));
const probes = fileURLToPath(new URL("./fixtures/native-redirect/", import.meta.url));

// Inherited values of these would change how the probes resolve or report.
const ambient =
  /^(NODE_OPTIONS|NODE_PATH|NODE_NO_WARNINGS|NODE_TEST_CONTEXT|OPENCLAW_NATIVE_APP_ROOT|OPENCLAW_NATIVE_STAGED_ROOT)$/i;
const baseEnvironment = Object.fromEntries(
  Object.entries(process.env).filter(([name]) => !ambient.test(name)),
);

let root;
let appRoot;
let stagedRoot;
let preloadUrl;

function writePackage(base, name, copy, { type = "commonjs", files = ["index.js"] } = {}) {
  const directory = join(base, "node_modules", name);
  mkdirSync(directory, { recursive: true });
  writeFileSync(
    join(directory, "package.json"),
    JSON.stringify(type === "module" ? { name, type, exports: "./index.js" } : { name, main: "index.js" }),
  );
  for (const file of files) {
    writeFileSync(
      join(directory, file),
      type === "module"
        ? `export const copy = ${JSON.stringify(copy)};\n`
        : `module.exports = { copy: ${JSON.stringify(copy)} };\n`,
    );
  }
}

before(() => {
  // Node.js resolves modules through real paths, so a short (8.3) temporary
  // path would never match the roots. Use the canonical form everywhere.
  root = realpathSync.native(mkdtempSync(join(tmpdir(), "openclaw-native-redirect-")));

  // Like the package, the preload sits under a path with a space, so the
  // NODE_OPTIONS entry that carries it to child processes is percent-encoded.
  const packageRoot = join(root, "Program Files", "OpenClaw");
  cpSync(packagedScripts, join(packageRoot, "node"), {
    recursive: true,
    // The launcher project packages only node\**\*.mjs.
    filter: (source) => statSync(source).isDirectory() || source.endsWith(".mjs"),
  });
  preloadUrl = pathToFileURL(join(packageRoot, "node", "native-redirect.mjs")).href;

  appRoot = join(packageRoot, "app");
  stagedRoot = join(root, "agent", "native");
  cpSync(probes, appRoot, { recursive: true });
  writePackage(appRoot, "cjs-pkg", "app", { files: ["index.js", "extra.js"] });
  writePackage(stagedRoot, "cjs-pkg", "staged");
  writePackage(appRoot, "esm-pkg", "app", { type: "module", files: ["index.js", "extra.js"] });
  writePackage(stagedRoot, "esm-pkg", "staged", { type: "module" });
  writePackage(appRoot, "unstaged-pkg", "app");
});

after(() => {
  if (root) {
    rmSync(root, { recursive: true, force: true });
  }
});

function runNode(args, environment = {}) {
  const result = spawnSync(process.execPath, args, {
    cwd: root,
    encoding: "utf8",
    env: {
      ...baseEnvironment,
      FIXTURE_APP_ROOT: appRoot,
      FIXTURE_STAGED_ROOT: stagedRoot,
      OPENCLAW_NATIVE_APP_ROOT: appRoot,
      OPENCLAW_NATIVE_STAGED_ROOT: stagedRoot,
      ...environment,
    },
    // A hang guard only; every probe finishes in well under a second.
    timeout: 120_000,
  });
  assert.equal(result.error, undefined, `Node.js did not complete: ${result.error}`);
  return result;
}

function probe(entry, { args = [], environment } = {}) {
  const result = runNode(["--import", preloadUrl, join(appRoot, entry), ...args], environment);
  // The preload runs in every OpenClaw Node.js process, so anything it writes
  // to stderr, including a Node.js warning, would reach every command.
  assert.equal(result.stderr, "");
  assert.equal(result.status, 0);
  return JSON.parse(result.stdout);
}

function assertReport(report, expected) {
  const actual = Object.fromEntries(Object.keys(expected).map((name) => [name, report[name]]));
  assert.deepEqual(actual, expected);
}

test("CommonJS require and require.resolve reach the staged copy", () => {
  assertReport(probe("commonjs.cjs"), {
    requireBare: "staged",
    requireAbsolute: "staged",
    resolveBare: "staged",
    resolveAbsolute: "staged",
  });
});

test("ESM import and import.meta.resolve reach the staged copy", () => {
  assertReport(probe("esm.mjs"), {
    importBare: "staged",
    importAbsolute: "staged",
    importCommonJs: "staged",
    createRequire: "staged",
    metaResolveBare: "staged",
    metaResolveAbsolute: "staged",
  });
});

test("unstaged packages and files missing from the staged copy stay on the application", () => {
  const expected = { unstaged: "app", missingStagedFile: "app" };
  assertReport(probe("commonjs.cjs"), expected);
  assertReport(probe("esm.mjs"), expected);
});

test("direct Module._resolveFilename callers reach the staged copy", () => {
  assertReport(probe("commonjs.cjs"), { resolveFilename: "staged" });
});

test("the application root matches regardless of case", () => {
  const swapped = [...appRoot]
    .map((character) =>
      character === character.toLowerCase() ? character.toUpperCase() : character.toLowerCase())
    .join("");
  assert.notEqual(swapped, appRoot);
  const environment = { OPENCLAW_NATIVE_APP_ROOT: swapped };

  assertReport(probe("commonjs.cjs", { environment }), { requireBare: "staged", resolveBare: "staged" });
  assertReport(probe("esm.mjs", { environment }), { importBare: "staged", metaResolveBare: "staged" });
});

test("child processes inherit the redirect through NODE_OPTIONS", () => {
  const preloadOption = `--import ${preloadUrl}`;
  const { nodeOptions, launched } = probe("launch.mjs", { args: ["child"] });

  assert.equal(nodeOptions, preloadOption);
  // The child starts without --import, so the redirect reached it only through
  // NODE_OPTIONS, and its own preload did not add a second entry.
  assertReport(launched, {
    execArgv: [],
    nodeOptions: preloadOption,
    importBare: "staged",
    createRequire: "staged",
    metaResolveBare: "staged",
  });
});

test("worker threads inherit the redirect", () => {
  const { launched } = probe("launch.mjs", { args: ["worker"] });

  assertReport(launched, {
    importBare: "staged",
    createRequire: "staged",
    metaResolveBare: "staged",
  });
});

test("without both native roots the preload changes nothing", () => {
  for (const environment of [
    { OPENCLAW_NATIVE_APP_ROOT: undefined, OPENCLAW_NATIVE_STAGED_ROOT: undefined },
    { OPENCLAW_NATIVE_STAGED_ROOT: undefined },
    { OPENCLAW_NATIVE_APP_ROOT: undefined },
  ]) {
    assertReport(probe("esm.mjs", { environment }), {
      importBare: "app",
      createRequire: "app",
      metaResolveBare: "app",
      nodeOptions: null,
    });
  }
});

test("the preload adds itself to NODE_OPTIONS exactly once", () => {
  const preloadOption = `--import ${preloadUrl}`;
  const other = "--max-old-space-size=4096";

  for (const [inherited, expected] of [
    [undefined, preloadOption],
    [other, `${other} ${preloadOption}`],
    [preloadOption, preloadOption],
    [`${other} ${preloadOption}`, `${other} ${preloadOption}`],
  ]) {
    const { nodeOptions } = probe("esm.mjs", { environment: { NODE_OPTIONS: inherited } });
    assert.equal(nodeOptions, expected);
  }
});

test("the preload stops startup when module.registerHooks is unavailable", () => {
  // Stands in for a Node.js without the API by removing it before the preload runs.
  const withoutRegisterHooks =
    'data:text/javascript,import module from "node:module"; delete module.registerHooks;';
  const entry = join(appRoot, "esm.mjs");

  const failed = runNode(["--import", withoutRegisterHooks, "--import", preloadUrl, entry]);
  assert.equal(failed.status, 1);
  assert.equal(failed.stdout, "");
  assert.match(failed.stderr, /module\.registerHooks\(\)/);
  assert.ok(failed.stderr.includes(process.version), failed.stderr);

  // Without the roots there is no redirect to lose, so the process still runs.
  const unconfigured = runNode(["--import", withoutRegisterHooks, "--import", preloadUrl, entry], {
    OPENCLAW_NATIVE_APP_ROOT: undefined,
    OPENCLAW_NATIVE_STAGED_ROOT: undefined,
  });
  assert.equal(unconfigured.stderr, "");
  assert.equal(unconfigured.status, 0);
});
