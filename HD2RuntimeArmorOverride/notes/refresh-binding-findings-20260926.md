# Binding inspection — September 26, 2026

## Result

There is now a concrete native association traversal to investigate.
The observed `Unit.is_child` Lua wrapper does **not** return its native
predicate result to Lua. Calling it and interpreting nil as false would
mislead the ownership experiment. No inspected binding was invoked.
This is not a refresh function discovery.

## Method and evidence

Probe v7 adds explicit `bindings` mode after same-session World preflight.
`jit.util.funcinfo` supplies each known C binding's address; LuaJIT source
`src/lib_jit.c`, `jit_util_funcinfo`, implements this via `fn->c.f`.
Reference: https://raw.githubusercontent.com/LuaJIT/LuaJIT/v2.1/src/lib_jit.c

Each read is limited to 2,048 bytes within a validated, readable executable
region of game.dll, helldivers2.exe or lua51.dll. No request-supplied
addresses, unknown-module reads, process attachment, hooks, debugger,
protection changes or target function calls are used.

First run `20260926-100512-0f34f024e3f54d4a8cc8110337fc9679`
rejected all seven addresses because its module scope was game.dll only.
Second run `20260926-100819-c1362527070a4c78a2d9a5b8bb92618a`
identified all seven bindings in **helldivers2.exe**, not game.dll.
Third run `20260926-101122-6e3aa7f7794f446ba17f4f41bc0a454d`
also captured one offline-reviewed direct callee of `Unit.is_child`.
All directories are under `evidence/refresh/`.

The callee read is separately gated by the observed executable timestamp
1790149348, image size 60719104, binding RVA 0x411e30, and an E8 instruction
at binding +0x4e. It reads a single callee window, does not execute it or
recursively follow calls. These constants are diagnostic guards, not a
production update-resilient locator.

## Observed implementation

At executable RVA 0x411e30, the `is_child` wrapper:

1. Extracts Lua arguments 1 and 2 through the same accessor.
2. Shifts their low 32-bit values right by two.
3. Resolves each through executable RVA 0x9d8c0.
4. Passes the two resolved values to executable RVA 0x204fd0.
5. Executes `xor eax,eax` and returns, without a Lua result-push call.

Thus the exposed Lua function returns zero values in this observed build.
The representation/resolver steps need independent validation before using
them to turn Lua handles into native pointers.

The native callee at RVA 0x204fd0:

- Immediately returns true for equal arguments.
- Calls a virtual function at the first object's vtable +0x1e8, supplying a
  temporary output structure.
- Iterates a count at output +0 and pointer array at output +8.
- Recursively checks each pointer against the second argument.
- Returns a boolean after temporary-allocation cleanup.

This strongly suggests a recursive containment/descendant predicate.
It is not proof that the traversed edges are armor ownership, nor does it
establish a documented direction/name for the vtable method. Cleanup code
does write allocator/temporary state; no claim of a fully side-effect-free
native call is made. No internal call has been made through FFI.

Other inspected wrappers share the same handle resolver. `Unit.world`
reads a field at resolved object +0x18; `scene_graph_parent` accesses
node-index data, consistent with its existing interpretation. Neither is
a substitute for an armor controller.

## Next step

Trace the concrete association-list implementation for a verified avatar
instance without executing an unknown method. Cross-check the resulting
references against the already observed worn assembly and stationary
negative-control groups. The missing proof is an actual avatar-to-armor
edge and its update consumer, not another movement capture.

Do not ship diagnostic build constants or call `is_child` expecting a Lua
boolean. Do not manually spawn/destroy armor or reinterpret this predicate
as the requested refresh operation.

## Validation

Lua tests, in-project Debug CLI build and Core embedded-resource test pass.
A new test executes the actual inspector helper against synthetic module
memory: module/guard/build gates, exact hex length including NUL bytes,
reviewed-callsite mismatch and zero target invocation are checked.
Deployment used manager plan/validate/apply; no mapping compilation.
The sampling request ends in `stopped`; protocol revision 45 stays intact.
