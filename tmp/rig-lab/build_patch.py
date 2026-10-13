# -*- coding: utf-8 -*-
"""Assemble a standard legacy HD2 patch package from the current pipeline.

This is the "fixed script" that turns

    out/blender/model.npz + out/blender/meta.json     (the rig-lab pipeline)
    + the L4D2 addon VPKs                             (materials / textures)

into

    <out>/9ba626afa44a3aa3.patch_0                    (TOC, legacy layout)
    <out>/9ba626afa44a3aa3.patch_0.gpu_resources      (vertex/index + pixels)
    <out>/<name>.protocol                             (armor-registry model)
    <out>/<name>.manifest.json                        (what was built)

**The whole model goes on ONE Unit** (round 59 decision, on the user's explicit
instruction): the patch contains a single unit payload holding the complete
mesh.  `--unit-mode` decides which id that one unit is written under, and
therefore whether the game needs the Lua registry to find it:

    fresh (default)     one brand-new id (`d…`).  The protocol addresses it
                        explicitly with nine `model_piece` lines that all point
                        at the same unit -- this is the "one unit, whole model"
                        shape.  Requires the Lua armour registry to be enabled.
    same-key            the same payload written under the nine **real game**
                        unit ids the armour occupies, so the game resolves them
                        itself with no Lua and no manager configuration.  Nine
                        units, one GPU blob (the writer dedups by SHA256).
    both                emit the nine plus the fresh one (ten units).

Everything except the unit ids is shared: one GPU blob, one material set, one
texture set.  In `same-key` only the 240-byte `customization_data` blob differs
per id, and it is copied from the manager's own unit for that position rather
than invented.

Nothing here is derived from the mesh's *authoring* pose: the vertices are
`ours_bind` (the frozen, user-accepted geometry) and the skin palette is the
shipped HD2 rest pose, so the game's rest frame reproduces the mesh 1:1.

usage:
    build_patch.py [--out DIR] [--materials basic|flat] [--normal-maps]
                   [--max-tex N] [--map-model ITEMID] [--name NAME]
                   [--unit-mode fresh|same-key|both] [--dry-run]
"""
import argparse
import copy
import glob
import hashlib
import json
import os
import shutil
import struct
import sys
import time

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_asset as A
import hd2_hash as H
import hd2_patch as HP
import hd2_patchw as PW
import hd2_source as S
import hd2_style as ST
import hd2_texpack as TP
import hd2_unit as U
import hd2_unitbuild as UB
import hd2_vertex as V
import export_obj as E

# --------------------------------------------------------------------- config
NPZ = os.path.join(HERE, 'out', 'blender', 'model.npz')
META = os.path.join(HERE, 'out', 'blender', 'meta.json')
AVATAR_TOC = os.path.join(HERE, 'avatar', 'unit_0x4d1c334d294dfa97.toc')
SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'

# The patch we take the container header, the unit *structure* and the two
# Bone assets from.  This is the manager's own output, i.e. the one artefact we
# know the game accepts -- the Chiffon source patch it was made from is NOT
# byte-compatible with the C# writer (different GPU packing).
STRUCTURE_PATCH = r'E:\mod\Lunalice-Visible-Virtual-Model\9ba626afa44a3aa3.patch_0'
STRUCTURE_UNIT = 0xD841B16544C8FBAE          # Stocky torso, a working HD2 unit
DEFAULT_OUT = r'E:\mod\Lunalice-RigTest'
NATIVE_DIR = r'E:\tmp\hd2-mgroot\orig'
MODEL_KEY = 'custom/lunalice-rigtest'
PATCH_NAME = '9ba626afa44a3aa3.patch_0'

# ------------------------------------------------------------------ same-key
# The nine **real game** unit ids behind the visible pieces of the armour.
#
# `lunalice-visible-model.manifest.json` records, for every visible piece, both
# the id the game itself resolves (`oldUnit`, e.g. 0x9841b16544c8fbae) and the
# manager's re-homed copy (`unit`, the same value with the top nibble forced to
# `d`).  Only the `oldUnit` ids exist in the shipped archives -- the `d…` ones
# are addressed *explicitly* by the Lua registry.  Writing our unit under the
# `oldUnit` ids therefore turns this patch into a plain armour replacement: the
# game resolves them by itself, with no Lua and no manager configuration.
#
# Entries are (game id, manager id, body, slot, piece type); the manager id is
# only used to borrow that slot's `customization_data`.
SOURCE_PATCH = (r'E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310'
                r'\9ba626afa44a3aa3.patch_0')
REF_MANIFEST = (r'E:\mod\Lunalice-Visible-Virtual-Model'
                r'\lunalice-visible-model.manifest.json')
SAME_KEY_FALLBACK = [
    (0xAF431352D9B95E83, 0xDF431352D9B95E83, 3, 0, 0),   # Head      Any
    (0x17DA843A4908511E, 0xD7DA843A4908511E, 1, 3, 1),   # Pelvis    Stocky
    (0x9A5E6DC74CDB08A3, 0xDA5E6DC74CDB08A3, 1, 6, 1),   # LeftArm   Stocky
    (0x4F7BCE4CA49CCDA8, 0xDF7BCE4CA49CCDA8, 1, 7, 1),   # RightArm  Stocky
    (0x91EB7BE84016CEAB, 0xD1EB7BE84016CEAB, 0, 6, 1),   # LeftArm   Slim
    (0x7336C6B662522F0E, 0xD336C6B662522F0E, 0, 7, 1),   # RightArm  Slim
    (0xCAE35EE15168145F, 0xDAE35EE15168145F, 0, 2, 1),   # Torso     Slim
    (0x9841B16544C8FBAE, 0xD841B16544C8FBAE, 1, 2, 1),   # Torso     Stocky
    (0x45D79D84B8933A7F, 0xD5D79D84B8933A7F, 0, 3, 1),   # Pelvis    Slim
]
BODY_CODE = {'Any': 3, 'Slim': 0, 'Stocky': 1}

