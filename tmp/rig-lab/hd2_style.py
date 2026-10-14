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


def _env_arm_rot():
    """Round-45c lab convenience: every probe hard-codes the pipeline arguments,
    so re-fitting one parameter would otherwise mean editing a dozen files.  Set
    HD2_ARM_ROT="SH EL WR" to make every probe that leaves `arm_rot` at its
    default use the new value.  A probe that passes `arm_rot` explicitly
    (probe_arm_angle.py, the sweeps) always wins."""
    v = os.environ.get('HD2_ARM_ROT', '').replace(',', ' ').split()
    if not v:
        return None
    if len(v) != 3:
        raise SystemExit('HD2_ARM_ROT needs three numbers: SH EL WR')
    return tuple(float(x) for x in v)


ENV_ARM_ROT = _env_arm_rot()
_b = os.environ.get('HD2_ARM_ROT_SMOOTH')
ENV_ARM_ROT_SMOOTH = None if _b is None else int(_b)

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
ARM_BONE_KEYS = ('clavicle', 'shoulder', 'elbow', 'hand', 'finger')


def is_arm_bone(name):
    """True for the left/right ARM chain, fingers included; False for everything
    else (legs, spine, head).  `l_shoulderarmour` and `l_hand_twist` count."""
    return bool(name) and name[:2] in ('l_', 'r_') and any(
        k in name for k in ARM_BONE_KEYS)


def arm_vertex_partition(verts, src_nm, side, prefix):
    """Split vertices into (upper-arm, distal, arm) for ONE side.

    Ownership is decided by the vertex's PRIMARY bone -- the largest of its
    three weights -- never by "any weight above zero".  A shoulder vertex can
    carry 0.9994 UpperArm and 0.0006 Forearm, and counting that trace as
    "distal" forced f = 1 on the upper-arm ROOT, which made the displacement
    jump 3.7 cm across a single edge (`tmp/rig-lab/_dbg_3b_jump.py`).  With the
    primary bone every vertex gets exactly one side and one group.
    """
    SD = side.upper()
    nv = len(verts)
    up = np.zeros(nv, dtype=bool)
    far = np.zeros(nv, dtype=bool)
    arm = np.zeros(nv, dtype=bool)
    for k, v in enumerate(verts):
        w = v['w']
        j = 0 if (w[0] >= w[1] and w[0] >= w[2]) else (1 if w[1] >= w[2] else 2)
        if w[j] <= 0.0:
            continue
        bi = int(v['bones'][j])
        if bi < 0 or bi >= len(src_nm):
            continue
        nm = src_nm[bi]
        if nm.startswith(prefix):
            nm = nm[len(prefix):]
        if not nm.startswith(SD + '_'):
            continue
        tail = nm[len(SD) + 1:]
        if tail.startswith('UpperArm'):
            up[k] = True
            arm[k] = True
        elif (tail.startswith('Forearm') or tail.startswith('Hand')
              or tail.startswith('Finger')):
            far[k] = True
            arm[k] = True
        elif tail.startswith('Clavicle') or tail.startswith('Shoulder'):
            arm[k] = True
    return up, far, arm


def arm_stretch_field(P, verts, src_nm, side, prefix, SH, EL):
    """The section-3b displacement ramp `f` for ONE side.

        s = dot(v - SH, u)         u = (EL - SH)/|EL - SH|,  L = |EL - SH|
        f = clamp(s / L, 0, 1)     LINEAR ramp -> uniform axial stretch
        f[distal] = 1              forearm/hand/fingers: rigid translation
        f[body side of joint] = 0  only what is INBOARD of the shoulder joint

    The last line is deliberately "inboard of the joint", not "everything the
    partition did not claim": `source_extra_78` is a helper bone hung off the
    HAND, so its vertices sit on the arm but the primary-bone partition cannot
    name them.  Pinning those left them behind while their neighbours moved a
    full gain and the shell tore open (5 cm jump, `_dbg_3b_jump.py`).
    """
    SH = np.asarray(SH, float)
    EL = np.asarray(EL, float)
    d = EL - SH
    L = float(np.linalg.norm(d))
    up, far, arm = arm_vertex_partition(verts, src_nm, side, prefix)
    if L < 1e-9:
        return np.zeros(len(P)), up, far, arm
    u = d / L
    s = (P - SH) @ u
    f = np.clip(s / L, 0.0, 1.0)
    f[far] = 1.0
    f[(~arm) & (s <= 0.0)] = 0.0
    return f, up, far, arm


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


def rot_axis_angle(axis, ang):
    """Axis + angle -> 3x3 rotation (Rodrigues), column-vector convention."""
    x, y, z = axis
    c, s = math.cos(ang), math.sin(ang)
    C = 1.0 - c
    return np.array([
        [c + x * x * C, x * y * C - z * s, x * z * C + y * s],
        [y * x * C + z * s, c + y * y * C, y * z * C - x * s],
        [z * x * C - y * s, z * y * C + x * s, c + z * z * C]], dtype=np.float64)


def min_arc_rotation(a, b):
    """The shortest rotation taking direction `a` onto direction `b`.

    ⚠️ Round 54: the anti-parallel branch used to `return -np.eye(3)`.  That
    matrix has det = -1 -- it is a MIRROR, not a rotation -- and anything that
    then composes it with real rotations (or checks `det(R) == +1`) inherits a
    flipped frame.  The real finger chains never hit it, but a copied-and-
    automated version of this function would.  A true 180 deg turn needs an
    axis perpendicular to `a`; pick the most stable one.
    """
    a = np.asarray(a, float)
    b = np.asarray(b, float)
    a = a / max(float(np.linalg.norm(a)), 1e-12)
    b = b / max(float(np.linalg.norm(b)), 1e-12)
    ax = np.cross(a, b)
    s = float(np.linalg.norm(ax))
    c = float(a @ b)
    if s < 1e-9:
        if c > 0.0:
            return np.eye(3)
        helper = np.array([1.0, 0.0, 0.0]) if abs(a[0]) < 0.9 \
            else np.array([0.0, 1.0, 0.0])
        axis = np.cross(a, helper)
        axis = axis / max(float(np.linalg.norm(axis)), 1e-12)
        return rot_axis_angle(axis, math.pi)
    return rot_axis_angle(ax / s, math.atan2(s, c))


# ------------------------------------------------------------------ fingers
# Round 54.  The source model is a T-pose hand with the fingers laid out FLAT
# and straight (measured bend 0.00 deg on all ten chains); HD2's native hand is
# a relaxed A-pose hand with the fingers curled forward-down (21-28 deg at the
# first joint).  The pipeline carries the hand as a rigid extension of the
# forearm, so our fingers inherit the flat pose and end up ~45 deg off HD2's
# finger bones -- and, worse, HD2's distal finger joints land 35-48 mm OUTSIDE
# our fingers, so at runtime the game would hinge them about thin air.
#
# The fix is a finger-only residual pose applied to the FINAL mesh (see
# `apply_finger_pose`), so the already-accepted palm, wrist and roots stay put.
FINGER_STEMS = (('thumb', 'Finger0'), ('index', 'Finger1'),
                ('middle', 'Finger2'), ('ring', 'Finger3'), ('pinky', 'Finger4'))


def finger_bone_pairs(side, short, hby):
    """[(finger, [3 hd2 bone names], [3 source bone names])] for one side.

    Both trees are 5 fingers x 3 phalanges and isomorphic:
        source  L_Finger2 / L_Finger21 / L_Finger22      (middle)
        HD2     l_middle_finger1 / _2 / _3
    """
    got = []
    for stem, sfx in FINGER_STEMS:
        hs = ['%s_%s_finger%d' % (side, stem, k) for k in (1, 2, 3)]
        ss = ['%s_%s%s' % (side.upper(), sfx, s) for s in ('', '1', '2')]
        if all(n in hby for n in hs) and all(n in short for n in ss):
            got.append((stem, hs, ss))
    return got


def slerp_dir(a, b, t):
    """Blend two unit directions along the short arc (t=0 -> a, t=1 -> b)."""
    d = float(np.clip(a @ b, -1.0, 1.0))
    th = math.acos(d)
    if th < 1e-9:
        return a
    k = math.sin(th)
    if abs(k) < 1e-12:
        return a
    return (math.sin((1.0 - t) * th) / k) * a + (math.sin(t * th) / k) * b


def apply_finger_pose(P0, verts, resolved, D, rest_sym, rest_hd2, hby, short,
                      post, amount, say, only=None):
    """Put the fingers onto HD2's native finger directions.  Mesh-only.

    Expert-reviewed (round 54, §10.5).  Per finger, root to tip, forward
    kinematics driven by the TARGET chain:

        Q  = Gp[:3, :3]
        Rl = min_arc(s[k], Q.T @ t[k])          <- the Q pair is the whole point
        Gk = Gp @ T(J[k]) @ R4(Rl) @ T(-J[k])
        Gp = Gk

    * `J[k]` are the joints of the UNBENT input P0.  The parent transform moves
      them; pre-moving them and putting them on the right of `Gp` would move the
      pivot twice.
    * `s[k]` are the FROZEN segment directions of P0 and `t[k]` the targets.
      The rotation must be conjugated by `Q` because the min-arc solved in world
      space is not a local rotation -- dropping that pair leaves the phalanges
      up to 14 deg off (the expert measured 11.3-14.3 deg on real chains).
    * The palm/wrist/root are already accepted, so `Gp` starts at the identity:
      this is a RESIDUAL pose, not a re-pose of the hand.
    * Lengths are preserved (rotations only) and the roots do not move, so the
      mesh goes back through the ORIGINAL three-weight skinning with no
      re-normalisation and no hard split at the root.

    `only` restricts the pass to a subset, for the expert's one-finger-first
    acceptance test (§10.7.1).  It accepts stems ('middle'), or side+stem
    ('l_middle', 'r_pinky'), case-insensitive; None means every finger.
    """
    P0 = np.asarray(P0, float)
    want = None
    if only:
        want = {str(x).strip().lower() for x in np.atleast_1d(only)}
    G, rows = {}, []
    for side in ('l', 'r'):
        for stem, hs, ss in finger_bone_pairs(side, short, hby):
            if want is not None and stem.lower() not in want \
                    and ('%s_%s' % (side, stem)).lower() not in want:
                continue
            bidx = [hby[n]['index'] for n in hs]
            # our current joints: the source joint carried by the pipeline's own
            # deform matrix, then through the same mesh-only post-processing the
            # final `out` got (shift + upper-body scale).  NOT rest_sym: that is
            # the pre-post space.
            J = post(np.asarray([(D[b] @ np.append(rest_sym[b][:3, 3], 1.0))[:3]
                                 for b in bidx], float))
            # the source chain is straight (measured 0.00 deg on all ten), so the
            # terminal segment continues the middle one; the source bend is
            # printed so a future model that is NOT straight cannot slip past.
            sp = np.asarray([rest_sym[b][:3, 3] for b in bidx], float)
            u0 = sp[1] - sp[0]
            u1 = sp[2] - sp[1]
            src_bend = math.degrees(math.acos(max(-1.0, min(1.0, float(
                (u0 / max(np.linalg.norm(u0), 1e-12)) @
                (u1 / max(np.linalg.norm(u1), 1e-12)))))))
            s = [J[1] - J[0], J[2] - J[1], J[2] - J[1]]
            s = [v / max(float(np.linalg.norm(v)), 1e-12) for v in s]
            # targets: HD2's native finger chain.  The last phalanx has no child,
            # so its direction is the bone's own +X axis.
            T = np.asarray([rest_hd2[b][:3, 3] for b in bidx], float)
            t = [T[1] - T[0], T[2] - T[1]]
            t = [v / max(float(np.linalg.norm(v)), 1e-12) for v in t]
            ax = np.asarray(rest_hd2[bidx[2]][:3, 0], float)
            t.append(ax / max(float(np.linalg.norm(ax)), 1e-12))
            if amount < 1.0:
                t = [slerp_dir(sv, tv, amount) for sv, tv in zip(s, t)]
            Gp = np.eye(4)
            angs = []
            for k in range(3):
                Q = Gp[:3, :3]
                Rl = min_arc_rotation(s[k], Q.T @ t[k])
                a_k, _ax_k = rot_angle_axis(Rl)
                angs.append(a_k)
                Tp = np.eye(4)
                Tp[:3, 3] = J[k]
                Tm = np.eye(4)
                Tm[:3, 3] = -J[k]
                R4 = np.eye(4)
                R4[:3, :3] = Rl
                Gp = Gp @ Tp @ R4 @ Tm
                G[bidx[k]] = Gp
            # residual: what the chain still misses after the pose
            err = []
            for k in range(3):
                got = G[bidx[k]][:3, :3] @ s[k]
                err.append(math.degrees(math.acos(max(-1.0, min(1.0, float(
                    (got / max(np.linalg.norm(got), 1e-12)) @ t[k]))))))
            rows.append(dict(side=side, stem=stem, hd2=hs, src=ss, bidx=bidx,
                             joints=J, angles_deg=angs, residual_deg=err,
                             source_bend_deg=src_bend))
            say('finger %s_%-6s target bends %5.2f %5.2f %5.2f deg -> applied '
                '%5.2f %5.2f %5.2f deg; residual %5.3f %5.3f %5.3f deg '
                '(source chain bend %.3f deg)'
                % (side, stem, *[math.degrees(math.acos(max(-1.0, min(1.0, float(
                    (s[k] / max(np.linalg.norm(s[k]), 1e-12)) @ t[k])))))
                    for k in range(3)], *angs, *err, src_bend))

    p4 = np.hstack([P0, np.ones((len(P0), 1))])
    new = np.empty_like(P0)
    for k, v in enumerate(verts):
        acc = np.zeros(3)
        tot = 0.0
        for j in range(3):
            w = v['w'][j]
            if w <= 0.0:
                continue
            M = G.get(resolved.get(v['bones'][j]))
            acc += w * (P0[k] if M is None else (M @ p4[k])[:3])
            tot += w
        new[k] = acc / tot if tot > 0 else P0[k]
    return new, rows, G


