"""Fit the splay angle that best reproduces the reference's leg profile.

The reference's mesh is only in the leg unit, so its profile is clean.  Both
profiles are expressed as |x| of the left leg's centroid at a fraction of body
height, which is the only scale-free way to compare a 2.113 m figure with a
1.983 m one.  Then we sweep our splay and take the RMS difference.
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
LEG_UNITS = ['17da843a4908511e']
H_REF = 2.113          # the reference patch's full height (head + hair included)
FRACS = np.arange(0.03, 0.31, 0.01)


def profile(P, fracs, h, band=0.008):
    """|x| of the left leg's centroid at z = frac * h, normalised by h.

    `h` is the BODY height, passed in -- the reference's leg unit is only 1.244 m
    tall on its own, and normalising by that instead of by the figure's 2.113 m
    silently inflates every number by 1.7x.
    """
    lo = P[:, 2].min()
    out = []
    for f in fracs:
        z = lo + f * h
        q = P[(P[:, 2] > z - band) & (P[:, 2] < z + band) & (P[:, 0] < -0.02)]
        out.append(abs(q[:, 0].mean()) / h if len(q) >= 8 else np.nan)
    return np.array(out)


def main():
    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    rp, h_ref = [], 0.0
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        if fid in LEG_UNITS:
            p, _t = PV.read_obj('out/patchdump/unit-%s.obj' % fid)
            rp.extend(p)
    ref = np.asarray(rp)
    refp = profile(ref, FRACS, H_REF)
    print('reference profile (|x| / BODY height %.3f):' % H_REF)
    print('  ' + '  '.join('%.2f:%.4f' % (f, v) for f, v in zip(FRACS, refp)
                           if not np.isnan(v)))

    best = None
    print('\n splay |   RMS    | mean signed err | sole x/h | calf@0.23h/h')
    for sp in np.arange(0.0, 8.01, 0.5):
        r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                       spine_map='mesh', fallback='distance', leg_tilt=5.7,
                       leg_splay=float(sp), verbose=False)
        P = np.asarray(r['out'])
        lo = P[:, 2].min()
        h = P[:, 2].max() - lo
        op = profile(P, FRACS, h)
        ok = ~np.isnan(op) & ~np.isnan(refp)
        diff = op[ok] - refp[ok]
        rms = float(np.sqrt((diff ** 2).mean()))
        sole = abs(P[(P[:, 2] < lo + 0.02) & (P[:, 0] < 0)][:, 0].mean()) / h
        cal = abs(P[(P[:, 2] > lo + 0.22 * h) & (P[:, 2] < lo + 0.24 * h)
                    & (P[:, 0] < 0)][:, 0].mean()) / h
        print(' %5.1f | %8.5f | %+15.5f | %8.4f | %8.4f'
              % (sp, rms, float(diff.mean()), sole, cal))
        if best is None or rms < best[0]:
            best = (rms, sp)
    print('\nBEST splay = %.1f deg (RMS %.5f of height = %.1f mm)'
          % (best[1], best[0], best[0] * 1983))
    lo = ref[:, 2].min()
    print('reference sole x/h %.4f  calf@0.23h/h %.4f'
          % (abs(ref[(ref[:, 2] < lo + 0.02) & (ref[:, 0] < 0)][:, 0].mean()) / H_REF,
             abs(ref[(ref[:, 2] > lo + 0.22 * H_REF) & (ref[:, 2] < lo + 0.24 * H_REF)
                     & (ref[:, 0] < 0)][:, 0].mean()) / H_REF))


if __name__ == '__main__':
    main()
