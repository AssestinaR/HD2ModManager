"""BatchTool-style retarget -- the replacement for the T = H*S^-1 pipeline.

Why this file exists
--------------------
`export_obj.py` solved the problem as a **cross-rig per-bone similarity map**
`T_i = H_sigma(i) * S_i^-1`.  Reading HD2-BatchTool's source
(`utils/rigging.py`, `workflows/mmd_mapping.py`) showed that is NOT what the
working tool does, and that solving it that way is why we kept fighting
"symptoms" (long neck, splayed feet, +11 % height, torso cross-over):

    HD2-BatchTool does exactly three things, none of them a per-bone rotation map:
      1. ONE uniform scale + ONE translation, locking the visible foot sole and
         landing the head on the HD2 head joint (minus a small offset).
      2. Set each HD2 bone's HEAD to the source's mapped joint world position.
         Orientation and bone length stay HD2's own.  HD2-only bones are filled
         with deterministic interpolations.
      3. Apply a FIXED 30-bone local-rotation table for T-pose <-> A-pose.

The reason that is robust: **the snap happens BEFORE the bind**.  The mesh is
re-bound to the already-snapped skeleton, so the bind pose is the identity and
the rest mesh does not deform at all.  No stretching is possible, hence no long
neck, no height inflation.  And because `l_foot`'s head becomes the source's own
ankle, the legs keep the source's straight stance -- which is exactly what the
user asked for ("腿部无需对齐").

Faithfulness notes (each one read out of the BatchTool source)
--------------------------------------------------------------
* `align_independent_head` (workflows/mmd_mapping.py:477) measures
  ``source_height = (source_head - source_ground) . local_z`` and
  ``desired_height = (reference_head + z*offset - reference_ground) . local_z``
  where ``source_head`` is the **head BONE head**, ``source_ground`` is the foot
  anchor projected onto the mesh sole, and ``reference_ground`` is HD2's own
  sole.  HD2's rig has its sole at z = 0 (`l_ball` z = 0.0081, min bone z = 0),
  so ``reference_ground_z = 0``.  The scale is applied around the sole and then
  a single translation lands the head -- which puts the source sole exactly on
  HD2's sole (the two z-terms cancel by construction).
* `build_snap_heads` (mmd_mapping.py:595) only produces HEAD positions.
  `snap_independent_to_hd2` (mmd_mapping.py:859) then does, per bone,
  ``edit_bone.head = new_head; edit_bone.tail = new_head + (tail - head)`` --
  i.e. **orientation and length are HD2's own**, only the head moves.  Hands and
  fingers additionally get their roll restored from the `TP_INITIAL`/`TP_TPOSE`
  template (`align_roll`); every other body bone keeps its own current axis.
* `convert_body_axis_space` (utils/rigging.py:463) walks the body bones
  parent-first and computes
  ``new_object_rot = new_parent_rot @ inv(old_parent_rot) @ old_rot @ delta``
  (falling back to ``old_parent_rot`` when the parent is not a body bone).
  That is algebraically identical to the recursion used here,
  ``rot[b] = rot[p] @ inv(rot_rest_hd2[p]) @ rot_rest_hd2[b] @ D[b]``,
  and it **preserves each bone's head** (Blender's `LocRotScale(head_local, ..)`).
* `_convert_pose_space` (rigging.py:613) order is
  clear body pose -> AXIS space -> apply pose rotations -> bake to rest.
  `_bake_body_pose_to_rest` deforms the mesh with
  ``D[b] = M_pose[b] @ inv(M_rest[b])``, and because
  ``M_pose[b] = M_pose[p] @ inv(M_rest[p]) @ M_rest[b] @ basis[b]``
  this collapses to ``D[b] = D[p] @ M_rest[b] @ basis[b] @ inv(M_rest[b])``.

What this script computes
-------------------------
  v0  source vertex (metres, source space)
  v1  = R_align * (scale * (v0 - sole)) + t          -> aligned in HD2 space
  head_HD2[b] = the aligned source joint of the source bone mapped to b   (step 2)
  rest[b]                                                    -> HD2 rest, AXIS_SYMMETRIC,
                                                                heads snapped, parents first
  D[b] = D[parent] @ rest[b] @ basis[b] @ inv(rest[b])
  v'  = sum_i w_i * D[b_i] * v1                      -> step 3, per-vertex LBS

Nothing here touches production code.
"""
import argparse
import json
import math
import os
import sys

