"""Step 4: acceptance gates for the finger residual pose (expert brief 10.7).

Runs the SAME pipeline twice in one process -- finger pass OFF, then ON for a
single finger first -- and checks the expert's numeric gates.  Nothing here is
a shape judgement: shape is the user's call (10.7.5).  These are the mechanical
invariants that must hold before a shape is even worth looking at.

usage: _probe_fingerpose1.py [finger ...]      (default: middle)
"""
import json
import os
import sys
import time

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
MODEL = 'survivor_teenangst'

COMMON = dict(align='yaw', spine_map='mesh', fallback='region',
              leg_tilt=5.7, leg_splay=4.0, skeleton='native',
              mesh_shift=(0.0, -0.100, -0.090),
              upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
              arm_scratch_upper_gain=0.05, head_scale=8.0 / 7.0,
              arm_elbow_open=6.5, arm_hand_tilt=3.0, verbose=False)

FINGERS = sys.argv[1:] or ['middle']
out = []


def say(*a):
    line = ' '.join(str(x) for x in a)
    print(line)
    out.append(line)


def kabsch_resid(A, B):
    """p95 of |rigid(A) - B| after the best rigid fit, in mm."""
    A = np.asarray(A, float)
    B = np.asarray(B, float)
    if len(A) < 4:
        return float('nan')
    ca, cb = A.mean(0), B.mean(0)
    U, S, Vt = np.linalg.svd((A - ca).T @ (B - cb))
    d = np.sign(np.linalg.det(Vt.T @ U.T))
    R = Vt.T @ np.diag([1.0, 1.0, d]) @ U.T
    err = np.linalg.norm((R @ (A - ca).T).T + cb - B, axis=1)
    return float(np.percentile(err, 95) * 1000.0)


def tri_normals(P, tris):
    a = P[tris[:, 0]]
    b = P[tris[:, 1]]
    c = P[tris[:, 2]]
    n = np.cross(b - a, c - a)
    ln = np.linalg.norm(n, axis=1)
    ln[ln < 1e-20] = 1.0
    return n / ln[:, None]


# ------------------------------------------------------------------ run both
t0 = time.time()
base = H.retarget(VPK, MODEL, TOC, BH, PP, **COMMON)
say('[run] finger pass OFF  (%.1f s)' % (time.time() - t0))
t0 = time.time()
posed = H.retarget(VPK, MODEL, TOC, BH, PP,
                   arm_finger_pose=list(FINGERS), **COMMON)
say('[run] finger pass ON   %s  (%.1f s)' % (FINGERS, time.time() - t0))

P0 = np.asarray(base['out'], float)
P1 = np.asarray(posed['out'], float)
verts = base['verts']
tris = np.asarray([(int(t[0]), int(t[1]), int(t[2])) for t in base['tris']],
                  dtype=np.int64)
resolved = base['resolved']
hby = {h['name']: h for h in base['hd2'] if h['name']}

say('')
say('== 0. structural =========================================')
say('  verts %d -> %d   tris %d -> %d' %
    (len(P0), len(P1), len(tris), len(base['tris'])))
assert len(P0) == len(P1)
assert len(tris) == len(posed['tris'])

# ---- A. the OFF path must be inert (the refactor changed nothing) ----------
ref = np.load('out/_before_fingerpose.npz')
d_ref = np.abs(P0 - ref['ours_bind'].astype(float)).max()
say('  OFF vs out/_before_fingerpose.npz  max|d| = %.3e m  (float32 store, '
    'expect ~1e-7)' % d_ref)

# ---- which bones the ON pass is allowed to touch --------------------------
allowed = set()
for r in posed['finger_rows']:
    allowed.update(r['bidx'])
say('  posed chains: %s' % ', '.join(
    '%s_%s' % (r['side'], r['stem']) for r in posed['finger_rows']))
say('  bones the pass may move: %s'
    % ', '.join(sorted(hby_b['name'] for hby_b in base['hd2']
                       if hby_b['index'] in allowed)))