# `model_piece=key,body,unit,slot,piece_type,tone` -- the (body, slot, type)
# triples are copied verbatim from the manager's working protocol so that the
# runtime's per-position weight metadata is guaranteed to resolve.
PIECE_POSITIONS = [(3, 0, 0),      # Head          (Any body)
                   (1, 3, 1),      # Pelvis        Stocky
                   (0, 6, 1),      # LeftArm       Slim
                   (0, 7, 1),      # RightArm      Slim
                   (1, 6, 1),      # LeftArm       Stocky
                   (1, 7, 1),      # RightArm      Stocky
                   (1, 2, 1),      # Torso         Stocky
                   (0, 2, 1),      # Torso         Slim
                   (0, 3, 1)]      # Pelvis        Slim

# HD2 material templates.
#
# `basic` and `basic+` share the SAME slot keys (756f6fa6 / ac652e43 / caed6cd6)
# and the same slot semantics (PBR / Base Color / Normal), so switching between
# them is a pure shader-contract swap.  The SDK's own `Global_Materials` table
# marks only `advanced` and `basic+` as "renders in the UI"; `basic` is the
# "trash bag prop" template.  Round-60 candidates:
#   M1 = template basic   (only the texture-table bug fixed)
#   M2 = template basic   + PBR.B = 255
#   M3 = template basic+  + PBR.B = 255
#   M4 = template advanced (needs a different slot table -- not wired yet)
TEMPLATE_DIR = os.path.join(SDK, 'materials')
TEMPLATE_FILE = {'basic': 'basic.material',
                 'basic+': 'basic+.material',
                 'advanced': 'advanced.material'}
# template -> (slot index of PBR/Base Color/Normal) for the 3-slot family
TEMPLATE_SLOTS = {'basic': (0, 1, 2), 'basic+': (0, 1, 2)}
# `basic` texture slots, in order (stingray `TextureTypeLookup`)
BASIC_SLOTS = ('PBR', 'Base Color', 'Normal')

PBR_ROUGHNESS = 0.62        # slot 0: R = metallic, G = roughness, B = AO (unused by the SDK preview)
DEFAULT_PBR_B = 0           # round-59 value; the round-60 M2/M3 control sets 255
FLAT_NORMAL_RGBA = (128, 128, 255, 255)
TEX_ID_PREFIX = 0xB
MAT_ID_PREFIX = 0xA
UNIT_ID_PREFIX = 0xD


# ------------------------------------------------------------------- helpers
def make_id(prefix, name):
    """A deterministic 64-bit asset id in `prefix`'s namespace."""
    h = hashlib.sha256(name.encode('utf-8')).digest()
    v = int.from_bytes(h[:8], 'little') & 0x0FFFFFFFFFFFFFFF
    return ((prefix & 0xF) << 60) | v


def log(msg=''):
    print(msg, flush=True)


def _sizes(path):
    try:
        return os.path.getsize(path)
    except OSError:
        return -1


# ------------------------------------------------------------------ pipeline
def load_pipeline():
    d = np.load(NPZ)
    meta = json.load(open(META, encoding='utf-8'))
    need = ('ours_bind', 'tris', 'w_bone', 'w_val')
    for k in need:
        if k not in d:
            raise KeyError('%s is missing %r' % (NPZ, k))
    pos = np.ascontiguousarray(d['ours_bind'], np.float32)
    tris = np.ascontiguousarray(d['tris'], np.int64)
    wb = np.ascontiguousarray(d['w_bone'], np.int32)
    wv = np.ascontiguousarray(d['w_val'], np.float32)
    log('pipeline: %d vertices / %d triangles  (frozen geometry %s)'
        % (len(pos), len(tris), meta.get('fingerprint', {}).get('geometry', '?')[:16]))
    return pos, tris, wb, wv, meta


def load_source(pos, tris):
    """Source VVD data: authored normals, UVs and per-triangle material index."""
    vpk = S.ADDONS + S.MODEL_VPK
    mdl_path, mdl, verts, tris4, _src, _short = ST.load_source(vpk, 'survivor_teenangst')
    tris4 = np.asarray(tris4, np.int64)
    if tris4.shape[1] < 4:
        raise RuntimeError('source triangles carry no material index')
    if not np.array_equal(tris4[:, :3], tris):
        bad = int((tris4[:, :3] != tris).any(axis=1).sum())
        raise RuntimeError('pipeline triangles no longer match the source order '
                           '(%d differing rows); material assignment would be wrong' % bad)
    src_pos = np.asarray([v['pos'] for v in verts], np.float64) * E.IN2M
    src_nrm = np.asarray([v['nrm'] for v in verts], np.float64)
    src_nrm /= np.maximum(np.linalg.norm(src_nrm, axis=1, keepdims=True), 1e-30)
    uv = np.asarray([v['uv'] for v in verts], np.float32)
    if len(src_pos) != len(pos):
        raise RuntimeError('source has %d vertices, the pipeline %d'
                           % (len(src_pos), len(pos)))
    log('source   : %d vertices, %d material slots referenced'
        % (len(src_pos), len(np.unique(tris4[:, 3]))))
    return mdl, src_pos, src_nrm, uv, tris4


