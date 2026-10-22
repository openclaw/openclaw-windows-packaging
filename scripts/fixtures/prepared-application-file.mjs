import fs from "node:fs/promises";
import path from "node:path";

export async function resolvePreparedApplicationFile(applicationDirectory, relativeFile) {
  const root = await fs.realpath(applicationDirectory);
  const file = await fs.realpath(path.resolve(root, relativeFile));
  const prefix = root.endsWith(path.sep) ? root : `${root}${path.sep}`;
  if (!file.startsWith(prefix)) {
    throw new Error(`Prepared application file escapes the selected directory: ${relativeFile}`);
  }
  if (!(await fs.stat(file)).isFile()) {
    throw new Error(`Prepared application path is not a regular file: ${relativeFile}`);
  }
  return file;
}
