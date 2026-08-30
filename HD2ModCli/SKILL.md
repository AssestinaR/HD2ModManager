# HD2Mod CLI Agent Guide

Protocol version: 1.0

`hd2mod` is the headless command-line companion for HD2 Mod Manager. This release is deliberately **read-only**: it can inspect the manager state and export a structured context file, but it cannot import, delete, deploy, mutate profiles, or write Patch bytes.

When it is shipped beside `HD2ModManager.exe`, pass `--manager-root .` from that directory to use the same `settings.json` as the desktop application.

## Required safety workflow

1. Discover the actual surface first:

   ```powershell
   hd2mod capabilities --json
   ```

2. Export a fresh local context before advising about a user's library:

   ```powershell
   hd2mod context export --manager-root "<manager directory>" --output "context.json"
   ```

   If the manager directory is unknown, pass the Mod library directly:

   ```powershell
   hd2mod context export --library "<mod library directory>" --output "context.json"
   ```

3. Read `context.json` before proposing a plan. Treat paths, IDs, Option relationships, activation states, and effective deployment order as authoritative for that moment only.

4. Do not fabricate write commands. The current capabilities report is the authority. Write-capable workflow will be introduced later as explicit `plan`, `validate`, and user-confirmed `apply` commands.

## Read-only commands

```text
hd2mod version
hd2mod capabilities --json
hd2mod status --manager-root <manager-dir> --json
hd2mod mod list --library <mod-library-dir> --json
hd2mod mod show <guid-or-unique-prefix> --library <mod-library-dir> --json
hd2mod profile list --library <mod-library-dir> --json
hd2mod profile show [name-or-guid-prefix] --library <mod-library-dir> --json
hd2mod import inspect <folder-or-archive-or-file> --json
hd2mod import plan <folder-or-archive-or-file> --output <import-plan.json>
hd2mod context export --library <mod-library-dir> --output <context.json>
hd2mod agent skill --output <SKILL.md>
```

Global options:

```text
--manager-root <directory>   A directory containing the manager's settings.json.
--library <directory>        Explicit Mod library directory; takes precedence over settings.
--game-data <directory>      Read-only GameData path override for inspection context.
--json                       Emit machine-readable JSON on stdout.
--output <file>              Required only for explicit export commands.
--overwrite                  Required to replace an existing explicit output file.
```

## Important semantics

- `profile show` returns both `logicalEntries` and `effectiveDeploymentEntries`. The latter uses the same recursive Option-expansion rule as deployment: top-level profile entries first, then enabled Options in their host-scoped effective order.
- `import inspect` is an inspect-only source scan. It must be used before any future import/apply command; it never copies source files or registers Mods. `import plan --output` saves the same inspection as an auditable, non-executable JSON artifact.
- Logical placeholder Options with no Patch group can be traversed but are absent from the effective deployable list.
- An Option is enabled relative to a specific host Mod or host Option. Read `optionActivation` rather than assuming globally enabled/disabled.
- Decoration activation state is separate from the portable decoration package and is reported as `decorationEnabledHostIds`.
- ReShade components and Lua-combined deployment are not ordinary GameData Patch entries. Do not invent Patch ordering from their presence.

## Error handling

- Use `--json` when another agent or script needs to parse results.
- When a selector is ambiguous, request or use a longer GUID prefix; do not guess.
- The CLI never repairs malformed data. Report the command error and ask the user to open the manager or provide a safe recovery direction.
- Explicit exports never overwrite an existing file unless `--overwrite` is present.
