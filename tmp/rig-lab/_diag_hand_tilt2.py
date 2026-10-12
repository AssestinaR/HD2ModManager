# -*- coding: utf-8 -*-
"""Round 53 (v2): the hand tilt about the wrist -- measured, then drawn.

Why v2: the v1 picture squeezed three views of the WHOLE ARM into 1320x740, so
the 10 deg we are actually arguing about came out about four pixels wide and the
reference hand was a handful of stray cyan specks.  This one draws THREE
orthographic views of the LEFT HAND ALONE, zoomed to the union of our hand and
the reference hand, and it prints the two numbers a wrist rotation actually
moves:

  * the hand's LONG AXIS -- `D[l_hand] . z`, z = the T-pose pipe axis (elbow->
    wrist).  This is the exact axis, not a centroid fit.
  * the PALM SLAB's plane NORMAL -- PCA of the palm vertices.  Translation
    invariant, so it isolates roll.  (Plane RMS ~8-9 mm, so treat it as a
    relative indicator, not gospel.)

grey  = our hand, tilt 0        (what we ship right now)
blue  = our hand, tilt +DEG     (extension -- the hand tips toward the back)
amber = our hand, tilt -DEG     (flexion)
cyan  = the REFERENCE patch's own left hand
yellow dot = the wrist joint (HD2 native -- it cannot move)

usage: python _diag_hand_tilt2.py [deg]
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import patch_view as PV

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
DEG = float(sys.argv[1]) if len(sys.argv) > 1 else 10.0

BG = (18, 20, 26)
COLS = {'0': (118, 122, 134), '+': (92, 142, 238), '-': (218, 152, 58)}
C_REF = (52, 200, 220)


def run(t):
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                      spine_map='mesh', fallback='region', leg_tilt=5.7,
                      leg_splay=4.0, skeleton='native',
                      mesh_shift=(0.0, -0.100, -0.090),
                      upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                      head_scale=8.0 / 7.0, arm_scratch_upper_gain=0.05,
                      arm_elbow_open=6.5, arm_hand_tilt=t, verbose=False)


def u(v):
    v = np.asarray(v, float)
    return v / max(float(np.linalg.norm(v)), 1e-12)


def ang(a, b):
    return float(np.degrees(np.arccos(np.clip(abs(u(a) @ u(b)), -1, 1))))


def signed(a, b, axis):
    """signed angle from a to b about `axis`"""
    return float(np.degrees(np.arctan2(float(np.cross(a, b) @ u(axis)),
                                       float(u(a) @ u(b)))))


runs = [('0', run(0.0)), ('+', run(DEG)), ('-', run(-DEG))]
r0 = runs[0][1]
pos = {h['name']: h['index'] for h in r0['hd2']}
children = {}
for h in r0['hd2']:
    children.setdefault(h['parent'], []).append(h['index'])


def subtree(root):
    out, st = set(), [root]
    while st:
        c = st.pop()
        if c in out:
            continue
        out.add(c)
        st.extend(children.get(c, []))
    return out


hand_sub = subtree(pos['l_hand'])
res = r0['resolved']
dom = np.array([res.get(v['bones'][int(np.argmax(v['w']))], -1) for v in r0['verts']])
is_dom = np.isin(dom, list(hand_sub))

# The DOMINANT-BONE set is not the hand: it came out with a 0.204 m radius from
# the wrist, i.e. it swallows a slab of forearm -- and the forearm does NOT move
# when we tilt the wrist, so it just dilutes every metric.  Define the hand by
# what the transform actually moves instead.
#
# Two traps, both hit on the way here:
#   * the pipeline tilts BOTH arms, so a plain displacement test also returns the
#     right hand (11126 verts, 1.44 m radius);
#   * keeping only the left half still left the ELBOW in (5574 verts, 0.308 m
#     radius, y down to -0.072 -- the elbow is at y = -0.054).  Those are
#     forearm vertices carrying a sliver of l_hand weight; they move 2-3 mm
#     while a fingertip moves 35 mm.  So threshold on the displacement's own
#     scale: keep only what moves at least 20% as far as the farthest vertex.
P0 = np.asarray(runs[0][1]['out'], float)
P1 = np.asarray(runs[1][1]['out'], float)
disp = np.linalg.norm(P1 - P0, axis=1)
_wr = np.array(r0['rest_shipped'][pos['l_hand']][:3, 3], float)
_rad = np.linalg.norm(P0 - _wr, axis=1)
is_hand = (disp > 0.20 * disp.max()) & (P0[:, 0] < 0.0) & (_rad < 0.20)
print('hand by displacement: %d verts, max displacement %.4f m '
      '(dominant-bone set was %d, only %d agree)'
      % (int(is_hand.sum()), disp.max(), int(is_dom.sum()),
         int((is_hand & is_dom).sum())))

wrist = np.array(r0['rest_shipped'][pos['l_hand']][:3, 3], float)
zax = u(np.array(r0['rest'][pos['l_hand']][:3, 3], float)
        - np.array(r0['rest'][pos['l_elbow']][:3, 3], float))   # T-pose pipe axis
zap = u(wrist - np.array(r0['rest_shipped'][pos['l_elbow']][:3, 3], float))
xap = u(np.array(r0['rest_shipped'][pos['l_thumb_finger1']][:3, 3], float) - wrist)
nap = u(np.cross(zap, xap))

# ---------------------------------------------------------------- the reference
ref = None
for fn in ('out/patchdump/unit-91eb7be84016ceab.obj',
           'out/patchdump/unit-7336c6b662522f0e.obj'):
    try:
        p, _ = PV.read_obj(fn)
        p = np.asarray(p, float)
    except Exception:
        continue
    if p[:, 0].mean() < 0:
        ref = p
        break
if ref is not None:
    rw = np.linalg.norm(ref - wrist, axis=1)
    ref = ref[(rw < 0.15) & (((ref - wrist) @ zap) > 0.01)]


def palm_slab(P, sel=None):
    """the palm slab: distal of the wrist, but NOT out along the thumb.

    The vertex SET is computed once (on tilt 0) and then reused for every run --
    a fixed vertex set is the only way to compare rotations honestly.
    """
    rel = P - wrist
    t = rel @ zap
    r = np.maximum(np.linalg.norm(rel, axis=1), 1e-9)
    ath = np.degrees(np.arccos(np.clip((rel @ xap) / r, -1, 1)))
    if sel is None:
        sel = (r < 0.105) & (t > 0.012) & (t < 0.098) & (ath > 45)
    return P[sel], sel


def plane_normal(Q):
    c = Q.mean(0)
    _w, _v = np.linalg.eigh((Q - c).T @ (Q - c))
    return u(_v[:, 0]), float(np.sqrt(((Q - c) @ _v[:, 0]) ** 2).mean())


def nn_dist(A, B, chunk=512):
    """symmetric nearest-neighbour distance, A->B.  Both clouds are the SAME
    hand from two different models, so the distance has a floor -- what we read
    is the TREND across the three tilts, not the absolute value."""
    out = np.empty(len(A))
    for i in range(0, len(A), chunk):
        blk = A[i:i + chunk]
        out[i:i + chunk] = np.sqrt(
            ((blk[:, None, :] - B[None, :, :]) ** 2).sum(-1)).min(1)
    return out


ALL0 = np.asarray(r0['out'], float)
_, SEL_ALL = palm_slab(ALL0)          # the slab's vertex SET, fixed once, at tilt 0
O0 = ALL0[SEL_ALL]
_, SEL_HAND = palm_slab(ALL0[is_hand])

print('wrist %s   |  our hand-dominant vertices %d (palm slab %d, of which %d '
      'carry hand weights)' % (np.array2string(wrist, precision=4),
                               int(is_hand.sum()), int(SEL_ALL.sum()),
                               int(SEL_HAND.sum())))
print('zap (A-pose forearm) %s   xap (to thumb) %s'
      % (np.array2string(zap, precision=4), np.array2string(xap, precision=4)))
_hb = ALL0[is_hand]
print('our hand cloud bbox  x[%.3f,%.3f] y[%.3f,%.3f] z[%.3f,%.3f]   '
      '(max radius from the wrist %.3f m)'
      % (_hb[:, 0].min(), _hb[:, 0].max(), _hb[:, 1].min(), _hb[:, 1].max(),
         _hb[:, 2].min(), _hb[:, 2].max(),
         float(np.linalg.norm(_hb - wrist, axis=1).max())))
if ref is not None:
    print('reference cloud bbox x[%.3f,%.3f] y[%.3f,%.3f] z[%.3f,%.3f]   '
          '(max radius from the wrist %.3f m)'
          % (ref[:, 0].min(), ref[:, 0].max(), ref[:, 1].min(), ref[:, 1].max(),
             ref[:, 2].min(), ref[:, 2].max(),
             float(np.linalg.norm(ref - wrist, axis=1).max())))

n_o0, rms_o = plane_normal(O0)
print()
print('%-8s %-32s %-12s %-32s %s'
      % ('', 'hand LONG AXIS (world)', 'vs tilt 0', 'PALM NORMAL (world)', 'vs tilt 0'))
base_ax = u(r0['D'][pos['l_hand']][:3, :3] @ zax)
meas = {}
for tag, r in runs:
    P = np.asarray(r['out'], float)
    ax = u(r['D'][pos['l_hand']][:3, :3] @ zax)
    Q, _ = palm_slab(P, SEL_ALL)
    n, rms = plane_normal(Q)
    if float(n @ n_o0) < 0:
        n = -n
    meas[tag] = (ax, n)
    print('%-8s %-32s %+8.2f deg  %-32s %+8.2f deg'
          % ('tilt %s' % tag, np.array2string(ax, precision=4),
             signed(base_ax, ax, np.cross(base_ax, ax)) if ang(base_ax, ax) > 1e-6 else 0.0,
             np.array2string(n, precision=4),
             signed(n_o0, n, np.cross(n_o0, n)) if ang(n_o0, n) > 1e-6 else 0.0))
print('  (palm-slab plane RMS %.4f m -- a soft number, read it as a trend)'
      % plane_normal(O0)[1])

if ref is not None and len(ref) > 8:
    R, nref_v = palm_slab(ref)
    n_r, rms_r = plane_normal(R)
    if float(n_r @ n_o0) < 0:
        n_r = -n_r
    _w, _v = np.linalg.eigh(((R - R.mean(0)).T @ (R - R.mean(0))))
    ax_r = u(_v[:, -1])
    if float(ax_r @ zap) < 0:
        ax_r = -ax_r
    print()
    print('REFERENCE hand: %d verts, plane RMS %.4f m' % (len(ref), rms_r))
    print('  long axis (PCA) %s   | vs our tilt 0: %+.2f deg about %s'
          % (np.array2string(ax_r, precision=4), signed(base_ax, ax_r, np.cross(base_ax, ax_r)),
             np.array2string(u(np.cross(base_ax, ax_r)), precision=3)))
    print('  (PCA on a cloud that our own thumb filter cut -- soft, read the trend)')
    print('  palm normal     %s' % np.array2string(n_r, precision=4))
    for tag, _r in runs:
        _ax, n = meas[tag]
        print('    tilt %-3s -> angle between palm normals: %5.2f deg' % (tag, ang(n, n_r)))
    print('    (the axis carrying ours onto the reference: %s)'
          % np.array2string(u(np.cross(n_o0, n_r)), precision=3))
    print('    vs xap(thumb) %.1f | vs zap(forearm) %.1f | vs nap %.1f deg'
          % (ang(np.cross(n_o0, n_r), xap), ang(np.cross(n_o0, n_r), zap),
             ang(np.cross(n_o0, n_r), nap)))
    print()
    print('HOW WELL DO THE TWO HANDS ACTUALLY OVERLAP?  nearest-neighbour, metres')
    print('  %-8s %-24s %-24s' % ('', 'ours -> ref  (mean/median)', 'ref -> ours  (mean/median)'))
    for tag, _r in runs:
        A = np.asarray(_r['out'], float)[is_hand]
        d1, d2 = nn_dist(A, ref), nn_dist(ref, A)
        print('  tilt %-3s %-24s %-24s'
              % (tag, '%.4f / %.4f' % (d1.mean(), np.median(d1)),
                 '%.4f / %.4f' % (d2.mean(), np.median(d2))))

# ---------------------------------------------------------------- picture
PW, PH, PAD, TOPY = 600, 620, 28, 124
W = PAD * 4 + PW * 3
HT = TOPY + PH + 46
img = Image.new('RGB', (W, HT), BG)
dr = ImageDraw.Draw(img)


def blob(f, p, ia, ib, col, rad=1):
    # NB: pass the axis indices.  An earlier version read p[0], p[1] inside, so
    # every panel silently drew the XY projection -- FRONT and SIDE came out
    # empty and it looked like the clouds were missing.
    x, y = f(p[ia], p[ib])
    if 0 <= x < W and 0 <= y < HT:
        dr.rectangle([x - rad, y - rad, x + rad, y + rad], fill=col)


def mkf(box, lo0, hi0, lo1, hi1):
    px0, py0, px1, py1 = box
    return lambda a, b: (px0 + (a - lo0) / (hi0 - lo0) * (px1 - px0),
                         py1 - (b - lo1) / (hi1 - lo1) * (py1 - py0))


# window = union of our hand and the reference hand, 12% pad
sel_all = np.asarray(r0['out'], float)[is_hand]
pts = sel_all if ref is None or len(ref) == 0 else np.vstack([sel_all, ref])
for extra in (np.array(meas['+'][0]) * 0.17, np.array(meas['-'][0]) * 0.17,
              n_o0 * 0.10, -n_o0 * 0.10):
    pts = np.vstack([pts, wrist + extra])
lo, hi = pts.min(0), pts.max(0)
pad = 0.12 * (hi - lo).max()
lo, hi = lo - pad, hi + pad
mid = 0.5 * (lo + hi)
span = max(float((hi - lo).max()), 1e-6)
lo, hi = mid - 0.5 * span, mid + 0.5 * span

VIEWS = [('TOP   (X right, Y fwd)', 0, 1), ('FRONT (X, Z up)', 0, 2),
         ('SIDE  (Y fwd, Z up)', 1, 2)]
fns = []
for i, (title, ia, ib) in enumerate(VIEWS):
    px0 = PAD + i * (PW + PAD)
    box = (px0, TOPY, px0 + PW, TOPY + PH)
    f = mkf(box, lo[ia], hi[ia], lo[ib], hi[ib])
    fns.append((f, ia, ib))
    step = 0.05 if span < 0.45 else 0.10
    g0 = np.ceil(lo[ia] / step) * step
    g1 = np.ceil(lo[ib] / step) * step
    for a in np.arange(g0, hi[ia] + 1e-9, step):
        px, _ = f(a, lo[ib])
        dr.line([(px, TOPY), (px, TOPY + PH)], fill=(30, 34, 42))
        dr.text((px - 15, TOPY + PH + 6), '%+.2f' % a, fill=(96, 106, 124))
    for b in np.arange(g1, hi[ib] + 1e-9, step):
        _, py = f(lo[ia], b)
        dr.line([(px0, py), (px0 + PW, py)], fill=(38, 42, 52))
        dr.text((px0 - 42, py - 6), '%+.2f' % b, fill=(96, 106, 124))
    dr.rectangle([px0, TOPY, px0 + PW, TOPY + PH], outline=(48, 54, 66))
    dr.text((px0, TOPY - 22), title, fill=(206, 212, 226))
    dr.text((px0, TOPY - 38), 'span %.3f m' % span, fill=(112, 122, 140))

# our three clouds, thin; the reference on top, thick -- it must be visible
for tag, _r in runs:
    P = np.asarray(_r['out'], float)[is_hand][::10]
    for p in P:
        for f, ia, ib in fns:
            blob(f, p, ia, ib, COLS[tag], 1)
if ref is not None:
    for p in ref[::4]:
        for f, ia, ib in fns:
            blob(f, p, ia, ib, C_REF, 2)

# long axes and palm normals, drawn from the wrist
for tag, _r in runs:
    ax, n = meas[tag]
    for f, ia, ib in fns:
        e = wrist + ax * 0.16
        dr.line([f(wrist[ia], wrist[ib]), f(e[ia], e[ib])], fill=COLS[tag], width=3)
        e2 = wrist + n * 0.075
        dr.line([f(wrist[ia], wrist[ib]), f(e2[ia], e2[ib])], fill=COLS[tag], width=2)
if ref is not None and len(ref) > 8:
    for f, ia, ib in fns:
        e = wrist + n_r * 0.075
        dr.line([f(wrist[ia], wrist[ib]), f(e[ia], e[ib])], fill=C_REF, width=2)
for f, ia, ib in fns:
    x, y = f(wrist[ia], wrist[ib])
    dr.ellipse([x - 5, y - 5, x + 5, y + 5], fill=(255, 214, 92))

dr.text((PAD, 16), 'HAND tilt about the wrist, LEFT hand -- THREE views of the '
        'hand only.  Axis = the T-pose THUMB (= pipe surface normal = world +Y there).',
        fill=(234, 234, 246))
dr.text((PAD, 34), 'grey = tilt 0 (shipped)   |   blue = tilt %+.0f (extension, '
        'toward the back of the hand)   |   amber = tilt %+.0f (flexion)   |   '
        'cyan = REFERENCE patch hand' % (DEG, -DEG), fill=(152, 162, 182))
dr.text((PAD, 52), 'thick line = the hand\'s exact LONG AXIS (D[l_hand] . z, z = the '
        'T-pose pipe axis).   thin line = the palm-slab plane NORMAL (PCA).',
        fill=(112, 122, 140))
dr.text((PAD, 70), 'yellow dot = the wrist joint (HD2 native -- it cannot move).  '
        'Grid step %.2f m.' % (0.05 if span < 0.45 else 0.10), fill=(112, 122, 140))

out = os.path.abspath('out/hand-tilt-%g-v2.png' % DEG)
img.save(out)
print('\n[written] %s' % out)
