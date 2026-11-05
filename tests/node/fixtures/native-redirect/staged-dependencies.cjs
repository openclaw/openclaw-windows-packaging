const Module = require("node:module");
const { join } = require("node:path");

const where = (path) =>
  path.startsWith(process.env.FIXTURE_STAGED_ROOT) ? "staged" : "app";
const errorCode = (action) => {
  try {
    action();
    return "no error";
  } catch (error) {
    return error.code;
  }
};
const filename = module.filename;
const paths = module.paths;

module.exports = {
  location: where(__filename),
  marked: require("marked").copy,
  width: require("get-east-asian-width").copy,
  relative: require("./relative.cjs").copy,
  missingStagedFile: require("./unstaged.cjs").copy,
  internal: require("#internal").copy,
  self: require("@earendil-works/pi-tui/relative").copy,
  nested: require("nested-pkg").copy,
  native: require("cjs-pkg").copy,
  resolve: where(require.resolve("marked")),
  direct: where(Module._resolveFilename("marked", module, false)),
  directRelative: where(Module._resolveFilename("./relative.cjs", module, false)),
  explicitPaths: require(Module._resolveFilename("marked", module, false, {
    paths: [join(process.env.FIXTURE_APP_ROOT, "custom")],
  })).copy,
  directMissing: errorCode(() => Module._resolveFilename("staged-only-pkg", module, false)),
  directBlocked: errorCode(() => Module._resolveFilename("exported-pkg/private.cjs", module, false)),
  invalidPaths: errorCode(() => Module._resolveFilename("marked", module, false, { paths: 1 })),
  parentUnchanged: module.filename === filename && module.paths === paths,
  missing: errorCode(() => require("staged-only-pkg")),
  blocked: errorCode(() => require("exported-pkg/private.cjs")),
  broken: errorCode(() => require("broken-pkg")),
};