touch = np.zeros(len(P0), bool)
for k, v in enumerate(verts):
    for j in range(3):
        if v['w'][j] > 0 and resolved.get(v['bones'][j]) in allowed:
            touch[k] = True
say('  vertices with any weight on those bones: %d (%.2f%%)'
    % (int(touch.sum()), 100.0 * touch.mean()))

# ---- B. everything else must be bit-identical ------------------------------
d = np.abs(P1 - P0).max(axis=1)
say('')
say('== 1. blast radius (10.7.3) ==============================')
say('  untouched vertices  max|d| = %.3e m   (gate < 1e-8)'
    % float(d[~touch].max()))
say('  touched   vertices  max|d| = %.4f m   mean %.4f m'
    % (float(d[touch].max()), float(d[touch].mean())))
ok_blast = float(d[~touch].max()) < 1e-8

# ---- C. per-chain frames: det, orthonormality, root, lengths, residual -----
say('')
say('== 2. per-chain rigid frames (10.7.2) =====================')
frames = posed['finger_frames']
worst_det = 0.0
worst_orth = 0.0
for r in posed['finger_rows']:
    side, stem, bidx, J = r['side'], r['stem'], r['bidx'], r['joints']
    for b in bidx:
        M = frames[b]
        R = M[:3, :3]
        worst_det = max(worst_det, abs(np.linalg.det(R) - 1.0))
        worst_orth = max(worst_orth, float(np.abs(R.T @ R - np.eye(3)).max()))
    # deformed joints, straight from the frames
    Jp = np.asarray([(frames[b] @ np.append(J[k], 1.0))[:3]
                     for k, b in enumerate(bidx)])
    root_move = float(np.linalg.norm(Jp[0] - J[0]))
    L0 = [np.linalg.norm(J[k + 1] - J[k]) for k in range(2)]
    L1 = [np.linalg.norm(Jp[k + 1] - Jp[k]) for k in range(2)]
    dlen = [abs(L1[k] - L0[k]) / max(L0[k], 1e-12) for k in range(2)]
    say('  %s_%-6s root move %.3e m | dLen/L %.2e %.2e | det-1 %.1e | '
        'orth %.1e | residual deg %s'
        % (side, stem, root_move, dlen[0], dlen[1], abs(np.linalg.det(
            frames[bidx[0]][:3, :3]) - 1.0),
           float(np.abs(frames[bidx[0]][:3, :3].T @ frames[bidx[0]][:3, :3]
                        - np.eye(3)).max()),
           ' '.join('%.2e' % e for e in r['residual_deg'])))
say('  worst |det-1| %.2e   worst |RtR-I| %.2e   (gates ~1e-12 / ~1e-12)'
    % (worst_det, worst_orth))
say('  applied local rotations (deg), root -> tip:')
for r in posed['finger_rows']:
    say('    %s_%-6s %s' % (r['side'], r['stem'],
                            '  '.join('%7.3f' % a for a in r['angles_deg'])))

