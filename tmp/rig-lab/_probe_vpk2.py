import collections, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
VPK = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/Lunalice_Zoey.vpk'
ver, do, items = E.parse_vpk_tree(VPK)
print('entries %d' % len(items))
ext = collections.Counter(os.path.splitext(x[0])[1].lower() for x in items)
print('extensions: %s' % ext.most_common(20))
print('dirs: %s' % collections.Counter(x[0].split('/')[0].lower() for x in items).most_common(10))
for kw in ('shinano_body','Shinano_face','.vmt','.vtf','.vmat'):
    hit=[x for x in items if kw.lower() in x[0].lower()]
    print('\n%s -> %d' % (kw, len(hit)))
    for h in hit[:8]: print('   %s a=%d o=%d l=%d' % h)
# all vmt/vtf if any
vmts=[x for x in items if x[0].lower().endswith('.vmt')]
vtfs=[x for x in items if x[0].lower().endswith('.vtf')]
print('\nvmt %d vtf %d' % (len(vmts), len(vtfs)))
for h in vmts[:10]: print('   %s' % h[0])
for h in vtfs[:10]: print('   %s' % h[0])
