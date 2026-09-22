---
name: hd2mod-armor-registry
description: Inspect and safely hot-update runtime armor visual-composition mappings without editing encrypted game registry files.
---

# HD2 Runtime Armor Registry

Use this capability only when the reviewed HD2 Runtime Armor Registry Lua Mod
is deployed. It edits the external desired-state protocol; it never edits
`generated_customization_armor_sets.dl_bin`, runtime status, locator cache, or
process memory directly.

```powershell
hd2mod armor-registry status --json
hd2mod armor-registry show --json
```

Create a JSON specification with the complete desired mapping set:

```json
{
  "schema": "hd2mod.armor-registry-spec",
  "enabled": true,
  "mappings": [
    {
      "targetItemId": 488691438,
      "sourceItemId": 820333399
    }
  ]
}
```

Then use the frozen plan workflow:

```powershell
hd2mod armor-registry plan armor-registry-spec.json --output armor-registry-plan.json --json
hd2mod armor-registry validate --plan armor-registry-plan.json --json
hd2mod armor-registry apply --plan armor-registry-plan.json --confirm --json
```

The mapping set is declarative and complete, not an incremental patch. Each
target must be unique and cannot map to itself. Apply rechecks the exact input
protocol fingerprint under a cross-process write lease, then atomically
replaces only `armor-registry.protocol`. `already-applied` is a successful
no-op. Use `--registry-root` only for a reviewed non-default runtime directory.

After apply, poll `armor-registry status --json` until
`runtimeProcessRunning` and `runtimeAcknowledged` are both true. A missing or
stopped runtime does not make the protocol write fail; it applies the desired
state on the next game start. Do not treat a stale on-disk
`runtimeStatus.state=ready` as proof that the game is still running.

Protocol v1 accepts only existing game armor as visual sources. Do not invent
Item IDs for model bases. Virtual, non-game model bases require a future
runtime/protocol version with explicit model-key support.
