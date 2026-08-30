---
name: hd2mod-profile-alignment
description: Review and precisely align one Profile's complete logical Mod order without deploying or rewriting library.json.
---

# HD2 Mod Profile Alignment

Use this when the final membership/order is known and a sequence of small `profile add/remove/reorder` calls would be noisy or unsafe. Export the current Profile first, edit only `entryNodeIds` and (if truly intended) `activeMode`, then use the frozen plan.

```powershell
hd2mod profile align export <profile> --library "<library>" --output profile-spec.json --json
hd2mod profile align inspect profile-spec.json --manager-root "<manager>" --json
hd2mod profile align plan profile-spec.json --manager-root "<manager>" --output profile-align-plan.json --json
hd2mod profile align validate --plan profile-align-plan.json --manager-root "<manager>" --json
hd2mod profile align apply --plan profile-align-plan.json --manager-root "<manager>" --confirm --json
```

`activeMode` defaults to `preserve`; valid values are `preserve`, `setActive`, and `clearIfActive`. Empty `entryNodeIds: []` is valid. `validate` returns `ready`, `already-aligned`, or `drifted`; `already-aligned` is a successful no-op. The operation uses the existing library write lease, revalidates inside it, and writes only the library-local `profiles.json`. It never changes `library.json`, Option/Decoration activation, Patch files, or GameData, and it never deploys.
