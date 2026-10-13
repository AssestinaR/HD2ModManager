# -*- coding: utf-8 -*-
"""The weight encoding the export chain must produce, in one place.

Why this exists
---------------
The mesh leaves this lab through Blender:

    hd2_style.retarget -> dump_blender (model.npz)
                       -> blender_build (vertex groups on OURS_bind)
                       -> HD2SDK addon  (helldiver2.export_patch)
                       -> the game reads vec4_half

Two things in the middle of that chain corrupt the weights, and neither is
visible in the bind pose:

1. `blender_build` used `np.round(w, 3)`.  Rounding each influence separately
   does not preserve the sum: 0.333333 x3 -> 0.999.  Measured: 11156 vertices
   (9.17%) left the .blend with a group sum that is not 1.

2. A vertex may list the SAME HD2 bone in two slots -- the source rig has
   duplicated influences, and `resolve()` maps two source bones onto one HD2
   bone.  Measured: 29635 vertices (24.35%), of which 5761 also carry a
   *different* bone.  `blender_build` writes each slot with `REPLACE`, so the
   later slot overwrites the earlier one instead of summing.  For the 23874
   vertices whose only bone is the repeated one, the SDK's export-time
   `vertex_group_normalize_all` happens to repair the ratio; for the other 5761
   the ratio is silently wrong.

3. Even after Blender's float normalise, casting to float16 does not land on an
   exact decoded sum.  Measured: 60074 vertices (49.36%) came out with
   `sum(half(w)) != 1`, worst 3.662e-04.

The fix
-------
Merge duplicate bones by summing, then snap every influence onto the 1/2048
grid by largest remainder.  Every `k/2048` is exactly representable as float16
(the spacing of halves at and below 0.5 is 2^-12 and above it 2^-11, and k/2048
is always a multiple of 2^-11), so:

  * the float32 sum is exactly 1.0  -> Blender's `vertex_group_normalize_all`
    becomes a no-op instead of a rescale,
  * the float16 sum is exactly 1.0  -> the package comes out closed without
    patching the SDK addon at all.

This matches the key invariants of the SDK's own
`sdk-raw/hd2_system_skin_weights.py::normalize_half4` (exact decoded sum, no new
influence, single-weight move bounded by 1/2048).  It is NOT bit-identical to
it: the SDK first tries to move one element to the exact residual, which can be
more accurate.  Porting the SDK's branch-for-branch behaviour is a separate job
for the C# writer; see WEIGHT_TRANSFER_BRIEF.md §10.7.

Measured on the 121704-vertex mesh: worst single-weight move 3.252174e-04,
0 influences added, 124 tiny weights zeroed.
"""

GRID = 2048


def merge_duplicate_bones(weights, keys):
    """Sum the influences that share a bone, keeping first-appearance order.

    `keys` are STABLE bone identities (HD2 bone names), never section-local
    palette slots.  Zero and negative weights are dropped, matching the SDK.
    """
    acc, order = {}, []
    for w, k in zip(weights, keys):
        w = float(w)
        if not w > 0.0:
            continue
        if k not in acc:
            acc[k] = 0.0
            order.append(k)
        acc[k] += w
    return [acc[k] for k in order], list(order)


def quantize_half4(weights, keys, max_influences=4):
    """Snap one vertex's weights onto the 1/2048 grid.

    Returns ``(weights4, keys4, dropped)``:

      * ``weights4`` -- ``max_influences`` floats, each an exact ``k/2048``;
        their float32 sum is exactly 1.0 and so is the sum of their float16
        images.  All zero when the input has no positive weight.
      * ``keys4`` -- the matching stable keys (``None`` for empty slots).
      * ``dropped`` -- ``[(key, weight), ...]`` that did not fit into the top
        ``max_influences``.  Callers must not throw this away silently: a
        dropped mass of 0.2 is a different bug from a dropped mass of 1e-6.
    """
    import math

    weights, keys = merge_duplicate_bones(weights, keys)
    if len(weights) > max_influences:
        order = sorted(range(len(weights)),
                       key=lambda i: (-weights[i], str(keys[i])))
        keep = set(order[:max_influences])
        dropped = [(keys[i], weights[i]) for i in range(len(weights))
                   if i not in keep]
        weights = [weights[i] for i in sorted(keep)]
        keys = [keys[i] for i in sorted(keep)]
    else:
        dropped = []

    n = len(weights)
    if n == 0:
        return [0.0] * max_influences, [None] * max_influences, dropped

    total = math.fsum(weights)
    if not math.isfinite(total) or total <= 0.0:
        return [0.0] * max_influences, [None] * max_influences, dropped

    units, fracs = [], []
    for w in weights:
        scaled = w / total * GRID
        u = int(math.floor(scaled))
        units.append(u)
        fracs.append(scaled - u)

    remaining = GRID - sum(units)
    if not 0 <= remaining <= n:
        raise ValueError('invalid quantization remainder %d' % remaining)
    # largest remainder first; stable bone identity breaks ties
    for i in sorted(range(n), key=lambda i: (-fracs[i], str(keys[i])))[:remaining]:
        units[i] += 1

    out_w = [u / GRID for u in units] + [0.0] * (max_influences - n)
    out_k = list(keys) + [None] * (max_influences - n)
    return out_w, out_k, dropped


def verify_half_closed(weights4):
    """True when the four values are k/2048 and their float16 sum is exactly 1."""
    import math
    import struct

    def half(v):
        return struct.unpack('<e', struct.pack('<e', float(v)))[0]

    for w in weights4:
        if w < 0.0 or half(w) != w:
            return False
    if all(w == 0.0 for w in weights4):
        return True
    return math.fsum(half(w) for w in weights4) == 1.0