def resolve_bone_map(src, src_index_to_hd2, fallback, ajoint, by_idx_name,
                     hd2_children=None, hd2_parents=None):
    """Source bone index -> HD2 bone index, including a fallback for unmapped bones.

    `parent`   walks up the source chain to the nearest mapped ANCESTOR.
    `distance` binds to the mapped bone whose joint sits CLOSEST to it.
    `region`   is `distance` restricted to bones that live in the SAME LIMB as
               that ancestor.  The limb is read off HD2's own hierarchy -- a bone
               belongs to `l_arm`/`r_arm`/`l_leg`/`r_leg`/`head`/`torso` according
               to which of `l_clavicle`, `r_clavicle`, `l_thigh`, `r_thigh`, `neck`
               it passes through on the way up.  This is what keeps the source
               skeleton's structure: `source_extra_73/76/85` are hair and
               hair-ribbon bones that hang beside the shoulder, their ancestor is
               `Bip01_Head1`, so they may only land on the head -- raw `distance`
               handed them to `l_shoulder`/`r_shoulder`, and the A-pose bake then
               swung the hair down and inward with the arm.

               A plain "ancestor's subtree" rule does NOT work here, because HD2
               hangs `hips` and `spine1` off `boss` as SIBLINGS: the subtree of
               `hips` is the legs alone, which would throw the skirt onto the
               thighs.  The limb test keeps the pelvis a torso part.

    This lives in one place on purpose: the shoes (`source_extra_62/63`) are
    unmapped, and the stance pass used to fall back to "carry along with the
    torso" for them -- which dragged the feet 15 cm forward together with the
    hips and cancelled out the very stance change it was supposed to produce.
    Both passes must give the same answer.

    THE CHAIN RULE (round 65).  A RUN of unmapped bones hanging off one mapped
    ancestor is ONE piece, not a set of independent bones: every bone in the run
    must land on the SAME HD2 bone, or the piece tears in half the first time a
    joint below it moves.  The run's ROOT -- the bone whose own parent is
    mapped -- decides, because that is the bone actually touching the skeleton,
    so its nearest HD2 joint IS the attachment point.

    This is not hypothetical, it is the whole of round 65.  Lilium's outfit has
    two 1.8 m ribbon/sleeve chains, `L_UpperArm -> 93 -> 94 -> 97 -> 98 -> 99 ->
    100 -> 101` and its right-hand mirror.  Resolved bone by bone, the ratio
    `d_best / d_ancestor` climbs smoothly 0.25 -> 0.48 -> 0.85 -> 0.90 -> 0.92
    -> 0.95 -> 0.96, so ANY distance threshold cuts the chain somewhere in the
    middle: `97` stays on the elbow while `98` goes to `l_pinky_finger1`.  The
    measured damage was 1430 ribbon vertices riding the pinky at up to 1.9 m
    from the bone (1301 mm of travel for 40 deg of pinky bend), plus a 0.39 m
    tear at the elbow.  Collapsing the run onto its root's answer removes both:
    the whole ribbon becomes one rigid piece on `l_elbow`, where it is attached.

    NOTE the rule is deliberately NOT "one bone per ancestor": Lunalice's skirt
    hangs 8 INDEPENDENT orphans off the spine (`source_extra_119..126`, parents
    `7`/`123`/`125`) and they legitimately split between `chest` and `spine2`.
    Only a parent-child run is collapsed.
    """
    resolved, hops, by_dist = {}, {}, []
    by_index = {b['index']: b for b in src}
    if fallback in ('distance', 'region'):
        cand = [(src_index_to_hd2[b['index']], ajoint(b))
                for b in src if b['index'] in src_index_to_hd2]
        sub, lim = {}, {}
        MARK = {'l_clavicle': 'l_arm', 'r_clavicle': 'r_arm',
                'l_thigh': 'l_leg', 'r_thigh': 'r_leg', 'neck': 'head'}

        def subtree(root):
            if root not in sub:
                seen, stack = set(), [root]
                while stack:
                    n = stack.pop()
                    if n in seen:
                        continue
                    seen.add(n)
                    stack.extend((hd2_children or {}).get(n, ()))
                sub[root] = seen
            return sub[root]

        def limb(h):
            if h not in lim:
                cur, n, got = h, 0, 'torso'
                while cur is not None and n < 64:
                    nmv = by_idx_name.get(cur)
                    if nmv in MARK:
                        got = MARK[nmv]
                        break
                    cur = (hd2_parents or {}).get(cur)
                    n += 1
                lim[h] = got
            return lim[h]

        chain = set()
        for b in src:
            i = b['index']
            if i in src_index_to_hd2:
                resolved[i], hops[i] = src_index_to_hd2[i], 0
                continue
            cur, n = b['parent'], 1
            while cur >= 0 and cur not in src_index_to_hd2 and n <= 64:
                cur = src[cur]['parent']
                n += 1
            hops[i] = 0 if fallback == 'distance' else n
            # a bone INSIDE a run: its answer is the run root's, resolved below
            if b['parent'] >= 0 and b['parent'] not in src_index_to_hd2:
                chain.add(i)
                continue
            pool = cand
            if (fallback == 'region' and hd2_children and hd2_parents
                    and cur >= 0 and cur in src_index_to_hd2):
                root = src_index_to_hd2[cur]
                allow = subtree(root)
                lk = limb(root)
                pool = [c for c in cand if c[0] in allow and limb(c[0]) == lk]
                if not pool:
                    pool = cand
            p = ajoint(b)
            best, bd = None, 1e30
            for hi, sp in pool:
                d = float(((p - sp) ** 2).sum())
                if d < bd:
                    bd, best = d, hi
            resolved[i] = best
            by_dist.append((b['name'], by_idx_name.get(best), math.sqrt(bd)))

        # propagate each run from its root down.  Swept rather than recursed so
        # the answer does not depend on `src` happening to be parent-first.
        for _ in range(len(src) + 1):
            left = [i for i in chain if i not in resolved]
            if not left:
                break
            for i in left:
                p = by_index[i]['parent']
                if p in resolved:
                    resolved[i] = resolved[p]
        # a run whose parent never resolved (cycle / truncated file) keeps the
        # plain ancestor answer rather than staying unbound
        for i in chain:
            if i in resolved:
                continue
            cur = by_index[i]['parent']
            while cur >= 0 and cur not in src_index_to_hd2:
                cur = by_index[cur]['parent']
            resolved[i] = src_index_to_hd2.get(cur)
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
def drop_outlier_tris(pts, tris, radius_frac=0.85):
    """Display-only: hide geometry that sits far outside the BODY.

    Why this is no longer an edge-length rule
    -----------------------------------------
    It used to drop "any triangle with an edge longer than
    max(8 x median, 0.06 m)", on the theory that these L4D2 mods ship a stray
    spike held on by absurdly long triangles.  Measuring all five models showed
    BOTH halves of that theory were wrong:

      * there is no 1.8 m / 1.0 m triangle anywhere.  The largest edge is 0.19 m
        (Zoey/Rochelle), 0.12 m (Coach), 0.34 m (Nick), 0.13 m (Ellis) -- in the
        raw source mesh, in the aligned mesh AND in the retargeted mesh;
      * the far geometry is a COMPACT BLOB, not a fan.  On Nick it is the
        ``LongSleeve`` body part (a sleeve sheet whose bind pose reaches y 1.78,
        held on by only 19 triangles); on Ellis it is the ``Tail`` part.

    So the old rule never actually removed the blob, while it DID delete real,
    named model geometry.  On Zoey it removed 510 vertices: the ponytail
    (``Base``, z 0.96-1.60, weighted to the hair bones ``source_extra_72..77``
    plus ``Bip01_Head1``) and the platform-shoe heel (``platform shoes``, z
    -0.062-0.008).  That is what made the figure look like a short-haired model
    with flat shoes.

    The rule that matches the real geometry is a radius measured against the
    mesh's OWN scale, so it needs no per-model constant: take the median vertex
    as the body centre, and hide any triangle that touches a vertex farther than
    ``radius_frac * height`` from it.  The per-body-part radii show why 0.85 is
    the right value (max radius, metres):

        part                     zoey    coach    nick    ellis
        shoe / boot              1.26     1.38    1.39     1.43
        Base (incl. hair)        1.19     1.36    1.34     1.37
        LongSleeve                  -        -    1.85       -
        Tail                        -        -       -     1.67
        -> cut = 0.85 * height   1.58     1.49    1.58     1.56

    So nothing at all is hidden on Zoey or Coach (ponytail and shoe heel are
    back), while Nick's far sleeve and Ellis's tail are hidden.  Both of those
    are source bind-pose artefacts: the sleeve is a jiggle-driven sheet lying
    1.78 m out in +Y, and the tail is a jiggle chain sticking forward -- in game
    the physics bones pull them into place.

    This is a DISPLAY filter only: the exported mesh keeps every triangle.
    """
    P = np.asarray(pts, dtype=np.float64)
    if not len(tris) or not len(P):
        return list(tris), (0, 0.0)
    centre = np.median(P, axis=0)
    d = np.linalg.norm(P - centre, axis=1)
    height = float(P[:, 2].max() - P[:, 2].min())
    cut = radius_frac * height
    far = d > cut
    kept = [t for t in tris if not (far[t[0]] or far[t[1]] or far[t[2]])]
    return kept, (len(tris) - len(kept), cut)


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
             align='kabsch', apose=True, spine_map='legacy', fallback='region',
             leg_tilt=0.0, leg_splay=0.0, axis_deltas=True, axis_roundtrip=True,
             skeleton='native', mesh_shift=(0.0, 0.0, 0.0), upper_scale=1.0,
             upper_pivot_z=None, upper_blend=0.20, arm_fit=1.0, arm_perp=0.0,
             arm_up=0.55, arm_smooth=8, arm_taper=(-0.05, 0.22), arm_rot=None,
             arm_rot_smooth=8, arm_rot_mode='rigid',
             arm_scratch=True, arm_scratch_deg=None, arm_scratch_elbow=True,
             arm_refit_scratch=False,
             arm_elbow_open=0.0, arm_hand_tilt=0.0, arm_aim_wrist=False,
             arm_finger_pose=None, arm_finger_pose_amount=1.0,
             arm_scratch_upper_gain=0.0, arm_scratch_upper_shift=(0.0, 0.0, 0.0),
             head_scale=1.0,
             verbose=True):
    """Run the whole BatchTool-style pipeline.  Returns a dict of results.

    The MESH is ALWAYS baked around the SNAPPED rest (`rest_sym`, whose heads are
    the source's own joints).  That is the whole reason the bind pose looks right:
    `D[b] = D[p] . rs[b] . basis[b] . inv(rs[b])` rotates the mesh about bone b's
    joint, so the pivot has to sit INSIDE the mesh.  HD2's own joints do not --
    its rig is much wider than the source -- which is exactly why pivoting about
    them tears the arms apart and throws the hand out to arm's length.

    The `skeleton` switch changes ONLY which rig we SHIP:
      * `skeleton='native'` (default) -- HD2's rest verbatim.  The runtime deforms
        by `rest_hd2[b] . inv(S_bind[b])`, so with `S_bind == rest_hd2` the idle
        frame is the exact identity and the game shows the mesh we posed here.
        This is what the reference patch does.
      * `skeleton='snap'` -- the snapped rest.  `S_bind != rest_hd2`, so the idle
        frame drags the mesh (missing heel, torn ankle, long neck, split skirt).
        Kept for A/B.
    """
    def say(*a):
        if verbose:
            print(*a)

    if arm_rot is None and ENV_ARM_ROT is not None:
        arm_rot = ENV_ARM_ROT
        if ENV_ARM_ROT_SMOOTH is not None:
            arm_rot_smooth = ENV_ARM_ROT_SMOOTH
        say('arm_rot taken from HD2_ARM_ROT: %s (smooth %d)'
            % (arm_rot, int(arm_rot_smooth)))

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

    # ---- 0. profile + HD2's rig -------------------------------------------
    # `rest_hd2` is HD2's native rest, i.e. what the game itself has.  We ship it
    # verbatim under `skeleton='native'` (step 4b), which is the only way the idle
    # frame can be the identity: the runtime deforms by
    # `rest_hd2[b] . inv(S_bind[b])`, so if `S_bind` is HD2's own rest the mesh
    # comes back untouched and "what we pose offline is what the game shows".
    # The reference patch does exactly this: its rig is bit-identical to HD2's
    # native one, and its mesh simply sits wherever it needs to for looks.
    #
    # `rest_t` below is HD2's WORKING T-pose rest (native axes + the BatchTool's
    # T-pose basis).  Nothing in the shipped result uses it any more -- the mesh
    # is pivoted about the SNAPPED rest instead -- but it is kept because it is
    # the "HD2 in T-pose" reference the alignment is measured against.
    prof = json.load(open(pose_profile, encoding='utf-8'))
    deltas = prof['symmetric_axis_deltas_wxyz']
    body_names = set(prof['body_bones'])
    if arm_scratch:
        # Round 46.  BatchTool's profile ships exactly three tables, and ALL
        # THREE only ever touch the arm chain -- verified by dumping the file:
        # tpose_basis(30 bones) / initial_basis(30) / symmetric_axis_deltas(20)
        # are 100% l|r clavicle, shoulder, elbow, hand + fingers, with ZERO
        # torso, leg or head bones.  So "stop letting BatchTool pose the arm" is
        # exactly "drop all three", and by construction it cannot affect anything
        # outside the arm.
        deltas = {}
        say('arm: BatchTool arm tables DISABLED '
            '(tpose_basis / initial_basis / axis_deltas are arm-only)')
    rest_hd2 = {h['index']: m_to_np(h['world']) for h in hd2}
    by_idx = {h['index']: h for h in hd2}
    order = sorted(hd2, key=lambda h: h['index'])   # TocData is parent-first

    def parent_of(h):
        p = h.get('parent')
        if p is None or p == h['index'] or p not in by_idx:
            return None
        return by_idx[p]

    # HD2's child/parent lists -- needed by the `region` fallback, which may only
    # bind an orphan source bone inside the same LIMB as its nearest mapped
    # ancestor.  Note HD2 hangs `hips` and `spine1` off `boss` as siblings, so the
    # subtree of `hips` is the legs alone; the limb test (not the subtree test) is
    # what keeps the skirt a torso part.
    hd2_children, hd2_parents = {}, {}
    for h in hd2:
        p = parent_of(h)
        if p is not None:
            hd2_children.setdefault(p['index'], []).append(h['index'])
            hd2_parents[h['index']] = p['index']

    def axis_delta(h):
        return (q_wxyz_to_m(deltas[h['name']])
                if (axis_deltas and h['name'] in body_names and h['name'] in deltas)
                else np.eye(4))

    basis_t = ({n: q_wxyz_to_m(q)
                for n, q in prof['tpose_basis_rotations_wxyz'].items()}
               if (axis_roundtrip and not arm_scratch) else {})
    rest_t = {}
    for h in order:
        p = parent_of(h)
        L = (rest_hd2[h['index']] if p is None else
             np.linalg.inv(rest_hd2[p['index']]) @ rest_hd2[h['index']])
        L = L @ axis_delta(h) @ basis_t.get(h['name'], np.eye(4))
        rest_t[h['index']] = L if p is None else rest_t[p['index']] @ L

    native = skeleton == 'native'
    say('skeleton: %s -- %s' % (
        'NATIVE' if native else 'SNAP',
        'ship HD2\'s own rest, so the idle frame is the identity'
        if native else 'ship the snapped rest; the idle frame will drag the mesh'))

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

    # ALWAYS snap.  The A-pose bake rotates the mesh about these heads, and the
    # source's own joints are the only ones that sit inside the source's mesh.
    # Which rig we ship is a separate decision -- see step 4b.
    new_head = {}
    for sn, hn in pairs + SNAP_TOES:
        if sn in short and hn in hby:
            new_head[hn] = joint(sn)
    missing = [(sn, hn) for sn, hn in pairs if sn not in short or hn not in hby]

    def lerp(p, q, f):
        return p + (q - p) * f

    # deterministic fills for HD2-only bones (mirrors build_snap_heads).
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
            new_head['%s_shoulderarmour' % sd] = lerp(
                new_head['%s_clavicle' % sd], sh, 0.90)
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
        # NB: `ct`/`st`, NOT `c`/`s`.  `s` is the closure's uniform SCALE, and
        # `align_pts` reads it; rebinding it here silently shrank every `joint()`
        # call made afterwards (the arm block's "pivot = source shoulder" log
        # printed (-0.0107, 0.0501, 0.1461) instead of (-0.1154, 0.0532, 1.5701)
        # -- a point at ankle height).  The mesh survived only because those
        # joints are used for DIRECTIONS, and min_arc_rotation normalises.
        ct, st = math.cos(th), math.sin(th)
        ankle = 0.5 * (new_head['l_foot'] + new_head['r_foot'])
        Rl = trans(ankle) @ np.array([[1, 0, 0, 0], [0, ct, st, 0],
                                      [0, -st, ct, 0], [0, 0, 0, 1]], float) @ trans(-ankle)
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
                                             by_idx_name, hd2_children, hd2_parents)
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

    # ---- 2c. optional stance: splay the legs outward in the FRONT view -----
    # HD2's own rig stands with each ankle 7.07 deg outboard of its hip (ankle
    # span 0.382 m against a hip span of 0.174 m) and the shipped reference
    # patch keeps that rig bit-identical, so its front view reads as a relaxed
    # "at ease" stance.  The L4D2 source is a strict T-pose with vertical legs,
    # which is why our front view looks stiff.
    #
    # The motion is the front-view twin of step 2b: rotate the leg about its
    # ROOT (the thigh head) by +theta on the left and -theta on the right, then
    # counter-rotate the foot about its ankle by -theta so the sole stays flat.
    # Because the ankle rides rigidly on the leg, that counter-rotation collapses
    # to a pure translation of the foot by (ankle_new - ankle_old) -- which is
    # exactly the "then shift each foot out by half a foot" step, except it comes
    # out of the geometry for free instead of being a magic number.
    if abs(leg_splay) > 1e-9 and 'l_thigh' in new_head and 'r_thigh' in new_head:
        th = math.radians(leg_splay)
        Tsp = {}
        span_before = abs(new_head['r_foot'][0] - new_head['l_foot'][0])
        foot_shift = 0.0
        for sd, sgn in (('l', +1.0), ('r', -1.0)):
            root = new_head['%s_thigh' % sd]
            a = sgn * th
            ca, sa = math.cos(a), math.sin(a)
            Rsp = (trans(root)
                   @ np.array([[ca, 0, sa, 0], [0, 1, 0, 0],
                               [-sa, 0, ca, 0], [0, 0, 0, 1]], float)
                   @ trans(-root))
            for j in ('thigh', 'knee'):
                Tsp['%s_%s' % (sd, j)] = Rsp
            ank = new_head['%s_foot' % sd]
            ank_new = (Rsp @ np.append(ank, 1.0))[:3]
            Tsp['%s_foot' % sd] = trans(ank_new - ank)
            foot_shift = max(foot_shift, abs(ank_new[0] - ank[0]))
            for j in ('ball', 'toe'):
                nm = '%s_%s' % (sd, j)
                if nm in new_head:
                    Tsp[nm] = Tsp['%s_foot' % sd]
        for nm in list(new_head):
            if nm in Tsp:
                new_head[nm] = (Tsp[nm] @ np.append(new_head[nm], 1.0))[:3]

        def ajoint2(b):
            return align_pts(origin_np(m_to_np(b['world_m'])).reshape(1, 3))[0] + t
        res2, _h2, _d2 = resolve_bone_map(src, src_index_to_hd2, fallback, ajoint2,
                                          by_idx_name, hd2_children, hd2_parents)
        moved = np.empty_like(src1)
        for k, v in enumerate(verts):
            acc = np.zeros(3)
            tot = 0.0
            p4 = np.append(src1[k], 1.0)
            for j in range(3):
                w = v['w'][j]
                if w <= 0:
                    continue
                nm = by_idx_name.get(res2.get(v['bones'][j]))
                M = Tsp.get(nm)
                if M is None:
                    M = np.eye(4)
                acc += w * (M @ p4)[:3]
                tot += w
            moved[k] = acc / tot if tot > 0 else src1[k]
        src1 = moved
        say('stance: legs splayed %.2f deg outward about the hip; foot carried out '
            '%.4f m and kept flat; ankle span %.4f -> %.4f m'
            % (leg_splay, foot_shift, span_before,
               abs(new_head['r_foot'][0] - new_head['l_foot'][0])))

    # ---- 3. HD2 rest: AXIS_SYMMETRIC + working heads, ONE parent-first pass
    # The two operations must be interleaved: an HD2-only bone that is a child of a
    # moved bone has to follow its moved parent, and doing the head moves in a
    # second pass would leave it behind.
    #
    # The BatchTool's WORKING rest is not the one it ships.  It converts the bone
    # axes to SYMMETRIC, bakes `tpose_basis_rotations_wxyz` into the rest (that is
    # the T-pose the source character is aligned against), later bakes
    # `initial_basis_rotations_from_baked_tpose_wxyz` back on top -- and only then
    # converts the axes back to ORIGINAL ("转回初态 + 原始骨轴").  Those two bakes
    # are exact inverses, so they CANCEL on the rig's local rotations and the
    # shipped rig ends up bit-identical to HD2's native one, which is exactly what
    # the reference patch has.  If we skip the first bake we leave basis_i baked
    # into the shipped bone orientations (34-49 deg on the arms, 180 deg on the
    # right clavicle); if we skip the final axes conversion we leave Dd in as well.
    # `rest_t` above is that working rest; under `skeleton='native'` the working
    # heads ARE `rest_t`'s own, so this pass reproduces `rest_t` exactly.
    rest_sym = {}
    for h in order:
        p = parent_of(h)
        Dd = axis_delta(h)
        base = (rest_hd2[h['index']] @ Dd if p is None else
                rest_sym[p['index']] @ np.linalg.inv(rest_hd2[p['index']])
                @ rest_hd2[h['index']] @ Dd)
        base = base @ basis_t.get(h['name'], np.eye(4))
        tgt = new_head.get(h['name'])
        if tgt is not None:
            base = trans(tgt - origin_np(base)) @ base
        rest_sym[h['index']] = base
    if not axis_deltas:
        say('axis deltas: DISABLED (HD2\'s original bone axes are kept, which is '
            'what the BatchTool\'s final "back to the initial state + original '
            'bone axes" step does)')

    # ---- 3b. UPPER-ARM LENGTHENING (MESH-LEVEL AXIAL STRETCH) --------------
    # Goal: make the upper arm genuinely LONGER on the MESH, with no step at the
    # elbow.  See `docs/arm-retarget-lessons.md` and
    # `tmp/rig-lab/ARM_ELBOW_ARTIFACT_BRIEF.md` (independent review).
    #
    # The first implementation only TRANSLATED THE WORKING PIVOTS
    # (`rest_sym[l_elbow]`, `rest_sym[l_hand]`).  That cannot lengthen anything:
    #     D[b] = D[parent] . rest_sym[b] . basis[b] . inv(rest_sym[b])
    # recurses PARENT-FIRST, so `D[l_shoulder]` -- the matrix every upper-arm
    # vertex uses -- does not depend on `rest_sym[l_elbow]` at all.  The upper
    # arm therefore stayed exactly where it was, the forearm was carried out by
    # the elbow's new pivot, and the difference had to be absorbed by the
    # elbow's weight transition band: a sideways STEP at the elbow, not a longer
    # arm.  (The independent review measured it on the real model: `src1` moved
    # by 0 m; the distal rigid region moved 33.9 mm, and only because the elbow
    # is rotated 39.6 deg -- with that rotation switched off the final mesh
    # difference was 1e-15 m.)
    #
    # The fix deforms the MESH ITSELF, in the T-POSE, along the SOURCE upper-arm
    # axis, and carries the working pivots with it:
    #
    #     s(v) = dot(v - S, u)              # metres along shoulder->elbow
    #     f(v) = clamp(s(v)/L, 0, 1)        # LINEAR ramp: uniform axial stretch
    #     v   += u * gain * f(v)
    #
    #   * shoulder end (f = 0)    -> pinned, the shoulder seam does not move;
    #   * inside the upper arm    -> an AFFINE axial scale of 1 + gain/L, the
    #                                cross-section is untouched;
    #   * elbow and beyond (f=1)  -> a RIGID translation by `gain * u`, so the
    #                                forearm keeps its length to the millimetre.
    #
    # Two details the review insisted on:
    #   (a) PARTITION BY SOURCE BONE OWNERSHIP, never by the axis projection.  A
    #       cuff or finger vertex can project back to s < L, and clamping it
    #       there would locally squash the distal shell.  Every vertex that
    #       belongs to `Forearm` / `Hand` / `Finger*` is forced to f = 1.
    #   (b) The WHOLE SUBTREE below the elbow moves with the pivots (hand,
    #       fingers, twist/armour helpers), not just the elbow and the wrist.
    #       Otherwise the identity  C_(E+d)(v+d) = C_E(v) + d  would not hold
    #       and the hand would later rotate about a stale pivot.
    #
    # `src1` and `rest_sym` are the only things touched.  Under
    # `skeleton='native'` the shipped rig is `rest_hd2` verbatim (4b below), so
    # the bones the game animates do not move by a micron.
    shift_l = np.asarray(arm_scratch_upper_shift, np.float64).reshape(3)
    if arm_scratch and (arm_scratch_upper_gain != 0.0
                        or float(np.abs(shift_l).max()) > 0.0):
        gain = float(arm_scratch_upper_gain)
        src_nm = [b['name'] for b in src]
        for side in ('l', 'r'):
            SD = side.upper()
            n_sh, n_el = '%s_shoulder' % side, '%s_elbow' % side
            if n_sh not in hby or n_el not in hby:
                say('arm %s: upper-arm gain skipped (joints missing)' % side)
                continue
            i_sh, i_el = hby[n_sh]['index'], hby[n_el]['index']
            SH = origin_np(rest_sym[i_sh])
            EL = origin_np(rest_sym[i_el])
            u = EL - SH
            L = float(np.linalg.norm(u))
            if L < 1e-9:
                say('arm %s: upper-arm gain skipped (degenerate axis)' % side)
                continue
            u = u / L

            # The displacement actually applied to the elbow-and-below subtree:
            #
            #     delta = u * gain  +  shift
            #
            # `u * gain` is the LENGTH fix (round 49) -- a pure axial stretch of
            # the upper arm.  `shift` (round 66) is a free 3-vector, given for
            # the LEFT arm and MIRRORED IN X for the right (HD2 is +X = the
            # character's right, so the left limb lives at -X).
            #
            # Why a scalar alone is not enough.  Measured on the SHIPPED mesh
            # (`_probe_gainfit.py`), Lilium's hand+finger cloud sits 32.5 mm
            # away from Lunalice's, and that decomposes into
            #     * -31.4 mm ALONG the arm axis  -> what `gain` fixes, and
            #     *   5.4 mm PERPENDICULAR to it -> what `gain` CANNOT fix.
            # The perpendicular part is not a modelling difference: both source
            # rigs are aligned by the SAME recipe (yaw + one uniform scale +
            # translate), and that recipe pins the HEAD HEIGHT, so two models
            # whose shoulder sits differently relative to the skull land on
            # different Y/Z.  A scalar gain would leave those 5.4 mm in place.
            shift = shift_l * np.array(
                [1.0 if side == 'l' else -1.0, 1.0, 1.0], np.float64)
            delta = u * gain + shift

            # (a)+(b) the partition and the displacement ramp.  The partition is
            #     BINARY (by the vertex's PRIMARY bone) and the magnitude comes
            #     from the geometric ramp alone -- the review was explicit that
            #     the stretch must NOT be multiplied by the LBS weights.
            f, up_v, far_v, arm_v = arm_stretch_field(
                src1, verts, src_nm, side, SRC_PREFIX, SH, EL)
            src1 = src1 + delta[None, :] * f[:, None]

            # (c) carry the working pivots: the elbow's WHOLE subtree
            moved = [i_el]
            stack = list(hd2_children.get(i_el, ()))
            while stack:
                c = stack.pop()
                moved.append(c)
                stack.extend(hd2_children.get(c, ()))
            shift4 = trans(delta)
            for i in moved:
                if i in rest_sym:
                    rest_sym[i] = shift4 @ rest_sym[i]

            newL = float(np.linalg.norm((EL + delta) - SH))
            say('arm %s: UPPER ARM moved on the MESH by (%.4f, %.4f, %.4f) m '
                '= %+.4f m along the SOURCE T-pose shoulder->elbow axis '
                '(%.3f, %.3f, %.3f) + shift (%.4f, %.4f, %.4f); upper arm '
                '%.4f -> %.4f m; %d upper-arm + %d distal + %d arm vertices; '
                '%d working pivots below the elbow carried with the mesh; the '
                'shipped rig is untouched'
                % (side, delta[0], delta[1], delta[2], gain, u[0], u[1], u[2],
                   shift[0], shift[1], shift[2], L, newL,
                   int(up_v.sum()), int(far_v.sum()), int(arm_v.sum()),
                   len(moved)))

    # ---- 4. T-pose -> A-pose, then the per-vertex transform ---------------
    basis = {}
    if arm_scratch:
        # OUR OWN shoulder rotation, injected at the `D` layer -- never on
        # vertices.
        #
        # Why the D layer: `D[b] = D[parent] . rest[b] . basis[b] . inv(rest[b])`
        # recurses PARENT-FIRST, so one rotation on `l_shoulder` is inherited by
        # the forearm, the hand and every finger.  The limb therefore turns as a
        # single rigid body and the elbow is welded by construction, while the
        # LBS weights blend it smoothly into the chest on their own.  Rotating
        # vertices instead cannot do that -- it would move the upper-arm shell
        # and leave the forearm shell behind, which is exactly the crack we have
        # been chasing.
        #
        # The pivot is the bone's own head: conjugating by `rest[b]` turns
        # `basis[b]` into a rotation about `rest[b]`'s ORIGIN, i.e. about the
        # SNAPPED shoulder joint -- the source model's own shoulder, as asked.
        for side in ('l', 'r'):
            SD = side.upper()
            s_up, s_fo, s_ha = ('%s_UpperArm' % SD, '%s_Forearm' % SD,
                                '%s_Hand' % SD)
            h_sh, h_el, h_ha = ('%s_shoulder' % side, '%s_elbow' % side,
                                '%s_hand' % side)
            if (s_up not in short or s_fo not in short
                    or h_sh not in hby or h_el not in hby):
                say('arm %s: shoulder/elbow missing -- skipped' % side)
                continue
            p_sh, p_el = joint(s_up), joint(s_fo)
            d_up_src = p_el - p_sh
            d_up_tgt = (origin_np(rest_hd2[hby[h_el]['index']])
                        - origin_np(rest_hd2[hby[h_sh]['index']]))
            if arm_scratch_deg is not None:
                # a plain front-view tilt about world +Y, kept for A/B.
                # +Y is FORWARD and the left arm sits at -X, so "tilt down" is a
                # NEGATIVE angle on the left and a positive one on the right.
                sgn = -1.0 if side == 'l' else 1.0
                R_sh = rot_axis_angle(np.array([0.0, 1.0, 0.0]),
                                      math.radians(sgn * float(arm_scratch_deg)))
                how = 'fixed %.2f deg down about +Y' % float(arm_scratch_deg)
            else:
                R_sh = min_arc_rotation(d_up_src, d_up_tgt)
                how = 'min-arc onto HD2 native %s_shoulder->%s_elbow' % (side, side)
            ang, ax = rot_angle_axis(R_sh)
            # `D[b] = rest[b] . basis[b] . inv(rest[b])`, and with `basis` a PURE
            # rotation `Rb` that expands to `T(ts) . (Rs . Rb . Rs^-1) . T(-ts)`
            # -- a rotation about the bone's head.  So rotating by `R` about a
            # WORLD axis needs `Rb = Rs^-1 . R . Rs`.  Passing the full 4x4
            # `rest` instead cancels the translation and spins the arm about the
            # WORLD ORIGIN, which throws the hand a metre out to the side.
            Rs = rest_sym[hby[h_sh]['index']][:3, :3]
            B4 = np.eye(4)
            B4[:3, :3] = np.linalg.inv(Rs) @ R_sh @ Rs
            basis[h_sh] = B4
            say('arm %s: shoulder %s -> %.2f deg about (%.3f, %.3f, %.3f); pivot = '
                'source shoulder (%.4f, %.4f, %.4f)'
                % (side, how, ang, ax[0], ax[1], ax[2],
                   p_sh[0], p_sh[1], p_sh[2]))

            # ---- the ELBOW: exactly the same idea, one level further out ----
            # The shoulder rotation above is inherited by every descendant, so
            # after it the forearm already points along the UPPER-ARM bone.  What
            # is still wrong is the elbow's own hinge: HD2's forearm does NOT
            # continue the upper arm, it swings FORWARD (top view 46.5 deg
            # against the upper arm's 3.3 deg).
            #
            # `D[el] = D[sh] . rest[el] . basis[el] . inv(rest[el])`, so anything
            # injected here is composed AFTER the shoulder.  The rotation the
            # forearm actually needs is therefore the one that takes the SOURCE
            # forearm direction onto `inv(R_sh) . target` -- if the shoulder had
            # already fixed the forearm this comes out as the identity.
            #
            # `arm_elbow_open` (round 52): the user measured a 6.5 deg residual on
            # the FOREARM in the top view and asked for it to be added on top.
            # It can be, and it is nothing exotic: the axis of `R_el` IS the
            # normal of the (upper-arm, forearm) plane -- `inv(R_sh) . cross(
            # d_up_tgt, d_fo_tgt)` reproduces `rot_angle_axis(R_el)[1]` to
            # machine precision (probe: |dot| = 1.00000, angle 0.000 deg) -- i.e.
            # the elbow's true hinge.  So "open the elbow by X deg" is just
            # "swing the target forearm direction X deg inside that plane",
            # which is a pure re-solve of the same min-arc.  Positive = STRAIGHTER
            # (bend angle drops by exactly that many degrees); negative = more bent.
            if arm_scratch_elbow and s_ha in short and h_ha in hby:
                d_fo_src = joint(s_ha) - p_el
                if arm_aim_wrist:
                    # ---- ROUND 78: the user's rule, verbatim -----------------
                    # "pivot on OUR elbow, and aim OUR elbow -> HD2's wrist."
                    #
                    # The pivot the forearm actually turns about is the SOURCE
                    # elbow AS IT STANDS AT THIS MOMENT: after the leg stance
                    # (`new_head`), after the upper-arm gain (which translated
                    # `rest_sym[l_elbow]` together with its whole subtree) and
                    # after the shoulder rotation above (which carries it as
                    # `S + R_sh . (E - S)`).  Measuring the aim from the T-pose
                    # elbow instead would be wrong by the whole ~9.6 cm the
                    # shoulder move puts between the two.
                    #
                    # This is also EXACTLY the user's "project HD2's wrist onto
                    # the plane normal to the arm axis, then swing inside the
                    # plane that line and the arm axis span".  The rotation that
                    # carries `d_fo_src` onto `d_fo_aim` has axis
                    # `cross(d_fo_src, d_fo_aim)`; the source T-pose arm is
                    # straight, so `d_fo_src` is parallel to `d_up_src` and that
                    # axis is perpendicular to the upper-arm axis -- i.e. it IS
                    # the elbow's own hinge, and the swing stays inside the one
                    # plane that contains both the upper arm and HD2's wrist.
                    # Projecting first gives the identical axis: it is a way of
                    # SEEING the construction, not a second processing step.
                    S_s = origin_np(rest_sym[hby[h_sh]['index']])
                    E_g = origin_np(rest_sym[hby[h_el]['index']])
                    W_h = origin_np(rest_hd2[hby[h_ha]['index']])
                    E_now = S_s + R_sh @ (E_g - S_s)
                    d_fo_tgt = W_h - E_now
                else:
                    d_fo_tgt = (origin_np(rest_hd2[hby[h_ha]['index']])
                                - origin_np(rest_hd2[hby[h_el]['index']]))
                if abs(arm_elbow_open) > 1e-9:
                    n_w = np.cross(d_up_tgt, d_fo_tgt)
                    nn = float(np.linalg.norm(n_w))
                    if nn > 1e-9:
                        d_fo_tgt = rot_axis_angle(
                            n_w / nn, -math.radians(float(arm_elbow_open))) @ d_fo_tgt
                R_el = min_arc_rotation(d_fo_src, R_sh.T @ d_fo_tgt)
                ae, axe = rot_angle_axis(R_el)
                Rse = rest_sym[hby[h_el]['index']][:3, :3]
                B4e = np.eye(4)
                B4e[:3, :3] = np.linalg.inv(Rse) @ R_el @ Rse
                basis[h_el] = B4e
                if os.environ.get('HD2_ARM_AIM_DUMP'):
                    _W_h = origin_np(rest_hd2[hby[h_ha]['index']])
                    _E_s2 = (origin_np(rest_sym[hby[h_sh]['index']])
                             + R_sh @ (origin_np(rest_sym[hby[h_el]['index']])
                                       - origin_np(rest_sym[hby[h_sh]['index']])))
                    _wr = _E_s2 + d_fo_tgt
                    say('   [aim-dump %s] our elbow  %s' % (side, np.array2string(_E_s2, precision=4)))
                    say('   [aim-dump %s] our wrist  %s   HD2 wrist %s   |delta| %.1f mm'
                        % (side, np.array2string(_wr, precision=4),
                           np.array2string(_W_h, precision=4),
                           1000.0 * float(np.linalg.norm(_wr - _W_h))))
                    say('   [aim-dump %s] our wrist from T-pose elbow %s'
                        % (side, np.array2string(p_el + d_fo_tgt, precision=4)))
                if arm_aim_wrist:
                    _d_hd2 = (origin_np(rest_hd2[hby[h_ha]['index']])
                              - origin_np(rest_hd2[hby[h_el]['index']]))
                    _cos = float(d_fo_tgt @ _d_hd2) / max(
                        float(np.linalg.norm(d_fo_tgt))
                        * float(np.linalg.norm(_d_hd2)), 1e-12)
                    _off = float(np.degrees(math.acos(max(-1.0, min(1.0, _cos)))))
                    _how = ('min-arc onto OUR elbow->HD2 %s_hand  [aim = %.1f mm '
                            'from our elbow, %.2f deg off HD2\'s own direction]'
                            % (side, 1000.0 * float(np.linalg.norm(d_fo_tgt)), _off))
                    _pv = E_now
                else:
                    _how = 'min-arc onto HD2 native %s_elbow->%s_hand' % (side, side)
                    _pv = p_el
                say('arm %s: elbow    %s '
                    '%s -> %.2f deg about (%.3f, %.3f, %.3f); pivot = source elbow '
                    '(%.4f, %.4f, %.4f); hand/fingers follow through D'
                    % (side, _how,
                       ('[elbow opened %+.2f deg]' % arm_elbow_open)
                       if abs(arm_elbow_open) > 1e-9 else '',
                       ae, axe[0], axe[1], axe[2],
                       _pv[0], _pv[1], _pv[2]))

            # ---- the HAND: a plain world-axis tilt about the wrist ----------
            # Round 53.  The user's model of the arm: in the T-pose it is a
            # steel pipe whose AXIS is z, and the thumb -- spread -- always points
            # along the pipe's SURFACE NORMAL, which is x; y is whatever is left.
            # Measured straight off the source T-pose (`_probe_hand_tpose.py`),
            # and it comes out exactly on the world axes:
            #     z  elbow->wrist = (-1, 0, 0)  = -X      (the pipe)
            #     x  thumb        = ( 0, 1, 0)  = +Y      <- the rotation axis
            #     y  z x x        = ( 0, 0,-1)  = -Z      (left; +Z on the right)
            # Both thumbs point +Y, i.e. both palms are pronated (face DOWN), so
            # the BACK of the hand is +Z on the left and -Z on the right.  Tilting
            # the hand's long axis toward the back of the hand -- "手掌翘起",
            # wrist EXTENSION, the reverse of the way the fingers curl -- is
            # therefore +arm_hand_tilt about +Y on the left and the NEGATIVE of it
            # on the right: one mirrored number, exactly like the shoulder.
            #
            # No min-arc here, no plane, no fitting: the axis the user named IS a
            # world axis in the frame we are told to work in.  Injected at
            # `l_hand`, so it lands AFTER the shoulder and the elbow in the D
            # recursion and the fingers ride along rigidly.
            if abs(arm_hand_tilt) > 1e-9 and h_ha in hby:
                sgn_h = 1.0 if side == 'l' else -1.0
                R_ha = rot_axis_angle(np.array([0.0, 1.0, 0.0]),
                                      math.radians(sgn_h * float(arm_hand_tilt)))
                Rsha = rest_sym[hby[h_ha]['index']][:3, :3]
                B4h = np.eye(4)
                B4h[:3, :3] = np.linalg.inv(Rsha) @ R_ha @ Rsha
                basis[h_ha] = B4h
                p_wr = origin_np(rest_sym[hby[h_ha]['index']])
                say('arm %s: hand     tilt %+.2f deg about world +Y (the T-pose '
                    'thumb = the pipe surface normal) at the wrist; pivot = '
                    '(%.4f, %.4f, %.4f); fingers follow through D'
                    % (side, sgn_h * float(arm_hand_tilt),
                       p_wr[0], p_wr[1], p_wr[2]))
    elif apose:
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

    # ---- 4b. the rig we actually SHIP: SYMMETRIC axes -> ORIGINAL axes -----
    # `S_A = D . rest_sym` is the new rest produced by baking the A-pose, and it
    # still carries the SYMMETRIC axes (each local rotation has Dd post-multiplied
    # in).  The BatchTool's very last step converts them back, which is a pure
    # re-parameterisation: each bone's own frame is rotated by Dd^-1 and its HEAD
    # is left exactly where it was (convert_body_axis_space sets
    # Matrix.LocRotScale(bone.head_local, new_rot, 1) and raises if the head drifts
    # by more than 1e-5).  After this the rig's orientations are HD2's native ones
    # -- so the game's own animations drive it correctly -- while the joint
    # POSITIONS are still the retargeted ones.
    rest_shipped = {}
    if native:
        # Ship HD2's OWN rest, verbatim.  Nothing to compute: the runtime's idle
        # deform `rest_hd2[b] . inv(S_bind[b])` is then the exact identity, so the
        # mesh the player sees is the mesh we posed here, joint for joint.
        rest_shipped = {i: rest_hd2[i].copy() for i in rest_hd2}
    else:
        for h in order:
            i = h['index']
            p = parent_of(h)
            sa = D[i] @ rest_sym[i]
            L = sa if p is None else (
                np.linalg.inv(D[p['index']] @ rest_sym[p['index']]) @ sa)
            if axis_roundtrip:
                L = L @ np.linalg.inv(axis_delta(h))
            chain = L if p is None else rest_shipped[p['index']] @ L
            rest_shipped[i] = trans(sa[:3, 3] - origin_np(chain)) @ chain

    # ---- skinning: source bone -> HD2 bone -------------------------------

    resolved, hops = {}, {}
    by_dist = []
    # A bone with no counterpart is bound to a mapped bone whose joint sits CLOSEST
    # to it -- not to its parent.  The source file hangs the shoes off `L_Calf`
    # (`source_extra_62/63`), so a parent-chain fallback binds the shoes to the KNEE
    # and, because our rest knee sits 13.4 cm above HD2's, they sink that far below
    # the floor the moment the leg is posed.  It is invisible in the bind pose --
    # which is exactly why the bind-pose metric looked perfect.
    #
    # `region` keeps that, but confines the search to the subtree of the nearest
    # mapped ancestor.  Plain `distance` also handed the hair/ribbon bones
    # (`source_extra_73/76/85/71/69`, ~700 verts, ancestors = `Bip01_Head1`) to the
    # shoulders, so the A-pose bake swung them down and inward with the arms.
    def ajoint(b):
        return align_pts(origin_np(m_to_np(b['world_m'])).reshape(1, 3))[0] + t
    resolved, hops, by_dist = resolve_bone_map(src, src_index_to_hd2, fallback,
                                               ajoint, by_idx_name, hd2_children, hd2_parents)
    if by_dist:
        say('%s fallback for %d bones, e.g.: %s'
            % (fallback, len(by_dist), ', '.join('%s->%s(%.3fm)' % r for r in
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

    # ---- 4e. MESH-ONLY arm re-fit -----------------------------------------
    # The shipped rig is HD2's own, but the mesh was baked about `rest_sym`, whose
    # heads are the SOURCE's joints.  Where a source joint sits away from HD2's --
    # here the shoulder is ~9 cm inboard and ~9 cm high measured along the arm
    # axis, and the elbow ~9.6 cm inboard -- the mesh's arm lands inboard of the
    # bones and the limb reads as "bowed" in the front view, even though every
    # segment's LENGTH is right (probe_arm_seg.py: our elbow boundary sits at
    # t=0.197 along HD2's shoulder->hand axis, HD2's elbow bone at t=0.286).
    #
    # Displacing each vertex by the weight-blended joint residual
    # (`shipped head - working head`, and ZERO for every non-arm bone) puts each
    # arm joint back onto HD2's bone and ramps smoothly through the shoulder, so
    # the torso stays exactly where it was and nothing tears.  This is the standard
    # translation retarget, and it is derived from the pipeline's own data -- no
    # per-model constants.
    #
    # It runs BEFORE the mesh-only shift/scale (4c/4d) so the residual is applied
    # in the same space the two rests are expressed in.
    #
    # Round 77 (`arm_refit_scratch`): allow this pass to run on the `arm_scratch`
    # path too.  Under `arm_scratch` the arm is posed by rotating the SOURCE
    # skeleton about the SOURCE joints, so the mesh's arm root still sits where
    # the source's shoulder/elbow are -- measured on MourningVeil, the elbow ends
    # up 112.9 mm from HD2's elbow (-97.1 mm along the HD2 upper arm, +57.7 mm
    # perpendicular) and only the hand-set `arm_scratch_upper_gain` (5 cm) is
    # compensating.  This flag is DEFAULT OFF, so the shipped byte stream is
    # unchanged unless it is asked for.  Probe: `_probe_arm_aim_geometry.py`.
    if arm_fit != 0.0 and (not arm_scratch or arm_refit_scratch):
        Z = np.array([0.0, 0.0, 1.0])
        T = np.asarray([t[:3] for t in tris], dtype=np.int64)
        e = np.concatenate([T[:, [0, 1]], T[:, [1, 2]], T[:, [2, 0]]])
        a_src = np.concatenate([e[:, 0], e[:, 1]])
        a_dst = np.concatenate([e[:, 1], e[:, 0]])
        deg = np.bincount(a_src, minlength=len(out)).astype(np.float64)
        deg[deg == 0] = 1.0
        inv_deg = (1.0 / deg)[:, None]
        delta = np.zeros_like(out)
        nbone = 0
        _dump = os.environ.get('HD2_ARM_REFIT_DUMP')
        _rows = []
        for side in ('l', 'r'):
            # the axis the user asked to "slide the forearm out along"
            a = rest_shipped.get(next(h['index'] for h in hd2
                                      if h['name'] == '%s_shoulder' % side))
            b = rest_shipped.get(next(h['index'] for h in hd2
                                      if h['name'] == '%s_hand' % side))
            if a is None or b is None:
                continue
            p0 = origin_np(a)
            u = origin_np(b) - p0
            u = u / max(float(np.linalg.norm(u)), 1e-9)
            # The residual is split into three orthogonal parts and each is
            # dialled separately, because they are NOT equally trustworthy:
            #   along u  -- the source's shoulder joint really is inboard of
            #               HD2's, so this part is genuinely missing.  Kept 1:1.
            #   w_up     -- the vertical part.  The mesh's forearm/palm sit a few
            #               cm too HIGH relative to the bones (probe_arm_dir.py:
            #               d_up +3.3 cm vs the reference's +0.8 cm), and this is
            #               the only term that fixes it.
            #   w_fwd    -- the fore/aft part.  `mesh_shift` was already fitted
            #               against the reference, so this DOUBLE-COUNTS it: at
            #               full strength the arm lands 12 cm behind the
            #               reference's (d_fwd -11.6 cm vs +1.1 cm).  Default 0.
            w_up = Z - u * float(u @ Z)
            w_up = w_up / max(float(np.linalg.norm(w_up)), 1e-9)
            w_fwd = np.cross(u, w_up)
            dvec = {}
            for h in hd2:
                if not is_arm_bone(h['name']) or not h['name'].startswith(side + '_'):
                    continue
                i = h['index']
                if i not in rest_sym or i not in rest_shipped:
                    continue
                d = origin_np(rest_shipped[i]) - origin_np(D[i] @ rest_sym[i])
                along = float(d @ u)
                perp = d - u * along
                dvec[i] = (u * along + w_up * (arm_up * float(perp @ w_up)) +
                           w_fwd * (arm_perp * float(perp @ w_fwd)))
                if _dump:
                    _rows.append(dict(
                        side=side, name=h['name'],
                        working=[float(x) for x in origin_np(D[i] @ rest_sym[i])],
                        shipped=[float(x) for x in origin_np(rest_shipped[i])],
                        rest_sym=[float(x) for x in origin_np(rest_sym[i])],
                        d=[float(x) for x in d], d_mm=1000.0 * float(np.linalg.norm(d)),
                        along_mm=1000.0 * along,
                        perp_mm=1000.0 * float(np.linalg.norm(perp)),
                        dvec=[float(x) for x in dvec[i]],
                        dvec_mm=1000.0 * float(np.linalg.norm(dvec[i]))))
            if not dvec:
                continue
            nbone += len(dvec)
            d_side = np.zeros_like(out)
            for k, v in enumerate(verts):
                acc = np.zeros(3)
                for j in range(3):
                    w = v['w'][j]
                    if w <= 0:
                        continue
                    d = dvec.get(resolved.get(v['bones'][j]))
                    if d is not None:
                        acc += w * d
                d_side[k] = acc
            # The raw field follows the LBS weights, and at the chest/clavicle
            # boundary those change fast: a vertex on `chest` gets 0 while its
            # neighbour on `l_clavicle` gets 0.115 m, so a 1 cm edge is stretched
            # ~11x (probe_arm_stretch.py).  A few Laplacian passes over the mesh
            # graph spread that jump over centimetres instead of one edge.
            for _ in range(int(arm_smooth)):
                acc = np.zeros_like(d_side)
                np.add.at(acc, a_src, d_side[a_dst])
                d_side = 0.5 * d_side + 0.5 * (acc * inv_deg)
            # TAPER, by position along THIS side's arm axis.  The arm-weighted
            # cloud reaches 8.5 cm INBOARD of the shoulder joint (t_lo -0.085
            # against the reference's +0.036) -- that is chest/armpit geometry
            # that the source's weights hand to `l_shoulder`.  Displacing it by
            # 18 cm slides it out of the torso and opens a real gap
            # (probe_arm_disp.py: the red >3 cm region reaches into the chest).
            # Ramps the displacement in from 0 at t0 to full at t1, so the
            # junction stays welded and only the arm itself slides out.
            if arm_taper is not None:
                t0, t1 = arm_taper
                tt = np.clip(((out - p0) @ u - t0) / max(t1 - t0, 1e-9), 0.0, 1.0)
                d_side *= (tt * tt * (3.0 - 2.0 * tt))[:, None]
            delta += d_side
        if _dump and _rows:
            _hdr = dict(
                scale=float(s), t=[float(x) for x in t],
                sole=[float(x) for x in sole],
                R=[[float(x) for x in row] for row in np.asarray(R, float)],
                head_hd2=[float(x) for x in head_hd2],
                head_src_aligned=[float(x) for x in align_pts(head_src_v.reshape(1, 3))[0]],
                new_head={k: [float(x) for x in v] for k, v in new_head.items()},
                rest_shipped={by_idx_name.get(i, str(i)): [float(x) for x in origin_np(rest_shipped[i])]
                              for i in rest_shipped},
                arm_up=float(arm_up), arm_perp=float(arm_perp),
                arm_taper=None if arm_taper is None else [float(x) for x in arm_taper],
                arm_smooth=int(arm_smooth), arm_fit=float(arm_fit))
            json.dump(dict(fit=_hdr, bones=_rows), open(_dump, 'w', encoding='utf-8'),
                      indent=1)
            say('arm re-fit dump -> %s (%d bones)' % (_dump, len(_rows)))
        if nbone:
            if arm_fit != 1.0:
                delta *= arm_fit
            out = out + delta
            say('arm re-fit: weight-blended joint residual on the MESH only '
                '(x%.2f, %d bones, %d smoothing passes, along 1.00 / up %.2f / '
                'fwd %.2f, taper %s) -- max |d| %.4f m, mean |d| %.4f m'
                % (arm_fit, nbone, int(arm_smooth), arm_up, arm_perp,
                   ('off' if arm_taper is None else '%.3f..%.3f' % arm_taper),
                   float(np.linalg.norm(delta, axis=1).max()),
                   float(np.linalg.norm(delta, axis=1).mean())))

    # ---- 4e-2. MESH-ONLY arm ORIENTATION correction (front X-Z plane) ------
    # A translation cannot fix a DIRECTION error, and that is what is left.  The
    # mesh's arm chain is rotated relative to HD2's bones:
    #   upper arm  39.4 deg below horizontal   (bone 29.9, reference mesh 28.5)
    #   forearm    17.9 deg                    (bone 29.6, reference mesh 24.9)
    #   hand       57.1 deg                    (bone 43.3, reference mesh 49.3)
    # so the front view shows a 21.5 deg kink at the elbow where the reference
    # shows 3.6 deg -- "the forearm is still bent" -- and the hand hangs far too
    # steeply.  The TOP view (X-Y) already matches (13.7/41.6 vs 13.6/39.4 deg),
    # so the correction is a rotation about the FORE/AFT (world Y) axis.
    #
    # The pivot is the ORIGINAL bone origin of each joint, and the rotation is a
    # RIGID one -- that is the whole point of this block (round 45d):
    #   * segment 0 (upper arm) is rotated about the shoulder origin,
    #   * segment 1 (forearm) is CARRIED by the shoulder rotation and then bent
    #     about the elbow origin, i.e. T1 = R(th0,J0) o R(th1,J1),
    #   * segment 2 (hand) is T2 = T1 o R(th2,J2).
    # Composing rotations about parallel axes gives one rotation about one pivot,
    # so each segment has an exact (angle, pivot) pair.  A vertex then gets the
    # weighted average of the ANGLES and of the PIVOTS -- still a rigid rotation,
    # so the surface cannot shear.
    #
    # Round 45c did the opposite and that was the bug the user found ("the
    # forearm is twisted into a weird shape"): it moved each vertex by
    # g0*(R0 v - v) + g1*(R1 v - v) + g2*(R2 v - v), i.e. it averaged the moved
    # POINTS of three rotations about three DIFFERENT pivots.  The linear part of
    # that is (g0 R0 + g1 R1 + g2 R2), which is not orthogonal: for a 17 deg
    # spread it shrinks by cos(8.6) = 0.989 and shears by sin(8.6) = 0.15.  That
    # is the "candy wrapper" spiral on the forearm, the torn tiger mouth and the
    # detached wristband.  `arm_rot_mode='blend'` keeps the old behaviour for A/B.
    if arm_fit != 0.0 and arm_rot is not None and any(arm_rot) and not arm_scratch:
        idx = {h['name']: h['index'] for h in hd2}

        def group(nm, side):
            """0 shoulder / 1 elbow / 2 wrist: which joint this bone rides.

            `None` for every bone that is not part of this arm, which is what
            keeps the legs out of the rotation -- see the note below."""
            if not is_arm_bone(nm) or not nm.startswith(side + '_'):
                return None
            tail = nm[len(side) + 1:]
            if 'finger' in tail or tail.startswith('hand'):
                return 2
            if tail.startswith('elbow'):
                return 1
            return 0

        for side in ('l', 'r'):
            nms = ('%s_shoulder' % side, '%s_elbow' % side, '%s_hand' % side)
            if any(n not in idx for n in nms):
                continue
            J = [origin_np(rest_shipped[idx[n]]) for n in nms]
            sgn = 1.0 if side == 'l' else -1.0
            th = [np.radians(sgn * float(arm_rot[k])) for k in range(3)]

            # WHICH VERTICES MAY ROTATE.  A positional ramp alone is NOT enough
            # here, and that is not a detail: `t` is the projection onto the
            # shoulder->hand axis, which points DOWNWARD, so the legs project
            # onto it too (a foot sits at t = +0.6, i.e. "past the wrist") and a
            # plain ramp rotated the whole lower body ~19 deg about the shoulder
            # -- the first version of this block dropped the mesh 19 cm and
            # stretched edges 14x.  The source's own LBS weights already say
            # which bones a vertex belongs to, so use them: sum the weights of
            # the bones of each segment.  The blend across the elbow/wrist then
            # comes from the weights themselves, and the torso/legs get zero.
            gidx = {}
            for h in hd2:
                gg = group(h['name'], side)
                if gg is not None:
                    gidx[h['index']] = gg
            a = [np.zeros(len(out)) for _ in range(3)]
            for j, v in enumerate(verts):
                s = [0.0, 0.0, 0.0]
                for q in range(3):
                    wq = v['w'][q]
                    if wq <= 0:
                        continue
                    gg = gidx.get(resolved.get(v['bones'][q], -1))
                    if gg is not None:
                        s[gg] += wq
                for k in range(3):
                    a[k][j] = s[k]

            # The weights jump across a single edge at the elbow and the wrist
            # (probe_arm_stretch.py: that is how LBS itself is built), so spread
            # the blend over a few edges first, exactly like `arm_smooth` does
            # for the residual in 4e.
            if arm_rot_mode != 'axis' and arm_rot_smooth > 0:
                T2 = np.asarray([q[:3] for q in tris], dtype=np.int64)
                e2 = np.concatenate([T2[:, [0, 1]], T2[:, [1, 2]], T2[:, [2, 0]]])
                s2 = np.concatenate([e2[:, 0], e2[:, 1]])
                d2 = np.concatenate([e2[:, 1], e2[:, 0]])
                dg = np.bincount(s2, minlength=len(out)).astype(np.float64)
                dg[dg == 0] = 1.0
                for _ in range(int(arm_rot_smooth)):
                    for k in range(3):
                        acc = np.zeros(len(out))
                        np.add.at(acc, s2, a[k][d2])
                        a[k] = 0.5 * a[k] + 0.5 * (acc / dg)

            # g[k] = how much of rotation k a vertex takes.
            #
            # Two ways to build it, and the difference is the whole round-45d
            # story.  `weights` uses the source's own LBS weights (round 45c);
            # `axis` uses the POSITION along the arm axis instead.
            #
            # The source model is NOT one surface: it is a stack of overlapping
            # shells (bare arm, sleeve, wrist cuff, elbow frill, ...) and each
            # shell carries its OWN skin weights.  probe: a2 (the hand weight)
            # along the arm reads 0.02 -> 0.16 -> 0.39 -> 0.67 (t=0.42) -> 0.43
            # (t=0.52) -> 1.00, i.e. it is NOT monotonic -- the cuff at t~0.5 is
            # weighted mostly to the ELBOW while the sleeve 10 cm away is
            # weighted to the HAND.  Rotating by those weights therefore moves
            # the shells by different amounts and they come apart: that is the
            # "crack along the forearm", the torn tiger mouth and the detached
            # wristband the user sees.
            #
            # A ramp along the AXIS is the same for every shell at the same
            # place, so the shells move together; the arm MASK keeps the legs and
            # torso out (a plain ramp rotated the whole lower body ~19 deg in the
            # first version of this block -- a foot projects to t = +0.6, "past
            # the wrist").  This is exactly "rotate the segment about its own
            # bone origin, and let the twist/stretch transition run along the
            # bone", which is how the user (a machinist/3D modeller) describes
            # the operation.
            if arm_rot_mode in ('axis', 'rigid'):
                W = a[0] + a[1] + a[2]
                if arm_rot_mode == 'rigid':
                    # Round 45e.  TWO things were still bending the forearm, and
                    # both live in the mask / the ramp -- not in the angles:
                    #
                    # (1) `W`, the sum of a vertex's arm-bone LBS weights, is
                    #     NOT monotonic along the arm, because the model is a
                    #     stack of shells and each shell carries its own weights
                    #     (probe: the hand weight reads 0.02 -> 0.67 -> 0.43 ->
                    #     1.00).  Feeding that through smoothstep keeps CHANGING
                    #     the rotation even after the ramp has reached 1, so the
                    #     "segment" is never rigid.  A BINARY mask only answers
                    #     "is this vertex part of the arm at all", which is the
                    #     only question it should answer.
                    # (2) A few rings of dilation (`HD2_ARM_MASK_GROW`) cover the
                    #     joint transition band, so the binary edge cannot tear.
                    m = W > float(os.environ.get("HD2_ARM_MASK", "0.5"))
                    grow = int(os.environ.get("HD2_ARM_MASK_GROW", "2"))
                    if grow > 0:
                        T2 = np.asarray([q[:3] for q in tris], dtype=np.int64)
                        e2 = np.concatenate([T2[:, [0, 1]], T2[:, [1, 2]],
                                             T2[:, [2, 0]]])
                        s2 = np.concatenate([e2[:, 0], e2[:, 1]])
                        d2 = np.concatenate([e2[:, 1], e2[:, 0]])
                        for _ in range(grow):
                            hit = np.zeros(len(out), dtype=bool)
                            np.logical_or.at(hit, s2, m[d2])
                            m = m | hit
                    mask = m.astype(np.float64)
                else:
                    mm = np.clip(W / float(os.environ.get("HD2_ARM_MASK", "0.5")),
                                 0.0, 1.0)
                    mask = mm * mm * (3.0 - 2.0 * mm)
                u = np.asarray(J[2], float) - np.asarray(J[0], float)
                u = u / max(float(np.linalg.norm(u)), 1e-9)
                tt = (out - np.asarray(J[0], float)) @ u
                # `rigid` uses a 1.5 cm joint transition, `axis` the old 18 cm
                # one.  18 cm is 65% of a 27 cm forearm, so the "segment" was
                # continuously bent along its WHOLE length instead of turning as
                # a rigid body -- that is the S-shaped forearm.  With a 1.5 cm
                # band the rotation is constant in the middle of every segment
                # (a true rigid body) and only the joint neighbourhood blends.
                if arm_rot_mode == 'rigid':
                    band = float(os.environ.get('HD2_ARM_GAP', '0.015'))
                else:
                    band = float(os.environ.get('HD2_ARM_BAND', '0.090'))

                def ramp(c):
                    x = np.clip((tt - (c - band)) / (2.0 * band), 0.0, 1.0)
                    return x * x * (3.0 - 2.0 * x)

                te = float((np.asarray(J[1], float) - np.asarray(J[0], float)) @ u)
                tw = float((np.asarray(J[2], float) - np.asarray(J[0], float)) @ u)
                # The SHOULDER rotation needs the same ramp as the others, and
                # that is not cosmetic: the mask alone lets the trapezius / base
                # of the neck (t = -0.24 along the arm axis) keep a little arm
                # weight, and 10 deg about the shoulder is a 24 mm move out
                # there -- it tore the worst edge in the whole mesh (ratio 5.65
                # on a 4 mm edge).  With the ramp the inboard side gets exactly
                # zero and the deltoid fades in.
                g = [mask * ramp(0.0), mask * ramp(te), mask * ramp(tw)]
            else:
                g = [a[0] + a[1] + a[2], a[1] + a[2], a[2]]

            if arm_rot_mode == 'blend':
                # --- round 45c behaviour, kept for A/B only ------------------
                # ROOT -> TIP, moving each child joint along with its parent.
                Jm = [np.asarray(q, float).copy() for q in J]
                for k in (0, 1, 2):
                    if float(arm_rot[k]) == 0.0:
                        continue
                    c, s = np.cos(th[k]), np.sin(th[k])
                    v = out - Jm[k]
                    rp = np.stack([v[:, 0] * c + v[:, 2] * s, v[:, 1],
                                   -v[:, 0] * s + v[:, 2] * c], 1)
                    out = out + g[k][:, None] * (rp - v)
                    for m in (k + 1, k + 2):
                        if m > 2:
                            continue
                        vm = Jm[m] - Jm[k]
                        Jm[m] = Jm[k] + np.array([vm[0] * c + vm[2] * s, vm[1],
                                                  -vm[0] * s + vm[2] * c])
                continue

            # --- nested rotations, applied INNERMOST (wrist) FIRST -----------
            # The arm chain is  T = R(th0,J0) o R(th1,J1) o R(th2,J2),  i.e. the
            # hand is rotated about the wrist, that assembly about the elbow, and
            # the whole thing about the shoulder.  Applying the INNERMOST first
            # lets every level use its ORIGINAL bone origin J[k] -- no composed
            # pivot is needed, and the composition comes out exactly right.
            #
            # Round 45c did this ROOT -> TIP and moved the child joints along the
            # way, which is algebraically the same T but applies THREE rotations
            # to a vertex that sits between two joints, e.g. the elbow band gets
            # g0*(R0 v - v) + g1*(R1 v - v) + ...  Averaging the moved POINTS of
            # two DIFFERENT rotations is not a rotation: the linear part
            # (g0 R0 + g1 R1) shrinks by cos(dth/2) and shears by sin(dth/2)
            # (17 deg -> 1.1% shrink, 15% shear).  That is the "candy wrapper"
            # spiral on the forearm, the torn tiger mouth and the detached
            # wristband the user found.  Here each level applies ONE rotation
            # with a smooth scalar weight, so all it can do is pinch.
            angle_space = (arm_rot_mode == 'rigid')
            for k in (2, 1, 0):
                if float(arm_rot[k]) == 0.0:
                    continue
                v = out - np.asarray(J[k], float)
                if angle_space:
                    # RIGID for ANY g: rotate by g*theta, i.e. exp(g*theta*A).
                    # `out + g*(R v - v)` mixes the IDENTITY and R with weights
                    # (1-g, g); that linear map is not orthogonal -- it pinches
                    # by cos((1-g)*theta/2) and shears by sin((1-g)*theta/2) --
                    # which is a residual twist on top of the bend.  Scaling the
                    # ANGLE instead keeps every step a pure rotation, so the
                    # composition of the three levels is rigid too.
                    ang = g[k] * th[k]
                    ca, sa = np.cos(ang), np.sin(ang)
                    rp = np.stack([v[:, 0] * ca + v[:, 2] * sa, v[:, 1],
                                   -v[:, 0] * sa + v[:, 2] * ca], 1)
                    out = np.asarray(J[k], float) + rp
                else:
                    c, s = np.cos(th[k]), np.sin(th[k])
                    rp = np.stack([v[:, 0] * c + v[:, 2] * s, v[:, 1],
                                   -v[:, 0] * s + v[:, 2] * c], 1)
                    out = out + g[k][:, None] * (rp - v)

        say('arm orientation: shoulder %+.1f / elbow %+.1f / wrist %+.1f deg about '
            'the world Y axis, MESH only, pivots = the original bone origins, '
            'nested innermost-first, mode %s (mask %s, band %.4f m)'
            % (arm_rot[0], arm_rot[1], arm_rot[2], arm_rot_mode,
               'binary' if arm_rot_mode == 'rigid' else
               ('axis ramp' if arm_rot_mode == 'axis' else 'LBS weights'),
               band if arm_rot_mode in ('axis', 'rigid') else 0.0))

    # ---- 4c. MESH-ONLY translation ----------------------------------------
    # The rig must not move -- `S_bind == rest_hd2` is the whole reason the idle
    # frame is the identity -- so "put the character somewhere else" can only
    # mean "move the mesh".  This is exactly what the reference patch does: its
    # skeleton is bit-identical to HD2's and its mesh simply sits where it looks
    # right.  `out_idle` is derived from `out`, so it follows automatically.
    if any(mesh_shift):
        out = out + np.asarray(mesh_shift, dtype=np.float64)
        say('mesh shift: (%+.4f, %+.4f, %+.4f) m on the MESH only; the rig stays '
            'where HD2 has it' % tuple(mesh_shift))

    # ---- 4d. MESH-ONLY upper-body scale -----------------------------------
    # Same reasoning as 4c: the rig must not move, so "make the torso the right
    # size" can only mean "resize the mesh".  That is what the reference patch
    # does -- its skeleton is bit-identical to HD2's while its upper garment is
    # modelled bigger.  Measured over the waist..chest band (probe_torso3.py):
    #   width  ref/ours = 1.14     depth ref/ours = 1.10
    # so a roughly uniform scale about the hips is what closes the gap.  The
    # bottom edge is ramped with a smoothstep so the hip/thigh seam does not
    # snap; round 3 replaces that ramp with a proper feather.
    if upper_scale != 1.0:
        hips = next(h for h in hd2 if h['name'] == 'hips')
        piv = rest_hd2[hips['index']][:3, 3].astype(np.float64).copy()
        if upper_pivot_z is not None:
            piv[2] = float(upper_pivot_z)
        z0 = piv[2] - upper_blend
        z1 = piv[2] + upper_blend
        tt = np.clip((out[:, 2] - z0) / max(z1 - z0, 1e-9), 0.0, 1.0)
        w = tt * tt * (3.0 - 2.0 * tt)                      # smoothstep
        s = (1.0 + (upper_scale - 1.0) * w)[:, None]
        out = piv + (out - piv) * s
        say('upper-body scale: x%.4f about the hips (z %.4f, y %+.4f), smoothstep '
            'over +/- %.3f m on the MESH only' % (upper_scale, piv[2], piv[1],
                                                  upper_blend))

    # ---- 4e. MESH-ONLY head scale -----------------------------------------
    # The rig stays, as always.  The source head is simply too small: its top
    # sits 0.2876 m above HD2's `head` joint while the reference's sits
    # 0.3677 m above it.  Scale about the neck/head seam -- halfway between the
    # two joints -- and scale UNIFORMLY, so the skull does not go pointy.  The
    # long hair is weighted to `head` too, so it grows with the skull for free.
    # Round 51.
    if abs(head_scale - 1.0) > 1e-9 and 'head' in hby and 'neck' in hby:
        sub = set()
        st = [hby['head']['index']]
        while st:
            c = st.pop()
            if c in sub:
                continue
            sub.add(c)
            st.extend(hd2_children.get(c, ()))
        piv = 0.5 * (np.asarray(origin_np(rest_shipped[hby['neck']['index']]), float)
                     + np.asarray(origin_np(rest_shipped[hby['head']['index']]), float))
        sel = np.zeros(len(out), bool)
        for k, v in enumerate(verts):
            best, bw = -1, -1.0
            for q in range(3):
                if v['w'][q] > bw:
                    best, bw = int(v['bones'][q]), v['w'][q]
            if best >= 0 and resolved.get(best) in sub:
                sel[k] = True
        out[sel] = piv[None, :] + (out[sel] - piv[None, :]) * head_scale
        say('head scale: x%.4f about the neck/head seam (z %+.4f) on the MESH '
            'only; %d vertices = skull + everything hanging off it'
            % (head_scale, piv[2], int(sel.sum())))

    # ---- 4f. MESH-ONLY finger residual pose --------------------------------
    # Round 54, expert-reviewed.  The source model's hand is a FLAT T-pose (its
    # ten chains measure 0.00 deg of bend) while HD2's native hand is a relaxed
    # A-pose (first joint 21-28 deg).  The pipeline carries the hand as a rigid
    # extension of the forearm, so the fingers inherit the flat pose and land
    # ~45 deg off HD2's phalanges -- and, worse, HD2's distal finger joints sit
    # 35-48 mm OUTSIDE our fingers, so the game would hinge them about thin air.
    #
    # The expert's fix (§10.4-10.5) is a finger-only residual pose on the FINAL
    # mesh: rotate each chain onto HD2's own bone directions, one phalanx at a
    # time, root to tip, using the frozen input joints as pivots.  Because it
    # runs after 4c/4d/4e and only touches bones that hang off the fingers, the
    # already-accepted palm, wrist and roots stay bit-identical.
    #
    # `arm_finger_pose` is off by default; `arm_finger_pose` may name a subset
    # ('middle', 'l_index', ...) so the one-finger acceptance test can run first.
    if arm_finger_pose:
        shift_v = np.asarray(mesh_shift, dtype=np.float64)

        def post(Q):
            """Map points from the pre-4c mesh space to the final `out` space.

            The pipeline moved the MESH (never the rig) in 4c/4d, so the finger
            pivots have to go through exactly the same two steps.  Using the
            raw `rest_sym` here would put every pivot in the wrong space -- the
            expert flagged this explicitly.  The head scale (4e) only touches
            the skull subtree, so the fingers never see it.
            """
            Q = np.asarray(Q, float).reshape(-1, 3).copy()
            if any(mesh_shift):
                Q = Q + shift_v
            if upper_scale != 1.0:
                hips = next(h for h in hd2 if h['name'] == 'hips')
                piv = rest_hd2[hips['index']][:3, 3].astype(np.float64).copy()
                if upper_pivot_z is not None:
                    piv[2] = float(upper_pivot_z)
                z0 = piv[2] - upper_blend
                z1 = piv[2] + upper_blend
                tt = np.clip((Q[:, 2] - z0) / max(z1 - z0, 1e-9), 0.0, 1.0)
                w = tt * tt * (3.0 - 2.0 * tt)                  # same smoothstep
                Q = piv + (Q - piv) * (1.0 + (upper_scale - 1.0) * w)[:, None]
            return Q

        only = None if arm_finger_pose is True else arm_finger_pose
        out, finger_rows, finger_frames = apply_finger_pose(
            out, verts, resolved, D, rest_sym, rest_hd2, hby, short,
            post, float(arm_finger_pose_amount), say, only=only)
        say('finger residual pose: %d chain(s) posed%s (amount %.3f); the palm, '
            'wrist and roots are untouched'
            % (len(finger_rows),
               '' if only is None else ' [only %s]' % only,
               float(arm_finger_pose_amount)))
    else:
        finger_rows, finger_frames = [], {}

    # ---- 5. the pose the GAME actually shows ------------------------------
    # The shipped rest is  S_A = D * rest_sym  (rest_sym is the T-pose rest; D is
    # the T->A conversion, so S_A is the A-pose rest that the shipped mesh `out`
    # is bound to).  The runtime deforms a vertex by  A[b] @ inv(S_A[b])  where A
    # is the animated bone; with the idle animation A == HD2's own rest, so the
    # mesh is re-posed into HD2's stance -- the bind pose itself is never
    # displayed.  This is the only render that can be compared against a
    # reference patch mod, and it is where a bad correspondence shows up: a
    # landmark pair with the wrong *semantics* stretches the mesh between them.
    idle = {h['index']: rest_hd2[h['index']] @ np.linalg.inv(rest_shipped[h['index']])
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
        rest_shipped=rest_shipped,
        hd2=hd2, rest_hd2=rest_hd2, resolved=resolved, src_index_to_hd2=src_index_to_hd2,
        finger_rows=finger_rows, finger_frames=finger_frames,
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


def _finger_pose_arg(a):
    """Turn the CLI finger flags into `retarget`'s `arm_finger_pose` value.

    None  -> skip the pass entirely
    True  -> every finger
    [...] -> just the named stems, e.g. ['middle'] or ['l_index', 'r_pinky']
    """
    if a.arm_finger_pose_only:
        only = [s.strip() for s in a.arm_finger_pose_only.split(',') if s.strip()]
        if only:
            return only
    return True if a.arm_finger_pose else None


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
    ap.add_argument('--fallback', choices=('parent', 'distance', 'region'),
                    default='region',
                    help='how a source bone with no counterpart is bound: to its '
                         'nearest mapped ANCESTOR (parent), to the mapped bone whose '
                         'joint is CLOSEST (distance), or to the closest bone INSIDE '
                         'the subtree of that ancestor (region, default -- distance '
                         'alone hands the hair bones to the shoulders).')
    ap.add_argument('--no-apose', action='store_true',
                    help='stop after alignment+snap (still a T-pose)')
    ap.add_argument('--leg-tilt', type=float, default=0.0,
                    help='degrees to lean the legs forward about the ankle, so the '
                         'ankle sits behind the hip the way HD2\'s rig has it')
    ap.add_argument('--leg-splay', type=float, default=0.0,
                    help='degrees to splay each leg outward in the front view about '
                         'the thigh head, foot kept flat (4.0 matches the reference)')
    ap.add_argument('--legacy-axis', action='store_true',
                    help='skip the BatchTool\'s final axes conversion; leaves '
                         'basis_i and the symmetric deltas in the shipped rig')
    ap.add_argument('--skeleton', choices=('native', 'snap'), default='native',
                    help='which rig to ship: native = HD2\'s rest verbatim (idle '
                         'frame is the identity); snap = the snapped rest (old)')
    ap.add_argument('--mesh-down', type=float, default=0.0,
                    help='metres to drop the MESH (the rig never moves)')
    ap.add_argument('--mesh-back', type=float, default=0.0,
                    help='metres to push the MESH backwards (+Y is forward)')
    ap.add_argument('--head-scale', type=float, default=1.0,
                    help='uniformly scale the MESH of the head (skull + every '
                         'part hanging off it, hair included) about the '
                         'neck/head seam; the rig never moves')
    ap.add_argument('--arm-elbow-open', type=float, default=0.0, metavar='DEG',
                    help='swing the FOREARM inside the (upper-arm, forearm) '
                         'plane by DEG degrees.  Positive = STRAIGHTER (the '
                         'elbow bend drops by exactly DEG); negative = more '
                         'bent.  The axis is the elbow\'s own hinge, so this is '
                         'literally "the same elbow, 6.5 deg less bent".  The '
                         'hand and fingers follow rigidly; the rig never moves.')
    ap.add_argument('--arm-aim-wrist', action='store_true',
                    help='ROUND 78.  Replace the forearm target: instead of '
                         'aiming the forearm at HD2\'s own elbow->wrist, aim it '
                         'at HD2\'s WRIST measured from OUR elbow (the source '
                         'T-pose elbow, carried along by the leg stance, the '
                         'upper-arm gain and the shoulder rotation).  Pivot is '
                         'unchanged (our elbow), and because the source arm is '
                         'straight the resulting axis is still the elbow\'s own '
                         'hinge -- which is what the user\'s "project HD2\'s '
                         'wrist onto the plane normal to the arm axis" '
                         'constructs.  Off by default: the shipped byte stream '
                         'is untouched.')
    ap.add_argument('--arm-hand-tilt', type=float, default=0.0, metavar='DEG',
                    help='tilt the HAND about the wrist by DEG, about the world '
                         '+Y axis of the SOURCE T-POSE frame -- which is exactly '
                         'the thumb, i.e. the pipe\'s surface normal in the '
                         'user\'s model.  Positive = wrist EXTENSION (the hand '
                         'tips toward the back of the hand, "palm up"), '
                         'mirrored automatically between the two arms.  The '
                         'fingers ride along rigidly; the rig never moves.')
    ap.add_argument('--arm-finger-pose', action='store_true',
                    help='apply the expert-reviewed FINGER residual pose: rotate '
                         'each finger chain onto HD2\'s own phalange directions '
                         'on the final mesh, root to tip, so the fingers curl '
                         'into a relaxed HD2 grip instead of staying flat.  Runs '
                         'after every body/head/wrist step, so the accepted '
                         'palm, wrist and roots stay bit-identical; the rig '
                         'never moves.  Off by default.')
    ap.add_argument('--arm-finger-pose-only', default=None, metavar='WHICH',
                    help='with --arm-finger-pose, restrict the pass to a comma '
                         'list of fingers -- stems like "middle" or side+stem '
                         'like "l_index,r_pinky" -- for the one-finger-first '
                         'acceptance test.  Implies --arm-finger-pose.')
    ap.add_argument('--arm-finger-pose-amount', type=float, default=1.0,
                    metavar='0..1',
                    help='blend the finger residual pose: 0 = off, 1 = fully '
                         'onto HD2\'s directions (default 1).  Intermediate '
                         'values slerp the target directions, for A/B and for '
                         'checking that the unposed fingers are untouched.')
    ap.add_argument('--upper-scale', type=float, default=1.0,
                    help='scale the MESH above the hips (torso/arms/head); the '
                         'rig never moves')
    ap.add_argument('--upper-pivot-z', type=float, default=None,
                    help='height the --upper-scale pivots about (default: the '
                         'hips joint z)')
    ap.add_argument('--upper-blend', type=float, default=0.20,
                    help='half-width in metres of the smoothstep ramp at the hip '
                         'seam used by --upper-scale')
    ap.add_argument('--arm-fit', type=float, default=1.0,
                    help='0..1 strength of the MESH-only arm re-fit: displaces each '
                         'vertex by the weight-blended arm joint residual '
                         '(`shipped head - working head`), so the arm joints land on '
                         "HD2's bones instead of the source's.  0 = off.")
    ap.add_argument('--no-arm-fit', dest='arm_fit', action='store_const', const=0.0,
                    help='same as --arm-fit 0')
    ap.add_argument('--arm-smooth', type=int, default=8,
                    help='Laplacian passes over the mesh that spread the arm '
                         'displacement field; without them the chest/clavicle weight '
                         'jump stretches a few 1 cm triangles ~11x')
    ap.add_argument('--arm-perp', type=float, default=0.0,
                    help='how much of the arm residual PERPENDICULAR to the arm axis '
                         'and along the FORE/AFT direction to apply (0 = none, 1 = '
                         'full).  The global --mesh-back already handles that part, '
                         'so 1 double-counts it (the arm lands 12 cm behind the '
                         'reference).')
    ap.add_argument('--arm-up', type=float, default=0.55,
                    help='how much of the arm residual along the VERTICAL direction '
                         '(perpendicular to the arm axis) to apply.  The mesh forearm '
                         'and palm sit ~2.5 cm too high relative to the bones, and '
                         'this is the only term that corrects it without moving the '
                         'arm fore/aft.')
    ap.add_argument('--arm-taper', type=float, nargs=2, default=[-0.05, 0.22],
                    metavar=('T0', 'T1'),
                    help='ramp the arm displacement in from 0 at T0 to full at T1, '
                         'measured in metres along the shoulder->hand axis.  The '
                         'arm-weighted cloud reaches 8.5 cm inboard of the shoulder '
                         'joint (chest/armpit geometry), and sliding THAT out opens a '
                         'gap; the taper keeps the junction welded.')
    ap.add_argument('--no-arm-taper', dest='arm_taper', action='store_const',
                    const=None, help='disable the taper (old behaviour)')
    ap.add_argument('--arm-rot', type=float, nargs=3, default=None,
                    metavar=('SH', 'EL', 'WR'),
                    help='mesh-only arm ORIENTATION fix: rotate the arm chain about '
                         'the world Y (fore/aft) axis by SH degrees at the shoulder, '
                         'EL at the elbow and WR at the wrist.  A translation cannot '
                         'fix a DIRECTION error: our upper arm hangs 39.4 deg below '
                         'horizontal against the reference\'s 28.5 (bone 29.9) and our '
                         'forearm is at 17.9 against 24.9 (bone 29.6), i.e. the front '
                         'view shows a 21.5 deg kink at the elbow where the reference '
                         'shows 3.6.  The TOP view already matches (13.7/41.6 vs '
                         '13.6/39.4 deg), so the rotation must be about Y only.')
    ap.add_argument('--arm-rot-smooth', type=int, default=8, metavar='N',
                    help='Laplacian passes used to spread the arm-rotation blend '
                         'over a few edges.  The LBS weights jump across one edge '
                         'at the elbow and the wrist, and a 26 deg wrist rotation '
                         'over a 1 cm edge stretches it ~5x.')
    ap.add_argument('--arm-rot-mode', choices=('rigid', 'axis', 'seq', 'blend'),
                    default='rigid',
                    help="'rigid' (default, round 45e): each arm segment turns as "
                         "a RIGID BODY -- a BINARY arm mask, a 1.5 cm transition "
                         "band at the joints (HD2_ARM_GAP) and the rotation angle "
                         "scaled by g (J + R(g*theta)(v-J)), which stays a pure "
                         "rotation for any g.  'axis' (round 45d): same rotations, "
                         "but a CONTINUOUS smoothstep(W) mask and an 18 cm band, so "
                         "the segment is continuously bent and the forearm comes "
                         "out S-shaped.  'seq': weighted by the source's LBS "
                         "weights -- the shells come apart.  'blend': round 45c's "
                         "ROOT->TIP version.  The last three are kept for A/B only.")
    ap.add_argument('--json-out', default='')
    a = ap.parse_args()

    r = retarget(a.vpk, a.model, a.toc, a.bonehash, a.pose_profile,
                 scale=a.scale, head_offset=a.head_offset, align=a.align,
                 apose=not a.no_apose, spine_map=a.spine_map, fallback=a.fallback,
                 leg_tilt=a.leg_tilt, leg_splay=a.leg_splay,
                 axis_roundtrip=not a.legacy_axis, skeleton=a.skeleton,
                 mesh_shift=(0.0, -a.mesh_back, -a.mesh_down),
                 upper_scale=a.upper_scale, upper_pivot_z=a.upper_pivot_z,
                 upper_blend=a.upper_blend, head_scale=a.head_scale,
                 arm_elbow_open=a.arm_elbow_open,
                 arm_hand_tilt=a.arm_hand_tilt,
                 arm_aim_wrist=a.arm_aim_wrist,
                 arm_finger_pose=_finger_pose_arg(a),
                 arm_finger_pose_amount=a.arm_finger_pose_amount,
                 arm_fit=a.arm_fit,
                 arm_perp=a.arm_perp, arm_up=a.arm_up, arm_smooth=a.arm_smooth,
                 arm_taper=(None if a.arm_taper is None else tuple(a.arm_taper)),
                 arm_rot=(None if a.arm_rot is None else tuple(a.arm_rot)),
                 arm_rot_smooth=a.arm_rot_smooth, arm_rot_mode=a.arm_rot_mode)

    os.makedirs(a.outdir, exist_ok=True)
    png = os.path.join(a.outdir, a.out)
    E.render(png, [tuple(p) for p in r['src1']], [tuple(p) for p in r['out']], r['tris'],
             'BatchTool-style -- row1 aligned source (T-pose) | row2 +A-pose   '
             '(%s, scale %.4f, spine=%s)'
             % (os.path.basename(r['mdl_path']),
                float(np.asarray(r['scale']).ravel()[0]), a.spine_map))
    print('[written] %s' % png)

    if a.json_out:
        with open(a.json_out, 'w', encoding='utf-8') as f:
            json.dump(r['metrics'], f, indent=1, ensure_ascii=False)
        print('[written] %s' % a.json_out)


if __name__ == '__main__':
    main()
