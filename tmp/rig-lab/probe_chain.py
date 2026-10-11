"""Dump the raw source vs HD2 joint positions for the correspondence table.

The point: before deciding what "joint semantics" fix to apply, look at where the
two rigs actually put their joints.  Everything is printed in metres, in each rig's
own world space (they differ by the ~180 deg space rotation, so compare *distances*,
not raw coordinates).
"""
import sys, os, math
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E

VPK = sys.argv[1]
TOC = sys.argv[2]
BONEHASH = sys.argv[3]
MODEL = sys.argv[4] if len(sys.argv) > 4 else 'survivor_gambler'

_v, do, ents = E.parse_vpk_tree(VPK)
by_path = {f.lower(): (f, a, o, l) for f, a, o, l in ents}
cands = [f for f in by_path if f.lower().endswith('.mdl') and MODEL.lower() in os.path.basename(f).lower()]
cands.sort(key=lambda f: (len(os.path.basename(f)), os.path.basename(f)))
mdl_path = cands[0]
f, arch, o, l = by_path[mdl_path.lower()]
mdl = E.read_range(VPK, do, arch, o, l)

src = E.read_source_bones(mdl)
hd2 = E.read_hd2_rig(TOC, BONEHASH)
by_name = {h['name']: h for h in hd2 if h['name']}
short = {b['name'][len(E.SRC_PREFIX):]: b for b in src if b['name'].startswith(E.SRC_PREFIX)}

def d(a, b):
    return math.dist(E.origin(a), E.origin(b))

print('=== %s ===' % os.path.basename(mdl_path))
print('%-14s %-22s %10s %10s %8s   %s' % ('source', 'hd2', 'srcSeg', 'hd2Seg', 'ratio', 'note'))
prev_s = prev_h = None
for sname, hname in E.BODY:
    sb, hb = short.get(sname), by_name.get(hname)
    if sb is None or hb is None:
        print('%-14s %-22s  MISSING' % (sname, hname)); continue
    seg_s = d(sb['world_m'], short[E.BODY[E.BODY.index((sname, hname)) - 1][0]]['world_m']) if E.BODY.index((sname, hname)) > 0 else 0.0
    # distance from the *previous* correspondence bone, which is what the segment
    # report measures
    i = E.BODY.index((sname, hname))
    if i == 0:
        seg_s = seg_h = 0.0
    else:
        ps = short.get(E.BODY[i - 1][0]); ph = by_name.get(E.BODY[i - 1][1])
        seg_s = d(sb['world_m'], ps['world_m']) if ps else 0.0
        seg_h = d(hb['world'], ph['world']) if ph else 0.0
    ratio = (seg_h / seg_s) if seg_s > 1e-6 else 0.0
    note = ''
    if sname in ('Neck1', 'Head1', 'L_Clavicle', 'L_UpperArm', 'R_Clavicle', 'R_UpperArm'):
        note = '<-- problem joint'
    print('%-14s %-22s %10.4f %10.4f %8.4f   %s' % (sname, hname, seg_s, seg_h, ratio, note))

print()
print('root: source Pelvis origin %s' % (tuple(round(v, 4) for v in E.origin(short['Pelvis']['world_m'])),))
print('root: hd2    hips   origin %s' % (tuple(round(v, 4) for v in E.origin(by_name['hips']['world'])),))

print()
print('--- absolute origins (m), source ---')
for sname, hname in E.BODY:
    sb = short.get(sname)
    if sb is None: continue
    print('  %-14s %s' % (sname, tuple(round(v, 4) for v in E.origin(sb['world_m']))))
print('--- absolute origins (m), hd2 ---')
for sname, hname in E.BODY:
    hb = by_name.get(hname)
    if hb is None: continue
    print('  %-22s %s' % (hname, tuple(round(v, 4) for v in E.origin(hb['world']))))

print()
print('--- all HD2 bone names (first 60) ---')
names = [h['name'] for h in hd2 if h['name']]
for i in range(0, min(60, len(names)), 4):
    print('   ' + '  '.join('%-18s' % n for n in names[i:i + 4]))
