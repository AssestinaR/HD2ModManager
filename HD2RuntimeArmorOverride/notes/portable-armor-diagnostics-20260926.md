# Portable armor diagnostic baseline

## Implemented

Native CLI interactive command `diagnostics armor wizard` / `recover`,
distributed with manager builds through `Armor-Diagnostics.cmd`.
Independent Debug companion ZIP generator:
`tools/PackageArmorDiagnostics.ps1` (packages in CLI bin/Debug only).
The companion is placed under any configured manager installation and
uses that installation's settings, private .NET runtime if present, and
existing Lua deployment. No Python/PowerShell 7/SDK dependencies.

Scope deliberately limited to:
1. Preserve original effective protocol AND all installed patches.
2. User chooses a target only for identification; other original mappings
   remain intact to avoid removing unknown reproduction dependencies.
3. Fresh-game PID/start-time/revision/count validation, 30-second idle,
   then three manually confirmed armory open/close operations.
4. Optional fresh launch with all mappings disabled; no repacking,
   template expansion, missing-resource creation or native refresh.
5. Byte-exact recovery and explicit allowlist export.

All recovery data is written before protocol mutation. File hash guards,
the runtime `.manager.lock`, library/deployment leases, and stopped-process
checks apply. Recovery refuses an externally edited protocol. Pending
state is detected by both the next wizard launch and the updated Manager
startup (which exits before normal startup work). Older managers receiving
the companion lack this startup protection; the README requires keeping
them closed until recovery.

The companion does not install or ship the third-party Lua loader.
Same-process runtime acknowledgement verifies the needed runtime is
working, not an exact third-party loader version; loaderVersionVerified=false.

## Evidence/privacy

Per-stage metadata sampler runs every two seconds, capped at 1800 samples;
only status files/process metadata, no process-memory access or active
probe request. Writes are flushed while the prompt awaits user input.
Event collection reads at most 512 Application records, with a 20-second
helper timeout, strips raw XML, names/paths/SIDs/account data and arbitrary
messages. Reports indicate unavailable/capped queries.

Protocol summary retains numeric Item IDs, Unit IDs and structural fields,
aliases model keys, excludes comments and names. Catalog metadata is
explicitly not claimed to be a live native snapshot. Inventory reuses Core
deployed-Unit validation and hashes base TOCs; GPU content is not fully
hashed. No whole-pack or full-loader logs are exported.

ZIP explicit allowlist excludes original protocol and recovery journal.
Users are warned not to share the full local diagnostics directory.

## Tests

- Seven new isolated tests: exact restore, durable interrupted-write
  recovery, process refusal, external-edit refusal, backup/target integrity,
  lock contention, privacy/ZIP allowlist, disabled-input rejection
  (some cases share a test).
- Targeted diagnostic + armor-registry suite: 15/15 passed.
- Full CLI suite: 120 passed / 5 deployment tests failed while user's game
  PID 35756 was deliberately left running. A focused rerun confirmed the
  `GameProcessRunning` guard failure; do not claim full-suite success.
- Debug Manager build succeeded; existing unassigned-field warnings.
- Existing test restore warning NU1903 for SQLitePCLRaw.lib.e_sqlite3 2.1.6
  remains unresolved by this task.
- Windows event-helper smoke test returned complete/empty results.
- Companion executable version smoke test passed; ZIP contains dependencies,
  no loose Lua source/loader/user configuration/backups/PDBs.
- Manual end-to-end user flow and actual reproducible game crash remain to
  be tested. No automatic game start/termination or deployment occurred.
- User's existing revision-48 experimental protocol remained untouched.
