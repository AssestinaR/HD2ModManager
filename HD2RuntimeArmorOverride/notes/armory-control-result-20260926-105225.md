# Control result: expanded-present startup failure, not a recorded armory close

Evidence:
`evidence/armory-control-20260926/run-20260926-105225`

## Findings

- Baseline PID 39452: three user-confirmed open/close successes.
- Low-present PID 20932: three user-confirmed open/close successes.
- Expanded-present PID 25020: runtime ready at 10:56:46 local (UTC+8),
  revision 48, one mapping, one clone of 1960 bytes. Custom body Units
  were verified present by deployment validation.
- User reports severe stutter and a crash before any armory interaction.
  There is no expanded-present `action_prompt` in actions.jsonl.
  This is a pre-armory failure, not a successful/failed armory-close trial.
- User restarted manually; subsequent runtime.status reports PID 35756,
  same revision, clone size and protocol hash. This confirms the new game
  applied the same protocol, NOT that it completed a controlled duration or
  any recorded armory cycles.
- The guide rejected PID 35756 because it was validating PID 25020.
  The generic validation warning was not a failed mapping indication.
- Neither absent-resource stage ran. These are untested, not successful.

## Windows event correlation

Original capture was capped at 256 mixed Application events. A follow-up
bounded query selected Application Error ID 1000 in 10:56:20–10:58:10 local.
All six game records name process `0x61bc` = 25020 and process creation
FILETIME `0x1dd4d629ea6b197`, exactly matching recorded start
`2026-09-26T02:56:25.4841239Z`.

First recorded error at 10:57:19.630:
`ntdll.dll`, code `c000000d`, offset `6f82f`.
Five further records at 10:57:27–10:57:58:
`ntdll.dll`, code `c0000026`, offset `dcbf`.
These are repeated reports from ONE process lifetime, not six independent
test failures. Event timestamps need not equal the initial failure instant.
The module/offset reports provide no call stack or root-cause attribution.
Referenced WER archives contained Report.wer only; no dump was found in
those specific archive directories. No machine-wide dump search was made.

## Interpretation and next scope

Expanded configuration is associated with a witnessed failure, but total
count, layers and custom Piece fields covary. Restart success does not
exonerate it, establish a failure rate, or prove a hardware cause.
Armory closure is not necessary for this observed failure; it is not yet
proven to share the users' armory-close root cause.

Before further closure/refresh tests, separate startup/idle stability from
preview teardown. Repeat clean low/expanded launches with explicit
ship-ready timestamps and fixed idle observation intervals. If expanded
also fails at idle, prioritize runtime consumer/initialization and native
object lifetime over an armory-only cleanup explanation. Do not inject
refresh calls, remove objects, or advance to absent-resource stages until
the current failure has been characterized.

Guide now preserves user comments at the ship-ready prompt and explicitly
explains PID changes instead of reporting a generic composition mismatch.
Current live PID 35756 was left untouched. Test protocol revision 48 remains
installed; original revision 45 can be restored using the existing launcher
after the user fully exits the game.