def build_materials(mdl, tris4, packs, mode, max_tex, normal_maps,
                    pbr_b=DEFAULT_PBR_B):
    """-> (groups, textures, slot_to_group)

    groups   : list of dicts(name, material_id, short_id, texture_ids)
    textures : list of dicts(name, file_id, asset, gpu)
    """
    names, cds, _skin = S.load_material_slots(mdl)
    used = sorted(int(x) for x in np.unique(tris4[:, 3]))

    slot_info = {}
    for i in used:
        vmt_name = names[i] if 0 <= i < len(names) else None
        text = packs.vmt_text(vmt_name) if vmt_name else None
        vmt = S.parse_vmt(text) if text else {}
        base = vmt.get('$basetexture')
        bump = vmt.get('$bumpmap')
        # the VMTs carry Chinese prose in `$bumpmap` for the non-normal slots;
        # only accept something that looks like a real texture path
        if bump and not (bump.endswith('_n') and '\\' in bump):
            bump = None
        slot_info[i] = dict(name=vmt_name, base=base, bump=bump,
                            outline=str(vmt.get('_raw', {}).get('$outline', '0')).strip())

    if mode == 'flat':
        key_of = lambda i: ('flat',)                      # noqa: E731
    else:
        key_of = lambda i: (slot_info[i]['base'],)        # noqa: E731

    groups, order = {}, []
    for i in used:
        k = key_of(i)
        if k not in groups:
            groups[k] = dict(key=k, slots=[])
            order.append(k)
        groups[k]['slots'].append(i)

    if max_tex and len(order) > max_tex:
        # keep the busiest groups, fold the rest into the biggest one
        weight = {k: sum(len(np.nonzero(tris4[:, 3] == i)[0]) for i in groups[k]['slots'])
                  for k in order}
        order.sort(key=lambda k: -weight[k])
        keep, fold = order[:max_tex], order[max_tex:]
        for k in fold:
            groups[keep[0]]['slots'].extend(groups[k]['slots'])
            del groups[k]
        order = keep
        log('materials: folded %d low-use groups into the largest (--max-tex %d)'
            % (len(fold), max_tex))

    textures = {}

    def as_asset(hdr_px):
        """`solid_texture` returns a raw (header, pixels) pair; wrap it."""
        hdr, px = hdr_px
        t = A.Texture()
        t.unk_id = 0
        t.dds_header = bytearray(hdr)
        t.pixels = px
        return t, px

    def texture(name, builder):
        if name not in textures:
            fid = make_id(TEX_ID_PREFIX, 'tex/' + name)
            asset, px = builder(fid)
            textures[name] = dict(name=name, file_id=fid, asset=asset, gpu=px)
        return textures[name]['file_id']

    pbr_rgba = (0, int(PBR_ROUGHNESS * 255), pbr_b & 0xFF, 255)
    # the id stays `shared_pbr` across the AO control -- the slot identity is
    # "the shared PBR map", only its pixels change; the manifest records the
    # actual rgba so a package can always be told apart
    shared_pbr = texture(
        'shared_pbr',
        lambda fid: as_asset(TP.solid_texture(
            4, 4, pbr_rgba, dxgi=TP.DXGI_BC7_UNORM)))
    shared_nrm = texture(
        'shared_flat_normal',
        lambda fid: as_asset(TP.solid_texture(
            4, 4, FLAT_NORMAL_RGBA, dxgi=TP.DXGI_BC7_UNORM)))
    if mode == 'flat':
        white = texture('flat_white',
                        lambda fid: as_asset(TP.solid_texture(
                            4, 4, (255, 255, 255, 255), dxgi=TP.DXGI_BC7_SRGB)))

    slot_to_group = {}
    out_groups = []
    for gi, k in enumerate(order):
        slots = groups[k]['slots']
        base = k[0]
        if mode == 'flat':
            base_id = white
        else:
            raw = packs.vtf_bytes(base) if base else None
            if raw is None:
                log('  [warn] slot group %r has no VTF; using flat white' % (base,))
                base_id = white
            else:
                base_id = texture(
                    'base/' + os.path.basename(base),
                    lambda fid, raw=raw, base=base: TP.build_texture(
                        raw, TP.DXGI_BC7_SRGB, fid, ref=None, tag=os.path.basename(base)))
        name = 'flat' if mode == 'flat' else (base or 'untextured')
        mat_id = make_id(MAT_ID_PREFIX, 'mat/' + name)
        short = H.murmur32_hash(('lunalice-matslot/%s' % name).encode('utf-8'))
        if short == 0:
            short = 0x5A5A0001
        g = dict(name=name, material_id=mat_id, short_id=short,
                 texture_ids=[shared_pbr, base_id, shared_nrm],
                 slots=slots)
        out_groups.append(g)
        for i in slots:
            slot_to_group[i] = gi
        log('  material %2d %-46s slots=%-3d tris=%-6d base=%s'
            % (gi, os.path.basename(name), len(slots),
               int((tris4[:, 3] == slots[0]).sum()) if len(slots) == 1 else -1,
               'flat' if mode == 'flat' else 'tex'))

    log('materials: %d HD2 materials, %d textures' % (len(out_groups), len(textures)))
    return out_groups, list(textures.values()), slot_to_group


