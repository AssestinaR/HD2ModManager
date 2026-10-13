import sys, hashlib, collections
import numpy as np
sys.path.insert(0, r'E:/Data/source/repos/WpfApp1/tmp/rig-lab')
import hd2_patch as P, hd2_unit as U

A = r'E:/tmp/rig-lab/cand-m4w/9ba626afa44a3aa3.patch_0'
B = r'E:/tmp/rig-lab/cand-m5/9ba626afa44a3aa3.patch_0'
UNIT = 0xCAE35EE15168145F

ha = hashlib.sha256(open(A,'rb').read()).hexdigest()
hb = hashlib.sha256(open(B,'rb').read()).hexdigest()
print('TOC sha256 M4W = %s' % ha)
print('TOC sha256 M5  = %s' % hb)
print('TOC identical  = %s' % (ha == hb))

def load(path):
    entries,_ = P.scan_entries(path)
    e = next(x for x in entries if x['type_id']==U.UNIT_ID and x['file_id']==UNIT)
    pl = P.entry_payload(e, path)
    return pl['toc'], bytes(pl['gpu'])

ta, ga = load(A); tb, gb = load(B)
print('gpu len %d vs %d  equal=%s' % (len(ga), len(gb), len(ga)==len(gb)))
d = np.frombuffer(ga, np.uint8) != np.frombuffer(gb, np.uint8)
print('differing bytes: %d of %d (%.3f%%)' % (d.sum(), len(ga), 100*d.sum()/len(ga)))

u = U.Unit.read(ta, ga)
FIELDS = [(0,'color',4),(4,'position',12),(16,'normal',4),(20,'uv0',8),
          (28,'uv1',8),(36,'uv2',8),(44,'bone_weight',8),(52,'bone_index',4)]
by = collections.Counter()
for s, si in enumerate(u.stream_infos):
    stride = si.vertex_stride; base0 = si.vertex_buffer_offset; n = si.num_vertices
    for i in range(n):
        b = base0 + i*stride
        for off, name, size in FIELDS:
            if off+size > stride: continue
            seg = d[b+off:b+off+size]
            if seg.any():
                by[name] += int(seg.sum())
print('by field:', dict(by))

# confirm uv1/uv2 are now constant (0,1) and uv0 is untouched
vs = u.decode_vertices(0)
uv0 = np.asarray([v['uv0'] for v in vs]); uv1 = np.asarray([v['uv1'] for v in vs])
uv2 = np.asarray([v['uv2'] for v in vs])
print('M5 stream0 uv0 range %s..%s' % (np.round(uv0.min(axis=0),3), np.round(uv0.max(axis=0),3)))
print('M5 stream0 uv1 unique %s' % np.unique(np.round(uv1,4), axis=0)[:4])
print('M5 stream0 uv2 unique %s' % np.unique(np.round(uv2,4), axis=0)[:4])
