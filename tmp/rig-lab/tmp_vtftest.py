import sys, collections, os
sys.path.insert(0,'.')
import hd2_source as S, hd2_vtf as V
p = S.Packs()
print("vmt %d  vtf %d" % (len(p.vmt), len(p.vtf)))
mdl, vvd, vtx = p.model_files()
print("mdl %d vvd %d vtx %d" % (len(mdl), len(vvd), len(vtx)))
names, cds, row = S.load_material_slots(mdl)
print("names %d cds %s skinrow %d uniq %d" % (len(names), cds, len(row), len(set(row))))
cnt = collections.Counter()
bad = []
for path, blob in p.vtf.items():
    try:
        v = V.VTF.read(blob)
        cnt[v.format_name] += 1
    except Exception as ex:
        bad.append((path, repr(ex)))
print("vtf formats:", dict(cnt))
print("bad:", bad[:10])
print()
for path in sorted(p.vtf):
    if 'lunalice' in path:
        v = V.VTF.read(p.vtf[path])
        print("  %-56s %s  hdr=%d  mip0=%d  filesize=%d" % (os.path.basename(path), v.summary(), v.header_size, v.mip0_size(), len(p.vtf[path])))