def build_mesh(pos, tris, wb, wv, src_pos, src_nrm, uv, tris4, slot_to_group):
    """-> the dict `hd2_unitbuild.build_unit` wants, plus a report."""
    nv, nt = len(pos), len(tris)
    nrm = V.final_normals(src_pos, src_nrm, tris, pos.astype(np.float64))
    T = tris[:, list(V.WINDING_FLIP)]              # HD2 culls back faces
    grp = np.asarray([slot_to_group[int(m)] for m in tris4[:, 3]], np.int32)
    order = np.argsort(grp, kind='stable')
    T = T[order]
    grp = grp[order]

    bone = np.zeros((nv, 4), np.uint8)
    weight = np.zeros((nv, 4), np.float16)
    # `dump_blender` leaves unused slots as -1/0.0; only a slot that carries
    # mass but no bone index is a real error.
    live = wv[:, :3] > 0.0
    if (live & (wb[:, :3] < 0)).any():
        raise AssertionError('%d weight slots carry mass with no bone index'
                             % int((live & (wb[:, :3] < 0)).sum()))
    bone[:, :3] = np.clip(wb, 0, 255).astype(np.uint8)
    weight[:, :3] = wv.astype(np.float16)
    ok, worst = UB.half4_close(weight.astype(np.float64))
    if not ok:
        raise AssertionError('float16 weights do not close (worst %.3e); the SDK '
                             'contract requires fsum(half(w)) == 1' % worst)
    log('weights  : half4 closes exactly (worst deviation %.3e); '
        '%d slots filled' % (worst, int((weight > 0).sum())))
    log('indices  : %d triangles, u32, winding flipped %s' % (nt, V.WINDING_FLIP))

    sections, off = [], 0
    nmat = int(grp.max()) + 1
    for g in range(nmat):
        cnt = int((grp == g).sum())
        sections.append(dict(material_index=g, vertex_offset=0, num_vertices=nv,
                             index_offset=off, num_indices=3 * cnt, group_index=g))
        off += 3 * cnt
    if off != 3 * nt:
        raise AssertionError('section index total %d != %d' % (off, 3 * nt))

    mesh = dict(pos=pos.astype(np.float32), nrm=nrm.astype(np.float32),
                uv=uv.astype(np.float32), bone=bone, weight=weight,
                indices=T.reshape(-1).astype(np.uint32), sections=sections)
    log('sections : %d (one per material), %d indices total' % (len(sections), off))
    return mesh, nrm


def load_reference():
    if not os.path.exists(STRUCTURE_PATCH):
        raise FileNotFoundError('structure donor patch missing: %s' % STRUCTURE_PATCH)
    ents, _raw = HP.scan_entries(STRUCTURE_PATCH)
    ref = None
    bones = []
    for e in ents:
        if e['type_id'] == U.UNIT_ID and e['file_id'] == STRUCTURE_UNIT:
            pl = HP.entry_payload(e, STRUCTURE_PATCH)
            ref = U.Unit.read(pl['toc'], pl['gpu'])
        elif e['type_id'] == U.BONE_ID:
            pl = HP.entry_payload(e, STRUCTURE_PATCH)
            bones.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'], pl['stream'],
                                  pl['gpu'], e['unknown1'], e['unknown2'],
                                  e['unknown3'], e['unknown4'], note='bone'))
    if ref is None:
        raise LookupError('unit 0x%x not found in %s' % (STRUCTURE_UNIT, STRUCTURE_PATCH))
    log('structure: unit 0x%x (%d streams / %d meshes) + %d Bone entries from %s'
        % (STRUCTURE_UNIT, len(ref.stream_infos), len(ref.mesh_infos), len(bones),
           os.path.basename(STRUCTURE_PATCH)))
    return ref, bones


def load_avatar():
    raw = open(AVATAR_TOC, 'rb').read()
    off = struct.unpack_from('<I', raw, 0x34)[0]
    ti = U.TransformInfo.read(U.Buf(raw), off)
    if ti.count != 107:
        raise RuntimeError('avatar rig has %d bones, expected 107' % ti.count)
    log('avatar   : %d bones from %s' % (ti.count, os.path.basename(AVATAR_TOC)))
    return ti


def culling_boxes(mesh, ref, ref_verts, policy='target'):
    """Both culling volumes, plus the box each one publishes in `MeshInfo`.

    `policy='target'` (default) reuses the reference unit's shells **verbatim**.

    Why this is the safe default, and why round 59 was wrong to rescale them:
      * the repository's own `CanonicalAutoLodMappingExpander.cs:36` calls these
        "optional cutouts, not interchangeable display LODs", and
        `SameKeyCanonicalUnitRebuilder.cs:114` says a culling mesh with no
        explicit source counterpart "must retain the target cutout".  They are
        therefore *not* ordinary visibility bounds, so "bigger is safer" does
        not hold -- a cutout covering the whole body masks the whole body.
      * the shells are shared, standard assets: the vanilla DS-10 torso and the
        Chiffon donor carry the *same* culling mesh id `0xa620a54c` with the same
        box `+/-0.314074/0.233464/0.236239`, radius `0.457118`.  A shipped
        whole-body mod unit keeps that torso-sized shell, so keeping it is the
        configuration with field evidence behind it.

    `policy='whole-body'` rescales both shells to wrap the entire model.  It is
    kept only so an independent control can be produced without rewriting code;
    it is not the default and is not evidence of anything.

    Both policies report the box they actually produced, and `MeshInfo` is
    written from those numbers (`hd2_unitbuild.mesh_bounds`), never copied.
    """
    out = {}
    lo, hi = mesh['pos'].min(axis=0), mesh['pos'].max(axis=0)
    log('bounds   : min %s  max %s  (size %s)'
        % (np.round(lo, 4), np.round(hi, 4), np.round(hi - lo, 4)))
    for tag, si, tf_idx in (('a', 3, 17), ('b', 2, 22)):
        tf = ref.transform_info.matrices[tf_idx]
        if policy == 'target':
            v = np.asarray(ref_verts[tag], np.float64)
            mn, mx = v.min(axis=0), v.max(axis=0)
            rad = float(np.linalg.norm(v, axis=1).max())
        else:
            v, mn, mx = UB.make_culling_box(ref_verts[tag], ref.stream_infos[si],
                                            lo, hi, tf)
            rad = float(np.linalg.norm(v, axis=1).max())
        out['cull_%s_verts' % tag] = v
        out['cull_%s_indices' % tag] = ref_verts[tag + '_idx']
        out['cull_%s_normals' % tag] = ref_verts[tag + '_nrm']
        out['cull_%s_box' % tag] = (mn, mx, rad)
        if policy == 'target':
            out['cull_%s_raw' % tag] = ref_verts[tag + '_raw']
        log('  culling %s (%s): mesh tf=%d parent tf=%d -> local box min %s max %s '
            'radius %.6f' % (tag.upper(), policy, tf_idx,
                             ref.transform_info.entries[tf_idx][1],
                             np.round(mn, 4), np.round(mx, 4), rad))
    out['cull_uv'] = ref_verts['uv']
    return out


