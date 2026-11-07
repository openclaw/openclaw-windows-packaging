# Windows image and Crabbox time scales

Separate **one-time image preparation**, **each new lease**, and **checks on a
reused lease**. A measured native acquisition took **7 minutes 42 seconds**;
two warm transport checks took **57 and 81 seconds** end to end. A successful
nested image/security qualification took **38 minutes 47 seconds**, but ran
extra copies and failure checks: it is **not** a single-conversion or normal
template-preparation estimate. Do not treat the multi-hour development/debugging
history as execution time.

## Retained measurements

These are individual observations from 2026-11-04, not promises or portable
performance ranges:

| Phase | Frequency | Retained measurement and boundary |
| --- | --- | --- |
| ISO download | Only when suitable media is absent | **Not measured**: an existing 6.41 GB ISO was reused. Sign-in, download bandwidth and media availability vary. |
| Offline ISO to VHDX: mount, selection/application, boot files, verification and detach | Once per new image | **No separate elapsed time retained.** Included in the combined qualification below; no per-command estimate is claimed. |
| Template copy, source hashes and protected answer-file creation | Once per template copy | **No separate elapsed time retained.** Copy/hash work reads the expanded VHDX file, not just the compressed ISO; 80 GiB is virtual capacity, not its measured physical size. |
| First boot/OOBE, intended administrator readiness and shutdown | Once per primed template | **No separate elapsed time retained.** Successful authenticated readiness was observed; the default 25-minute readiness budget is not its measured duration or a total command limit. |
| Isolated qualification: ISO transfer plus conversion, three template calls and real permission checks | Validation sample, not the ordinary user path | **28m 01s**, from completed actor preflight to collected successful guest receipt. Includes injection-only, successful priming and an intentional unbootable/failure-output case. |
| Complete successful isolated qualification | Validation sample | **38m 47s** controller-observed wall time: 7m 15s environment/runtime preparation, the 28m 01s above, then 3m 31s teardown/receipt observation including an immutable-parent hash check. Failed attempts, retries and harness debugging are excluded. |
| New native Crabbox lease/bootstrap | Every new lease, from a prepared template | **7m 41.513s**, the CLI's successful `warmup complete total`. Missing provider baseline tools can require verified downloads; this does not install repository PowerShell/.NET/Visual Studio prerequisites. |
| Warm `transport-smoke` on an explicit reused lease | Each check | CLI `end_to_end`: **56.735s / 1m 20.937s** for roughly 3.6 MiB of source. Sync: **28.643s / 31.220s**; remote command: **5.480s / 5.598s**. Excludes separate acquisition and final explicit stop. |
| Repository restore/build, other jobs and separate lease release | Workload-dependent | **Not separately benchmarked here.** NuGet/tool downloads, workload size and cleanup state matter; do not substitute Node smoke timing. |

The qualification used Windows x64, an 80 GiB virtual output, and nested
Hyper-V: a 4-vCPU/12-GiB outer machine and a 2-vCPU/4-GiB priming VM. The native
lease sample used 4 vCPUs/8 GiB. Nested virtualization, media/runtime transfer
and qualification overhead are not normal native-host conversion costs.
Storage throughput, antivirus scanning (left enabled), actual disk-file growth,
network and installed prerequisites all affect elapsed time.

The coarse qualification intervals come from retained controller events and
receipt timestamps on one controller clock; finer apply/copy/OOBE timestamps
were not retained. The final boundary is observation of completed execution,
not a per-helper stopwatch. CLI subphase timers can overlap: use end-to-end
time, not their sum. None of these samples measures guest managed quality/tests.

## Quiet does not mean stuck

DISM application is intentionally quiet; copies and hashes may print nothing.
Windows first boot can wait before credentials work. Read-only file
growth/activity or the exact owned VM's state can explain activity, but
**neither VM Running nor Heartbeat proves administrator readiness**.
The result object, successful authenticated probe and final detached/cleanup
state remain the completion evidence.

The ISO converter has no overall elapsed-time limit. Template readiness checks
have a default 25-minute budget **after VM start**, checked between bounded
probes; copying, hashing, VM start and the separate shutdown-state check add
time. Crabbox v0.81.0 has its own provider acquisition budgets. A timeout is a
safety bound, not an estimate.

Investigate an explicit nonzero exit, expired readiness budget, missing required
tool, stalled/stopped owner or failed verification using that phase's diagnostic
and exact resource identity. Do not reboot, overwrite partial disks, acquire
duplicate VMs, or extend timeouts merely because a long phase is quiet.
Keep credentials out of timing logs and transcripts.

Return to the [local Windows workflow](crabbox-local-windows.md).
