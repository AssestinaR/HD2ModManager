import os, sys, hashlib
sys.path.insert(0, r'E:/Data/source/repos/WpfApp1/tmp/rig-lab')
import export_obj as E
import hd2_source as S

WANT = ('shinano_hair_ear','white','shinano_face','shinano_body','_4',
        'ornament_metal_underwear','headdress_white01_catears',
        'lace_white01_catears','bra_white01_bra_inner',
        'glove_white01_wristcuffs','dress_white01_dress_corset',
        'lower_white01_underwear','shoes_white01_highheels','shoes_02')

hits = {}   # basename -> list of (pack, size, sha1)
for name in S.PACK_VPKS:
    p = os.path.join(S.ADDONS, name)
    if not os.path.exists(p):
        print('MISSING PACK', name); continue
    _v, do, ents = E.parse_vpk_tree(p)
    for f, a, o, l in ents:
        fl = f.replace('/', '\\').lower()
        if not fl.endswith('.vtf'):
            continue
        base = os.path.basename(fl)[:-4]
        if base not in WANT:
            continue
        blob = E.read_range(p, do, a, o, l)
        hits.setdefault(base, []).append((name, l, hashlib.sha1(blob).hexdigest()[:12]))

print()
print('%-32s %-38s %10s %s' % ('texture', 'pack', 'size', 'sha1[:12]'))
for base in WANT:
    lst = hits.get(base, [])
    if not lst:
        print('%-32s  -- none in any pack --' % base)
        continue
    for i, (pk, sz, h) in enumerate(lst):
        print('%-32s %-38s %10d %s%s'
              % (base if i == 0 else '', pk, sz, h,
                 '   <== our Packs picks this (last wins)' if i == len(lst) - 1 else ''))
    if len(set(x[2] for x in lst)) > 1:
        print('%-32s *** %d DIFFERENT variants ***' % ('', len(set(x[2] for x in lst))))
    print()
