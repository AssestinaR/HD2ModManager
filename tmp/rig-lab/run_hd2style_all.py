"""Run the BatchTool-style pipeline over all five L4D2 survivors and report.

Also compares the two alignment rotations (kabsch vs an exact 180 deg about Z)
and the two spine wirings, so the choice is made on numbers, not taste.
"""
import argparse
import json
import os
import sys
import traceback

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
import hd2_style as H

MODELS = [
    ('Lunalice_Zoey.vpk', 'survivor_teenangst', 'zoey'),
    ('Lunalice_Rochelle.vpk', 'survivor_producer', 'rochelle'),
    ('Elise_Coach.vpk', 'survivor_coach', 'coach'),
    ('Lilium_Sancta_Nick.vpk', 'survivor_gambler', 'nick'),
    ('Serenade_Ellis.vpk', 'survivor_mechanic', 'ellis'),
]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--addons', default='D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons')
    ap.add_argument('--toc', required=True)
    ap.add_argument('--bonehash', required=True)
    ap.add_argument('--pose-profile', required=True)
    ap.add_argument('--outdir', default='out')
    ap.add_argument('--json-out', default='out/hd2style-report.json')
    a = ap.parse_args()

    os.makedirs(a.outdir, exist_ok=True)
    rows = []
    for vpk, model, tag in MODELS:
        path = os.path.join(a.addons, vpk)
        for align in ('kabsch', 'rotz180'):
            for spine in ('legacy', 'mesh'):
                key = '%s/%s/%s' % (tag, align, spine)
                try:
                    r = H.retarget(path, model, a.toc, a.bonehash, a.pose_profile,
                                   align=align, spine_map=spine, verbose=False)
                except Exception as exc:                      # noqa: BLE001
                    print('%-26s FAILED %s' % (key, exc))
                    traceback.print_exc()
                    continue
                m = r['metrics']
                rows.append(dict(model=tag, **m))
                print('%-26s edge mean %.4f max %6.2f bad %5.2f%%  height %.3f  '
                      'foot spread src %.3f out %.3f  scale %.4f  snap %d'
                      % (key, m['edge_mean'], m['edge_max'], 100 * m['bad'],
                         m['height_after'], m['foot_spread_src'],
                         m['foot_spread_out'], m['scale'], m['snap_count']))

                if align == 'kabsch' and spine == 'legacy':
                    png = os.path.join(a.outdir, '%s-hd2style.png' % tag)
                    clean, good = H.drop_outlier_tris(r['src_pts'], r['tris'])
                    print('        render: %d/%d tris after dropping stray geometry'
                          % (len(clean), len(r['tris'])))
                    E.render(png, [tuple(p) for p in r['src1']],
                             [tuple(p) for p in r['out']], clean,
                             'BatchTool-style  row1 aligned source (T-pose) | row2 +A-pose  '
                             '(%s, scale %.4f)' % (os.path.basename(r['mdl_path']), r['scale']))

    with open(a.json_out, 'w', encoding='utf-8') as f:
        json.dump(rows, f, indent=1, ensure_ascii=False)
    print('\n[written] %s' % a.json_out)

    # ---- summary: mean over the five models, per configuration ------------
    print('\n-- mean over models (lower is better) --')
    print('%-10s %-8s %10s %10s %10s %10s' % ('align', 'spine', 'edge_mean',
                                              'edge_max', 'bad%', 'footΔ'))
    for align in ('kabsch', 'rotz180'):
        for spine in ('legacy', 'mesh'):
            sel = [r for r in rows if r['align'] == align and r['spine_map'] == spine]
            if not sel:
                continue
            n = len(sel)
            print('%-10s %-8s %10.4f %10.2f %9.2f%% %10.4f'
                  % (align, spine,
                     sum(r['edge_mean'] for r in sel) / n,
                     max(r['edge_max'] for r in sel),
                     100 * sum(r['bad'] for r in sel) / n,
                     sum(abs(r['foot_spread_out'] - r['foot_spread_src']) for r in sel) / n))


if __name__ == '__main__':
    main()
