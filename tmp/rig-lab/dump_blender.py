"""Dump the CURRENT pipeline result into a neutral archive that the Blender
builder script turns into a .blend / .glb.

Why a dump + a Blender-side builder, instead of writing a .glb by hand?
Because a .blend with a REAL armature, REAL vertex groups and an Armature
modifier is what you actually want to poke at in Blender -- and Blender itself
is the most trustworthy writer of that.  This script only produces arrays.

What goes in:
  OURS_bind  r['out']        the current deliverable (mesh shifted + upper scaled)
  OURS_game  r['out_idle']   the frame the game actually renders (== bind when
                             the shipped rig is HD2-native, which is the point)
  SRC_tpose  r['src_pts']    the source model after the initial rigid fit
  REF_*      the reference patch units, straight from out/patchdump/*.obj
  armature   HD2 native rest: head / bone axis / roll axis / parent / length

usage:
    dump_blender.py [--vpk PATH] [--model STEM] [--out DIR] [--gain M]
                    [--head-scale F] [--elbow-open DEG] [--hand-tilt DEG]
                    [--finger-pose {all,none,list}]

Every flag defaults to the value that produced the shipped Lunalice build, so
a bare `dump_blender.py` reproduces it byte for byte.
"""
import argparse
import hashlib
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import patch_view as PV
import weight_half4 as W48

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
# Defaults only -- every one of these is overridable from the command line so
# the SAME script turns any L4D2 addon model into `model.npz`.  Round 63 added
# the CLI precisely so a second source pack (Lilium_Sancta) could be run through
# the identical pipeline without editing this file.
DEFAULT_VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
               'Lunalice_Zoey.vpk')
DEFAULT_OUT = 'out/blender'
DEFAULT_MODEL = 'survivor_teenangst'
# ONE source of truth for the global mesh shift (x, y, z).  Round 51: 3 cm more
# drop than the original 6 cm, so the knee AND the shoulder come down together.
MESH_SHIFT = (0.0, -0.100, -0.090)
# Round 51: the source head is too small.  Scale the skull (and everything
# weighted to it -- the long hair included) UNIFORMLY about the neck/head seam.
# 8/7 = 1.1429 is the user's read off Blender: head height 35 -> 40.
HEAD_SCALE = 8.0 / 7.0
# Round 52: the forearm is 6.5 deg too far forward in the TOP view.  The fix is
# the elbow's own hinge, so it is a single number on the bone-level min-arc --
# see hd2_style.retarget(arm_elbow_open=...).  Measured: our forearm sits at
# +45.96 deg forward of -X and the reference patch at +39.43; opening the elbow
# by 6.5 puts us at +39.48, i.e. 0.96 deg off instead of 6.49.
ARM_ELBOW_OPEN = 6.5
# Round 49/52: the upper-arm lengthening, in metres.  It was only ever reaching
# the pipeline through main()'s argv default -- pin it down.
ARM_UPPER_GAIN = 0.05
# Round 53: tilt the HAND about the wrist, about the T-pose THUMB axis (which is
# exactly world +Y in that frame -- see _probe_hand_tpose.py).  Positive =
# EXTENSION: the hand tips toward the back of the hand, i.e. "手掌翘起".
# This is the user's VISUAL read off Blender ("微调 10 度"), not a fitted number.
# Be honest about the mesh-level evidence, which does NOT back it up: the
# palm-slab plane normal is already only 3.50 deg off the reference at tilt 0,
# and tilting makes it worse (6.67 at -7, 8.92 at +7, 9.40 at -10, 11.77 at +10).
# Nearest-neighbour overlap says the same thing.  So this knob is currently
# fighting the metrics -- if the hands still do not line up, suspect that what
# the eye is seeing is a TRANSLATION (or the fingers), not a wrist roll.
# One signed number; 0 disables.  `_diag_hand_tilt2.py` draws all three.
#
# Round 55b: the user asked for this to come DOWN again, from what they called
# "the 7 deg" to 3 deg.  The value actually shipped in round 53/54 was 10.0 --
# the file `zoey-before-hand7.blend` is misleadingly named (its content is the
# 10 deg build).  The user's target is absolute ("change to 3 deg"), so this is
# 3.0, not 10-4.
ARM_HAND_TILT = 3.0
# Round 55: the FINGER residual pose (expert brief 10.4-10.7).  The source hand
# is a flat T-pose (its ten chains measure 0.00 deg of bend) while HD2's native
# hand is a relaxed A-pose, so without this the fingers stay flat and land
# ~45 deg off HD2's phalanges -- and HD2's distal finger joints sit 35-48 mm
# OUTSIDE our fingers, so the game would hinge them about thin air.  This pass
# rotates each chain onto HD2's own bone directions on the FINAL mesh, so the
# accepted palm/wrist/roots are untouched.  `_probe_fingerpose1.py` checks the
# expert's gates; `_diag_fingerpose.py` draws it.  A list of stems poses only
# those ('middle'), True poses all ten, None disables.
ARM_FINGER_POSE = True


