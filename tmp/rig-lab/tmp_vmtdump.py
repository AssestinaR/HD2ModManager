import sys
sys.path.insert(0, r'E:/Data/source/repos/WpfApp1/tmp/rig-lab')
import hd2_source as S
p = S.Packs()
for nm in ('shinano_hair_ear', 'white', 'shinano_face', 'shinano_body',
           'headdress_white01_catears', 'lace_white01_catears'):
    t = p.vmt_text(nm)
    print('=' * 70)
    print('VMT', nm, '->', 'FOUND' if t else 'MISSING')
    if t:
        print(t)
