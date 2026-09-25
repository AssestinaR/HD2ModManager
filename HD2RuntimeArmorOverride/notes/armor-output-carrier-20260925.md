# Armor output carrier correction

The user confirmed the container-only B retest renders correctly and closes
the armory normally. The custom TOC and GPU bytes were unchanged; moving the
carrier from `d923906f8b815987` to `9ba626afa44a3aa3` restored rendering.
This establishes a working carrier for this scenario, not proof that every
other archive is universally invalid.

Canonical cross-armor output now always uses
`9ba626afa44a3aa3.patch_0`, independently of source/target armor identity.
One-click custom conversion also stages under that carrier. Native package
headers, Unit IDs, GPU payloads and armor metadata are not changed by this fix.
The deployment allocator continues assigning sequential patch indices when
model packages and managed Lua share the carrier.

Old library outputs are not automatically renamed or rewritten. Regenerate
them for testing. The temporary B protocol was backed up and disabled; the
manager workspace remains untouched and should be compiled through GUI Apply.
The opt-in B parser/build branch remains diagnostic only; normal GUI output
does not emit `test_piece_template`.

Next manual validation must use GUI-generated standard protocol (not B) and
newly generated carrier-correct packages. Arbitrary nonzero component types,
helmet composition and all-target stress stability are not established solely
by the successful eight-body-Unit B retest.

## User acceptance, 2026-09-25

After the carrier fix and Debug build, the user reported successful manual
pressure testing with very fast full-armor switching and requested a Git
checkpoint. This is user-reported acceptance of the tested workflow, not a
claim of coverage of every possible component/helmet combination.

Automated validation at this checkpoint: Core 430/430 and CLI 118/118 passed;
manager Debug build succeeded. The initial Core run had one SQLite cleanup
file-lock failure (full rerun passed); CLI needs LuaJIT on PATH (full rerun
with the installed LuaJIT passed).
