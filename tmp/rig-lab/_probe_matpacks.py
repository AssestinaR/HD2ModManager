import collections, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
AD = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
packs = [f for f in os.listdir(AD) if f.lower().endswith('.vpk') and 'Lunalice' in f and os.path.getsize(os.path.join(AD,f)) < 300*1024*1024]
for f in sorted(packs):
    p = os.path.join(AD, f)
    try:
        ver, do, items = E.parse_vpk_tree(p)
    except Exception as exc:
        print('%-40s  FAILED %s' % (f, exc)); continue
    ext = collections.Counter(os.path.splitext(x[0])[1].lower() for x in items)
    print('%-44s %5d entries  %s' % (f, len(items), ext.most_common(6)))
    vmts=[x[0] for x in items if x[0].lower().endswith('.vmt')]
    for v in vmts[:6]: print('        %s' % v)
    if len(vmts)>6: print('        ... +%d' % (len(vmts)-6))
