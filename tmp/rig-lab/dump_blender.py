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

usage: dump_blender.py
"""
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import patch_view as PV

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
OUT = 'out/blender'
MODEL = 'survivor_teenangst'
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
ARM_HAND_TILT = 10.0


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


def main():
    os.makedirs(OUT, exist_ok=True)
    # Round 49: the CURRENT model version is the one with the 5 cm upper-arm
    # lengthening (scheme A -- the elbow, and the wrist with it, moved out along
    # the source shoulder->elbow axis; the forearm keeps its length and the
    # shipped rig is untouched).
    gain = float(sys.argv[1]) if len(sys.argv) > 1 else ARM_UPPER_GAIN
    r = H.retarget(VPK, MODEL, TOC, BH, PP,
                   align='yaw', spine_map='mesh', fallback='region',
                   leg_tilt=5.7, leg_splay=4.0, skeleton='native',
                   mesh_shift=MESH_SHIFT,
                   upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                   arm_scratch_upper_gain=gain, head_scale=HEAD_SCALE,
                   arm_elbow_open=ARM_ELBOW_OPEN,
                   arm_hand_tilt=ARM_HAND_TILT,
                   verbose=True)
    print('[dump] upper-arm gain = %+.4f m   elbow open = %+.2f deg   '
          'hand tilt = %+.2f deg'
          % (gain, ARM_ELBOW_OPEN, ARM_HAND_TILT))

    tris = np.asarray([tri3(t) for t in r['tris']], dtype=np.int32)
    nv = len(r['verts'])
    print('mesh: %d verts / %d tris' % (nv, len(tris)))

    # ---- weights: source bone index -> HD2 bone index ----------------------
    pos = {h['index']: i for i, h in enumerate(r['hd2'])}
    wb = np.full((nv, 3), -1, dtype=np.int32)
    wv = np.zeros((nv, 3), dtype=np.float32)
    orphan = 0
    for k, v in enumerate(r['verts']):
        for j in range(3):
            w = float(v['w'][j])
            if w <= 0.0:
                continue
            h = r['resolved'].get(v['bones'][j])
            if h is None or h not in pos:
                orphan += 1
                continue
            wb[k, j] = pos[h]
            wv[k, j] = w
    print('weight slots: %d filled, %d unresolved'
          % (int((wb >= 0).sum()), orphan))

    names, heads, axes, rolls, lengths, parents = bone_table(r)

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

    np.savez(os.path.join(OUT, 'model.npz'), **arrays)
    json.dump(dict(model=MODEL, bones=names, refs=refs,
                   params=dict(align='yaw', spine_map='mesh',
                               fallback='region', leg_tilt=5.7,
                               leg_splay=4.0, skeleton='native',
                               mesh_shift=list(MESH_SHIFT),
                               upper_scale=1.12, upper_pivot_z=1.15,
                               upper_blend=0.45, head_scale=HEAD_SCALE,
                               arm_upper_gain=gain,
                               arm_elbow_open=ARM_ELBOW_OPEN,
                               arm_hand_tilt=ARM_HAND_TILT),
                   metrics=plain(r['metrics'])),
              open(os.path.join(OUT, 'meta.json'), 'w', encoding='utf-8'),
              indent=1, ensure_ascii=False)
    for f in ('model.npz', 'meta.json'):
        p = os.path.join(OUT, f)
        print('  [written] %s  (%.1f MB)' % (p, os.path.getsize(p) / 1e6))


if __name__ == '__main__':
    main()
