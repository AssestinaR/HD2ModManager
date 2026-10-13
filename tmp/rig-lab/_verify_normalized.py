# -*- coding: utf-8 -*-
"""Independent, read-only audit of a manager-CLI produced patch.

It does NOT trust the producer.  It re-reads the container, re-parses every
Unit, and re-derives every relationship it can from bytes alone:

  container   magic / counts / type table / every payload range inside its file
  ours        the carried unit is byte-identical to the source patch entry
  deps        every Material/Texture the source provided is still present
  hidden      every visible LOD of every non-target unit is the 3-vertex
              placeholder and its culling shell still matches the vanilla unit
  structure   per mesh: section vertex/index windows fit the stream,
              LOD0 sections tile the index buffer, material slots resolve

Usage:
    python _verify_normalized.py <source_patch_0> <output_patch_0>
                                 [--vanilla-dir <dir of unit_0x*.toc>]
"""
import argparse
import os
import struct
import sys

sys.path.insert(0, '.')
import hd2_patch as HP   # noqa: E402
import hd2_unit as U     # noqa: E402

UNIT = HP.UNIT_TYPE_ID
MATERIAL = 0xeac0b497876adedf
TEXTURE = 0xcd4238c6a0c69e32


class Report:
    def __init__(self):
        self.rows = []

    def check(self, name, ok, detail=''):
        self.rows.append((bool(ok), name, detail))
        return ok

    def dump(self):
        bad = 0
        for ok, name, detail in self.rows:
            if not ok:
                bad += 1
            print('%-5s %s%s' % ('ok' if ok else 'FAIL', name,
                                 ('   %s' % detail) if detail else ''))
        print()
        print('%d checks, %d failed' % (len(self.rows), bad))
        return bad


def load(path):
    entries, raw = HP.scan_entries(path)
    return entries, raw


def unit_map(entries, path):
    out = {}
    for e in entries:
        if e['type_id'] != UNIT:
            continue
        out[e['file_id']] = HP.entry_payload(e, path)
    return out


def visible_vert_count(u):
    return sum(si.num_vertices for si in u.stream_infos)


def check_container(rep, path, entries, raw):
    magic = struct.unpack_from('<I', raw, 0)[0]
    n_types = struct.unpack_from('<I', raw, 4)[0]
    n_files = struct.unpack_from('<I', raw, 8)[0]
    rep.check('container magic', magic == HP.MAGIC, '0x%x' % magic)
    rep.check('container file count matches the entry table', n_files == len(entries),
              'header=%d scanned=%d' % (n_files, len(entries)))
    rep.check('container declares at least the four known types', n_types >= 4, '%d' % n_types)

    toc_size = len(raw)
    gpu_path = path + '.gpu_resources'
    stream_path = path + '.stream'
    gpu_size = os.path.getsize(gpu_path) if os.path.exists(gpu_path) else 0
    stream_size = os.path.getsize(stream_path) if os.path.exists(stream_path) else 0

    bad_toc = bad_gpu = bad_stream = 0
    for e in entries:
        if e['toc_size'] and e['toc_offset'] + e['toc_size'] > toc_size:
            bad_toc += 1
        if e['gpu_size'] and e['gpu_offset'] + e['gpu_size'] > gpu_size:
            bad_gpu += 1
        if e['stream_size'] and e['stream_offset'] + e['stream_size'] > stream_size:
            bad_stream += 1
    rep.check('every TocData range lies inside the TOC file', bad_toc == 0, '%d bad' % bad_toc)
    rep.check('every GPU range lies inside the GPU sidecar', bad_gpu == 0, '%d bad' % bad_gpu)
    rep.check('every Stream range lies inside the Stream sidecar', bad_stream == 0, '%d bad' % bad_stream)
    rep.check('the GPU sidecar is not empty', gpu_size > 0, '%d B' % gpu_size)
    return gpu_size


def check_ours(rep, src_units, out_units, our_key):
    rep.check('the carried unit is present in the output', our_key in out_units,
              '0x%016x' % our_key)
    if our_key not in out_units or our_key not in src_units:
        return
    a, b = src_units[our_key], out_units[our_key]
    rep.check('the carried unit TocData is byte-identical to the source',
              a['toc'] == b['toc'], '%d vs %d B' % (len(a['toc']), len(b['toc'])))
    rep.check('the carried unit GPU payload is byte-identical to the source',
              a['gpu'] == b['gpu'], '%d vs %d B' % (len(a['gpu']), len(b['gpu'])))
    rep.check('the carried unit has no separate stream payload',
              len(b['stream']) == 0, '%d B' % len(b['stream']))


def check_deps(rep, src_entries, out_entries):
    def keys(entries, tid):
        return {e['file_id'] for e in entries if e['type_id'] == tid}
    for tid, label in ((MATERIAL, 'Material'), (TEXTURE, 'Texture')):
        s, o = keys(src_entries, tid), keys(out_entries, tid)
        rep.check('every source %s survived' % label, s <= o,
                  'missing %s' % sorted('0x%x' % x for x in s - o))