# ---- D0. intra-segment rigidity (10.7.4 first clause) ---------------------
# The gate is about the SEGMENT being rigid, i.e. the vertices that belong to
# one bone outright.  Vertices that blend two bones are LBS by definition and
# are reported separately as information, not as a pass/fail.
say('')
say('== 3a. intra-segment rigid-body deviation ==================')
seg_p95 = 0.0
blend_p95 = 0.0
for r in posed['finger_rows']:
    side, stem, bidx = r['side'], r['stem'], r['bidx']
    for k, b in enumerate(bidx):
        pure, blend = [], []
        for i, v in enumerate(verts):
            best, bw = -1, -1.0
            for j in range(3):
                if v['w'][j] > bw:
                    best, bw = int(v['bones'][j]), v['w'][j]
            if resolved.get(best) != b:
                continue
            (pure if bw >= 0.999 else blend).append(i)
        M = posed['finger_frames'][b]
        for tag, grp, acc in (('pure ', pure, 'gate p95<=0.5'),
                              ('blend', blend, 'info only')):
            if len(grp) < 8:
                continue
            grp = np.asarray(grp, dtype=int)
            pred = (M @ np.hstack([P0[grp], np.ones((len(grp), 1))]).T).T[:, :3]
            e = np.linalg.norm(pred - P1[grp], axis=1) * 1000.0
            p95 = float(np.percentile(e, 95))
            if tag == 'pure ':
                seg_p95 = max(seg_p95, p95)
            else:
                blend_p95 = max(blend_p95, p95)
            say('  %s_%-6s f%d %s n=%4d  p50 %.4f  p95 %.4f  max %.4f mm  (%s)'
                % (side, stem, k + 1, tag, len(grp),
                   float(np.percentile(e, 50)), p95, float(e.max()), acc))
say('  WORST pure-segment p95 %.4f mm  (gate <= 0.5)' % seg_p95)
say('  WORST blend-zone  p95 %.4f mm  (expected: LBS across a hinge)' % blend_p95)

# ---- D1. edge stretch inside the moved region ----------------------------
say('')
say('== 3b. edge stretch inside the moved region ===============')
e0 = np.linalg.norm(P0[tris[:, 1]] - P0[tris[:, 0]], axis=1)
e1 = np.linalg.norm(P0[tris[:, 2]] - P0[tris[:, 0]], axis=1)
f0 = np.linalg.norm(P0[tris[:, 2]] - P0[tris[:, 1]], axis=1)
g0 = np.linalg.norm(P1[tris[:, 1]] - P1[tris[:, 0]], axis=1)
g1 = np.linalg.norm(P1[tris[:, 2]] - P1[tris[:, 0]], axis=1)
f1_ = np.linalg.norm(P1[tris[:, 2]] - P1[tris[:, 1]], axis=1)
sel_e = touch[tris[:, 0]] & touch[tris[:, 1]] & touch[tris[:, 2]]
A0 = np.concatenate([e0[sel_e], e1[sel_e], f0[sel_e]])
A1 = np.concatenate([g0[sel_e], g1[sel_e], f1_[sel_e]])
keep = A0 > 1e-9
ratio = A1[keep] / A0[keep]
say('  edges wholly inside the moved region: %d' % int(keep.sum()))
say('  length ratio  p50 %.4f  p95 %.4f  p99 %.4f  max %.3f  min %.3f'
    % (float(np.percentile(ratio, 50)), float(np.percentile(ratio, 95)),
       float(np.percentile(ratio, 99)), float(ratio.max()), float(ratio.min())))
say('  new separation across a seam (>0.5 mm = look, >1 mm = must explain):')
dnew = A1[keep] - A0[keep]
say('    edges grown  > 0.5 mm: %d ; > 1 mm: %d ; worst growth %.4f mm'
    % (int((dnew > 0.0005).sum()), int((dnew > 0.001).sum()),
       float(dnew.max() * 1000)))
say('    edges shrunk > 0.5 mm: %d ; > 1 mm: %d ; worst shrink %.4f mm'
    % (int((dnew < -0.0005).sum()), int((dnew < -0.001).sum()),
       float(dnew.min() * 1000)))

# where does the worst stretch live?
idx2name = {h['index']: (h['name'] or 'bone_%d' % h['index'])
            for h in base['hd2']}
pairs = np.concatenate([
    tris[sel_e][:, [0, 1]], tris[sel_e][:, [0, 2]], tris[sel_e][:, [1, 2]]])
