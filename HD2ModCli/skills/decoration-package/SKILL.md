---
name: hd2mod-decoration-package
description: Inspect source Units and publish a new external, importable Decoration package through a reviewed plan.
---

# HD2 Mod Decoration Package

Use this to make a portable Decoration package from one source Mod for one explicit host. It writes a new external package only; it does not import, enable, rebuild `Overwrite`, or deploy GameData.

```powershell
hd2mod decoration inspect source <source-id> --manager-root "<manager>" --library "<library>" --game-data "<data>" --json
hd2mod decoration package plan <source-id> --host <host-id> --manager-root "<manager>" --library "<library>" --game-data "<data>" --output decoration-plan.json --json
hd2mod decoration package validate --plan decoration-plan.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --json
hd2mod decoration package apply --plan decoration-plan.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --output "<new-external-directory>" --confirm --json
```

Review `selections` and `attachment` before validation; IDs must come from the inspect/plan JSON. Validation rejects source Patch-family and sidecar drift. Apply requires a fresh directory outside the Mod library and game directory. Import the result later with `safe-import`; `decoration show` only inspects an already imported package.

For several independent sources, repeat this workflow one source package at a time and review each result separately. The desktop “batch generate” page is only a convenience loop; it does not add a different Patch format or a hidden transactional operation.
