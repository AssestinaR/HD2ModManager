# Guided armor refresh observation

V2 update: run `scripts/Start-ArmorRefreshPreflight.cmd` first. See
`notes/refresh-instance-preflight.md` for the read-only native allowlist and
risks. The full guide now requires successful World and Unit preflight, then
uses instance observations plus metadata in its eight phases. The
metadata-only limits below describe v1/default metadata mode, not v2 units mode.

Start the newly deployed game and enter the ship. Double-click
`scripts/Start-ArmorRefreshGuide.cmd`. Choose two distinct armors A/B and follow
the eight phases. Keep the mapping protocol and deployment unchanged throughout.
There is no automatic game launch, equipment change, refresh, death or respawn.

The guide acquires a single-writer lock, targets the current game PID, sends a
fresh session/request ID, waits for Lua acknowledgment, then counts down five
seconds before the action cue. Each phase captures for 20 seconds including
the countdown. Switch back to the terminal after the second beep and record
whether the requested action was actually performed. Preview can be marked
unavailable. Ctrl+C interrupts the guide; Lua always stops the phase by its
deadline even if the terminal cannot send a stop request.

Results go under `HD2RuntimeArmorOverride/evidence/refresh/<timestamp-session>/`.
Provide the whole directory, including `session.json`, JSONL captures and
status snapshots. Partial captures are copied on exit. Raw game-side files
remain under the existing ArmorRegistry directory. This is separate from the
production armor-registry protocol and never compiles or edits it.

**V4 update:** the current guide defaults to 10-second phases and a
3-second countdown (approximately 7 seconds for the action). It runs
`worlds` then `focus` preflight and uses interleaved candidate-window
sampling for the action phases. The older timing and broad-scan description
above documents V1, not the current defaults. See
`refresh-focus-preflight-20260926.md` for measured coverage and limitations.

## Observation boundary

The first probe compares bounded Lua surface snapshots across equip phases.
It records object identity tokens (not addresses), function source metadata,
and only allowlisted boolean/numeric fields such as armor_id, item_id, slot
and dirty. It does not record arbitrary scalar strings, invoke discovered
functions, access metatables/getters, walk native memory or hook game functions.
This describes metadata mode. V3 additionally supports staged read-only
World/Unit enumeration; see `refresh-instance-preflight.md` for its gates,
coverage limitations, and observed results. No call stack tracing is claimed.

It polls the request file twice per second when idle. During capture, scans
advance by at most 64 entries with a soft 0.5 ms CPU target per update; snapshots
are limited to 64 tables / 6000 entries, 512 entries per table and depth 3.
Logs cap at 4 MiB / 12000 records per phase. Synchronous file IO is not covered
by a strict frame-time guarantee. Capture duration is at most 45 seconds.
Truncation and non-atomic enumeration are explicit; an absent path is never
reported as a proven deletion. No visible candidates is a useful negative
result, not proof that the native equipment controller does not exist.

Normal game update and production registry errors are independent of probe
errors. The probe is embedded in both manager-managed and standalone runtime
builds, but sampling is only activated by a matching fresh request.

## V9 targeted transition mode (not yet live-validated)

`Watch-ArmorRefresh.ps1 -NativeOwnerTransitions` adds a short three-phase
sequence: idle in the armory, equip B, equip A. It first requires repeated
successful avatar association observations. It does not ask for an extra
armory-close trial and does not automatically equip or refresh anything.
The avatar resource and largest-World selection remain candidates, not
authoritative local-player detection.

Unlike one-shot owner_native, owner_trace reacquires live Unit handles and
reads the reviewed child list repeatedly, at most once per 250ms and 160
times per phase. It records failures and complete list heartbeats without
repeated machine-code blobs. The standalone Python summarizer compares
only complete observations within the same phase and breaks continuity on
failed reads; list change is not proof of destruction or refresh entry.
See `refresh-progress-20260926-v9.md` for the deployment blocker and next steps.