kept = pairs[keep]
order = np.argsort(-dnew)
say('  worst-growing edges, and the bones their endpoints are dominated by:')
for i in order[:6]:
    a, b = kept[i]
    tag = []
    for c in (a, b):
        best, bw = -1, -1.0
        for j in range(3):
            if verts[c]['w'][j] > bw:
                best, bw = int(verts[c]['bones'][j]), v['w'][j]
        tag.append('%s(%.2f)' % (idx2name.get(resolved.get(best), '?'), bw))
    say('    %+.3f mm  (len %.2f -> %.2f mm)  %s  --  %s'
        % (dnew[i] * 1000, A0[keep][i] * 1000, A1[keep][i] * 1000,
           tag[0], tag[1]))

# ---- D. rigid vs blended (10.7.3 last clause) -----------------------------
# NOTE: a "pure" group must be per BONE.  Pooling all three phalanges gives
# three different rigid bodies in one set, so its Kabsch residual is meaningless.
say('')
say('== 3. rigid where it must be, blended where it must be ===')
for r in posed['finger_rows']:
    side, stem, bidx = r['side'], r['stem'], r['bidx']
    for k, b in enumerate(bidx):
        grp = []
        for i, v in enumerate(verts):
            nz = [j for j in range(3) if v['w'][j] > 0]
            if len(nz) == 1 and resolved.get(v['bones'][nz[0]]) == b:
                grp.append(i)
        if len(grp) < 8:
            continue
        grp = np.asarray(grp, dtype=int)
        rr = kabsch_resid(P0[grp], P1[grp])
        say('  %s_%-6s f%d  pure verts %5d -> Kabsch p95 %.5f mm (must be ~0)'
            % (side, stem, k + 1, len(grp), rr))
    grp = []
    for i, v in enumerate(verts):
        best, bw = -1, -1.0
        for j in range(3):
            if v['w'][j] > bw:
                best, bw = int(v['bones'][j]), v['w'][j]
        if resolved.get(best) in bidx:
            grp.append(i)
    grp = np.asarray(grp, dtype=int)
    if len(grp) >= 8:
        rm = kabsch_resid(P0[grp], P1[grp])
        say('  %s_%-6s whole-group Kabsch p95 %.3f mm (MUST NOT be forced to 0 '
            '-- the chain is a hinge, not a rigid body)' % (side, stem, rm))

# ---- E. flipped faces among the moved triangles ---------------------------
say('')
say('== 4. no flipped faces in the moved region (10.7.4) ======')
n0 = tri_normals(P0, tris)
n1 = tri_normals(P1, tris)
a0 = np.linalg.norm(np.cross(P0[tris[:, 1]] - P0[tris[:, 0]],
                             P0[tris[:, 2]] - P0[tris[:, 0]]), axis=1)
sel = touch[tris].any(axis=1)
dot = (n0[sel] * n1[sel]).sum(axis=1)
bad = np.where(sel)[0][dot < 0]
say('  triangles touching moved verts: %d ; flipped (dot<0): %d ; '
    'rotated >30 deg: %d'
    % (int(sel.sum()), len(bad),
       int((dot < np.cos(np.radians(30))).sum())))
if len(bad):
    say('  flipped triangles -- area before (mm^2) and the same triangles '
        'already-degenerate?')
    # dominant HD2 bone of a vertex + its name
    idx2name = {h['index']: (h['name'] or 'bone_%d' % h['index'])
                for h in base['hd2']}
    joint_ref = {}
    for r in posed['finger_rows']:
        for k, b in enumerate(r['bidx']):
            joint_ref[b] = r['joints'][k]
    for t in bad:
        corners = tris[t]
        dom = []
        for c in corners:
            best, bw = -1, -1.0
            for j in range(3):
                if verts[c]['w'][j] > bw:
                    best, bw = int(verts[c]['bones'][j]), verts[c]['w'][j]
            dom.append('%s %.2f' % (idx2name.get(resolved.get(best), '?'), bw))
        # how close to the f1/f2 hinge does this sliver sit?
        hinge = joint_ref.get(posed['finger_rows'][0]['bidx'][1])
        hd = (np.linalg.norm(P0[corners] - hinge, axis=1).min()
              if hinge is not None else float('nan'))
        say('    tri %6d area_before %.6f mm^2  area_after %.6f mm^2  '
            'dot %.3f  max|d| on its verts %.4f m  |min dist to f1/f2 hinge '
            '%.1f mm' % (t, a0[t] * 1e6,
                         float(np.linalg.norm(np.cross(
                             P1[corners[1]] - P1[corners[0]],
                             P1[corners[2]] - P1[corners[0]]))) * 1e6,
                         float(dot[np.where(sel)[0] == t][0]),
                         float(d[corners].max()), hd * 1000))
        say('           weights: %s' % ' | '.join(dom))
    deg_before = int((a0[bad] < 1e-10).sum())
    say('  of those, already degenerate before the pass: %d' % deg_before)

