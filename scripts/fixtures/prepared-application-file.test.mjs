import assert from "node:assert/strict";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { resolvePreparedApplicationFile } from "./prepared-application-file.mjs";

test("prepared application reads stay inside the physical selected directory", async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "openclaw-app-paths-"));
  const application = path.join(root, "app");
  const sibling = path.join(root, "app-sibling");
  try {
    await fs.mkdir(path.join(application, "dist"), { recursive: true });
    await fs.mkdir(sibling);
    const inside = path.join(application, "dist", "fixture.txt");
    const outside = path.join(sibling, "private.txt");
    await fs.writeFile(inside, "fixture application file");
    await fs.writeFile(outside, "fixture outside file");

    const resolved = await resolvePreparedApplicationFile(application, path.join("dist", "fixture.txt"));
    assert.equal(await fs.readFile(resolved, "utf8"), "fixture application file");
    await assert.rejects(
      resolvePreparedApplicationFile(application, path.join("..", "app-sibling", "private.txt")),
      /escapes the selected directory/,
    );
    await assert.rejects(resolvePreparedApplicationFile(application, outside), /escapes the selected directory/);
    await assert.rejects(resolvePreparedApplicationFile(application, "dist"), /not a regular file/);

    await fs.symlink(sibling, path.join(application, "linked-outside"),
      process.platform === "win32" ? "junction" : "dir");
    await assert.rejects(
      resolvePreparedApplicationFile(application, path.join("linked-outside", "private.txt")),
      /escapes the selected directory/,
    );
    assert.equal(await fs.readFile(outside, "utf8"), "fixture outside file");
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});