try:
    import numpy as np
except ImportError:                # pragma: no cover
    np = None

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E

SRC_PREFIX = E.SRC_PREFIX

# name-based mapping (BatchTool's `snap_map`), i.e. source -> HD2 by NAME.
# Mirrors `presets/bone_name_list/cats修复.py`:
#     Spine -> spine1, Chest -> spine2, Upper Chest -> chest
# For ValveBiped that reads Spine -> spine1, Spine2 -> spine2, Spine4 -> chest.
SNAP_BODY = [
    ('Pelvis', 'hips'), ('Spine', 'spine1'), ('Spine2', 'spine2'), ('Spine4', 'chest'),
    ('Neck1', 'neck'), ('Head1', 'head'),
    ('L_Clavicle', 'l_clavicle'), ('L_UpperArm', 'l_shoulder'),
    ('L_Forearm', 'l_elbow'), ('L_Hand', 'l_hand'),
    ('R_Clavicle', 'r_clavicle'), ('R_UpperArm', 'r_shoulder'),
    ('R_Forearm', 'r_elbow'), ('R_Hand', 'r_hand'),
    ('L_Thigh', 'l_thigh'), ('L_Calf', 'l_knee'), ('L_Foot', 'l_foot'),
    ('R_Thigh', 'r_thigh'), ('R_Calf', 'r_knee'), ('R_Foot', 'r_foot'),
]

# `--spine-map mesh`: the alternative found by A4.  The source rig's joint sits at
# the *bottom* of the region it drives, HD2's sits at the *centre* of it, so the
# source Spine's mesh centroid (1.1662) lands almost exactly on HD2's spine2
# (1.1633) and Spine4's centroid (1.3409) on HD2's chest (1.3437).
SPINE_MAPS = {
    'legacy': [('Spine', 'spine1'), ('Spine2', 'spine2'), ('Spine4', 'chest')],
    'mesh': [('Spine', 'spine2'), ('Spine2', 'chest'), ('Spine4', 'chest')],
}

# optional: not every L4D2 rig has toes (Lunalice_Zoey does not).  Snapping them
# is a bonus, not a contract -- their absence must not be reported as an error.
SNAP_TOES = [('L_Toe0', 'l_ball'), ('R_Toe0', 'r_ball')]


def snap_body(spine_map='legacy'):
    out = [(n, t) for n, t in SNAP_BODY if n not in ('Spine', 'Spine2', 'Spine4')]
    for i, row in enumerate(SPINE_MAPS[spine_map]):
        out.insert(1 + i, row)
    return out


def snap_pairs(spine_map='legacy'):
    return snap_body(spine_map) + E.FINGERS


# ------------------------------------------------------------------ maths
def q_wxyz_to_m(q):
    """Quaternion (w,x,y,z) -> 4x4 rotation, column-vector convention."""
    w, x, y, z = q
    n = math.sqrt(w * w + x * x + y * y + z * z) or 1.0
    w, x, y, z = w / n, x / n, y / n, z / n
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w), 0.0],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w), 0.0],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y), 0.0],
        [0.0, 0.0, 0.0, 1.0]], dtype=np.float64)


def m_to_np(m):
    return np.array(m, dtype=np.float64).reshape(4, 4)


def trans(v):
    t = np.eye(4)
    t[0, 3], t[1, 3], t[2, 3] = v
    return t


def origin_np(m):
    return m[:3, 3].copy()


def kabsch(A, B):
    """Best rigid rotation taking point set A onto B (no scale, det +1)."""
    ca, cb = A.mean(0), B.mean(0)
    H = (A - ca).T @ (B - cb)
    U, _S, Vt = np.linalg.svd(H)
    d = 1.0 if np.linalg.det(Vt.T @ U.T) > 0 else -1.0
    return Vt.T @ np.diag([1.0, 1.0, d]) @ U.T, ca, cb


