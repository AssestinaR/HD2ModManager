# -*- coding: utf-8 -*-
"""源模型 .mdl 的 bodygroup 表 + 每个 bodygroup 用到的材质槽名（含贴图路径）。
用途：查清 L4D2 模型「部件开关 / 穿脱」的原始标记，并与 HD2 侧材质组对齐。
只读。"""
import os, sys, struct, json
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE)
import export_obj as E

VPK = r'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/Lunalice_Zoey.vpk'
BP_STRIDE, MDL_STRIDE, MSH_STRIDE, TEX_STRIDE = 16, 148, 116, 64

def i32(d, o): return struct.unpack_from('<i', d, o)[0]
def cstr(d, o, n=128):
    b = d[o:o+n]; z = b.find(b'\0')
    return b[:z if z >= 0 else n].decode('utf-8', 'replace')

ver, do, ents = E.parse_vpk_tree(VPK)
out = {}
for name, a, o, l in ents:
    if not name.lower().endswith('.mdl'): continue
    d = E.read_range(VPK, do, a, o, l)
    if d is None: continue
    numtex = i32(d, 204); texindex = i32(d, 208)
    numcd  = i32(d, 212); cdindex  = i32(d, 216)
    numskinref = i32(d, 220); skinindex = i32(d, 228)
    # 贴图名：sznameindex 相对自身
    tex = []
    for t in range(numtex):
        tb = texindex + t * TEX_STRIDE
        szname = i32(d, tb)
        tex.append(cstr(d, tb + szname))
    skinref = [i32(d, skinindex + 2 * i) for i in range(numskinref)] if skinindex else list(range(numtex))
    cds = [cstr(d, cdindex + i * 4 + i32(d, cdindex + i * 4)) for i in range(numcd)] if cdindex else []
    print('=' * 100)
    print('%s   bodyparts=%d   textures=%d   cdtextures=%s' % (name, i32(d,232), numtex, cds))
    print('=' * 100)
    numbp = i32(d, 232); bpi = i32(d, 236)
    recs = []
    for bp in range(numbp):
        base = bpi + bp * BP_STRIDE
        szname, nummodels, bbase, modelindex = struct.unpack_from('<iiii', d, base)
        bpname = cstr(d, base + szname)
        variants = []
        for m in range(nummodels):
            mb = base + modelindex + m * MDL_STRIDE
            nmsh = i32(d, mb + 72); mshi = i32(d, mb + 76); nvt = i32(d, mb + 80)
            mats = [i32(d, mb + mshi + mi * MSH_STRIDE) for mi in range(nmsh)]
            variants.append(dict(meshes=nmsh, verts=nvt, mats=mats))
        recs.append(dict(index=bp, name=bpname, nummodels=nummodels, base=bbase, variants=variants))
        v0 = variants[0]
        state = '可切换' if nummodels == 2 and variants[1]['meshes'] == 0 else ('固定' if nummodels == 1 else '?')
        names = sorted({tex[skinref[m]] if 0 <= m < len(skinref) and skinref[m] < len(tex) else '?%d' % m
                        for m in v0['mats']})
        print('  [%2d] %-34s %-6s base=%-9d meshes=%d verts=%-6d mats=%s'
              % (bp, bpname, state, bbase, v0['meshes'], v0['verts'], ', '.join(names)))
    out[name] = dict(bodyparts=recs, textures=tex, skinref=skinref, cdtextures=cds)
json.dump(out, open('out/_bodygroups.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print()
print('written out/_bodygroups.json')
