# HD2 Mod Manager CLI

`HD2ModCli` is the headless companion for HD2 Mod Manager. It is intended for local scripts, Agent assistants, and users who want to inspect their Mod library without automating WPF clicks.

The first release is deliberately conservative: it only reads manager state and writes user-requested export files. It does **not** import, delete, deploy, edit profiles, or alter Patch data.

## Build and run during development

From the repository root:

```powershell
dotnet run --project .\HD2ModCli\HD2ModCli.csproj -- capabilities --json
```

After a Manager build, `hd2mod.exe` is copied beside `HD2ModManager.exe`. From that directory, use `--manager-root .` to read the same `settings.json` as the desktop app.

## Discover the current library

Pass the manager directory when it contains the manager's `settings.json`:

```powershell
hd2mod status --manager-root "C:\path\to\HD2ModManager" --json
hd2mod mod list --manager-root "C:\path\to\HD2ModManager" --json

# When hd2mod.exe is beside HD2ModManager.exe:
.\hd2mod.exe status --manager-root . --json
```

Or explicitly pass the Mod library directory. This is preferable for scripts and agents because it does not depend on a GUI installation location:

```powershell
hd2mod profile list --library "E:\mod" --json
hd2mod mod show 4a6d8ea4 --library "E:\mod" --json
```

`--library` always takes precedence over `ModLibraryFolder` from `settings.json`.

## AI / Agent workflow

First discover the real command surface, then export a fresh snapshot of the current library:

```powershell
hd2mod capabilities --json
hd2mod context export --library "E:\mod" --output ".\context.json"
hd2mod agent skill --output ".\SKILL.md"
```

The context contains Mod IDs, patch groups, Option/Decoration relationships and activation states, selected and active profiles, and both logical and effective deployment order. It is an inspection snapshot, not a plan to be applied.

Explicit exports never overwrite an existing file unless `--overwrite` is supplied.

## Commands

```text
hd2mod version
hd2mod help
hd2mod capabilities --json
hd2mod status --manager-root <directory> --json
hd2mod mod list --library <directory> --json
hd2mod mod show <guid-or-unique-prefix> --library <directory> --json
hd2mod profile list --library <directory> --json
hd2mod profile show [name-or-guid-prefix] --library <directory> --json
hd2mod import inspect <folder-or-archive-or-file> --json
hd2mod import plan <folder-or-archive-or-file> --output <import-plan.json>
hd2mod context export --library <directory> --output <context.json>
hd2mod agent skill --output <SKILL.md>
```

Global options:

```text
--manager-root <directory>  Directory containing the manager's settings.json.
--library <directory>       Explicit Mod library directory.
--game-data <directory>     Read-only GameData path override in context/status.
--json                      Machine-readable stdout result.
--output <file>             Required by explicit export commands.
--overwrite                 Explicitly replace an existing export file.
```

## Safety contract

- CLI inspection never creates a missing library, migrates old data, repairs JSON, or writes state back.
- `profile show` uses the same recursive Option expansion as the deploy pipeline, including host-scoped Option activation order.
- `import inspect` is read-only. It scans a source folder, archive, or file and returns the current import planner's proposed roles and Patch groups without copying or registering anything. Use `import plan --output` when an Agent needs an auditable file for a later conversation or review.
- A selector must be exact or uniquely resolvable. The CLI rejects ambiguous prefixes rather than guessing.
- Future write support will use a declaration-first `inspect → plan → validate → apply` workflow. It is intentionally absent from this release.
