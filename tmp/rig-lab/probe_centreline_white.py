"""Round 45e step 2: the "three axes" centre-line method, done properly.

black background  ->  flat white model  ->  smooth silhouette  ->  centre line

Nothing but pixels: no mesh, no weights, no bones.  Uses the shared tool from the
skill  mesh-deform-diagnosis/scripts/silhouette_centreline.py.

usage: python probe_centreline_white.py
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, r'C:\Users\Administrator\.workbuddy-ai\skills'
                   r'\mesh-deform-diagnosis\scripts')
import hd2_style as H
import patch_view as PV
import silhouette_centreline as SC

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
REF_OBJ = 'out/patchdump/unit-91eb7be84016ceab.obj'
ROT = (10.46, -18.55, 29.63)
PW, PH = 620, 520
BOX = [-0.680, -1.0, 1.180, -0.440, 1.0, 1.370]


def run(**kw):
    base = dict(align='yaw', spine_map='mesh', fallback='region', leg_tilt=5.7,
                leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                verbose=False)
    base.update(kw)
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, **base)


def main():
    builds = [('no arm_rot', dict(arm_fit=1.0, arm_rot=None)),
              ('ALL (current)', dict(arm_fit=1.0, arm_rot=ROT,
                                     arm_rot_mode='axis')),
              ('no elbow rot', dict(arm_fit=1.0, arm_rot=(ROT[0], 0.0, ROT[2]),
                                    arm_rot_mode='axis'))]
    R = [(tag, run(**kw)) for tag, kw in builds]
    ref = PV.read_obj(REF_OBJ)
    cols = [(t, r['out'], r['tris']) for t, r in R]
    cols.append(('REFERENCE', ref[0], ref[1]))

    W = 12 + len(cols) * (PW + 10)
    Hh = 74 + PH + 40
    img = Image.new('RGB', (W, Hh), (0, 0, 0))
    dr = ImageDraw.Draw(img)
    dr.text((12, 8), 'round 45e step 2 -- "three axes": BLACK background, FLAT WHITE '
            'model, no bones, no lighting', fill=(232, 232, 244))
    dr.text((12, 26), 'left forearm only.  a flat white blob on black is a binary mask, '
            'so "the middle of the silhouette in this column" IS the centre line.',
            fill=(150, 160, 180))
    dr.text((12, 44), 'box  x[-0.680,-0.440]  z[1.180,1.370]  = elbow to wrist, nothing else  --  red '
            'must be a straight line from the elbow to the wrist.', fill=(150, 160, 180))

    stats = []
    for ci, (tag, pts, tris) in enumerate(cols):
        cx = 12 + ci * (PW + 10)
        cy = 68
        dr.text((cx + 4, cy - 14), tag, fill=(205, 214, 228))
        p = PV.Panel(img, dr, (cx, cy, cx + PW, cy + PH), BOX, 0, 2, 1, False)
        use = tris
        if tag != 'REFERENCE':
            r0 = [r2 for t2, r2 in R if t2 == tag][0]
            by_idx = {h['index']: h['name'] for h in r0['hd2'] if h['name']}
            res0 = r0['resolved']
            keep = np.zeros(len(r0['verts']), bool)
            for j, v in enumerate(r0['verts']):
                best, bw = None, -1.0
                for q in range(3):
                    if v['w'][q] > bw:
                        best, bw = v['bones'][q], v['w'][q]
                keep[j] = bool(H.is_arm_bone(by_idx.get(res0.get(best, -1), '')))
            use = [z for z in tris if all(keep[int(q)] for q in z[:3])]
            print('%-16s arm-bone tris %5d / %5d' % (tag, len(use), len(tris)))
        p.mesh([tuple(q) for q in np.asarray(pts, float)], use, (1.0, 1.0, 1.0), 1)
        # --- the tool, on this column's pixels -----------------------------
        roi = (cx, cy, cx + PW, cy + PH)
        a = np.asarray(img).astype(np.float32)
        fg, bg = SC.foreground(a, 40.0)
        x0, y0, x1, y1 = roi
        sub = SC.close_along(fg[y0:y1, x0:x1], 0, 1)
        mids = SC.centre_line(sub, 'v', 3)
        sm = SC.smooth(mids, 9)
        ok = np.isfinite(sm)
        xs = np.arange(len(sm))
        i0 = int(np.argmax(ok))
        i1 = int(len(ok) - 1 - np.argmax(ok[::-1]))
        line = sm[i0] + (sm[i1] - sm[i0]) * (xs - i0) / max(i1 - i0, 1)
        dev = np.where(ok, sm - line, np.nan)
        d = np.diff(sm[ok])
        flips = int((np.sign(d[1:]) * np.sign(d[:-1]) < 0).sum()) if len(d) > 2 else 0
        devmax = float(np.nanmax(np.abs(dev)))
        stats.append((tag, devmax, flips, int(ok.sum())))
        # draw
        seq = [(x0 + i, y0 + sm[i]) for i in range(len(sm)) if ok[i]]
        for i in range(len(seq) - 1):
            dr.line([seq[i], seq[i + 1]], fill=(255, 60, 60), width=3)
        dr.line([(x0 + i0, y0 + sm[i0]), (x0 + i1, y0 + sm[i1])],
                fill=(80, 170, 255), width=2)
        dr.text((cx + 6, cy + PH + 6), 'devmax %.1f px   flips %d'
                % (devmax, flips), fill=(255, 200, 120))

    print('%-16s %10s %8s %8s' % ('build', 'devmax(px)', 'flips', 'slices'))
    for tag, devmax, flips, n in stats:
        print('%-16s %10.1f %8d %8d' % (tag, devmax, flips, n))
    out = os.path.abspath('out/centreline-white.png')
    img.save(out)
    print('[written] %s  %dx%d' % (out, W, Hh))


if __name__ == '__main__':
    main()
