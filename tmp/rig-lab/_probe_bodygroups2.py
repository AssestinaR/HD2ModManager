# -*- coding: utf-8 -*-
"""通用版：解析任意 L4D2 模型 VPK 里 .mdl 的 bodygroup 表。
用法: _probe_bodygroups2.py <vpk路径> [只列主模型]
只读。"""
import os, sys, struct
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE)
import export_obj as E

BP_STRIDE, MDL_STRIDE, MSH_STRIDE, TEX_STRIDE = 16, 148, 116, 64

def i32(d, o): return struct.unpack_from('<i', d, o)[0]
def u16(d, o): return struct.unpack_from('<H', d, o)[0]
def cstr(d, o, n=128):
    b = d[o:o+n]; z = b.find(b'\0')
    return b[:z if z >= 0 else n].decode('utf-8', 'replace')

def probe(vpk, verbose=True):
    ver, do, ents = E.parse_vpk_tree(vpk)
    out = {}
    for name, a, o, l in ents:
        if not name.lower().endswith('.mdl'): continue
        d = E.read_range(vpk, do, a, o, l)
        if d is None: continue
        numtex = i32(d, 204); texindex = i32(d, 208)
        numskinref = i32(d, 220); skinindex = i32(d, 228); nfam = i32(d, 224)
        tex = [cstr(d, texindex + t*TEX_STRIDE + i32(d, texindex + t*TEX_STRIDE)) for t in range(numtex)]
        skinref = [u16(d, skinindex + 2*i) for i in range(numskinref)] if skinindex else list(range(numtex))
        numbp = i32(d, 232); bpi = i32(d, 236)
        recs = []
        for bp in range(numbp):
            base = bpi + bp*BP_STRIDE
            szname, nummodels, bbase, modelindex = struct.unpack_from('<iiii', d, base)
            bpname = cstr(d, base + szname)
            variants = []
            for m in range(nummodels):
                mb = base + modelindex + m*MDL_STRIDE
                nmsh = i32(d, mb+72); mshi = i32(d, mb+76); nvt = i32(d, mb+80); vidx = i32(d, mb+84)
                mats = [i32(d, mb + mshi + mi*MSH_STRIDE) for mi in range(nmsh)]
                variants.append(dict(meshes=nmsh, verts=nvt, vfirst=vidx, mats=mats))
            recs.append(dict(index=bp, name=bpname, nummodels=nummodels, base=bbase, variants=variants))
        out[name] = dict(tex=tex, skinref=skinref, nfam=nfam, bodyparts=recs)
        if verbose:
            togg = sum(1 for r in recs if r['nummodels'] >= 2 and all(v['meshes'] == 0 for v in r['variants'][1:]))
            multi = sum(1 for r in recs if r['nummodels'] > 2)
            print('  %-46s bp=%-3d tex=%-3d fam=%d  可切换=%-3d 多变体=%d' % (name, numbp, numtex, nfam, togg, multi))
    return out

if __name__ == '__main__':
    vpk = sys.argv[1]
    print('=' * 90); print(vpk); print('=' * 90)
    res = probe(vpk)
    for m, info in res.items():
        if 'survivor' not in m or 'light' in m: continue
        print()
        print('--- %s : %d bodygroups ---' % (m, len(info['bodyparts'])))
        for r in info['bodyparts']:
            v0 = r['variants'][0]
            st = '可切换' if r['nummodels'] >= 2 and all(v['meshes']==0 for v in r['variants'][1:]) else ('固定' if r['nummodels']==1 else 'N选一(%d)'%r['nummodels'])
            names = [info['tex'][info['skinref'][x]] if 0<=x<len(info['skinref']) and info['skinref'][x]<len(info['tex']) else '?%d'%x for x in v0['mats']]
            seen=set(); uniq=[n for n in names if not (n in seen or seen.add(n))]
            print('  [%2d] %-30s %-10s meshes=%-2d verts=%-6d %s' % (r['index'], r['name'], st, v0['meshes'], v0['verts'], ', '.join(uniq)))
