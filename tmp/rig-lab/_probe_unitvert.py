import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as P, hd2_unit as U
from hd2_hash import load_bonehash
names = load_bonehash(os.path.join('..','..','docs','HD2SDK-CommunityEdition-3.8.0','hashlists','bonehash.txt'))
D='E:/mod/Lunalice-Visible-Virtual-Model'
tocs=[os.path.join(D,f) for f in os.listdir(D) if '.patch_' in f and not f.endswith(('.stream','.gpu_resources'))]
entries,_=P.scan_entries(tocs[0])
e=[x for x in entries if x['type_id']==U.UNIT_ID][6]
pl=P.entry_payload(e,tocs[0]); u=U.Unit.read(pl['toc'],pl['gpu'])
ti=u.transform_info
for bi in u.bone_infos[:1]:
    print('bi num_bones %d remap_offsets %s remap_counts %s' % (bi.num_bones, bi.remap_offsets, bi.remap_counts))
    print('  real_indices %s' % bi.real_indices)
    print('  remap[0] %s' % bi.remaps[0])
    print('  remap[1] %s' % bi.remaps[1])
# decode LOD0 mesh (mesh index 3, stream 0)
si = u.stream_infos[0]
print('\nstream0 stride %d nverts %d' % (si.vertex_stride, si.num_vertices))
vs = u.decode_vertices(0)
for i in (0,1,2,1000,119577):
    r = vs[i]
    print(' v%-7d %s' % (i, {k:(v if not isinstance(v,list) else [round(x,4) for x in v]) for k,v in r.items()}))
    # oct decode of normal
    n = r['normal']
    def octdec(p):
        x = (p & 0x3ff)/1023.0*2-1
        y = ((p>>10)&0x3ff)/1023.0*2-1
        z = 1-abs(x)-abs(y)
        if z<0:
            import math
            sx = 1 if x>=0 else -1; sy=1 if y>=0 else -1
            x2=(1-abs(y))*sx; y2=(1-abs(x))*sy; x,y=x2,y2
        import math
        l=math.sqrt(x*x+y*y+z*z) or 1
        return (x/l,y/l,z/l)
    print('        oct 0x%08x -> %s' % (n, [round(v,4) for v in octdec(n)]))
    bw = r['bone_weight']; bi_ = r['bone_index0']
    print('        bone_index %s bone_weight %s  (sum %.6f)' % (bi_, [round(x,6) for x in bw], sum(bw)))
idx = u.index_list(0)
print('\nstream0 indices: len %d  first 12 %s  max %d' % (len(idx), idx[:12], max(idx)))
mi = u.mesh_infos[3]
print('mesh[3] sections: %s' % [(s.material_index,s.vertex_offset,s.num_vertices,s.index_offset,s.num_indices) for s in mi.sections])
print('mesh[3] material_ids %s' % ['0x%x'%m for m in mi.material_ids])
# section 0 indices range
s0 = mi.sections[0]
print('sec0 idx %d..%d -> %s' % (s0.index_offset, s0.index_offset+s0.num_indices, idx[s0.index_offset:s0.index_offset+12]))
s1 = mi.sections[1]
print('sec1 idx %d..%d -> %s' % (s1.index_offset, s1.index_offset+s1.num_indices, idx[s1.index_offset:s1.index_offset+12]))
import numpy as np
a=np.array(idx)
print('sec0 max %d sec1 max %d  overall max %d  nverts %d' % (a[s0.index_offset:s0.index_offset+s0.num_indices].max(), a[s1.index_offset:s1.index_offset+s1.num_indices].max(), a.max(), si.num_vertices))
