import sys
import numpy as np
sys.path.insert(0, r'E:/Data/source/repos/WpfApp1/tmp/rig-lab')
import hd2_patch as P, hd2_unit as U

def show(tag, path, unit=0xCAE35EE15168145F):
    entries,_ = P.scan_entries(path)
    e = next(x for x in entries if x['type_id']==U.UNIT_ID and x['file_id']==unit)
    pl = P.entry_payload(e, path)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    print('=== %s ===' % tag)
    for si in range(len(u.stream_infos)):
        n = u.stream_infos[si].num_vertices
        if n < 1000:
            print('  stream %d: %d verts (culling) - skipped' % (si, n)); continue
        vs = u.decode_vertices(si)
        for ch in ('uv0','uv1','uv2'):
            a = np.asarray([v[ch] for v in vs], np.float64)
            print('  stream %d %s: min %s max %s  frac==0 %.4f'
                  % (si, ch, np.round(a.min(axis=0),4), np.round(a.max(axis=0),4),
                     (np.abs(a) < 1e-9).all(axis=1).mean()))
        d = np.abs(np.asarray([v['uv0'] for v in vs],np.float64)
                   - np.asarray([v['uv1'] for v in vs],np.float64)).max()
        print('  stream %d  max|uv0-uv1| = %.6f' % (si, d))
    print()

show('REFERENCE', r'E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0')
show('OURS M4W', r'E:/tmp/rig-lab/cand-m4w/9ba626afa44a3aa3.patch_0')
