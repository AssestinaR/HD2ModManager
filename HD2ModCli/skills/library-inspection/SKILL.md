---
name: hd2mod-library-inspection
description: Inspect an HD2 Mod Manager library, relationships, activation state, Profiles, and effective deployment order without mutation.
---

# HD2 Mod Library Inspection

Use only for read-only questions. It does not import, delete, deploy, or write a plan.

```powershell
hd2mod status --manager-root "<manager>" --json
hd2mod mod list --library "<library>" --json
hd2mod mod show <mod-id> --library "<library>" --json
hd2mod option show <option-id> --library "<library>" --json
hd2mod decoration show <decoration-id> --manager-root "<manager>" --library "<library>" --json
hd2mod profile list --library "<library>" --json
hd2mod profile show [profile] --library "<library>" --json
hd2mod context export --library "<library>" --output context.json --json
```

Treat `context export` as a point-in-time JSON file. Use `directOptions`, `optionActivation`, `decorationEnabledHostIds`, `logicalEntries`, and `effectiveDeploymentEntries` rather than inferring relationships from names. An ambiguous GUID prefix is an error, not a selection rule.