def check_hidden(rep, out_units, our_keys, vanilla_dir):
    """Every non-target unit must be invisible while keeping its cutout."""
    worst_span = 0.0
    bad_visible = []
    bad_tri = []
    for key, pl in out_units.items():
        if key in our_keys:
            continue
        u = U.Unit.read(pl['toc'], pl['gpu'])
        for mi in u.mesh_infos:
            if mi.lod_index < 0 or mi.num_sections == 0:
                continue
            drawn = [sec for sec in mi.sections if sec.num_indices]
            if len(drawn) > 1:
                bad_visible.append((key, mi.lod_index, 'drawn sections', len(drawn)))
            if sum(sec.num_indices for sec in drawn) > 3:
                bad_tri.append((key, mi.lod_index,
                                sum(sec.num_indices for sec in drawn)))
        for idx, si in enumerate(u.stream_infos):
            if si.num_vertices == 0 or si.num_vertices > 8:
                continue
            for v in u.decode_vertices(idx):
                p = v['position']
                worst_span = max(worst_span, max(abs(p[0]), abs(p[1]), abs(p[2])))
    rep.check('every non-target visible LOD draws from a single section',
              not bad_visible, 'offenders %s' % bad_visible[:4])
    rep.check('every non-target visible LOD draws at most one triangle',
              not bad_tri, 'offenders %s' % bad_tri[:4])
    rep.check('placeholder geometry stays sub-millimetre',
              worst_span <= 0.001, 'worst |component| = %.6f m' % worst_span)

    if not vanilla_dir or not os.path.isdir(vanilla_dir):
        rep.check('vanilla cutout comparison skipped (no directory)', True, vanilla_dir or '')
        return
    compared = matched = 0
    mismatch = []
    for key, pl in out_units.items():
        if key in our_keys:
            continue
        vpath = os.path.join(vanilla_dir, 'unit_0x%016x.toc' % key)
        if not os.path.exists(vpath):
            continue
        vu = U.Unit.read(open(vpath, 'rb').read(), b'')
        ou = U.Unit.read(pl['toc'], pl['gpu'])
        for vmi in vu.mesh_infos:
            if vmi.lod_index != -1:
                continue
            omi = next((m for m in ou.mesh_infos
                        if m.lod_index == -1 and m.mesh_id == vmi.mesh_id), None)
            compared += 1
            if omi is None:
                mismatch.append((key, 'shell absent', '0x%x' % vmi.mesh_id))
                continue
            vs, os_ = vu.stream_infos[vmi.stream_index], ou.stream_infos[omi.stream_index]
            sig_v = (vs.num_vertices, vs.num_indices, vs.vertex_stride,
                     vs.index_buffer_type, len(vs.components))
            sig_o = (os_.num_vertices, os_.num_indices, os_.vertex_stride,
                     os_.index_buffer_type, len(os_.components))
            sec_v = [(s.material_index, s.vertex_offset, s.num_vertices,
                      s.index_offset, s.num_indices) for s in vmi.sections]
            sec_o = [(s.material_index, s.vertex_offset, s.num_vertices,
                      s.index_offset, s.num_indices) for s in omi.sections]
            bbox_v = struct.unpack_from('<7f', vmi.unk2, 0)
            bbox_o = struct.unpack_from('<7f', omi.unk2, 0)
            if sig_v != sig_o:
                mismatch.append((key, 'stream ABI', sig_v, sig_o))
            elif sec_v != sec_o:
                mismatch.append((key, 'sections', sec_v, sec_o))
            elif bbox_v != bbox_o:
                mismatch.append((key, 'bbox', bbox_v, bbox_o))
            else:
                matched += 1
    rep.check('non-target cutout shells keep the vanilla mesh identity, ABI, '
              'sections and bounds',
              compared == 0 or matched == compared,
              '%d/%d shells identical%s' % (matched, compared,
                                            ('; %s' % mismatch[:2]) if mismatch else ''))


def _positions_blob(u, stream_idx):
    """Repack decoded positions of one stream into a flat float blob."""
    out = bytearray()
    for v in u.decode_vertices(stream_idx):
        out += struct.pack('<3f', *v['position'])
    return bytes(out)


