"""Front-view leg splay probe.

In HD2 coords +X = left, +Y = forward, +Z = up, so the FRONT view is the X-Z
plane and "splaying the legs" is a rotation about Y.

Three things are measured for the reference patch, for HD2 native, and for our
retarget:

  * the bone chain in the front view: hip -> knee -> ankle, as an angle away
    from vertical (positive = the joint moves OUTWARD as it descends);
  * the mesh: the X centre of each leg at knee height and at ankle height;
  * the bottom band (sole), split left/right, to get the actual foot centres.

The point is to find out (a) how much the reference's legs are splayed and
(b) how far the reference's mesh feet sit OUTSIDE its own foot bones -- which
is the thing the user spotted by eye.
"""
import json
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import patch_view as PV

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
REF = ['af431352d9b95e83', 'cae35ee15168145f', '17da843a4908511e',
       '91eb7be84016ceab', '7336c6b662522f0e']
LEG_UNITS = ['17da843a4908511e']          # hips + legs (the geometry we care about)


def ang(a, b):
    """Angle of the a->b segment away from vertical, in the X-Z plane.
    Positive means b is further OUTWARD (larger |x|) than a."""
    dx, dz = b[0] - a[0], b[2] - a[2]
    return math.degrees(math.atan2(abs(dx), abs(dz))) * (1 if abs(b[0]) > abs(a[0]) else -1)


def chain(label, heads, mesh=None):
    print('\n=== %s ===' % label)
    if heads:
        for sd, sgn in (('l', -1), ('r', +1)):
            h = heads.get('%s_thigh' % sd)
            k = heads.get('%s_knee' % sd)
            f = heads.get('%s_foot' % sd)
            if h is None or k is None or f is None:
                print('  %s: missing bones' % sd)
                continue
            print('  %s  hip x %+.4f z %.4f | knee x %+.4f z %.4f | ankle x %+.4f z %.4f'
                  % (sd, h[0], h[2], k[0], k[2], f[0], f[2]))
            print('     splay hip->knee %+.2f deg | knee->ankle %+.2f deg | hip->ankle %+.2f deg'
                  % (ang(h, k), ang(k, f), ang(h, f)))
        if heads.get('l_thigh') is not None and heads.get('r_thigh') is not None:
            print('     hip span  %.4f m' % abs(heads['r_thigh'][0] - heads['l_thigh'][0]))
            print('     knee span %.4f m' % abs(heads['r_knee'][0] - heads['l_knee'][0]))
            print('     ankle span %.4f m' % abs(heads['r_foot'][0] - heads['l_foot'][0]))
    if mesh is not None and len(mesh):
        P = np.asarray(mesh)
        lo = P[:, 2].min()
        for band, what in ((0.02, 'sole  z<%+.2f' % (lo + 0.02)),
                           (0.05, 'ankle z<%+.2f' % (lo + 0.05))):
            q = P[P[:, 2] < lo + band]
            for sd, m in (('l', q[q[:, 0] < 0]), ('r', q[q[:, 0] > 0])):
                if len(m):
                    print('  mesh %s %-18s n=%5d  x[%+.4f,%+.4f] centre %+.4f  y centre %+.4f'
                          % (sd, what, len(m), m[:, 0].min(), m[:, 0].max(),
                             m[:, 0].mean(), m[:, 1].mean()))
        # leg cross-section at knee height (bone knee z) and mid-calf
        for zz, what in ((0.45, 'mid-calf'),):
            q = P[(P[:, 2] > zz - 0.02) & (P[:, 2] < zz + 0.02)]
            for sd, m in (('l', q[q[:, 0] < 0]), ('r', q[q[:, 0] > 0])):
                if len(m):
                    print('  mesh %s %-18s n=%5d  x centre %+.4f'
                          % (sd, what, len(m), m[:, 0].mean()))


def main():
    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    names = PV.read_bone_names(BH)
    ref_heads = PV.patch_rig_heads(meta, names)
    ref_mesh = []
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        if fid in LEG_UNITS:
            p, t = PV.read_obj('out/patchdump/unit-%s.obj' % fid)
            ref_mesh.extend(p)
    chain('REFERENCE PATCH', ref_heads, ref_mesh)

    # HD2 native
    hd2 = PV.hd2_rig_heads('avatar/unit_0x4d1c334d294dfa97.toc', BH)
    chain('HD2 NATIVE', hd2, None)

    # the L4D2 source rig
    chain('L4D2 SOURCE', PV.src_rig_heads(
        'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
        'Lunalice_Zoey.vpk', 'survivor_teenangst'), None)

    # ours
    import hd2_style as H
    r = H.retarget('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
                   'Lunalice_Zoey.vpk', 'survivor_teenangst',
                   'avatar/unit_0x4d1c334d294dfa97.toc', BH,
                   '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json',
                   align='yaw', spine_map='mesh', fallback='distance', verbose=False)
    chain('OURS (leg-tilt 0)', r['new_head'], np.asarray(r['out']))


if __name__ == '__main__':
    main()