def reference_culling_geometry(ref):
    """Decode the reference's two culling meshes (24 verts / 36 indices each).

    Also hands back the raw vertex bytes: under the `target` policy we ship them
    verbatim, because the reference carries three UV channels and we only know
    the first one -- re-packing would silently alter a shell the manager's own
    rebuilder deliberately leaves alone.
    """
    out = {}
    for tag, si in (('a', 3), ('b', 2)):
        vs = ref.decode_vertices(si)
        out[tag] = np.asarray([v['position'] for v in vs], np.float64)
        out[tag + '_nrm'] = [v['normal'] for v in vs]
        out[tag + '_idx'] = ref.index_list(si)
        n = ref.stream_infos[si].num_vertices * ref.stream_infos[si].vertex_stride
        out[tag + '_raw'] = bytes(ref.gpu_vertex[si][:n])
    out['uv'] = np.asarray([[v['uv0'][0], v['uv0'][1]]
                            for v in ref.decode_vertices(2)], np.float32)
    return out


def load_same_key_targets():
    """-> [{game, manager, body, slot, piece_type, customization, unknown1, unknown2}]

    Everything here comes from artefacts the game already accepts: the id pairs
    and the per-slot `customization_data` from the manager's manifest + patch,
    the two opaque entry fields from the shipping Chiffon patch.  Nothing is
    invented, because `Unknown1/Unknown2` are per-slot constants whose meaning
    is still unknown (the C# writer copies them verbatim for the same reason).
    """
    targets = []
    try:
        man = json.load(open(REF_MANIFEST, encoding='utf-8'))
        for p in man['model']['pieces']:
            targets.append(dict(game=int(p['oldUnit'], 16), manager=int(p['unit'], 16),
                                body=BODY_CODE.get(p['body'], 3), slot=p['slot'],
                                piece_type=p['pieceType'], customization=b'',
                                unknown1=0, unknown2=0))
    except (OSError, ValueError, KeyError) as exc:
        log('  [warn] %s unreadable (%s); using the built-in id table'
            % (os.path.basename(REF_MANIFEST), exc))
        targets = [dict(game=g, manager=m, body=b, slot=s, piece_type=t,
                        customization=b'', unknown1=0, unknown2=0)
                   for g, m, b, s, t in SAME_KEY_FALLBACK]

    # per-slot customisation blob (240 B: body type + slot + weight + piece type)
    ents, _ = HP.scan_entries(STRUCTURE_PATCH)
    for t in targets:
        e = next((x for x in ents
                  if x['type_id'] == U.UNIT_ID and x['file_id'] == t['manager']), None)
        if e is not None:
            pl = HP.entry_payload(e, STRUCTURE_PATCH)
            t['customization'] = U.Unit.read(pl['toc'], b'').customization_data

    # the two opaque per-entry fields, from the shipping patch that owns the ids
    if os.path.exists(SOURCE_PATCH):
        sents, _ = HP.scan_entries(SOURCE_PATCH)
        src = {x['file_id']: x for x in sents if x['type_id'] == U.UNIT_ID}
        for t in targets:
            x = src.get(t['game'])
            if x is not None:
                t['unknown1'], t['unknown2'] = x['unknown1'], x['unknown2']
    else:
        log('  [warn] source patch missing (%s); entry fields default to 0'
            % SOURCE_PATCH)
    got = sum(1 for t in targets if t['customization'])
    log('same-key : %d game unit ids (%d with per-slot customisation, %d with '
        'source entry fields)' % (len(targets), got,
                                  sum(1 for t in targets if t['unknown1'] or t['unknown2'])))
    return targets


def pair_pieces(targets):
    """Assign every `PIECE_POSITIONS` triple a same-key target.

    Match on (body, slot) first so each piece borrows the customisation blob of
    the unit that really occupies that position; the reference protocol carries
    one triple (`0,3,1`) that has no matching piece, so that one takes whatever
    is left over.  The result is a bijection: every unit is used exactly once.
    """
    free = list(targets)
    out = []
    for body, slot, ptype in PIECE_POSITIONS:
        hit = next((t for t in free if t['body'] == body and t['slot'] == slot), None)
        if hit is None:
            hit = free[0]
        free.remove(hit)
        out.append((body, slot, ptype, hit))
    if free:
        raise AssertionError('%d same-key units left unassigned' % len(free))
    return out


