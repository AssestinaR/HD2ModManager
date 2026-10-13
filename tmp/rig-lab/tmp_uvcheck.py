import sys
import numpy as np
sys.path.insert(0, r'E:/Data/source/repos/WpfApp1/tmp/rig-lab')
import hd2_patch as P, hd2_unit as U
B = r'E:/tmp/rig-lab/cand-m5/9ba626afa44a3aa3.patch_0'
UNIT = 0xCAE35EE15168145F
entries,_ = P.scan_entries(B)
e = next(x for x in entries if x['type_id']==U.UNIT_ID and x['file_id']==UNIT)
pl = P.entry_payload(e, B)
u = U.Unit.read(pl['toc'], bytes(pl['gpu']))
for si in (0,1,2,3):
    vs = u.decode_vertices(si)
    for ch in ('uv0','uv1','uv2'):
        a = np.asarray([v[ch] for v in vs])
        uniq = np.unique(np.round(a,4), axis=0)
        print('stream %d %s: range %s..%s  distinct %d  first %s'
              % (si, ch, np.round(a.min(axis=0),3), np.round(a.max(axis=0),3),
                 len(uniq), uniq[0] if len(uniq) else None))
