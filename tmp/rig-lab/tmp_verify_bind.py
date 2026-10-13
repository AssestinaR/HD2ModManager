import hd2_unit, hd2_patch, struct, numpy as np
BH = r"E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0/hashlists/bonehash.txt"
names={}
for line in open(BH, encoding='utf-8', errors='replace'):
    p=line.split()
    if len(p)>=2:
        try: names[int(p[0])]=p[1]
        except: pass

toc = open('avatar/unit_0x4d1c334d294dfa97.toc','rb').read()
av = hd2_unit.Unit.read(toc, b'')
ti = av.transform_info
byhash = {ti.hashes[i]: i for i in range(ti.count)}

P = r"E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0"
ents,_ = hd2_patch.scan_entries(P)
e = ents[7]
pl = hd2_patch.entry_payload(e, P)
u = hd2_unit.Unit.read(pl['toc'], pl['gpu'])
ut = u.transform_info

print("--- avatar locals (StingrayLocalTransform pos) vs matrices (world?) ---")
for i in (4, 7, 9, 21, 26, 28, 89, 90, 44, 47, 16, 18):
    nm = names.get(ti.hashes[i], '?')
    lc = ti.locals[i]; m = ti.matrices[i]
    print("  %-12s localPos=(%+.4f,%+.4f,%+.4f) localScale=(%.3f,%.3f,%.3f)  mtxPos=(%+.4f,%+.4f,%+.4f) mtxLastRow=%s" %
          (nm, lc['pos'][0],lc['pos'][1],lc['pos'][2], lc['scale'][0],lc['scale'][1],lc['scale'][2],
           m[12],m[13],m[14], ['%.3f'%x for x in m[3:16:4]]))

def inv_row(M):
    A=np.array(M,dtype=float).reshape(4,4)
    R=A[:3,:3]; t=A[3,:3]
    Ri=R.T
    B=np.zeros((4,4)); B[:3,:3]=Ri; B[3,:3]=-t@Ri; B[3,3]=1.0
    return B.reshape(-1)

print()
print("--- unit BoneInfo ibind  vs  inv(avatar world bind) ---")
bi = u.bone_infos[0]
ok=True
for k in range(bi.num_bones):
    tfi = bi.real_indices[k]
    h = ut.hashes[tfi]
    ai = byhash.get(h)
    nm = names.get(h,'?')
    if ai is None:
        print("  slot[%d] hash 0x%08x not in avatar" % (k,h)); ok=False; continue
    exp = inv_row(ti.matrices[ai])
    got = np.array(bi.bones[k], dtype=float)
    d = float(np.abs(exp-got).max())
    print("  slot[%2d] %-12s tfhash=0x%08x avatarIdx=%3d  maxdiff=%.3e  gotT=(%+.4f,%+.4f,%+.4f) expT=(%+.4f,%+.4f,%+.4f)" %
          (k, nm, h, ai, d, got[12],got[13],got[14], exp[12],exp[13],exp[14]))
    if d > 1e-5: ok=False
print("ALL MATCH:", ok)

print()
print("--- unit TransformInfo matrices vs avatar matrices (same hash) ---")
for i in range(ut.count):
    h = ut.hashes[i]; ai = byhash.get(h); nm = names.get(h,'?')
    if ai is None:
        print("  tf[%2d] 0x%08x %-28s  (no avatar counterpart) T=(%+.4f,%+.4f,%+.4f)" % (i,h,nm,ut.matrices[i][12],ut.matrices[i][13],ut.matrices[i][14]))
    else:
        d = float(np.abs(np.array(ut.matrices[i])-np.array(ti.matrices[ai])).max())
        print("  tf[%2d] 0x%08x %-28s  avatar[%3d] maxdiff=%.3e" % (i,h,nm,ai,d))
