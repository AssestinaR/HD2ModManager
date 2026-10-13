import hd2_patch, hd2_unit, struct, os
P = r"E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0"
BH = r"E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0/hashlists/bonehash.txt"
names = {}
for line in open(BH, encoding='utf-8', errors='replace'):
    p = line.split()
    if len(p)>=2:
        try: names[int(p[0])] = p[1]
        except: pass
print("bonehash entries", len(names))
ents,_ = hd2_patch.scan_entries(P)
e = ents[7]
pl = hd2_patch.entry_payload(e, P)
u = hd2_unit.Unit.read(pl['toc'], pl['gpu'])
ti = u.transform_info
print("nTf", ti.count)
for i in range(ti.count):
    m = ti.matrices[i]
    nm = names.get(ti.hashes[i], '?')
    print("  tf[%2d] hash=0x%08x %-24s parent=%3d incr=%3d  T=(%+.4f,%+.4f,%+.4f)" %
          (i, ti.hashes[i], nm, ti.entries[i][1], ti.entries[i][0], m[12], m[13], m[14]))
print()
bi = u.bone_infos[0]
print("BoneInfo slots (nb=%d) real_indices=%s" % (bi.num_bones, bi.real_indices))
for k in range(bi.num_bones):
    b = bi.bones[k]
    print("  slot[%2d] ibind T=(%+.4f,%+.4f,%+.4f)  tfIdx=%d tfhash=0x%08x %s" %
          (k, b[12],b[13],b[14], bi.real_indices[k], ti.hashes[bi.real_indices[k]] if bi.real_indices[k]<ti.count else -1,
           names.get(ti.hashes[bi.real_indices[k]],'?') if bi.real_indices[k]<ti.count else '?'))
print()
print("remaps:", bi.remaps)
print("cust(240B):", ' '.join('%02x'%x for x in u.customization_data))
print("conn(64B):", ' '.join('%02x'%x for x in u.connecting_bone_data))
print("unkHdr1:", len(u.unk_header_data1), ' '.join('%02x'%x for x in u.unk_header_data1))
print("lodGroup:", ' '.join('%02x'%x for x in u.lod_group_list_data))
print("unrev:", ' '.join('%02x'%x for x in u.unreversed_data2), "ending", u.ending_bytes)
print("preLight:", ' '.join('%02x'%x for x in u.pre_light_list_data))