def tri3(t):
    return (int(t[0]), int(t[1]), int(t[2]))


def plain(o):
    """metrics come back with numpy scalars/arrays sprinkled in -- json needs
    plain python."""
    if isinstance(o, dict):
        return {str(k): plain(v) for k, v in o.items()}
    if isinstance(o, (list, tuple)):
        return [plain(v) for v in o]
    if isinstance(o, np.ndarray):
        return plain(o.tolist())
    if isinstance(o, (np.floating, np.integer, np.bool_)):
        return o.item()
    return o


def bone_table(r):
    """HD2 native rest -> flat arrays the Blender side can consume.

    Blender armature bones point along their local +Y, while BOTH engines put
    the bone along local +X (see export_obj.bone_axis), so head/axis/roll get
    re-expressed there.  Length is NOT in the data -- it is derived from the
    children (a chain's tail is its child's head).  Bones whose children sit on
    top of them (HD2 really does have spine1 == hips and chest == spine2) get a
    fallback so they do not collapse to a dot.
    """
    hd2 = r['hd2']
    B = len(hd2)
    pos = {h['index']: i for i, h in enumerate(hd2)}

    names = [h['name'] or 'bone_%d' % h['index'] for h in hd2]
    heads = np.zeros((B, 3))
    axes = np.zeros((B, 3))
    rolls = np.zeros((B, 3))
    parents = np.full(B, -1, dtype=np.int32)

    for i, h in enumerate(hd2):
        m = np.asarray(r['rest_hd2'][h['index']], dtype=np.float64)
        heads[i] = m[:3, 3]
        a = m[:3, 0]
        z = m[:3, 2]
        axes[i] = a / (np.linalg.norm(a) or 1.0)
        rolls[i] = z / (np.linalg.norm(z) or 1.0)
        p = h.get('parent', 0)
        if p in pos and pos[p] != i:
            parents[i] = pos[p]

    kids = [[] for _ in range(B)]
    for i, p in enumerate(parents):
        if p >= 0:
            kids[p].append(i)

    lengths = np.zeros(B)
    for i in range(B):
        proj, dist = [], []
        for c in kids[i]:
            d = heads[c] - heads[i]
            n = float(np.linalg.norm(d))
            if n < 1e-4:
                continue
            proj.append(float(np.dot(d, axes[i])))
            dist.append(n)
        if proj:
            best = max(proj)
            # a chain child sits ON the axis; children that are purely lateral
            # (hips -> thighs, chest -> clavicles) have ~0 projection, so fall
            # back to the nearest one instead of the farthest.
            lengths[i] = best if best > 1e-3 else min(dist)
    # leaves / coincident children -> borrow from the parent, else a stub
    for i in range(B):
        if lengths[i] < 1e-4:
            p = parents[i]
            lengths[i] = lengths[p] * 0.6 if p >= 0 and lengths[p] > 1e-4 else 0.04
    np.clip(lengths, 0.01, 0.55, out=lengths)

    # sanity: does head + L*axis land on the child head?
    dev = []
    for i in range(B):
        for c in kids[i]:
            d = heads[c] - heads[i]
            n = float(np.linalg.norm(d))
            if n < 1e-4:
                continue
            ang = float(np.degrees(np.arccos(np.clip(
                np.dot(d / n, axes[i]), -1.0, 1.0))))
            dev.append(ang)
    dev = np.asarray(dev) if dev else np.zeros(1)
    unnamed = sum(1 for h in hd2 if not h['name'])
    print('bones %d | unnamed %d | child-direction deviation from bone +X: '
          'median %.2f deg, p95 %.2f deg, max %.2f deg'
          % (B, unnamed, np.median(dev), np.percentile(dev, 95), dev.max()))
    print('  first 12: ' + ', '.join(
        '%d:%s(p%d,z%.3f)' % (i, names[i][:18], parents[i], heads[i][2])
        for i in range(min(12, B))))
    print('  unnamed: ' + ', '.join(
        '%d:%s(p%d,head %.3f/%.3f/%.3f)' % (i, names[i], parents[i],
                                            heads[i][0], heads[i][1], heads[i][2])
        for i in range(B) if not hd2[i]['name']))
    return names, heads, axes, rolls, lengths, parents


