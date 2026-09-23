# Armor registry protocol

The runtime and manager communicate through:

`%LOCALAPPDATA%/CowboyBingus/Helldivers2/ArmorRegistry`

Files:

- `armor-registry.protocol`: manager-owned desired state.
- `runtime.status`: Lua-owned current state and process-local addresses.
- `locator.cache`: Lua-owned validated locator shape for the current game build.

Protocol version 1 is line based and UTF-8/ASCII compatible:

```text
schema=1
revision=7
enabled=true
map=488691438,820333399
```

Each `map` means that the target armor keeps its own identity/gameplay metadata
and uses the source armor's Body/Piece visual composition. Targets must be
unique. Managers should increment `revision` and atomically replace the file.
The runtime also compares complete file contents, so a missed revision bump
does not silently suppress a change.

Absolute addresses are process-local and MUST NOT be reused after restart.
`locator.cache` stores only the game fingerprint and validated structural
shape. Every process cheaply relocates that shape, then writes the current
PID/addresses to `runtime.status`. Cache validation failure triggers automatic
structural rediscovery.

## Manager transaction

The Manager/CLI owns `armor-registry.protocol` and the zero-content
`.manager.lock` used to serialize concurrent writers.

1. Read and strictly parse the current protocol.
2. Build a reviewed plan containing the exact input SHA-256 fingerprint.
3. Recheck the fingerprint immediately before apply.
4. Write a same-directory temporary file and atomically replace the protocol.
5. Never write `runtime.status`, `locator.cache`, or process-local addresses.

The runtime polls complete file contents approximately once per second. It
constructs and validates every replacement clone before publishing any index
pointer. Removing a mapping restores the original root pointer.

The CLI input specification is JSON so the future GUI does not need to emit
the runtime wire format:

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

## Version update behavior

`locator.cache` is an optimization, not an authority. It contains the PE
timestamp, image size, record count, blob byte length, and allocation region
sizes. At every game process start the runtime relocates and validates the
actual blob/index. A mismatched or invalid cache triggers structural
rediscovery and is replaced only after the complete registry is validated.

## Protocol v2: virtual model bases

Version 1 intentionally maps a real target armor to a real source armor. A
future version must also let users register a visual model base that does not
exist as an equipable game item.

The semantic model is:

```text
ModelBase {
    key: stable namespaced string
    composition: complete Body/Piece/Unit visual definition
}

Mapping {
    target: existing game armor item ID
    source: Armor(item ID) | Model(model key)
}
```

Required rules:

- A model key is not a synthetic Item ID and is never published as a game
  inventory item. This avoids collisions with future official armor IDs.
- A model may initially clone a real armor composition, but the final format
  must also accept a complete manager-generated Body/Piece/Unit definition.
- Model bases contain visual composition only. Names, armor class, passive
  skills, unlock state, shop identity, and other gameplay metadata always come
  from the real target armor.
- The Lua runtime owns model clone memory for the life of the process. Multiple
  target clones may reference one validated model composition without copying
  GPU payloads.
- Every referenced Unit/resource must already be available through the
  deployed game/Patch asset set. The protocol registers composition references;
  it does not embed GPU payload bytes.
- Duplicate model keys, missing model references, invalid Body/Piece layouts,
  unresolved weight ties, and dependency cycles are blocking. Target weight
  restoration chooses a unique mode at the exact `(Body, slot, piece_type)`
  scope, then falls back to the same Body and metadata/visible class, then to
  the global metadata/visible class. A tied mode falls back to the next wider
  scope; a tie or missing values at every scope remains blocking.
- An update is transactional: parse all model bases, build/validate all
  compositions, build all target clones, then publish. Failure leaves the last
  applied revision active.
- `schema=1` readers continue to reject v2 fields. The Manager must not emit
  v2 until a runtime advertising v2 support is ready.

Publishing a virtual model base inside the game's native 411-root registry is
not required for this feature. The safe first implementation is a Lua-owned
model pool consumed only while building real target armor clones. Native index
growth can remain a separate experiment because it changes registry ownership
and count semantics without improving the user-facing reuse workflow.
