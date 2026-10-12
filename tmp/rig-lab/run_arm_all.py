"""Round 45: five survivors, the FULL current config, with the arm re-fit on.

Checks that the arm re-fit needs no per-model tuning: for each model it prints
where the mesh's elbow boundary and fingertip land along HD2's own arm axis,
next to where HD2's elbow and fingertip BONES are.
"""
import os
import sys
import traceback

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import export_obj as E

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
ADD = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons'
MODELS = [('Lunalice_Zoey.vpk', 'survivor_teenangst', 'zoey'),
          ('Lunalice_Rochelle.vpk', 'survivor_producer', 'rochelle'),
          ('Elise_Coach.vpk', 'survivor_coach', 'coach'),
          ('Lilium_Sancta_Nick.vpk', 'survivor_gambler', 'nick'),
          ('Serenade_Ellis.vpk', 'survivor_mechanic', 'ellis')]
FINGERS = ['%s_%s_finger%d' % (s, f, i)
           for s in ('l', 'r') for f in ('index', 'middle', 'ring', 'pinky', 'thumb')
           for i in (1, 2, 3)]


def arm_metrics(r, head, arm_fit):
    res, verts, out = r['resolved'], r['verts'], r['out']
    by_idx_name = {h['index']: h['name'] for h in r['hd2'] if h['name']}
    dom = []
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
        dom.append((by_idx_name.get(best), out[k]))
    out_s = []
    for side in ('l', 'r'):
        sh = head['%s_shoulder' % side]
        hd = head['%s_hand' % side]
        u = (hd - sh) / np.linalg.norm(hd - sh)
        L = float(np.linalg.norm(hd - sh))
        up = set(['%s_shoulder' % side, '%s_shoulder_twist' % side,
                  '%s_shoulderarmour' % side])
        fg = set(n for n in FINGERS if n.startswith(side + '_'))
        Pu = np.asarray([p for n, p in dom if n in up], float)
        Pf = np.asarray([p for n, p in dom if n in fg], float)
        te = float(((Pu - sh) @ u).max()) if len(Pu) else float('nan')
        tf = float(((Pf - sh) @ u).max()) if len(Pf) else float('nan')
        be = float((head['%s_elbow' % side] - sh) @ u)
        bf = float((head['%s_middle_finger3' % side] - sh) @ u)
        out_s.append('  %s arm_fit=%.1f  elbow %+.4f vs bone %+.4f (%+.4f) | '
                     'tip %+.4f vs bone %+.4f (%+.4f)'
                     % (side.upper(), arm_fit, te, be, te - be, tf, bf, tf - bf))
    return out_s, (float(np.linalg.norm(np.asarray(r['out_idle'])
                                        - np.asarray(r['out']), axis=1).max()))


def main():
    rig = E.read_hd2_rig(TOC, BH)
    head = {h['name']: np.array([h['world'][3], h['world'][7], h['world'][11]])
            for h in rig if h['name']}
    print('%-9s %-4s %10s %7s %8s %9s  %s'
          % ('model', 'fit', 'edge_mean', 'bad%', 'height', 'idle max', 'arm'))
    for vpk, model, tag in MODELS:
        for arm_fit in (0.0, 1.0):
            try:
                r = H.retarget(os.path.join(ADD, vpk), model, TOC, BH, PP,
                               align='yaw', spine_map='mesh', fallback='region',
                               leg_tilt=5.7, leg_splay=4.0, skeleton='native',
                               mesh_shift=(0.0, -0.100, -0.060),
                               upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                               arm_fit=arm_fit, verbose=False)
            except Exception as exc:                                  # noqa: BLE001
                print('%-9s %-4.1f FAILED %s' % (tag, arm_fit, exc))
                traceback.print_exc()
                continue
            m = r['metrics']
            lines, idle = arm_metrics(r, head, arm_fit)
            print('%-9s %-4.1f %10.4f %6.2f%% %8.3f %9.1e  %s'
                  % (tag, arm_fit, m['edge_mean'], 100 * m['bad'],
                     m['height_after'], idle, lines[0].strip()))
            for ln in lines[1:]:
                print('%-9s %-4s %10s %7s %8s %9s  %s' % ('', '', '', '', '', '',
                                                          ln.strip()))


if __name__ == '__main__':
    main()
