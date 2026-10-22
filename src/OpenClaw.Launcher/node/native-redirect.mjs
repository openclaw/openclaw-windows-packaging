// Redirects resolution of the application's native dependency packages to the
// agent-owned copies staged by `clawctl setup`.
//
// The agent identity may read package content but may not map it as an
// executable image, so a packaged `.node` fails to load with
// ERR_DLOPEN_FAILED / access denied. Setup mirrors each native-bearing package
// into the agent's own profile; this module makes resolution find those copies.
//
// Whole package directories are redirected, not individual binaries. Once a
// package resolves to its staged copy, the `__dirname` inside it is staged too,
// so the sibling DLLs and helper executables it derives from its own location
// are correct without any further interception.
//
// Redirection is gated on the staged file existing, so a package that was not
// staged keeps resolving to the immutable package exactly as before.
import Module from "node:module";
import { existsSync } from "node:fs";
import { fileURLToPath, pathToFileURL } from "node:url";
import { join, sep } from "node:path";

const appRoot = process.env.OPENCLAW_NATIVE_APP_ROOT;
const stagedRoot = process.env.OPENCLAW_NATIVE_STAGED_ROOT;

if (appRoot && stagedRoot) {
  // Every Node.js release that upstream supports provides these hooks. Stop
  // here rather than let a packaged native addon fail later, far from the cause.
  if (typeof Module.registerHooks !== "function") {
    throw new Error(
      "The OpenClaw native dependency redirect requires module.registerHooks(), " +
        `which Node.js ${process.version} at ${process.execPath} does not provide. ` +
        "Use the Node.js runtime that clawctl setup installs.",
    );
  }

  const preloadOption = `--import ${import.meta.url}`;
  const inheritedNodeOptions = process.env.NODE_OPTIONS;
  if (!inheritedNodeOptions?.includes(preloadOption)) {
    process.env.NODE_OPTIONS = inheritedNodeOptions?.trim()
      ? `${inheritedNodeOptions} ${preloadOption}`
      : preloadOption;
  }

  const from = join(appRoot, "node_modules") + sep;
  const fromLower = from.toLowerCase();
  const to = join(stagedRoot, "node_modules") + sep;

  // The one redirect rule. Every entry point below goes through it.
  const redirect = (resolved) => {
    if (typeof resolved !== "string") return null;
    // Windows paths are case-insensitive, and the two roots are produced by
    // different components, so the prefix is compared case-insensitively.
    if (resolved.length <= from.length) return null;
    if (resolved.slice(0, from.length).toLowerCase() !== fromLower) return null;
    const candidate = to + resolved.slice(from.length);
    return existsSync(candidate) ? candidate : null;
  };

  // These hooks run on the resolving thread for require, require.resolve,
  // import, and import.meta.resolve, the last being how sqlite-vec locates its
  // loadable extension. Off-thread module.register() hooks would add a
  // cross-thread round trip to every resolution in every process.
  Module.registerHooks({
    resolve(specifier, context, nextResolve) {
      const result = nextResolve(specifier, context);
      if (typeof result.url !== "string" || !result.url.startsWith("file:")) {
        return result;
      }
      let path;
      try {
        path = fileURLToPath(result.url);
      } catch {
        return result;
      }
      const candidate = redirect(path);
      return candidate === null
        ? result
        : { ...result, url: pathToFileURL(candidate).href, shortCircuit: true };
    },
  });

  // The hooks run around Module._resolveFilename rather than inside it, so code
  // that calls it directly bypasses them; patching it keeps those CommonJS
  // callers redirected. A hooked require passes through both, which is
  // harmless: a path the patch already redirected no longer matches the rule.
  const resolveFilename = Module._resolveFilename;
  Module._resolveFilename = function (request, parent, isMain, options) {
    const resolved = resolveFilename.call(this, request, parent, isMain, options);
    return redirect(resolved) ?? resolved;
  };
}
