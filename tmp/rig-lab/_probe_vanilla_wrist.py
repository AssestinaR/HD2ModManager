# -*- coding: utf-8 -*-
"""READ-ONLY: pull a vanilla Unit's GPU payload straight out of bundles.nxa.

Round 68 background
-------------------
The manager CLI's `gamedata extract asset` writes only the .toc for a bundled
Unit: GameDataAssetCatalogService.ReadPartAsync -> GameDataPackageResolver
.GetPackageResourceAsync returns null for the GPU part of a *bundled* archive,
so the vanilla skinning weights never reach disk.  This script reproduces
GameDataPackageResolver's bundle logic in Python (DSAR chunk walk + bundles.nxa
package table) so we can finally read the ground truth:

    how does HD2 itself weight the skin across the wrist?

That answers, with evidence instead of a guess, whether the forearm should be
bound to l_hand / l_hand_twist / l_forearm_twist and over what span.

Usage:
  python _probe_vanilla_wrist.py 0xcae35ee15168145f [--archive d923906f8b815987]
                                 [--out E:/tmp/round68-van-u]
                                 [--no-write]

Default target is the RE-2310 torso unit; pass --archive to disambiguate.
"""
import argparse
import os
import sqlite3
import struct
import sys

import numpy as np

import hd2_hash as H
import hd2_unit as U

GAME_DATA = 'D:/SteamLibrary/steamapps/common/Helldivers 2/data'
SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
INDEX = 'E:/tmp/hd2-mgroot/data/indexes/asset-index.sqlite'
DSAR_MAGIC = 0x52415344
CHUNK_START = 0x02
LZ4_COMPRESSION = 0x03

_chunk_map_cache = {}
_chunk_count_cache = {}


# ---------------------------------------------------------------- DSAR / bundle
def chunk_count(path):
    if path in _chunk_count_cache:
        return _chunk_count_cache[path]
    with open(path, 'rb') as f:
        head = f.read(12)
    n = struct.unpack_from('<I', head, 8)[0] if struct.unpack_from('<I', head, 0)[0] == DSAR_MAGIC else 0
    _chunk_count_cache[path] = n
    return n


def chunk_map(path):
    if path in _chunk_map_cache:
        return _chunk_map_cache[path]
    out = {}
    with open(path, 'rb') as f:
        for i in range(chunk_count(path)):
            f.seek(0x20 + i * 0x20)
            out[struct.unpack('<Q', f.read(8))[0]] = i
    _chunk_map_cache[path] = out
    return out


def read_chunk(path, index):
    import lz4.block
    with open(path, 'rb') as f:
        f.seek(0x20 + index * 0x20)
        head = f.read(0x20)
        comp_off = struct.unpack_from('<Q', head, 8)[0]
        unc_size = struct.unpack_from('<I', head, 16)[0]
        comp_size = struct.unpack_from('<I', head, 20)[0]
        comp_type = head[24]
        flags = head[25]
        f.seek(comp_off)
        raw = f.read(comp_size)
    if comp_type == 0:
        return flags, raw
    if comp_type == LZ4_COMPRESSION:
        return flags, lz4.block.decompress(raw, uncompressed_size=unc_size)
    raise ValueError('unsupported DSAR compression type %d' % comp_type)


def dsar_decompress_all(path):
    return b''.join(read_chunk(path, i)[1] for i in range(chunk_count(path)))


def dsar_read_from(path, offset):
    """One chunk group starting exactly at `offset` (mirrors ReadDsarResourceAsync)."""
    idx = chunk_map(path).get(offset)
    if idx is None:
        return b''
    out = []
    total = chunk_count(path)
    for i in range(idx, total):
        flags, data = read_chunk(path, i)
        if (flags & CHUNK_START) and out:
            break
        out.append(data)
    return b''.join(out)


