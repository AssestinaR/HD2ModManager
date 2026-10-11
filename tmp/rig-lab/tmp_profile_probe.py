import json, os, sys
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
import hd2_style as H

TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
BH = 'tmp_bh.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
VPK = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/Lunalice_Zoey.vpk'
MODEL = 'survivor_teenangst'

def load_obj(p):
    P = []
    for line in open(p, encoding='utf-8', errors='replace'):
        if line.startswith('v '):
            _, x, y, z = line.split()[:4]
            P.append((float(x), float(y), float(z)))
    return np.array(P)

def slab_profile(P, label, zs):
    print('-- %s --  bbox y[%+.3f,%+.3f] z[%+.3f,%+.3f]' %
          (label, P[:, 1].min(), P[:, 1].max(), P[:, 2].min(), P[:, 2].max()))
    print('   %6s %8s %8s %8s %8s' % ('z', 'y_min', 'y_max', 'y_mid', 'x_w'))
    for z in zs:
        m = P[(np.abs(P[:, 2] - z) < 0.02) & (np.abs(P[:, 0]) < 0.05)]
        if len(m) < 5:
            continue
        print('   %6.2f %+8.4f %+8.4f %+8.4f %8.3f'
              % (z, m[:, 1].min(), m[:, 1].max(), m[:, 1].mean(),
                 m[:, 0].max() - m[:, 0].min()))

zs = [0.10, 0.30, 0.60, 0.90, 1.00, 1.10, 1.20, 1.30, 1.40, 1.50, 1.60, 1.70]

# --- patch: hip unit + torso unit (HD2 space already) -----------------------
hip = load_obj('out/patchdump/unit-17da843a4908511e.obj')
tor = load_obj('out/patchdump/unit-cae35ee15168145f.obj')
slab_profile(np.vstack([hip, tor]), 'PATCH (hip 0x17da + torso 0xcae3)', zs)

# --- ours ------------------------------------------------------------------
r = H.retarget(VPK, MODEL, TOC, BH, PP, verbose=False)
slab_profile(np.asarray(r['src1']), 'OURS aligned source (T-pose)', zs)
slab_profile(np.asarray(r['out']), 'OURS final (A-pose)', zs)

# --- HD2 native mesh is not available offline; use joint heads --------------
print()
for tag, heads in (('SRC(raw)', None),):
    pass
