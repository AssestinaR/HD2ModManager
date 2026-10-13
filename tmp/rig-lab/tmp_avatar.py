import hd2_unit, struct, numpy as np
BH = r"E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0/hashlists/bonehash.txt"
names={}
for line in open(BH, encoding='utf-8', errors='replace'):
    p=line.split()
    if len(p)>=2:
        try: names[int(p[0])]=p[1]
        except: pass
toc = open('avatar/unit_0x4d1c334d294dfa97.toc','rb').read()
u = hd2_unit.Unit.read(toc, b'')
ti = u.transform_info
print("nTf", ti.count, "version", u.version, "hdr1", u.header_data1)
# build world matrices: HD2 row-vector convention  p' = p @ M  => M_world = M_local @ M_parent
def mul(a,b):  # row-vector: A then B  -> A*B
    A=np.array(a).reshape(4,4); B=np.array(b).reshape(4,4)
    return (A@B).reshape(-1).tolist()
world=[None]*ti.count
def getw(i):
    if world[i] is not None: return world[i]
    m = ti.matrices[i]
    p = ti.entries[i][1]
    if p==i or p>=ti.count: w=m
    else: w=mul(m, getw(p))
    world[i]=w
    return w
for i in range(ti.count):
    getw(i)
targets = ['root','boss','hips','spine1','spine2','chest','neck','head','l_clavicle','l_shoulder','l_elbow','l_hand','r_shoulder','l_thigh','l_knee','l_foot','l_toe']
byname = {}
for i in range(ti.count):
    n = names.get(ti.hashes[i])
    if n: byname[n]=i
for n in targets:
    i = byname.get(n)
    if i is None: print("  %-12s MISSING" % n); continue
    lw = ti.matrices[i]; ww = world[i]
    print("  %-12s tf[%3d] parent=%3d  localT=(%+.4f,%+.4f,%+.4f)  worldT=(%+.4f,%+.4f,%+.4f)" %
          (n, i, ti.entries[i][1], lw[12],lw[13],lw[14], ww[12],ww[13],ww[14]))
print()
# check inverse-bind expectation for spine1 etc. using world
def inv_row(M):
    A=np.array(M).reshape(4,4)
    R=A[:3,:3]; t=A[3,:3]
    Ri=R.T
    ti_ = -t@Ri
    B=np.zeros((4,4)); B[:3,:3]=Ri; B[3,:3]=ti_; B[3,3]=1
    return B.reshape(-1).tolist()
print("inverse-bind of avatar world (row-vector style):")
for n in ['root','boss','spine1','spine2','chest','neck','head','l_clavicle','l_shoulder','r_clavicle','r_shoulder']:
    i=byname.get(n)
    if i is None: continue
    B=inv_row(world[i])
    print("  %-12s ibind T=(%+.4f,%+.4f,%+.4f)" % (n, B[12],B[13],B[14]))
print()
print("unit TransformInfo incr values:", sorted(set(e[0] for e in ti.entries)))