def load_native_units(ids, directory):
    """-> [{id, name, customization, unknown1, unknown2, source}]

    `directory` holds the game's own Unit TOCs, as written by
    `hd2mod gamedata extract asset e0a48d0be9a7453f <id> --output <directory>`.

    We take the framework unit's 240-byte `customization_data` verbatim so the
    replacement describes itself exactly like the unit it stands in for (body
    type / slot / weight / piece type).  Round 59 wrote the Chiffon donor's blob
    -- "Torso, Stocky, Undergarment, Medium" -- even under a different id, which
    at best mislabels the unit and at worst places it in the wrong slot.

    The two opaque entry fields stay 0: they are per-slot constants we have not
    decoded, and inventing them would be worse than leaving the container
    defaults.  A native id needs neither them nor the Lua registry.
    """
    out = []
    for raw in ids:
        key = int(raw, 16) if isinstance(raw, str) else int(raw)
        path = os.path.join(directory, 'unit_0x%016x.toc' % key)
        if not os.path.exists(path):
            raise FileNotFoundError(
                'no framework unit at %s -- extract it with:\n'
                '  hd2mod gamedata extract asset e0a48d0be9a7453f %016x '
                '--game-data <游戏data目录> --output %s' % (path, key, directory))
        u = U.Unit.read(open(path, 'rb').read(), b'')
        out.append(dict(id=key, name=os.path.basename(path),
                        customization=u.customization_data or b'',
                        unknown1=0, unknown2=0, source=path))
        log('  native unit 0x%016x: version 0x%08x, %d streams / %d meshes, '
            'customization %d B' % (key, u.version, len(u.stream_infos),
                                    len(u.mesh_infos), len(u.customization_data or b'')))
    return out


def rewrite_lod_group_list(ref, ref_cull, boxes):
    """Point the donor's two volume records at our model.

    The 192-byte list is a threshold table (record 0: two screen-size thresholds
    per group) followed by one record per volume, each shaped

        u32 a(=1) u32 b(=1) vec3 min vec3 max f32 radius u32 parentTF
        f32 unk u32 0 u32 0xffffffff

    The offset table at +0x1c is relative to +8 for the first record but the
    donor's own values do not fit that rule for the others, so we locate each
    record by the (min, max) pair it stores -- which is bit-for-bit the local
    bbox of the matching culling mesh -- instead of trusting the table.
    """
    lg = bytearray(ref.lod_group_list_data)
    for tag in ('a', 'b'):
        v = np.asarray(ref_cull[tag], np.float64)
        needle = (v.min(axis=0).astype('<f4').tobytes()
                  + v.max(axis=0).astype('<f4').tobytes())
        at = bytes(lg).find(needle)
        if at < 0:
            raise LookupError('LOD group list: no volume record for culling %s' % tag)
        if bytes(lg).find(needle, at + 1) >= 0:
            raise LookupError('LOD group list: ambiguous volume record for %s' % tag)
        mn, mx, rad = boxes[tag]
        struct.pack_into('<3f', lg, at, *[float(x) for x in mn])
        struct.pack_into('<3f', lg, at + 12, *[float(x) for x in mx])
        struct.pack_into('<f', lg, at + 24, float(rad))
    return bytes(lg)


def build_patch_files(mesh, groups, textures, ref, ref_cull, bones, avatar, mesh_ids,
                      out_dir, mode='same-key', targets=None, cull_policy='target',
                      native=None, template='basic'):
    unit_id = make_id(UNIT_ID_PREFIX, 'unit/lunalice-rigtest')
    u = UB.build_unit(mesh, [(g['material_id'], g['short_id']) for g in groups],
                      ref, avatar, mesh_ids)
    UB.check_unit(u, mesh, avatar)

    if cull_policy == 'target':
        # the donor's LOD group list already describes exactly these shells
        log('lodgroup : kept verbatim (culling shells are the donor\'s own)')
    else:
        u.lod_group_list_data = rewrite_lod_group_list(
            ref, ref_cull, {'a': mesh['cull_a_box'], 'b': mesh['cull_b_box']})

    gpu = UB.UW.serialize_gpu(u)          # fills the stream offsets first
    toc = UB.UW.serialize_unit(u)         # every unit below reuses this payload

    if mode == 'native':
        # One unit, under a **real game** unit id, carrying that unit's own
        # 240-byte `customization_data`.  The game resolves it by itself: no Lua
        # registry, no manager configuration, and the manager CLI can label the
        # mod as equipment because the id belongs to a known armour archive.
        if not native:
            raise ValueError('native mode needs at least one --native-unit')
        units, pieces = [], []
        for entry in native:
            v = copy.copy(u)
            if entry['customization']:
                v.customization_data = entry['customization']
            units.append(PW.Entry(U.UNIT_ID, entry['id'], UB.UW.serialize_unit(v),
                                  b'', gpu, entry['unknown1'], entry['unknown2'],
                                  note='unit:native 0x%016x (%s)'
                                       % (entry['id'], entry['name'])))
            pieces.append((3, 0, 0, entry['id']))
        log('native   : %d unit id(s) from %s'
            % (len(units), os.path.dirname(native[0]['source'])))
    elif mode == 'fresh':
        units = [PW.Entry(U.UNIT_ID, unit_id, toc, b'', gpu, note='unit')]
        pieces = [(b, s, p, unit_id) for b, s, p in PIECE_POSITIONS]
    else:
        if not targets:
            raise ValueError('same-key mode needs the target id list')
        # one geometry, one GPU blob (the writer dedups by SHA256), N ids
        units = []
        pieces = []
        for body, slot, ptype, t in pair_pieces(targets):
            # shallow copy: only the 240-byte customisation blob differs, so the
            # TOC stays byte-identical apart from that one section
            v = copy.copy(u)
            if t['customization']:
                v.customization_data = t['customization']
            units.append(PW.Entry(U.UNIT_ID, t['game'],
                                  UB.UW.serialize_unit(v), b'', gpu,
                                  t['unknown1'], t['unknown2'],
                                  note='unit:%s/%s/slot%d' % (t['body'], t['slot'], slot)))
            pieces.append((body, slot, ptype, t['game']))
        if mode == 'both':
            units.append(PW.Entry(U.UNIT_ID, unit_id, toc, b'', gpu, note='unit:custom'))
            pieces = [(b, s, p, unit_id) for b, s, p in PIECE_POSITIONS]

    entries = list(units)
    entries += bones
    tpl = _template_bytes(template)
    for g in groups:
        entries.append(PW.Entry(A.MATERIAL_TYPE_ID, g['material_id'],
                                _material_bytes(g, tpl), b'', b'',
                                note='material:%s/%s'
                                     % (template, os.path.basename(g['name']))))
    for t in textures:
        entries.append(PW.Entry(A.TEXTURE_TYPE_ID, t['file_id'],
                                t['asset'].write_toc(), b'', t['gpu'],
                                note='texture:' + os.path.basename(t['name'])))
    res = PW.write_patch(entries, STRUCTURE_PATCH, out_dir, PATCH_NAME)
    return unit_id, pieces, res, len(toc), len(gpu), units


