"""Probe: why do finger-weighted vertices move up to 1.59 m?

Prints the source and HD2 world origins of the hand/finger chain so we can see
whether the swing transform's translation term (h - s) is what blows up.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import export_obj as E  # noqa: E402

ADDONS = r'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons'
TOC = os.path.join(HERE, 'avatar', 'unit_0x4d1c334d294dfa97.toc')
BONEHASH = (r'E:/Data/source/repos/HD2AgentWorkbench/docs/'
            r'HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt')

vpk = os.path.join(ADDONS, 'Lunalice_Zoey.vpk')
_v, do, ents = E.parse_vpk_tree(vpk)
by_path = {f.lower(): (f, arch, o, l) for f, arch, o, l in ents}
mdl_path = next(f for f in by_path if f.lower().endswith('survivor_teenangst.mdl'))
mdl = E.read_range(vpk, do, *by_path[mdl_path.lower()][1:])

src = E.read_source_bones(mdl)
hd2 = E.read_hd2_rig(TOC, BONEHASH)
by_name = {h['name']: h for h in hd2 if h['name']}
short = {b['name'][len(E.SRC_PREFIX):]: b for b in src if b['name'].startswith(E.SRC_PREFIX)}

print('%-14s %-22s %-28s %-28s %8s' % ('source', 'hd2', 'src origin (m)', 'hd2 origin (m)', '|h-s|'))
print('-' * 110)
for sname, hname in E.CORRESPONDENCE:
    sb, hb = short.get(sname), by_name.get(hname)
    if sb is None or hb is None:
        print('%-14s %-22s MISSING' % (sname, hname))
        continue
    s = E.origin(sb['world_m'])
    h = E.origin(hb['world'])
    d = ((h[0] - s[0]) ** 2 + (h[1] - s[1]) ** 2 + (h[2] - s[2]) ** 2) ** 0.5
    flag = '  <<<' if d > 0.3 else ''
    print('%-14s %-22s (%7.3f,%7.3f,%7.3f) (%7.3f,%7.3f,%7.3f) %8.4f%s'
          % (sname, hname, s[0], s[1], s[2], h[0], h[1], h[2], d, flag))

# Also: does the source actually have all 30 finger bones with sane positions?
print('\nsource finger chain depths (distance from L_Hand):')
hand = short.get('L_Hand')
if hand:
    ho = E.origin(hand['world_m'])
    for sname in ('L_Finger1', 'L_Finger11', 'L_Finger12', 'L_Finger2', 'L_Finger22',
                  'L_Finger4', 'L_Finger42'):
        b = short.get(sname)
        if b:
            o = E.origin(b['world_m'])
            d = ((o[0] - ho[0]) ** 2 + (o[1] - ho[1]) ** 2 + (o[2] - ho[2]) ** 2) ** 0.5
            print('   %-12s %8.4f m from L_Hand' % (sname, d))