def _finger_pose_arg(a):
    """CLI string -> `retarget`'s `arm_finger_pose` value.

    'none' / '' / '0' / 'false' -> None (skip the pass)
    'all' / '1' / 'true'        -> True (every finger)
    anything else               -> the comma list, e.g. 'middle' or 'l_index,r_pinky'
    """
    s = str(a).strip().lower()
    if s in ('none', '', '0', 'false', 'no'):
        return None
    if s in ('all', '1', 'true', 'yes'):
        return True
    only = [p.strip() for p in str(a).split(',') if p.strip()]
    return only or True


def parse_args(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument('--vpk', default=DEFAULT_VPK,
                    help='L4D2 addon VPK holding the model (default: Lunalice_Zoey)')
    ap.add_argument('--model', default=DEFAULT_MODEL,
                    help='model stem inside the VPK (default: survivor_teenangst)')
    ap.add_argument('--out', default=DEFAULT_OUT,
                    help='directory for model.npz + meta.json (default: out/blender)')
    # Round 49: the CURRENT model version is the one with the 5 cm upper-arm
    # lengthening (scheme A -- the elbow, and the wrist with it, moved out along
    # the source shoulder->elbow axis; the forearm keeps its length and the
    # shipped rig is untouched).
    ap.add_argument('--gain', type=float, default=ARM_UPPER_GAIN,
                    help='upper-arm lengthening in metres (round 49)')
    ap.add_argument('--head-scale', type=float, default=HEAD_SCALE,
                    help='uniform skull scale about the neck seam (round 51)')
    ap.add_argument('--elbow-open', type=float, default=ARM_ELBOW_OPEN,
                    help='elbow hinge opening in degrees (round 52)')
    ap.add_argument('--hand-tilt', type=float, default=ARM_HAND_TILT,
                    help='wrist tilt about the T-pose thumb axis, degrees (round 55b)')
    ap.add_argument('--finger-pose', default=str(ARM_FINGER_POSE),
                    help="residual finger pose: 'all' (True), 'none' (False), "
                         "or a comma list of stems e.g. 'middle' (round 55)")
    return ap.parse_args(argv)


def main(argv=None):
    args = parse_args(argv)
    out_dir = args.out
    os.makedirs(out_dir, exist_ok=True)
    gain = args.gain
    finger_pose = _finger_pose_arg(args.finger_pose)
    print('[dump] vpk   = %s' % args.vpk)
    print('[dump] model = %s' % args.model)
    print('[dump] out   = %s' % out_dir)
    r = H.retarget(args.vpk, args.model, TOC, BH, PP,
                   align='yaw', spine_map='mesh', fallback='region',
                   leg_tilt=5.7, leg_splay=4.0, skeleton='native',
                   mesh_shift=MESH_SHIFT,
                   upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                   arm_scratch_upper_gain=gain, head_scale=args.head_scale,
                   arm_elbow_open=args.elbow_open,
                   arm_hand_tilt=args.hand_tilt,
                   arm_finger_pose=finger_pose,
                   verbose=True)
    print('[dump] upper-arm gain = %+.4f m   elbow open = %+.2f deg   '
          'hand tilt = %+.2f deg   finger pose = %s'
          % (gain, args.elbow_open, args.hand_tilt, finger_pose))

    tris = np.asarray([tri3(t) for t in r['tris']], dtype=np.int32)
    nv = len(r['verts'])
    print('mesh: %d verts / %d tris' % (nv, len(tris)))

    names, heads, axes, rolls, lengths, parents = bone_table(r)

    # ---- weights: source bone index -> HD2 bone index ----------------------
    # Round 57: this is where the weights the game will actually read are
    # decided, so the SDK's encoding contract is applied HERE, once.  Two
    # distinct source bones routinely resolve onto one HD2 bone, and the
    # .blend writer adds each slot with REPLACE -- so duplicates must be
    # summed before anything is written.  After that every influence is snapped
    # to k/2048, which makes the float32 sum exactly 1 (so Blender's export-time
    # normalise is a no-op) and the float16 sum exactly 1 as well.
    pos = {h['index']: i for i, h in enumerate(r['hd2'])}
    name_to_idx = {nm: i for i, nm in enumerate(names)}
    wb = np.full((nv, 3), -1, dtype=np.int32)
    wv = np.zeros((nv, 3), dtype=np.float32)
    orphan = 0
    merged_v = 0
    zero_v = 0
    dropped = []
    worst_move = 0.0
    worst_v = -1
    for k, v in enumerate(r['verts']):
        raw_w, raw_k = [], []
        for j in range(3):
            w = float(v['w'][j])
            if w <= 0.0:
                continue
            h = r['resolved'].get(v['bones'][j])
            if h is None or h not in pos:
                orphan += 1
                continue
            raw_w.append(w)
            raw_k.append(names[pos[h]])
        if not raw_w:
            zero_v += 1
            continue
        if len(set(raw_k)) < len(raw_k):
            merged_v += 1
        before = {}
        for w, nm in zip(raw_w, raw_k):
            before[nm] = before.get(nm, 0.0) + w
        q, qk, dr = W48.quantize_half4(raw_w, raw_k, max_influences=3)
        if not W48.verify_half_closed(list(q) + [0.0]):
            raise AssertionError('vertex %d did not close: %s' % (k, q))
        for j in range(3):
            if q[j] <= 0.0:
                continue
            wb[k, j] = name_to_idx[qk[j]]
            wv[k, j] = q[j]
            d = abs(q[j] - before[qk[j]])
            if d > worst_move:
                worst_move, worst_v = d, k
        dropped.extend(dr)
    print('weight slots: %d filled, %d unresolved' % (int((wb >= 0).sum()), orphan))
    print('half4 encoding: %d vertices had a duplicate HD2 bone merged, '
          '%d unweighted, %d influences dropped, worst single-weight move '
          '%.10f (v%d)' % (merged_v, zero_v, len(dropped), worst_move, worst_v))
    if dropped:
        raise AssertionError('the 3-slot layout dropped %d influence(s) with '
                             'total mass %.6g; widen it to 4 before shipping'
                             % (len(dropped), sum(w for _k, w in dropped)))
    if zero_v:
        raise AssertionError('%d vertices ended up with no influence at all; '
                             'an unweighted rigid part needs an explicit anchor'
                             % zero_v)

    arrays = dict(
        ours_bind=np.asarray(r['out'], dtype=np.float32),
        ours_game=np.asarray(r['out_idle'], dtype=np.float32),
        src_tpose=np.asarray(r['src_pts'], dtype=np.float32),
        tris=tris,
        w_bone=wb, w_val=wv,
        b_head=heads.astype(np.float32),
        b_axis=axes.astype(np.float32),
        b_roll=rolls.astype(np.float32),
        b_len=lengths.astype(np.float32),
        b_parent=parents,
    )

    # ---- reference patch units --------------------------------------------
    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    refs, base = [], 0
    rp, rt = [], []
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        path = 'out/patchdump/unit-%s.obj' % fid
        if not os.path.exists(path):
            continue
        p, t = PV.read_obj(path)
        if len(t) < 50:
            continue
        rp.extend(p)
        rt.extend([(x[0] + base, x[1] + base, x[2] + base) for x in t])
        refs.append(dict(fid=fid, tag=u.get('tag') or '',
                         start=base, count=len(p), tris=len(t)))
        base += len(p)
    arrays['ref_pts'] = np.asarray(rp, dtype=np.float32)
    arrays['ref_tris'] = np.asarray(rt, dtype=np.int32)
    print('reference units: %d (%d verts / %d tris)'
          % (len(refs), base, len(rt)))

    np.savez(os.path.join(out_dir, 'model.npz'), **arrays)
    # Round 57 (expert WEIGHT_TRANSFER_BRIEF.md §10.8 step 1): freeze the inputs
    # this build was made from, so a later weight change cannot be confused with
    # a geometry change.
    def sha256_file(p):
        h = hashlib.sha256()
        with open(p, 'rb') as fh:
            for chunk in iter(lambda: fh.read(1 << 20), b''):
                h.update(chunk)
        return h.hexdigest()

    here = os.path.dirname(os.path.abspath(__file__))

    def rest_bytes(rest):
        """`rest` is {hd2 bone index: 4x4}, so hash it key-sorted and dense."""
        if isinstance(rest, dict):
            items = sorted(rest.items())
            blob = np.concatenate([np.asarray(v, np.float64).ravel()
                                   for _k, v in items])
            keys = np.asarray([k for k, _v in items], np.int64)
        else:
            blob = np.asarray(rest, np.float64).ravel()
            keys = np.zeros(0, np.int64)
        return np.ascontiguousarray(keys).tobytes() + blob.tobytes()

    fingerprint = dict(
        geometry=hashlib.sha256(np.ascontiguousarray(
            arrays['ours_bind'], dtype=np.float32).tobytes()).hexdigest(),
        vertices=int(nv), triangles=int(len(tris)),
        bone_count=len(names),
        rest_shipped=hashlib.sha256(rest_bytes(r['rest_shipped'])).hexdigest(),
        weights=hashlib.sha256(
            np.ascontiguousarray(wb).tobytes()
            + np.ascontiguousarray(wv).tobytes()).hexdigest(),
        scripts={f: sha256_file(os.path.join(here, f))
                 for f in ('hd2_style.py', 'dump_blender.py', 'blender_build.py',
                           'weight_half4.py')},
    )
    json.dump(dict(model=args.model, bones=names, refs=refs,
                   fingerprint=fingerprint,
                   params=dict(align='yaw', spine_map='mesh',
                               fallback='region', leg_tilt=5.7,
                               leg_splay=4.0, skeleton='native',
                               mesh_shift=list(MESH_SHIFT),
                               upper_scale=1.12, upper_pivot_z=1.15,
                               upper_blend=0.45, head_scale=args.head_scale,
                               arm_upper_gain=gain,
                               arm_elbow_open=args.elbow_open,
                               arm_hand_tilt=args.hand_tilt,
                               arm_finger_pose=finger_pose,
                               weight_encoding='half4-1/2048-largest-remainder'),
                   metrics=plain(r['metrics'])),
              open(os.path.join(out_dir, 'meta.json'), 'w', encoding='utf-8'),
              indent=1, ensure_ascii=False)
    for f in ('model.npz', 'meta.json'):
        p = os.path.join(out_dir, f)
        print('  [written] %s  (%.1f MB)' % (p, os.path.getsize(p) / 1e6))
    print('  [frozen] geometry %s' % fingerprint['geometry'][:16])
    print('  [frozen] weights  %s' % fingerprint['weights'][:16])


if __name__ == '__main__':
    main()
