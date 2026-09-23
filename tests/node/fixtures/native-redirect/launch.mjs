// Starts esm.mjs as a child process or a worker thread, the two ways OpenClaw
// starts more Node.js work, and reports what it resolved along with this
// process's NODE_OPTIONS.
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { Worker } from "node:worker_threads";

const entry = new URL("./esm.mjs", import.meta.url);

let launched;
switch (process.argv[2]) {
  case "child": {
    // The child inherits this environment, including the NODE_OPTIONS the
    // preload extended, but not this process's --import argument.
    const child = spawnSync(process.execPath, [fileURLToPath(entry)], { encoding: "utf8" });
    launched = child.status === 0 && child.stderr === ""
      ? JSON.parse(child.stdout)
      : { status: child.status, stderr: child.stderr };
    break;
  }
  case "worker":
    launched = await new Promise((resolve, reject) => {
      const worker = new Worker(entry);
      worker.once("message", resolve);
      worker.once("error", reject);
      worker.once("exit", (code) => reject(new Error(`The worker exited with ${code} before reporting.`)));
    });
    break;
  default:
    throw new Error(`Unknown launch kind: ${process.argv[2]}`);
}

process.stdout.write(JSON.stringify({ nodeOptions: process.env.NODE_OPTIONS ?? null, launched }));
