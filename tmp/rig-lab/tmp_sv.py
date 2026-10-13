SDK = r"E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0"
sv = {}
for line in open(SDK + r"\hashlists\shadervariables.txt", encoding='utf-8', errors='replace'):
    p = line.split()
    if len(p) >= 2:
        try: sv[int(p[1], 16)] = p[0]
        except: pass
print("count", len(sv))
import hd2_asset
for name in ('basic','alphaclip','advanced','emissive'):
    d = open(SDK + r"\materials\%s.material" % name, 'rb').read()
    m = hd2_asset.Material.read(d)
    print("=== %s ntex=%d nvar=%d" % (name, len(m.tex_ids), len(m.variables)))
    for v in m.variables:
        print("    %-30s klass=%d id=0x%08x off=%3d vals=%s" % (sv.get(v.id,'?'), v.klass, v.id, v.offset, [round(x,3) for x in v.values]))
