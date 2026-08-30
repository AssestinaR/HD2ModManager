---
name: hd2mod-safe-import
description: Safely import a reviewed HD2 Mod source through a plan, fresh validation, and explicit user-confirmed apply step.
---

# HD2 Mod Safe Import

Use only to import a reviewed local folder, archive, or file. It does not authorize deployment, Profile changes, deletion, or Patch reconstruction.

```powershell
hd2mod import inspect "<folder-or-archive>" --json
hd2mod import plan "<folder-or-archive>" --output import.plan.json
hd2mod import validate --plan import.plan.json --manager-root "<manager directory>" --json
hd2mod import apply --plan import.plan.json --manager-root "<manager directory>" --confirm --json
```

Read the complete validation result and stop unless `ok` is `true`. Apply only after the user explicitly approves that exact source and target. A plan is a reviewed snapshot, not permission to deploy, alter Profiles, or retry a partial post-commit result blindly.