_TEMPLATE_CACHE = {}


def _template_bytes(key):
    """Raw bytes of a clean SDK template.  Always start from the pristine
    template -- never from an already-built material, which may carry the
    round-60 texture-table corruption."""
    if key not in _TEMPLATE_CACHE:
        path = os.path.join(TEMPLATE_DIR, TEMPLATE_FILE[key])
        _TEMPLATE_CACHE[key] = open(path, 'rb').read()
    return _TEMPLATE_CACHE[key]


def _material_bytes(group, template):
    """Rebuild one material from the pristine template with the group's own
    texture ids in the template's slot order."""
    m = A.Material.read(template)
    slots = TEMPLATE_SLOTS.get('basic')          # basic and basic+ share 0/1/2
    ids = group['texture_ids']
    if len(slots) != len(ids):
        raise AssertionError('template exposes %d slots, group carries %d ids'
                             % (len(slots), len(ids)))
    for slot, tid in zip(slots, ids):
        m.set_texture(slot, tid)
    return m.write()


def write_protocol(out_dir, name, pieces, map_model, enabled=True, note=None):
    lines = ['schema=2', 'revision=1', 'enabled=%s' % ('true' if enabled else 'false'),
             'model=%s,' % MODEL_KEY]
    for body, slot, ptype, uid in pieces:
        lines.append('model_piece=%s,%d,%016x,%d,%d,0'
                     % (MODEL_KEY, body, uid, slot, ptype))
    if map_model:
        lines.append('map_model=%s,%s' % (map_model, MODEL_KEY))
    if note:
        for line in note.splitlines():
            lines.append('# ' + line)
    path = os.path.join(out_dir, name + '.protocol')
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    return path


