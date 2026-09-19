# Test implementation guide

Read the repository-root `AGENTS.md` before this file. This scope owns JIT xUnit tests and the NativeAOT CLI scenario driver.

## Test standard

- A test earns its place by the realistic defect it would catch. Prefer functional and integration tests that drive the real handler, protocol, pipeline, CLI route, or published binary.
- Assert observable behavior: return values, emitted protocol records, persisted fixture state, process arguments, exit codes, rendered output, and cleanup. Never read production source and assert on implementation text or line ordering.
- Regression tests fail on the original defect. Do not weaken an existing assertion, increase a timeout, add retries, skip a case, or update a baseline until source evidence shows the expectation changed.
- Tests never modify the real user profile, packaged LocalState, registry, certificate store, scheduled tasks, isolated sessions, installed packages, or cloud resources.
- Use `TestDirectory.Create()` and explicitly injected paths or collaborators. Clean fixture-owned state in `finally`; do not rely on ambient environment variables to redirect state.
- Keep tests deterministic: no sleeps, wall-clock timing assumptions, hardcoded ports, inter-test ordering, live network dependencies, or shared mutable machine state. Inject clocks and use synchronization primitives.
- Fake only the isolation boundary. Prefer real collaborators inside it and assert outcomes rather than mock call counts.
- Extend existing fixtures and scenario helpers before creating a parallel test harness. Keep x64 and ARM64 expectations synchronized where architecture is part of the contract.

## NativeAOT driver

- `OpenClaw.Launcher.AotSmoke` calls the same `Program.RunAsync` route as production; it must never call `Main`, which resolves diagnostics under the real user profile.
- Inject a temporary diagnostic path, in-memory writers, and delegates that cannot launch Node, modify package state, or start external work.
- Prove native alias/root-command behavior by executing the published driver as `clawctl.exe` and under the wrong executable name; a JIT test or successful publish is not equivalent.

## Proof

- Run targeted tests while iterating, then the smallest complete owning lane. Use the full Release suite when a shared fixture, protocol, command tree, or startup path changes.
- Run `scripts\Test-NativeAotCli.Tests.ps1` when the native driver or any entrypoint, startup, CLI, help, version, color, JSON, trimming, reflection, or interop behavior changes.
