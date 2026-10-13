"""Compare three versions of the same Unit AssetKey (0x9841b16544c8fbae, Torso/Stocky):
  A = game ORIGINAL unit, read straight out of the real game data
  B = the Chiffon patch's Unit  (what we have been using as the structural donor)
  C = OUR patch's Unit

Prints TransformInfo (count, hashes, which matrices are identity), every MeshInfo
(mesh id, transform index, lod, stream, sections), the stream layout, the LOD group
list bytes, and the material/section id tables.  Round 59 follow-up.
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as P
import hd2_unit as U

KEY = 0x9841B16544C8FBAE
GAME_ORIG = r"E:/tmp/hd2-mgroot/orig/unit_0x9841b16544c8fbae.toc"
CHIFFON = r"E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0"
OURS = r"E:/mod/Lunalice-RigTest/9ba626afa44a3aa3.patch_0"


def load_patch_unit(path, key):
    entries, _ = P.scan_entries(path)
    if key is None:
        units = [e for e in entries if e['type_id'] == P.UNIT_TYPE_ID]
        if len(units) != 1:
            raise SystemExit(f"expected exactly one Unit in {path}, found {len(units)}")
        return P.entry_payload(units[0], path)['toc']
    for e in entries:
        if e['file_id'] == key:
            return P.entry_payload(e, path)['toc']
    raise SystemExit(f"unit 0x{key:016x} not found in {path}")


def load_orig(path):
    with open(path, "rb") as f:
        return f.read()


def identity_slots(ti):
    out = []
    for i, m in enumerate(ti.matrices):
        if all(abs(m[k] - (1.0 if k in (0, 5, 10, 15) else 0.0)) < 1e-6 for k in range(16)):
            out.append(i)
    return out


def dump(label, toc):
    print("=" * 100)
    print(f"{label}   toc={len(toc)} B")
    u = U.Unit.read(toc)
    print(f"  version={u.version} hdr1={u.header_data1} composite={u.composite_ref}")
    print(f"  offsets: lod={u.lod_group_list_data_offset:#x} tf={u.transform_info_offset:#x} "
          f"light={u.light_list_offset:#x} pre={u.pre_light_list_offset:#x} "
          f"cust={u.customization_info_offset:#x} unkh={u.unk_header_offset1:#x} "
          f"conn={u.connecting_bone_hash_offset:#x} bi={u.bone_info_offset:#x} "
          f"si={u.stream_info_offset:#x} mesh={u.mesh_info_offset:#x} mat={u.materials_offset:#x} "
          f"end={u.ending_offset:#x}")
    ti = u.transform_info
    print(f"  TransformInfo: count={ti.count}")
    ident = set(identity_slots(ti))
    for i, h in enumerate(ti.hashes):
        e = ti.entries[i]
        m = ti.matrices[i]
        pos = (m[12], m[13], m[14])
        loc = ti.locals[i]["pos"]
        tag = "IDENT" if i in ident else "    "
        print(f"    [{i:3d}] 0x{h:08x} inc={e[0]:<3d} par={e[1]:<3d} {tag} "
              f"matT=({pos[0]:+.5f},{pos[1]:+.5f},{pos[2]:+.5f}) "
              f"loc=({loc[0]:+.5f},{loc[1]:+.5f},{loc[2]:+.5f})")
    print(f"  BoneInfo count={len(u.bone_infos)}")
    for i, bi in enumerate(u.bone_infos):
        print(f"    bi[{i}] bones={bi.num_bones} real={bi.real_indices[:12]}"
              f"{'...' if len(bi.real_indices) > 12 else ''} remaps={len(bi.remaps)}")
    print(f"  Streams={len(u.stream_infos)}")
    for s in u.stream_infos:
        comps = ','.join(f"{c.type}/{c.format}@{c.index}" for c in s.components)
        print(f"    s{s.component_info_id} nv={s.num_vertices} ni={s.num_indices} stride={s.vertex_stride} "
              f"ibtype={s.index_buffer_type} vb=({s.vertex_buffer_offset},{s.vertex_buffer_size}) "
              f"ib=({s.index_buffer_offset},{s.index_buffer_size})")
        print(f"        components[{len(s.components)}]: {comps}")
    print(f"  Meshes={len(u.mesh_infos)}")
    for m in u.mesh_infos:
        print(f"    mi{m.mesh_id:08x} mesh_id=0x{m.mesh_id:08x} tf={m.transform_index} unk3={m.unk3} "
              f"lod={m.lod_index} stream={m.stream_index} nmat={m.num_materials} "
              f"mat_slots={m.material_ids} nsect={len(m.sections)}")
        for si, sec in enumerate(m.sections):
            print(f"        sec{si}: mat_idx={sec.material_index} "
                  f"voff={sec.vertex_offset} nv={sec.num_vertices} "
                  f"ioff={sec.index_offset} ni={sec.num_indices} grp={sec.group_index}")
    print(f"  Materials offset={u.materials_offset:#x}")
    print(f"    section_ids={u.section_ids}")
    print(f"    material_ids={[hex(m) for m in u.material_ids]}")
    lg = getattr(u, "lod_group_list_data", b"")
    print(f"  LOD group list: {len(lg)} B")
    print("    " + lg.hex(" ", 4))
    return u


def main():
    orig = load_orig(GAME_ORIG)
    chiffon = load_patch_unit(CHIFFON, KEY)
    ours = load_patch_unit(OURS, None)

    uo = dump("A = GAME ORIGINAL", orig)
    uc = dump("B = CHIFFON PATCH (our old structural donor)", chiffon)
    uu = dump("C = OURS", ours)

    print("=" * 100)
    print("SUMMARY")
    print(f"  toc sizes       : orig={len(orig)}  chiffon={len(chiffon)}  ours={len(ours)}")
    print(f"  TF node counts  : orig={uo.transform_info.count}  chiffon={uc.transform_info.count}  "
          f"ours={uu.transform_info.count}")
    print(f"  TF identity     : orig={identity_slots(uo.transform_info)}")
    print(f"                    chiffon={identity_slots(uc.transform_info)}")
    print(f"                    ours={identity_slots(uu.transform_info)}")
    print(f"  meshes          : orig={len(uo.mesh_infos)}  chiffon={len(uc.mesh_infos)}  ours={len(uu.mesh_infos)}")
    print(f"  streams         : orig={len(uo.stream_infos)}  chiffon={len(uc.stream_infos)}  ours={len(uu.stream_infos)}")
    print(f"  stream verts    : orig={[s.num_vertices for s in uo.stream_infos]}")
    print(f"                    chiffon={[s.num_vertices for s in uc.stream_infos]}")
    print(f"                    ours={[s.num_vertices for s in uu.stream_infos]}")
    print(f"  stream strides  : orig={[s.vertex_stride for s in uo.stream_infos]}")
    print(f"                    chiffon={[s.vertex_stride for s in uc.stream_infos]}")
    print(f"                    ours={[s.vertex_stride for s in uu.stream_infos]}")
    print(f"  mesh tf indices : orig={[m.transform_index for m in uo.mesh_infos]}")
    print(f"                    chiffon={[m.transform_index for m in uc.mesh_infos]}")
    print(f"                    ours={[m.transform_index for m in uu.mesh_infos]}")
    print(f"  mesh ids        : orig={[hex(m.mesh_id) for m in uo.mesh_infos]}")
    print(f"                    chiffon={[hex(m.mesh_id) for m in uc.mesh_infos]}")
    print(f"                    ours={[hex(m.mesh_id) for m in uu.mesh_infos]}")
    print(f"  boneinfo bones  : orig={[b.num_bones for b in uo.bone_infos]}")
    print(f"                    chiffon={[b.num_bones for b in uc.bone_infos]}")
    print(f"                    ours={[b.num_bones for b in uu.bone_infos]}")
    print(f"  lodgroup len    : orig={len(getattr(uo, 'lod_group_list_data', b''))} "
          f"chiffon={len(getattr(uc, 'lod_group_list_data', b''))} "
          f"ours={len(getattr(uu, 'lod_group_list_data', b''))}")


if __name__ == "__main__":
    main()