def read_bundled_resources(path, offset, size):
    """Mirrors GameDataPackageResolver.ReadBundledResourcesAsync."""
    out = []
    read = 0
    while read < size:
        chunk = dsar_read_from(path, offset + read)
        if not chunk:
            break
        read += len(chunk)
        out.append(chunk)
    return b''.join(out)


def load_packages():
    """Mirrors GameDataPackageResolver.EnsureInitializedAsync."""
    db = os.path.join(GAME_DATA, 'bundles.nxa')
    data = dsar_decompress_all(db)
    count = struct.unpack_from('<i', data, 0x10)[0]
    packages = {}
    for index in range(count):
        base = 0x18 + index * 0x18
        if base + 0x18 > len(data):
            break
        size = struct.unpack_from('<q', data, base)[0]
        name_off = struct.unpack_from('<i', data, base + 8)[0]
        item_count = struct.unpack_from('<i', data, base + 12)[0]
        items_off = struct.unpack_from('<i', data, base + 16)[0]
        end = data.find(b'\x00', name_off)
        name = data[name_off:end].decode('utf-8', 'replace') if 0 <= name_off < len(data) else ''
        if not name:
            continue
        entries = []
        for item in range(item_count):
            if items_off + item * 0x10 + 0x10 > len(data):
                break
            off = items_off + item * 0x10
            entries.append((struct.unpack_from('<q', data, off)[0],
                            struct.unpack_from('<I', data, off + 8)[0],
                            data[off + 0x0f]))
        packages[name] = (size, entries)
    return packages


def extract_bundle_range(package_name, offset, size, packages):
    """Mirrors GameDataPackageResolver.ReadBundledResourceAsync."""
    if package_name not in packages:
        return None
    pkg_size, entries = packages[package_name]
    if offset < 0 or offset + size > pkg_size:
        return None
    entries = sorted(entries)
    result = bytearray(size)
    resource_end = offset + size
    for index, (arch_off, bundle_off, bundle_idx) in enumerate(entries):
        entry_end = entries[index + 1][0] if index + 1 < len(entries) else pkg_size
        copy_start = max(offset, arch_off)
        copy_end = min(resource_end, entry_end)
        if copy_start >= copy_end:
            continue
        entry_size = entry_end - arch_off
        decoded = read_bundled_resources(
            os.path.join(GAME_DATA, 'bundles.%02d.nxa' % bundle_idx), bundle_off, entry_size)
        src = copy_start - arch_off
        cnt = copy_end - copy_start
        if src + cnt > len(decoded):
            return None
        result[copy_start - offset:copy_start - offset + cnt] = decoded[src:src + cnt]
    return bytes(result)


# ---------------------------------------------------------------------- helpers
def s64(u):
    u &= (1 << 64) - 1
    return u - (1 << 64) if u >= (1 << 63) else u


def lookup(file_id, archive=None):
    conn = sqlite3.connect(INDEX)
    rows = conn.execute(
        'select archive_id, toc_data_offset, toc_data_size, gpu_resource_offset, gpu_resource_size '
        'from archive_entries where file_id=?', (s64(file_id),)).fetchall()
    conn.close()
    if archive:
        rows = [r for r in rows if r[0] == archive]
    return rows


