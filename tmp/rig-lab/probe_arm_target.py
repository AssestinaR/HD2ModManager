"""Round 45: where does the ARM need to reach?  HD2's own finger bones answer it.

Prints, for the left arm, the position of every HD2 arm/finger bone head measured
along HD2's own `l_shoulder -> l_hand` axis (t in metres and as a fraction of the
shoulder->wrist bone), next to the t-range of our mesh and of the reference
sleeve.  That gives the target for any stretch we apply.
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import patch_view as PV
import export_obj as E

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
REF = {'l': 'out/patchdump/unit-91eb7be84016ceab.obj',
       'r': 'out/patchdump/unit-7336c6b662522f0e.obj'}


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='region', leg_tilt=5.7,
                   leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                   upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                   verbose=False)
    rig = E.read_hd2_rig(TOC, BH)
    head = {h['name']: np.array([h['world'][3], h['world'][7], h['world'][11]])
            for h in rig if h['name']}

    for side in ('l', 'r'):
        sh, hd = head['%s_shoulder' % side], head['%s_hand' % side]
        u = hd - sh
        L = float(np.linalg.norm(u))
        u = u / L
        print('\n============ %s arm : t along shoulder->hand (bone %.4f m) ============'
              % (side.upper(), L))
        names = [n for n in head if n.startswith(side + '_') and
                 any(k in n for k in ('clavicle', 'shoulder', 'elbow', 'hand',
                                      'finger'))]
        # keep the chain readable: sort by t
        rows = []
        for n in names:
            t = float((head[n] - sh) @ u)
            rows.append((t, n))
        for t, n in sorted(rows):
            print('   t %+.4f m (%+5.2f x bone)   %s' % (t, t / L, n))

        # our mesh, per dominant bone, and the reference
        res, verts, out = r['resolved'], r['verts'], r['out']
        by_idx_name = {h['index']: h['name'] for h in r['hd2'] if h['name']}
        buckets = {}
        for k, v in enumerate(verts):
            best, bw = None, -1.0
            for j in range(3):
                w = v['w'][j]
                if w <= bw:
                    continue
                hb = res.get(v['bones'][j])
                if hb is None:
                    continue
                bw, best = w, hb
            nm = by_idx_name.get(best)
            if nm and nm.startswith(side + '_'):
                buckets.setdefault(nm, []).append(out[k])
        print('   -- our mesh, centroid t of each dominant bone cloud --')
        for n in sorted(buckets, key=lambda x: float((np.mean(buckets[x], 0) - sh) @ u)):
            P = np.asarray(buckets[n], float)
            print('   t %+.4f m (%+5.2f x bone)   %-22s  N=%d'
                  % (float((P.mean(0) - sh) @ u), float((P.mean(0) - sh) @ u) / L,
                     n, len(P)))
        ref, _t = PV.read_obj(REF[side])
        R = np.asarray(ref, float)
        tr = (R - sh) @ u
        print('   -- reference sleeve: t [%+.4f, %+.4f] m = [%+5.2f, %+5.2f] x bone'
              % (tr.min(), tr.max(), tr.min() / L, tr.max() / L))


if __name__ == '__main__':
    main()