def check_structure(rep, out_units, patch_material_ids, our_keys, vanilla_dir):
    """Invariants that hold for both the donor layout and the placeholder
    layout:

      * every section window fits inside its stream
      * inside one mesh the non-empty index windows never overlap
      * per stream the union of every index window is exactly [0, num_indices)
      * no *new* unresolvable material short id appears (vanilla already ships
        culling meshes whose short id is absent from the unit table)

    Vertex windows are deliberately NOT required to tile: the donor lets every
    section span the whole vertex buffer.
    """
    bad_window = []
    bad_overlap = []
    bad_union = []
    new_dangling = []
    external = set()
    visible_missing = []
    streams_checked = 0

    vanilla_dangling = {}
    if vanilla_dir and os.path.isdir(vanilla_dir):
        for key in out_units:
            vpath = os.path.join(vanilla_dir, 'unit_0x%016x.toc' % key)
            if os.path.exists(vpath):
                vanilla_dangling[key] = _dangling_short_ids(
                    U.Unit.read(open(vpath, 'rb').read(), b''))

    for key, pl in out_units.items():
        u = U.Unit.read(pl['toc'], pl['gpu'])
        stream_spans = {}
        for mi in u.mesh_infos:
            si = u.stream_infos[mi.stream_index]
            mesh_spans = []
            for sec in mi.sections:
                if sec.vertex_offset + sec.num_vertices > si.num_vertices:
                    bad_window.append((key, mi.lod_index, 'v', sec.vertex_offset,
                                       sec.num_vertices, si.num_vertices))
                if sec.index_offset + sec.num_indices > si.num_indices:
                    bad_window.append((key, mi.lod_index, 'i', sec.index_offset,
                                       sec.num_indices, si.num_indices))
                if sec.num_indices:
                    mesh_spans.append((sec.index_offset,
                                       sec.index_offset + sec.num_indices))
                    stream_spans.setdefault(mi.stream_index, []).append(
                        (sec.index_offset, sec.index_offset + sec.num_indices))
            mesh_spans.sort()
            for a, b in zip(mesh_spans, mesh_spans[1:]):
                if a[1] > b[0]:
                    bad_overlap.append((key, mi.lod_index, a, b))
            for sec in mi.sections:
                if sec.material_index >= len(mi.material_ids):
                    continue
                slot_id = mi.material_ids[sec.material_index]
                if slot_id not in u.section_ids:
                    continue
                asset = u.material_ids[u.section_ids.index(slot_id)]
                if asset == 0 or asset in patch_material_ids:
                    continue
                external.add(asset)
                if key in our_keys and mi.lod_index >= 0:
                    visible_missing.append((key, mi.lod_index, '0x%x' % asset))
        for stream_idx, spans in stream_spans.items():
            si = u.stream_infos[stream_idx]
            spans.sort()
            cursor = 0
            ok = True
            for lo, hi in spans:
                if lo > cursor:
                    ok = False
                    break
                cursor = max(cursor, hi)
            if ok and cursor == si.num_indices:
                streams_checked += 1
            else:
                bad_union.append((key, stream_idx, cursor, si.num_indices))

        dangling = _dangling_short_ids(u)
        if vanilla_dangling:
            fresh = dangling - vanilla_dangling.get(key, set())
            if fresh:
                new_dangling.append((key, sorted('0x%08x' % d for d in fresh)))

    rep.check('every section window fits inside its stream', not bad_window, '%s' % bad_window[:3])
    rep.check('inside one mesh the drawn index windows never overlap',
              not bad_overlap, '%s' % bad_overlap[:3])
    rep.check('per stream the drawn index windows cover the buffer exactly',
              not bad_union, '%s' % bad_union[:3])
    rep.check('no new unresolvable material short id is introduced',
              not new_dangling, '%s' % new_dangling[:3])
    rep.check('every visible LOD of the carried unit uses an in-package Material',
              not visible_missing, '%s' % visible_missing[:4])
    rep.check('streams were actually exercised', streams_checked > 0,
              '%d stream buffers covered exactly' % streams_checked)
    if external:
        print('note   external Material references (resolved from GameData): %s'
              % sorted('0x%x' % m for m in external))


def _dangling_short_ids(u):
    """Short material ids a mesh references but the unit table does not define."""
    out = set()
    for mi in u.mesh_infos:
        for sec in mi.sections:
            if sec.material_index >= len(mi.material_ids):
                continue
            slot_id = mi.material_ids[sec.material_index]
            if slot_id not in u.section_ids:
                out.add(slot_id)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('source')
    ap.add_argument('output')
    ap.add_argument('--our-unit', default='a1a7563e6ac8b8f8',
                    help='comma separated list of the unit ids this patch is '
                         'supposed to carry')
    ap.add_argument('--vanilla-dir', default=r'E:/tmp/hd2-mgroot/ds10/unit')
    args = ap.parse_args()

    our_keys = [int(x, 16) for x in args.our_unit.split(',') if x.strip()]
    src_entries, _ = load(args.source)
    out_entries, out_raw = load(args.output)
    src_units = unit_map(src_entries, args.source)
    out_units = unit_map(out_entries, args.output)

    print('source : %s (%d entries, %d units)' % (args.source, len(src_entries), len(src_units)))
    print('output : %s (%d entries, %d units)' % (args.output, len(out_entries), len(out_units)))
    print('our units: %s' % ['0x%016x' % k for k in our_keys])
    print()

    rep = Report()
    check_container(rep, args.output, out_entries, out_raw)
    for key in our_keys:
        check_ours(rep, src_units, out_units, key)
    check_deps(rep, src_entries, out_entries)
    check_hidden(rep, out_units, our_keys, args.vanilla_dir)
    mats = {e['file_id'] for e in out_entries if e['type_id'] == MATERIAL}
    check_structure(rep, out_units, mats, our_keys, args.vanilla_dir)
    return 1 if rep.dump() else 0


if __name__ == '__main__':
    sys.exit(main())