def yaw_rotation(A, B):
    """The rotation about +Z that best takes A onto B (a 1-DOF fit).

    This is the *right* fit for the source -> HD2 space change and the free
    3-DOF `kabsch` above is a trap: the two rigs place their joints at
    different heights (the source puts `Pelvis` 12 cm BELOW the hip joints, HD2
    puts `hips` at them), so the 20-point sets are not congruent and the
    best-fit rigid motion comes out tilted by ~2.8 deg off vertical.  A 180 deg
    rotation about an axis 2.8 deg off +Z mixes 0.94 m of height into Y, which
    moves the ankle ~9.8 cm away from the hip -- i.e. the fit "invents" the very
    leg offset we are trying to measure.  Projecting to XY first keeps the
    rotation exactly about +Z, so heights and the vertical leg stance survive.
    """
    ca, cb = A[:, :2].mean(0), B[:, :2].mean(0)
    H = (A[:, :2] - ca).T @ (B[:, :2] - cb)
    U, _S, Vt = np.linalg.svd(H)
    d = 1.0 if np.linalg.det(Vt.T @ U.T) > 0 else -1.0
    R2 = Vt.T @ np.diag([1.0, d]) @ U.T
    R = np.eye(3)
    R[0, 0], R[0, 1] = R2[0, 0], R2[0, 1]
    R[1, 0], R[1, 1] = R2[1, 0], R2[1, 1]
    return R, ca, cb


def rot_angle_axis(R):
    ang = math.degrees(math.acos(max(-1.0, min(1.0, (float(np.trace(R)) - 1.0) / 2.0))))
    w = np.array([R[2, 1] - R[1, 2], R[0, 2] - R[2, 0], R[1, 0] - R[0, 1]])
    n = np.linalg.norm(w)
    return ang, (w / n if n > 1e-9 else np.zeros(3))


def resolve_bone_map(src, src_index_to_hd2, fallback, ajoint, by_idx_name):
    """Source bone index -> HD2 bone index, including a fallback for unmapped bones.

    `distance` binds an unmapped bone to the *mapped* bone whose joint sits closest
    to it; `parent` walks up the source chain instead.  This lives in one place on
    purpose: the shoes (`source_extra_62/63`) are unmapped, and the stance pass used
    to fall back to "carry along with the torso" for them -- which dragged the feet
    15 cm forward together with the hips and cancelled out the very stance change it
    was supposed to produce.  Both passes must give the same answer.
    """
    resolved, hops, by_dist = {}, {}, []
    if fallback == 'distance':
        cand = [(src_index_to_hd2[b['index']], ajoint(b))
                for b in src if b['index'] in src_index_to_hd2]
        for b in src:
            i = b['index']
            if i in src_index_to_hd2:
                resolved[i], hops[i] = src_index_to_hd2[i], 0
                continue
            p = ajoint(b)
            best, bd = None, 1e30
            for hi, sp in cand:
                d = float(((p - sp) ** 2).sum())
                if d < bd:
                    bd, best = d, hi
            resolved[i], hops[i] = best, 0
            by_dist.append((b['name'], by_idx_name.get(best), math.sqrt(bd)))
    else:
        for b in src:
            i = b['index']
            if i in src_index_to_hd2:
                resolved[i], hops[i] = src_index_to_hd2[i], 0
                continue
            cur, n = b['parent'], 1
            while cur >= 0 and cur not in src_index_to_hd2 and n <= 64:
                cur = src[cur]['parent']
                n += 1
            resolved[i], hops[i] = src_index_to_hd2.get(cur), n
    return resolved, hops, by_dist