def rodrigues(axis, a):
    K = np.array([[0.0, -axis[2], axis[1]],
                  [axis[2], 0.0, -axis[0]],
                  [-axis[1], axis[0], 0.0]])
    return np.eye(3) + np.sin(a) * K + (1.0 - np.cos(a)) * K @ K


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('file_id')
    ap.add_argument('--archive', default=None)
    ap.add_argument('--out', default='E:/tmp/round68-van-u')
    ap.add_argument('--no-write', action='store_true')
    ap.add_argument('--radius', type=float, default=0.20)
    ap.add_argument('--angle', type=float, default=180.0)
    args = ap.parse_args()

    fid = int(args.file_id, 16)
    rows = lookup(fid, args.archive)
    if not rows:
        print('no archive entry for %s' % args.file_id)
        return 1
    for row in rows:
        print('archive=%s toc=(%d,%d) gpu=(%d,%d)' % row)

    packages = load_packages()
    print('bundles.nxa packages: %d' % len(packages))

    archive_id, toc_off, toc_size, gpu_off, gpu_size = rows[0]
    # A bundled archive is THREE packages in bundles.nxa:
    #   <id>                  -> the TOC region
    #   <id>.gpu_resources    -> the GPU region
    #   <id>.stream           -> the stream region
    # GameDataPackageResolver.GetPackageResourceAsync uses the bare ArchiveId for
    # every part, so it can never resolve the GPU/stream of a bundled archive.
    # That is why `gamedata extract asset` writes only a .toc for vanilla units.
    toc = extract_bundle_range(archive_id, toc_off, toc_size, packages)
    gpu = extract_bundle_range(archive_id + '.gpu_resources', gpu_off, gpu_size, packages)
    if gpu is None and gpu_size:
        print('  (no separate .gpu_resources package; retrying bare name)')
        gpu = extract_bundle_range(archive_id, gpu_off, gpu_size, packages)
    print('toc %s bytes, gpu %s bytes' % (None if toc is None else len(toc),
                                          None if gpu is None else len(gpu)))
    if toc is None or gpu is None:
        print('FAILED to reconstruct bundled payload')
        return 1

    if not args.no_write:
        os.makedirs(args.out, exist_ok=True)
        base = os.path.join(args.out, 'unit_0x%016x' % fid)
        with open(base + '.toc', 'wb') as f:
            f.write(toc)
        with open(base + '.gpu_resources', 'wb') as f:
            f.write(gpu)
        print('written %s.toc / .gpu_resources' % base)

    bones = H.load_bonehash(SDK + '/hashlists/bonehash.txt')
    unit = U.Unit.read(toc, gpu)
    ti, bi = unit.transform_info, unit.bone_infos[0]
    names, heads = {}, {}
    for slot, ti_idx in enumerate(bi.real_indices):
        names[slot] = bones.get(ti.hashes[ti_idx], '0x%08x' % ti.hashes[ti_idx])
        m = ti.matrices[ti_idx]
        heads[slot] = np.array([m[12], m[13], m[14]], np.float64)

    print()
    print('palette (%d slots):' % len(names))
    for slot in sorted(names):
        print('  %3d  %-24s head=(%9.5f,%9.5f,%9.5f)' % (slot, names[slot], *heads[slot]))

    for mi in unit.mesh_infos:
        if mi.lod_index != 0:
            continue
        verts = unit.decode_vertices(mi.stream_index)
        bi_keys = sorted(k for k in verts[0] if k.startswith('bone_index'))
        bw_keys = sorted(k for k in verts[0] if k.startswith('bone_weight'))

        def cols(keys, dtype):
            # A component may decode to a scalar or to an N-vector per vertex;
            # flatten both shapes back to (verts, N).
            arrs = [np.asarray([v[k] for v in verts], dtype).reshape(len(verts), -1) for k in keys]
            return np.concatenate(arrs, axis=1) if arrs else np.zeros((len(verts), 0), dtype)

        bidx = cols(bi_keys, np.int64)
        wgt = cols(bw_keys, np.float64)
        if bi_keys != ['bone_index0'] or bidx.shape != wgt.shape:
            raise ValueError('probe supports one matching index/weight group only; multi-group data needs a dedicated decoder')
        mesh_matrix = np.asarray(ti.matrices[mi.transform_index]).reshape(4, 4)
        if not np.allclose(mesh_matrix, np.eye(4), atol=1e-6, rtol=0):
            raise ValueError('probe requires identity mesh transform; convert spaces before profiling')
        # Native indices are material-local: resolve remap[material][raw] to BoneInfo slot.
        raw = bidx.copy()
        mat_of = np.full(len(verts), -1, np.int32)
        indices = np.asarray(unit.index_list(mi.stream_index), np.int64)
        for section in mi.sections:
            used = np.unique(indices[section.index_offset:section.index_offset + section.num_indices])
            if np.any((mat_of[used] >= 0) & (mat_of[used] != section.material_index)):
                raise ValueError('vertex has conflicting material remaps')
            mat_of[used] = section.material_index
        if np.any(mat_of < 0):
            raise ValueError('unreferenced vertices have unknown remap')
        bi_info = unit.bone_infos[mi.lod_index]
        for mat_idx in np.unique(mat_of):
            remap = np.asarray(bi_info.remaps[mat_idx], np.int64)
            mask = mat_of == mat_idx
            if np.any(raw[mask][wgt[mask] > 1e-6] >= len(remap)):
                raise ValueError('positive weight references missing remap index')
            bidx[mask] = remap[np.minimum(raw[mask], len(remap) - 1)]
        print('  material remaps %s, positive raw indices changed %d' %
              (list(map(len, bi_info.remaps)), int(np.count_nonzero((bidx != raw) & (wgt > 1e-6)))))
        pos = np.asarray([v['position'] for v in verts], np.float64)
        mass = np.zeros(max(len(names), int(bidx.max()) + 1))
        for j in range(bidx.shape[1]):
            np.add.at(mass, bidx[:, j], wgt[:, j])
        order = np.argsort(-mass)[:len(names)]
        print()
        print('mesh lod0 stream=%d verts=%d sections=%d' % (mi.stream_index, len(pos), mi.num_sections))
        print('  palette bones by summed weight:')
        for slot in order:
            print('    %3d  %-24s %10.1f' % (slot, names.get(int(slot), '?'), mass[slot]))
        print('  pos bounds x[%.3f %.3f] y[%.3f %.3f] z[%.3f %.3f]'
              % (pos[:, 0].min(), pos[:, 0].max(), pos[:, 1].min(), pos[:, 1].max(),
                 pos[:, 2].min(), pos[:, 2].max()))

        # forearm profile: elbow -> hand, for every elbow-like bone present
        for el_name, hd_name in (('l_elbow', 'l_hand'), ('r_elbow', 'r_hand')):
            slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}
            if el_name not in slot_of or hd_name not in slot_of:
                continue
            E, W = heads[slot_of[el_name]], heads[slot_of[hd_name]]
            axis = W - E
            L = float(np.linalg.norm(axis))
            axis /= L
            rel = pos - E
            t = rel @ axis
            rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
            print()
            print('  [%s] L=%.1f mm   (radius gate %.0f mm)' % (el_name, L * 1000, args.radius * 1000))
            print('     t/L   n    ' + '  '.join('%-8s' % n for n in
                  (el_name, hd_name, 'twist', 'fore_tw', 'distal')))
            tw = slot_of.get(el_name.replace('elbow', 'hand_twist'), None)
            ft = slot_of.get(el_name.replace('elbow', 'forearm_twist'), None)
            for k in range(23):
                lo, hi = k * 0.05, (k + 1) * 0.05
                sel = (rad < args.radius) & (t > 0) & (t < L) & (t / L >= lo) & (t / L < hi)
                if sel.sum() == 0:
                    continue

                def w_of(slot):
                    if slot is None:
                        return 0.0
                    m = np.zeros(len(pos))
                    hit = bidx == slot
                    m = np.where(hit, wgt, 0.0).sum(axis=1)
                    return float(m[sel].mean())

                el_w, hd_w = w_of(slot_of[el_name]), w_of(slot_of[hd_name])
                tw_w, ft_w = w_of(tw), w_of(ft)
                print('     %4.2f  %5d    %8.3f  %8.3f  %8.3f  %8.3f  %8.3f'
                      % (0.5 * (lo + hi), int(sel.sum()), el_w, hd_w, tw_w, ft_w,
                         hd_w + tw_w + ft_w))
    return 0


if __name__ == '__main__':
    sys.exit(main())
