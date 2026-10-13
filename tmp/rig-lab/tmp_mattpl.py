import sys, struct
sys.path.insert(0, '.')
import hd2_asset
SDK = r"E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0"
sv = {}
for line in open(SDK + r"\hashlists\shadervariables.txt", encoding='utf-8', errors='replace'):
    p = line.split()
    if len(p) >= 2:
        try: sv[int(p[0])] = p[1]
        except: pass
print("shadervariables", len(sv))
for name in ('basic', 'advanced', 'alphaclip', 'emissive', 'original', 'armorlut'):
    d = open(SDK + r"\materials\%s.material" % name, 'rb').read()
    m = hd2_asset.Material.read(d)
    print("=== %s  size=%d endOff=%d parent=%s ntex=%d nvar=%d vds=%d rem=%d" %
          (name, len(d), m.end_offset, m.parent_name, len(m.tex_ids), len(m.variables), m.variable_data_size, len(m.remaining)))
    print("   undat1:", m.undat1.hex(' '))
    print("   texids:", [hex(t) for t in m.tex_ids])
    for v in m.variables[:200]:
        nm = sv.get(v.id, '?')
        print("     %-28s klass=%d elems=%d id=0x%08x off=%3d stride=%d vals=%s" % (nm, v.klass, v.elements, v.id, v.offset, v.element_stride, [round(x,4) for x in v.values]))
