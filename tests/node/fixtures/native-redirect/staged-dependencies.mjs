import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import { copy as marked } from "marked";
import { copy as width } from "get-east-asian-width";
import relative from "./relative.cjs";
import unstaged from "./unstaged.cjs";
import internal from "#internal";
import self from "@earendil-works/pi-tui/relative";
import nested from "nested-pkg";
import native from "cjs-pkg";

const require = createRequire(import.meta.url);
const where = (url) =>
  fileURLToPath(url).startsWith(process.env.FIXTURE_STAGED_ROOT) ? "staged" : "app";
const errorCode = async (specifier) => {
  try {
    await import(specifier);
    return "no error";
  } catch (error) {
    return error.code;
  }
};

export default {
  location: where(import.meta.url),
  marked,
  width,
  relative: relative.copy,
  missingStagedFile: unstaged.copy,
  internal: internal.copy,
  self: self.copy,
  nested: nested.copy,
  native: native.copy,
  resolve: where(import.meta.resolve("marked")),
  createRequire: require("marked").copy,
  missing: await errorCode("staged-only-pkg"),
  blocked: await errorCode("exported-pkg/private.cjs"),
  broken: await errorCode("broken-pkg"),
};