def write_manifest(out_dir, name, info):
    path = os.path.join(out_dir, name + '.manifest.json')
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(info, f, indent=2, ensure_ascii=False)
    return path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default=DEFAULT_OUT)
    ap.add_argument('--name', default='lunalice-rigtest')
    ap.add_argument('--materials', choices=('basic', 'flat'), default='basic')
    ap.add_argument('--template', choices=('basic', 'basic+', 'advanced'),
                    default='basic',
                    help='which clean SDK .material to clone per material. '
                         'basic = the round-59 "trash bag prop" template; '
                         'basic+ = same slot contract, SDK-marked UI-capable '
                         '(round-60 M3); advanced is not wired up yet')
    ap.add_argument('--pbr-b', type=int, default=DEFAULT_PBR_B,
                    help='B channel of the shared 4x4 PBR texture (R=metallic, '
                         'G=roughness). 0 = round-59 value, 255 = round-60 M2 '
                         'AO control')
    ap.add_argument('--normal-maps', action='store_true',
                    help='(reserved) bake the VMT bump maps into the normal slot')
    ap.add_argument('--max-tex', type=int, default=0)
    ap.add_argument('--map-model', default='')
    ap.add_argument('--unit-mode', choices=('fresh', 'same-key', 'both', 'native'),
                    default='fresh',
                    help='fresh: ONE unit holding the whole model, addressed by '
                         'the protocol (default, needs the Lua registry); '
                         'same-key: the same payload under the nine real game '
                         'unit ids, so no registry is needed; both: emit both; '
                         'native: ONE unit under a real game id you name with '
                         '--native-unit, so the game resolves it itself')
    ap.add_argument('--native-unit', action='append', default=[], metavar='0xID',
                    help='real game Unit id to write the whole model under '
                         '(repeatable; --unit-mode native)')
    ap.add_argument('--native-dir', default=NATIVE_DIR,
                    help='directory holding the game\'s own unit_0x<id>.toc '
                         'files (see load_native_units)')
    ap.add_argument('--cull-policy', choices=('target', 'whole-body'),
                    default='target',
                    help='target (default): reuse the reference unit\'s culling '
                         'shells verbatim, as the manager\'s own rebuilder does; '
                         'whole-body: rescale them to wrap the whole model '
                         '(round-59 behaviour, kept only as a control)')
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    t0 = time.time()
    log('=' * 78)
    log('build_patch.py -- %s -> %s' % (os.path.basename(NPZ), args.out))
    log('=' * 78)

    pos, tris, wb, wv, meta = load_pipeline()
    mdl, src_pos, src_nrm, uv, tris4 = load_source(pos, tris)
    ref, bones = load_reference()
    avatar = load_avatar()
    ref_cull = reference_culling_geometry(ref)
    targets = None if args.unit_mode in ('fresh', 'native') else load_same_key_targets()
    native = (load_native_units(args.native_unit, args.native_dir)
              if args.unit_mode == 'native' else None)
    if args.unit_mode == 'native' and not native:
        log('error: --unit-mode native needs at least one --native-unit 0x<id>')
        return 2

    log('-' * 78)
    packs = S.Packs()
    groups, textures, slot_to_group = build_materials(
        mdl, tris4, packs, args.materials, args.max_tex, args.normal_maps,
        pbr_b=args.pbr_b)
    log('template : %s   PBR rgba = (0, %d, %d, 255)'
        % (args.template, int(PBR_ROUGHNESS * 255), args.pbr_b & 0xFF))

    log('-' * 78)
    mesh, nrm = build_mesh(pos, tris, wb, wv, src_pos, src_nrm, uv, tris4,
                           slot_to_group)
    mesh.update(culling_boxes(mesh, ref, ref_cull, args.cull_policy))

    mesh_ids = {k: H.murmur32_hash(('lunalice/%s/%s' % (args.name, k)).encode('utf-8'))
                for k in ('lod0', 'lod1', 'lod2', 'lod3', 'cull_a', 'cull_b')}
    if len(set(mesh_ids.values())) != 6:
        raise AssertionError('mesh id collision: %s' % mesh_ids)

    if args.dry_run:
        log('dry run: stopping before the patch is written')
        return 0

    log('-' * 78)
    if os.path.isdir(args.out):
        for f in os.listdir(args.out):
            if f.startswith(os.path.splitext(PATCH_NAME)[0]) or f.startswith(args.name):
                os.remove(os.path.join(args.out, f))
    unit_id, pieces, res, toc_len, gpu_len, units = build_patch_files(
        mesh, groups, textures, ref, ref_cull, bones, avatar, mesh_ids, args.out,
        mode=args.unit_mode, targets=targets, cull_policy=args.cull_policy,
        native=native, template=args.template)

    if args.unit_mode == 'native':
        # A native id needs no runtime mapping at all, so the protocol is
        # written disabled: it stays as a record of what was built without
        # asking the Lua registry to remap anything.
        proto = write_protocol(
            args.out, args.name, pieces, '', enabled=False,
            note='native replacement: the game resolves unit 0x%016x itself.\n'
                 'No runtime mapping is required or wanted; this file is a '
                 'record only.' % native[0]['id'])
    else:
        proto = write_protocol(args.out, args.name, pieces, args.map_model,
                               note=None if args.map_model else
                               'map_model lines are intentionally omitted; add a '
                               'real target Item ID after installing the Patch.')
    info = dict(
        schema='hd2mod.rig-lab-build',
        generatedAt=time.strftime('%Y-%m-%dT%H:%M:%S'),
        sourcePatch=STRUCTURE_PATCH,
        outputPatch=res['toc'],
        modelKey=MODEL_KEY,
        unitMode=args.unit_mode,
        cullPolicy=args.cull_policy,
        materialTemplate=args.template,
        pbrRGBA=[0, int(PBR_ROUGHNESS * 255), args.pbr_b & 0xFF, 255],
        unitId='%016x' % unit_id,
        nativeUnits=([dict(id='%016x' % e['id'], customizationBytes=len(e['customization']),
                           source=e['source']) for e in native] if native else []),
        unitCount=len(units),
        unitIds=sorted('%016x' % e.file_id for e in units),
        pieceCount=len(pieces),
        entryCount=len(res['entries']),
        tocBytes=res['toc_size'], gpuBytes=res['gpu_size'],
        unitTocBytes=toc_len, unitGpuBytes=gpu_len,
        vertices=int(len(mesh['pos'])), triangles=int(len(mesh['indices']) // 3),
        materials=[dict(name=g['name'], id='%016x' % g['material_id'],
                        shortId='%x' % g['short_id'], slots=g['slots'])
                   for g in groups],
        textures=[dict(name=t['name'], id='%016x' % t['file_id'],
                       bytes=len(t['gpu']),
                       dxgi=t['asset'].dxgi_format,
                       width=t['asset'].width, height=t['asset'].height)
                  for t in textures],
        pieces=[dict(body=b, slot=s, pieceType=p, unit='%016x' % u)
                for b, s, p, u in pieces],
        fingerprint=meta.get('fingerprint'),
        params=meta.get('params'),
        files=dict(toc=res['toc'], gpu=res['gpu'], protocol=proto),
    )
    man = write_manifest(args.out, args.name, info)

    log('=' * 78)
    log('patch    : %s  (%d bytes, %d entries)'
        % (res['toc'], res['toc_size'], len(res['entries'])))
    log('gpu      : %s  (%.1f MB)' % (res['gpu'], res['gpu_size'] / 1e6))
    log('units    : %d unit payload(s) / %d protocol piece(s)  (mode %s)  '
        'toc %.1f KB / gpu %.1f MB'
        % (len(units), len(pieces), args.unit_mode, toc_len / 1e3, gpu_len / 1e6))
    for e in units:
        log('           unit 0x%016x  %s' % (e.file_id, e.note))
    log('protocol : %s' % proto)
    log('manifest : %s' % man)
    log('done in %.1fs' % (time.time() - t0))
    return 0


if __name__ == '__main__':
    sys.exit(main())
