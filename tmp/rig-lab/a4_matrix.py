"""A4 -- run the whole A1..A3 pipeline over several L4D2 models and compare.

Nothing here talks to the game.  Each model is pushed through export_obj.py in
`swing` roll mode (the mode A3 proved correct for rest-pose retargeting) and the
resulting JSON metrics are collected into one table, so we can see whether the
mapping generalises or whether some models are pathological.

Usage:
    python a4_matrix.py [--roll-mode swing] [--models zoey,coach,...]
"""
import argparse
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ADDONS = r'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons'
TOC = os.path.join(HERE, 'avatar', 'unit_0x4d1c334d294dfa97.toc')
BONEHASH = (r'E:/Data/source/repos/HD2AgentWorkbench/docs/'
            r'HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt')

# tag -> (vpk basename, model name inside the vpk, source skeleton bone count)
MODELS = [
    ('zoey',     'Lunalice_Zoey.vpk',      'survivor_teenangst', 127),
    ('rochelle', 'Lunalice_Rochelle.vpk',  'survivor_producer',  127),
    ('coach',    'Elise_Coach.vpk',        'survivor_coach',     125),
    ('nick',     'Lilium_Sancta_Nick.vpk', 'survivor_gambler',   124),
    ('ellis',    'Serenade_Ellis.vpk',     'survivor_mechanic',  125),
]


def run_one(tag, vpk_name, model, roll_mode, outdir, python_exe):
    vpk = os.path.join(ADDONS, vpk_name)
    if not os.path.exists(vpk):
        return None, 'vpk missing: %s' % vpk
    json_out = os.path.join(outdir, 'a4-%s.json' % tag)
    cmd = [python_exe, os.path.join(HERE, 'export_obj.py'),
           '--vpk', vpk, '--model', model,
           '--toc', TOC, '--bonehash', BONEHASH,
           '--outdir', outdir, '--roll-mode', roll_mode,
           '--skip-render', '--json-out', json_out, '--tag', 'a4']
    p = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8',
                       errors='replace')
    if p.returncode != 0:
        return None, 'exit %d\n%s' % (p.returncode, (p.stdout or '')[-1500:])
    if not os.path.exists(json_out):
        return None, 'no json produced'
    with open(json_out, encoding='utf-8') as f:
        return json.load(f), p.stdout


def fmt_hops(hops):
    return ' '.join('%d:%d' % (int(k), v) for k, v in sorted(hops.items(), key=lambda kv: int(kv[0])))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--roll-mode', default='swing', choices=('frame', 'swing', 'both'))
    ap.add_argument('--models', default='')
    ap.add_argument('--outdir', default=HERE)
    ap.add_argument('--python', default=sys.executable)
    a = ap.parse_args()

    modes = ['swing', 'frame'] if a.roll_mode == 'both' else [a.roll_mode]
    want = set(x.strip() for x in a.models.split(',') if x.strip())
    rows = []
    for tag, vpk_name, model, bones_expected in MODELS:
        if want and tag not in want:
            continue
        for mode in modes:
            run_tag = tag if len(modes) == 1 else '%s-%s' % (tag, mode)
            print('=== %s (%s / %s) [%s] ===' % (run_tag, vpk_name, model, mode))
            data, log = run_one(run_tag, vpk_name, model, mode, a.outdir, a.python)
            if data is None:
                print('   FAILED: %s' % log)
                rows.append(dict(tag=run_tag, model=model, vpk=vpk_name,
                                 bones_expected=bones_expected, error=log))
                continue
            data['tag'] = run_tag
            data['vpk'] = vpk_name
            data['bones_expected'] = bones_expected
            rows.append(data)
            print('   bones=%d maps=%d missing=%d  height %.3f->%.3f (x%.3f)  dev p50 %.4f p95 %.4f  edge p95 %.3f'
                  % (data['source_bones'], data['direct_maps'], data['missing'],
                     data['height_src'], data['height_dst'],
                     data['height_dst'] / data['height_src'] if data['height_src'] else 0.0,
                     data['dev_p50'], data['dev_p95'], data['edge_p95']))

    # ---------------------------------------------------------------- table
    print('\n' + '=' * 128)
    print('A4 cross-model summary   (roll mode: %s)' % a.roll_mode)
    print('=' * 128)
    hdr = ('%-9s %-18s %5s %5s %4s %6s %6s %7s %12s %7s %7s %7s %6s'
           % ('tag', 'model', 'bones', 'maps', 'miss', 'torRol', 'legRol',
              'hRatio', 'segRatio', 'dev50', 'dev95', 'edg95', 'edgBad'))
    print(hdr)
    print('-' * 128)
    for r in rows:
        if r.get('error'):
            print('%-9s %-18s  FAILED' % (r['tag'], r['model']))
            continue
        print('%-9s %-18s %5d %5d %4d %6.1f %6.1f %7.3f %12s %7.4f %7.4f %7.3f %5.1f%%'
              % (r['tag'], r['model'].replace('.mdl', ''), r['source_bones'],
                 r['direct_maps'], r['missing'],
                 r['torso_roll'] or 0.0, r['legs_roll'] or 0.0,
                 r['height_dst'] / r['height_src'] if r['height_src'] else 0.0,
                 '%.3f..%.3f' % (r['seg_ratio_min'], r['seg_ratio_max']),
                 r['dev_p50'], r['dev_p95'], r['edge_p95'],
                 100.0 * r['edge_bad_frac']))
    print('-' * 128)
    print('  hRatio  = mapped height / source height')
    print('  segRatio= min..max landmark bone-length ratio (HD2/source); 1.0 = no stretch')
    print('  dev     = residual after the single best-fit global rigid motion (Kabsch), in m')
    print('  edg     = triangle edge-length ratio dst/src; edgBad = %% of edges off by >25%%')

    # worst per model
    print('\nworst landmarks by |ratio-1| (the real mesh stretch):')
    for r in rows:
        if r.get('error'):
            continue
        segs = sorted(r.get('segments') or [], key=lambda kv: -abs(kv[1] - 1.0))
        top = ', '.join('%s %.3f' % (n, v) for n, v in segs[:4])
        print('   %-9s %s' % (r['tag'], top))

    report = os.path.join(a.outdir, 'a4-report.json')
    with open(report, 'w', encoding='utf-8') as f:
        json.dump(rows, f, indent=2, ensure_ascii=False)
    print('\n[written] %s' % report)


if __name__ == '__main__':
    main()
