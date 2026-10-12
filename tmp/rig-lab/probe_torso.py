"""Step 2 groundwork: how does OUR torso compare with the REFERENCE torso?

Both meshes sit on the SAME HD2 native rig, so the rig's own joints are a fair
common frame.  Slice the body between two rig heights and report, per slice, the
x extent (width), the y extent (depth) and the |x| percentiles, for both meshes.

The arms are the problem: in A-pose they hang beside the chest, so a raw x extent
at chest height measures the arm span, not the torso.  Two views are printed so
the torso can be read off unambiguously:
  * y extent  -- arms barely affect depth, so this is the honest torso depth;
  * |x| p50   -- half the vertices are inside the torso, arms are the minority.
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
REF = ['af431352d9b95e83', 'cae35ee15168145f', '17da843a4908511e',
       '91eb7be84016ceab', '7336c6b662522f0e']


def load():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                   align='yaw', spine_map='mesh', fallback='distance',
                   leg_tilt=5.7, leg_splay=4.0, skeleton='native',
                   mesh_shift=(0.0, -0.100, -0.060), verbose=False)
    ours = np.asarray(r['out'], dtype=np.float64)
    rig = {h['name']: np.asarray(r['rest_hd2'][h['index']][:3, 3])
           for h in r['hd2'] if h['name']}
    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    rp = []
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        if fid in REF:
            p, t = PV.read_obj('out/patchdump/unit-%s.obj' % fid)
            if t:
                rp.extend(p)
    return ours, np.asarray(rp, dtype=np.float64), rig


def slice_stats(P, z0, z1, band):
    sel = P[np.abs(P[:, 2] - (z0 + z1) / 2) < band]
    if not len(sel):
        return None
    ax = np.abs(sel[:, 0])
    return dict(n=len(sel),
                wx=sel[:, 0].max() - sel[:, 0].min(),
                wy=sel[:, 1].max() - sel[:, 1].min(),
                px50=float(np.percentile(ax, 50)),
                px90=float(np.percentile(ax, 90)),
                px99=float(np.percentile(ax, 99)))


def main():
    ours, ref, rig = load()
    print('rig joints (both meshes share this rig):')
    for nm in ('hips', 'spine1', 'spine2', 'chest', 'neck', 'head',
               'l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand',
               'r_shoulder', 'r_elbow', 'r_hand'):
        if nm in rig:
            print('   %-12s z %.4f   y %+.4f   x %+.4f' % (nm, rig[nm][2],
                                                           rig[nm][1], rig[nm][0]))
    ho = float(ours[:, 2].max() - ours[:, 2].min())
    hr = float(ref[:, 2].max() - ref[:, 2].min())
    print('\nheight  ours %.4f   ref %.4f   ratio %.4f' % (ho, hr, hr / ho))
    print('sole z  ours %.4f   ref %.4f' % (ours[:, 2].min(), ref[:, 2].min()))
    print('bbox    ours x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%+.3f,%+.3f]'
          % (ours[:, 0].min(), ours[:, 0].max(), ours[:, 1].min(),
             ours[:, 1].max(), ours[:, 2].min(), ours[:, 2].max()))
    print('        ref  x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%+.3f,%+.3f]'
          % (ref[:, 0].min(), ref[:, 0].max(), ref[:, 1].min(),
             ref[:, 1].max(), ref[:, 2].min(), ref[:, 2].max()))

    z0 = float(rig['hips'][2])
    z1 = float(rig['neck'][2])
    print('\ntorso profile  hips z %.3f -> neck z %.3f  (band %.3f m)'
          % (z0, z1, 0.02))
    print('%-7s | %-26s | %-26s | %s'
          % ('z', 'OURS  wy  |x|p50  |x|p90', 'REF   wy  |x|p50  |x|p90',
             'ratio wy / p50'))
    n = 18
    for i in range(n):
        a = z0 + (z1 - z0) * i / n
        b = z0 + (z1 - z0) * (i + 1) / n
        so = slice_stats(ours, a, b, 0.02)
        sr = slice_stats(ref, a, b, 0.02)
        if not so or not sr:
            continue
        print('%.3f   | %7.3f %7.3f %7.3f      | %7.3f %7.3f %7.3f      | %.3f / %.3f'
              % ((a + b) / 2, so['wy'], so['px50'], so['px90'],
                 sr['wy'], sr['px50'], sr['px90'],
                 sr['wy'] / max(so['wy'], 1e-6), sr['px50'] / max(so['px50'], 1e-6)))

    print('\nhand gap (mesh weighted centroid of hand-dominant verts vs the rig joint):')
    idx2name = {h['index']: h['name'] for h in _last['hd2']}
    for side in ('l', 'r'):
        nm = side + '_hand'
        acc, w = np.zeros(3), 0.0
        for k, v in enumerate(_last['verts']):
            j = int(np.argmax(v['w']))
            if v['w'][j] <= 0:
                continue
            if idx2name.get(_last['resolved'].get(v['bones'][j])) == nm:
                acc += ours[k]
                w += 1.0
        if w:
            c = acc / w
            print('   %s  centroid (%+.3f %+.3f %+.3f)  joint (%+.3f %+.3f %+.3f)'
                  '  gap %.3f m' % (nm, c[0], c[1], c[2], rig[nm][0], rig[nm][1],
                                    rig[nm][2], np.linalg.norm(c - rig[nm])))
        # and the extreme fingertip
        sel = np.array([ours[k] for k, v in enumerate(_last['verts'])
                        if idx2name.get(_last['resolved'].get(
                            v['bones'][int(np.argmax(v['w']))])) == nm])
        if len(sel):
            print('       farthest vertex from joint: %.3f m'
                  % float(np.linalg.norm(sel - rig[nm], axis=1).max()))


_last = {}


def load2():
    _last.update(dict(zip(('out', 'ref', 'rig'), (None, None, None))))
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                   align='yaw', spine_map='mesh', fallback='distance',
                   leg_tilt=5.7, leg_splay=4.0, skeleton='native',
                   mesh_shift=(0.0, -0.100, -0.060), verbose=False)
    _last.update(r)


if __name__ == '__main__':
    load2()
    main()