# ---- F. the whole point: do our fingers now sit on HD2's joints? ----------
say('')
say('== 5. finger mesh vs HD2 finger joints (the real goal) ====')
rest_hd2 = {h['index']: np.asarray(base['rest_hd2'][h['index']], float)
            for h in base['hd2']}
for r in posed['finger_rows']:
    side, stem, bidx = r['side'], r['stem'], r['bidx']
    # vertices dominated by this chain
    grp = []
    for k, v in enumerate(verts):
        best, bw = -1, -1.0
        for j in range(3):
            if v['w'][j] > bw:
                best, bw = int(v['bones'][j]), v['w'][j]
        if resolved.get(best) in bidx:
            grp.append(k)
    grp = np.asarray(grp, dtype=int)
    if not len(grp):
        continue
    dists_b, dists_a = [], []
    for b in bidx:
        j = rest_hd2[b][:3, 3]
        dists_b.append(np.linalg.norm(P0[grp] - j, axis=1).min())
        dists_a.append(np.linalg.norm(P1[grp] - j, axis=1).min())
    say('  %s_%-6s %5d verts | joint->mesh  before %s  after %s  (mm)'
        % (side, stem, len(grp),
           ' '.join('%5.1f' % (x * 1000) for x in dists_b),
           ' '.join('%5.1f' % (x * 1000) for x in dists_a)))

# ---- G. global edge distortion regression ---------------------------------
say('')
say('== 6. regression: global edge distortion ==================')
mb = base['metrics']
mp = posed['metrics']
for key in ('edge_mean', 'edge_p50', 'edge_p95', 'edge_p99', 'edge_max', 'bad'):
    say('  %-10s off %.5f   on %.5f   delta %+.5f'
        % (key, mb[key], mp[key], mp[key] - mb[key]))
say('  height_after  off %.4f  on %.4f' % (mb['height_after'], mp['height_after']))

say('')
say('VERDICT: blast-radius gate %s' % ('PASS' if ok_blast else 'FAIL'))
json.dump(dict(fingers=FINGERS, blast_ok=ok_blast,
               untouched_max=float(d[~touch].max()),
               touched_max=float(d[touch].max()),
               touched_n=int(touch.sum()),
               worst_det=worst_det, worst_orth=worst_orth,
               metrics_off={k: mb[k] for k in
                            ('edge_mean', 'edge_p95', 'edge_p99', 'edge_max', 'bad')},
               metrics_on={k: mp[k] for k in
                           ('edge_mean', 'edge_p95', 'edge_p99', 'edge_max', 'bad')},
               rows=[{k: v for k, v in r.items() if k != 'joints'}
                     for r in posed['finger_rows']]),
          open('out/fingerpose-validation.json', 'w', encoding='utf-8'),
          indent=1, default=lambda o: o.tolist() if hasattr(o, 'tolist') else str(o))
open('out/fingerpose-validation.txt', 'w', encoding='utf-8').write('\n'.join(out))
np.savez('out/_fingerpose_check.npz', off=P0.astype(np.float32),
         on=P1.astype(np.float32), touch=touch)
print('[written] out/fingerpose-validation.{json,txt}')