# ------------------------------------------------------------------ loader
def drop_outlier_tris(pts, tris, factor=8.0, floor=0.06):
    """Display-only: drop triangles that are stretched far beyond the mesh's own scale.

    Several of these L4D2 mods ship a stray spike (Nick has one 1.8 m out in +Y
    weighted to `source_extra_101`, Ellis one 1.0 m out).  It is present in the
    source bind pose as well, so it is the model's own artefact and NOT something
    the retarget introduces -- but it wrecks the auto-framing of the render.

    A spike is attached to the body by a handful of absurdly long triangles, so the
    criterion is edge length: these meshes have 100 k+ vertices, i.e. millimetre
    edges, and anything `factor` times the median is an artefact.
    """
    P = np.asarray(pts, dtype=np.float64)
    lens = []
    for a, b, c, _m in tris:
        lens.append(max(np.linalg.norm(P[a] - P[b]), np.linalg.norm(P[b] - P[c]),
                        np.linalg.norm(P[c] - P[a])))
    med = float(np.median(lens)) if lens else 0.0
    lim = max(med * factor, floor)
    return [t for t in tris if max(np.linalg.norm(P[t[0]] - P[t[1]]),
                                   np.linalg.norm(P[t[1]] - P[t[2]]),
                                   np.linalg.norm(P[t[2]] - P[t[0]])) <= lim], lim


def load_source(vpk, model):
    """VPK -> (mdl_path, mdl_bytes, verts, tris, src_bones, short_by_name)."""
    _v, do, ents = E.parse_vpk_tree(vpk)
    by = {f.lower(): (f, arch, o, l) for f, arch, o, l in ents}
    cands = [f for f in by if f.lower().endswith('.mdl')
             and model.lower() in os.path.basename(f).lower()]
    cands.sort(key=lambda f: (len(os.path.basename(f)), os.path.basename(f)))
    mdl_path = cands[0]
    base = mdl_path[:-4]

    def blob(p):
        f, arch, o, l = by[p.lower()]
        return E.read_range(vpk, do, arch, o, l)

    mdl = blob(mdl_path)
    verts, tris = E.build_geometry(E.read_mdl_geometry(mdl),
                                   E.read_vvd(blob(base + '.vvd'))[0],
                                   E.read_vtx(blob(next(p for p in by
                                                        if p.lower() == (base + '.dx90.vtx').lower())))[0],
                                   [])
    src = E.read_source_bones(mdl)
    short = {b['name'][len(SRC_PREFIX):]: b for b in src
             if b['name'].startswith(SRC_PREFIX)}
    return mdl_path, mdl, verts, tris, src, short


# ------------------------------------------------------------------ core
def retarget(vpk, model, toc, bonehash, pose_profile, scale=0.0, head_offset=-0.015,
             align='kabsch', apose=True, spine_map='legacy', fallback='parent',
             leg_tilt=0.0, verbose=True):
    """Run the whole BatchTool-style pipeline.  Returns a dict of results."""
    def say(*a):
        if verbose:
            print(*a)

    mdl_path, mdl, verts, tris, src, short = load_source(vpk, model)
    say('mesh: %d verts / %d tris' % (len(verts), len(tris)))

    hd2 = E.read_hd2_rig(toc, bonehash)
    hby = {h['name']: h for h in hd2 if h['name']}
    by_idx_name = {h['index']: h['name'] for h in hd2}
    pairs = snap_pairs(spine_map)
    src_index_to_hd2 = {}
    for sn, hn in pairs + SNAP_TOES:
        sb, hb = short.get(sn), hby.get(hn)
        if sb is not None and hb is not None:
            src_index_to_hd2[sb['index']] = hb['index']

    src_pts = np.array([[v['pos'][0] * E.IN2M, v['pos'][1] * E.IN2M, v['pos'][2] * E.IN2M]
                        for v in verts], dtype=np.float64)

    # ---- 1a. the alignment rotation (source left is +X, HD2 left is -X) ----
    body = snap_body(spine_map)
    A = np.array([origin_np(m_to_np(short[n]['world_m'])) for n, _h in body])
    B = np.array([origin_np(m_to_np(hby[h]['world'])) for _n, h in body])
    if align == 'kabsch':
        R, _ca, _cb = kabsch(A, B)
        ang, ax = rot_angle_axis(R)
        say('align: kabsch over %d body joints -> %.2f deg about (%.3f, %.3f, %.3f)'
            % (len(body), ang, ax[0], ax[1], ax[2]))
        say('  WARNING: the free 3-DOF fit is tilted %.2f deg off vertical; that mixes '
            'height into Y and invents a leg offset.  Use --align yaw.' % ang
            if abs(ax[2]) < 0.999 else '  (axis is essentially +Z)')
    elif align == 'yaw':
        R, _ca, _cb = yaw_rotation(A, B)
        ang, ax = rot_angle_axis(R)
        say('align: yaw-only fit over %d body joints -> %.4f deg about +Z (exact vertical)'
            % (len(body), ang))
    elif align == 'rotz180':
        R = np.diag([-1.0, -1.0, 1.0])
        say('align: exact 180 deg about +Z (matches the old project CreateScale(-1,-1,1))')
    else:
        R = np.eye(3)
        say('align: identity')

    # ---- 1b. ONE uniform scale + ONE translation -------------------------
    # heights are measured in HD2 space along +Z, exactly like
    # `(head - ground).dot(local_z_world)` in align_independent_head
    def rot_only(P):
        return P @ R.T

    lo = float(src_pts[:, 2].min())
    sole = src_pts[src_pts[:, 2] < lo + 0.01].mean(0)      # the visible sole patch
    head_src_v = origin_np(m_to_np(short['Head1']['world_m']))
    head_hd2 = origin_np(m_to_np(hby['head']['world']))
    ref_ground_z = 0.0                                     # HD2's own sole is at z = 0
    src_h = float(rot_only(head_src_v.reshape(1, 3))[0][2]
                  - rot_only(sole.reshape(1, 3))[0][2])
    if scale > 0:
        s = scale
    else:
        s = (head_hd2[2] + head_offset - ref_ground_z) / max(src_h, 1e-6)
    say('scale: %.6f  (source head joint %.4f -> sole %.4f = %.4f m tall; '
        'HD2 head %.4f, offset %+.3f)' % (s, head_src_v[2], sole[2], src_h,
                                          head_hd2[2], head_offset))

    def align_pts(P):
        return ((P - sole) * s) @ R.T

    t = (head_hd2 + np.array([0.0, 0.0, head_offset])) - align_pts(head_src_v.reshape(1, 3))[0]
    src1 = align_pts(src_pts) + t
    say('aligned: source height %.3f -> %.3f m   sole z %.4f (want %.4f)   bbox z [%.3f, %.3f]'
        % (src_pts[:, 2].max() - src_pts[:, 2].min(),
           src1[:, 2].max() - src1[:, 2].min(),
           (align_pts(sole.reshape(1, 3))[0] + t)[2], ref_ground_z,
           src1[:, 2].min(), src1[:, 2].max()))

    # ---- 2. snap: HD2 bone head := aligned source joint -------------------
    def joint(sname):
        return align_pts(origin_np(m_to_np(short[sname]['world_m'])).reshape(1, 3))[0] + t

    new_head = {}
    for sn, hn in pairs + SNAP_TOES:
        if sn in short and hn in hby:
            new_head[hn] = joint(sn)
    missing = [(sn, hn) for sn, hn in pairs if sn not in short or hn not in hby]

    def lerp(p, q, f):
        return p + (q - p) * f

    # deterministic fills for HD2-only bones (mirrors build_snap_heads)
    if 'spine1' not in new_head and 'spine2' in new_head:
        new_head['spine1'] = lerp(new_head['hips'], new_head['spine2'], 0.50)
    if 'spine2' not in new_head:
        new_head['spine2'] = lerp(new_head['spine1'], new_head['neck'], 0.50)
    new_head.setdefault('chest', new_head['spine2'].copy())
    for sd in ('l', 'r'):
        sh = new_head['%s_shoulder' % sd]
        el = new_head['%s_elbow' % sd]
        wr = new_head['%s_hand' % sd]
        new_head.setdefault('%s_shoulder_twist' % sd, lerp(sh, el, 0.42))
        new_head.setdefault('%s_hand_twist' % sd, wr.copy())
        if '%s_shoulderarmour' % sd in hby:
            new_head['%s_shoulderarmour' % sd] = lerp(new_head['%s_clavicle' % sd], sh, 0.90)
        ball = new_head.get('%s_ball' % sd)
        if ball is not None and '%s_toe' % sd in hby:
            new_head['%s_toe' % sd] = ball + (ball - new_head['%s_foot' % sd]) * 0.35
    if 'head_tip' in hby:
        new_head['head_tip'] = new_head['head'] + (new_head['head'] - new_head['neck'])
    say('snap: %d HD2 bone heads set (of %d transforms)' % (len(new_head), len(hd2)))
    if missing:
        say('  no correspondence for: %s' % ', '.join('%s->%s' % p for p in missing))

    # ---- 2b. optional stance: lean the legs forward about the ankle -------
    # The source ships with strictly vertical legs; HD2 (and the reference patch)
    # stand with the ankle ~10 cm BEHIND the hip.  Rotating everything ABOVE the
    # ankle about the ankle -- and then carrying the torso along with the hip,
    # rigidly, so the torso stays upright -- reproduces that stance without
    # touching the mesh's own shape.  The foot itself stays planted.  Doing this
    # per-bone by only moving the bone HEADS would shear the leg instead; that is
    # the same mechanism that tears the arms apart in the "re-posed by HD2's
    # rest" render, so the mesh is transformed too.
    if abs(leg_tilt) > 1e-9 and 'hips' in new_head and 'l_foot' in new_head:
        th = math.radians(leg_tilt)
        c, s = math.cos(th), math.sin(th)
        ankle = 0.5 * (new_head['l_foot'] + new_head['r_foot'])
        Rl = trans(ankle) @ np.array([[1, 0, 0, 0], [0, c, s, 0],
                                      [0, -s, c, 0], [0, 0, 0, 1]], float) @ trans(-ankle)
        delta = (Rl @ np.append(new_head['hips'], 1.0))[:3] - new_head['hips']
        turned = {'%s_%s' % (sd, j) for sd in ('l', 'r') for j in ('thigh', 'knee')}
        planted = {'%s_%s' % (sd, j) for sd in ('l', 'r') for j in ('foot', 'ball', 'toe')}

        def move(nm, p):
            if nm in turned:
                return (Rl @ np.append(p, 1.0))[:3]
            if nm in planted:
                return p
            return p + delta

        for nm in list(new_head):
            new_head[nm] = move(nm, new_head[nm])

        def ajoint(b):
            return align_pts(origin_np(m_to_np(b['world_m'])).reshape(1, 3))[0] + t
        res_idx, _rh, _rd = resolve_bone_map(src, src_index_to_hd2, fallback, ajoint,
                                             by_idx_name)
        moved = np.empty_like(src1)
        for k, v in enumerate(verts):
            acc = np.zeros(3)
            tot = 0.0
            p4 = np.append(src1[k], 1.0)
            for j in range(3):
                w = v['w'][j]
                if w <= 0:
                    continue
                nm = by_idx_name.get(res_idx.get(v['bones'][j]))
                if nm in turned:
                    T = Rl
                elif nm in planted:
                    T = np.eye(4)
                else:
                    T = trans(delta)
                acc += w * (T @ p4)[:3]
                tot += w
            moved[k] = acc / tot if tot > 0 else src1[k]
        src1 = moved
        say('stance: legs %.2f deg forward about the ankle; torso carried %+.4f m '
            'forward (upright); foot planted' % (leg_tilt, delta[1]))

    # ---- 3. HD2 rest: AXIS_SYMMETRIC + snapped heads, in ONE parent-first pass
    # The two operations must be interleaved: an HD2-only bone that is a child of a
    # snapped bone has to follow its moved parent, and doing the head moves in a
    # second pass would leave it behind.
    prof = json.load(open(pose_profile, encoding='utf-8'))
    deltas = prof['symmetric_axis_deltas_wxyz']
    body_names = set(prof['body_bones'])
    rest_hd2 = {h['index']: m_to_np(h['world']) for h in hd2}
    by_idx = {h['index']: h for h in hd2}

    def parent_of(h):
        p = h.get('parent')
        if p is None or p == h['index'] or p not in by_idx:
            return None
        return by_idx[p]

    order = sorted(hd2, key=lambda h: h['index'])   # TocData is parent-first
    rest_sym = {}
    for h in order:
        p = parent_of(h)
        Dd = (q_wxyz_to_m(deltas[h['name']])
              if (h['name'] in body_names and h['name'] in deltas) else np.eye(4))
        base = (rest_hd2[h['index']] @ Dd if p is None else
                rest_sym[p['index']] @ np.linalg.inv(rest_hd2[p['index']])
                @ rest_hd2[h['index']] @ Dd)
        tgt = new_head.get(h['name'])
        if tgt is not None:
            base = trans(tgt - origin_np(base)) @ base
        rest_sym[h['index']] = base

    # ---- 4. T-pose -> A-pose, then the per-vertex transform ---------------
    basis = {}
    if apose:
        for name, q in prof['initial_basis_rotations_from_baked_tpose_wxyz'].items():
            basis[name] = q_wxyz_to_m(q)
    else:
        say('apose: disabled')

    D = {}
    for h in order:
        p = parent_of(h)
        Dp = D[p['index']] if p is not None else np.eye(4)
        rs = rest_sym[h['index']]
        D[h['index']] = Dp @ rs @ basis.get(h['name'], np.eye(4)) @ np.linalg.inv(rs)

    # ---- skinning: source bone -> HD2 bone -------------------------------

    resolved, hops = {}, {}
    by_dist = []
    # A bone with no counterpart is bound to the mapped bone whose joint sits
    # CLOSEST to it -- not to its parent.  The source file hangs the shoes off
    # `L_Calf` (`source_extra_62/63`), so a parent-chain fallback binds the shoes
    # to the KNEE and, because our rest knee sits 13.4 cm above HD2's, they sink
    # that far below the floor the moment the leg is posed.  It is invisible in the
    # bind pose -- which is exactly why the bind-pose metric looked perfect.
    def ajoint(b):
        return align_pts(origin_np(m_to_np(b['world_m'])).reshape(1, 3))[0] + t
    resolved, hops, by_dist = resolve_bone_map(src, src_index_to_hd2, fallback,
                                               ajoint, by_idx_name)
    if by_dist:
        say('distance fallback for %d bones, e.g.: %s'
            % (len(by_dist), ', '.join('%s->%s(%.3fm)' % r for r in
                                       sorted(by_dist, key=lambda r: -r[2])[:6])))

    out = np.empty_like(src1)
    for k, v in enumerate(verts):
        acc = np.zeros(3)
        tot = 0.0
        p4 = np.append(src1[k], 1.0)
        for j in range(3):
            w = v['w'][j]
            if w <= 0:
                continue
            M = D.get(resolved.get(v['bones'][j]))
            if M is None:
                M = np.eye(4)
            acc += w * (M @ p4)[:3]
            tot += w
        out[k] = acc / tot if tot > 0 else src1[k]

    # ---- 5. the pose the GAME actually shows ------------------------------
    # The shipped rest is  S_A = D * rest_sym  (rest_sym is the T-pose rest; D is
    # the T->A conversion, so S_A is the A-pose rest that the shipped mesh `out`
    # is bound to).  The runtime deforms a vertex by  A[b] @ inv(S_A[b])  where A
    # is the animated bone; with the idle animation A == HD2's own rest, so the
    # mesh is re-posed into HD2's stance -- the bind pose itself is never
    # displayed.  This is the only render that can be compared against a
    # reference patch mod, and it is where a bad correspondence shows up: a
    # landmark pair with the wrong *semantics* stretches the mesh between them.
    idle = {h['index']: rest_hd2[h['index']] @ np.linalg.inv(D[h['index']] @ rest_sym[h['index']])
            for h in hd2}
    out_idle = np.empty_like(out)
    for k, v in enumerate(verts):
        acc = np.zeros(3)
        tot = 0.0
        p4 = np.append(out[k], 1.0)
        for j in range(3):
            w = v['w'][j]
            if w <= 0:
                continue
            M = idle.get(resolved.get(v['bones'][j]))
            if M is None:
                M = np.eye(4)
            acc += w * (M @ p4)[:3]
            tot += w
        out_idle[k] = acc / tot if tot > 0 else out[k]

    # ---- metrics ---------------------------------------------------------
    em, p50, p95, p99, emax, ebad = E.edge_distortion(
        [tuple(p) for p in src1], [tuple(p) for p in out], tris)
    say('edge mean %.4f p50 %.4f p95 %.4f p99 %.4f max %.2f | bad>25%% %.2f%%'
        % (em, p50, p95, p99, emax, 100 * ebad))
    hh = {}
    for b in src:
        hh[hops[b['index']]] = hh.get(hops[b['index']], 0) + 1
    say('fallback hops: %s' % dict(sorted(hh.items())))

    # foot spread: the metric that showed the old pipeline splaying the legs
    def foot_spread(P):
        bot = P[P[:, 2] < P[:, 2].min() + 0.06]
        return float(bot[:, 0].max() - bot[:, 0].min())

    return dict(
        mdl_path=mdl_path, mdl=mdl, verts=verts, tris=tris, src=src,
        src_pts=src_pts, src1=src1, out=out, out_idle=out_idle, rest=rest_sym, D=D,
        hd2=hd2, rest_hd2=rest_hd2, resolved=resolved, src_index_to_hd2=src_index_to_hd2,
        scale=s, sole=sole, head_src=head_src_v, head_hd2=head_hd2,
        new_head=new_head, missing=missing, hops=hh,
        metrics=dict(scale=s, source_height=src_h, sole_z=float(sole[2]),
                     height_before=float(src_pts[:, 2].max() - src_pts[:, 2].min()),
                     height_after=float(out[:, 2].max() - out[:, 2].min()),
                     edge_mean=em, edge_p50=p50, edge_p95=p95, edge_p99=p99,
                     edge_max=emax, bad=ebad,
                     foot_spread_src=foot_spread(src_pts),
                     foot_spread_snap=foot_spread(src1),
                     foot_spread_out=foot_spread(out),
                     snap_count=len(new_head), spine_map=spine_map, align=align,
                     missing=['%s->%s' % p for p in missing],
                     hops={str(k): v for k, v in sorted(hh.items())}),
    )


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--vpk', required=True)
    ap.add_argument('--model', default='survivor_teenangst')
    ap.add_argument('--toc', required=True)
    ap.add_argument('--bonehash', required=True)
    ap.add_argument('--pose-profile', required=True,
                    help='HD2-BatchTool assets/body_pose_profile.json')
    ap.add_argument('--outdir', default='.')
    ap.add_argument('--out', required=True)
    ap.add_argument('--scale', type=float, default=0.0,
                    help='uniform scale; 0 = derive from the BatchTool rule '
                         '(foot sole -> head joint, minus --head-offset)')
    ap.add_argument('--head-offset', type=float, default=-0.015)
    ap.add_argument('--align', choices=('kabsch', 'yaw', 'rotz180', 'identity'), default='kabsch')
    ap.add_argument('--spine-map', choices=('legacy', 'mesh'), default='legacy')
    ap.add_argument('--fallback', choices=('parent', 'distance'), default='parent',
                    help='how a source bone with no counterpart is bound: to its nearest '
                         'mapped ANCESTOR (parent) or to the mapped bone whose joint is '
                         'CLOSEST (distance).  The shoes need distance.')
    ap.add_argument('--no-apose', action='store_true',
                    help='stop after alignment+snap (still a T-pose)')
    ap.add_argument('--json-out', default='')
    a = ap.parse_args()

    r = retarget(a.vpk, a.model, a.toc, a.bonehash, a.pose_profile,
                 scale=a.scale, head_offset=a.head_offset, align=a.align,
                 apose=not a.no_apose, spine_map=a.spine_map, fallback=a.fallback)

    os.makedirs(a.outdir, exist_ok=True)
    png = os.path.join(a.outdir, a.out)
    E.render(png, [tuple(p) for p in r['src1']], [tuple(p) for p in r['out']], r['tris'],
             'BatchTool-style -- row1 aligned source (T-pose) | row2 +A-pose   '
             '(%s, scale %.4f, spine=%s)'
             % (os.path.basename(r['mdl_path']), r['scale'], a.spine_map))
    print('[written] %s' % png)

    if a.json_out:
        with open(a.json_out, 'w', encoding='utf-8') as f:
            json.dump(r['metrics'], f, indent=1, ensure_ascii=False)
        print('[written] %s' % a.json_out)


if __name__ == '__main__':
    main()
