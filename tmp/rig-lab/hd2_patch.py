"""Minimal reader for a legacy HD2 patch container (`*.patch_0` + sidecars).

Ported from `HD2ModAdaptation/PatchReconstruction/PatchTocScanner.cs` and
`PatchEntryPayloadReader.cs`.  Only what the offline lab needs:

  * list the entries (typeId / fileId / payload offsets)
  * pull the Unit asset's TocData and parse its TransformInfo (bone rest matrices)
  * pull the Bone asset (hash -> bone name)

Nothing here writes anything.
"""
import struct
import sys

MAGIC = 4026531857
LEGACY_TYPE_OFFSET = 60
SLIM_TYPE_OFFSET = 72
TYPE_RECORD_SIZE = 32
ENTRY_RECORD_SIZE = 80

UNIT_TYPE_ID = 0xE0A48D0BE9A7453F
BONE_TYPE_ID = 0x18DEAD01056B72E9
COMPOSITE_UNIT_TYPE_ID = 0xC4F0F4BE7FB0C8D6

TYPE_NAMES = {
    UNIT_TYPE_ID: 'Unit',
    BONE_TYPE_ID: 'Bone',
    COMPOSITE_UNIT_TYPE_ID: 'CompositeUnit',
}


def _u32(d, o):
    return struct.unpack_from('<I', d, o)[0]


def _u64(d, o):
    return struct.unpack_from('<Q', d, o)[0]


def _score(d, type_off, entries_off, type_count, file_count):
    if len(d) < entries_off + file_count * ENTRY_RECORD_SIZE:
        return -(1 << 30)
    types = set()
    total = 0
    for i in range(type_count):
        o = type_off + i * TYPE_RECORD_SIZE
        types.add(_u64(d, o + 8))
        total += _u64(d, o + 16)
    s = 1000 if total == file_count else 0
    for i in range(file_count):
        o = entries_off + i * ENTRY_RECORD_SIZE
        if _u64(d, o) != 0:
            s += 1
        if _u64(d, o + 8) in types:
            s += 10
    return s


def scan_entries(path):
    d = open(path, 'rb').read()
    if len(d) < 12 or _u32(d, 0) != MAGIC:
        raise ValueError('not a patch TOC: %s' % path)
    type_count, file_count = _u32(d, 4), _u32(d, 8)
    legacy = LEGACY_TYPE_OFFSET + type_count * TYPE_RECORD_SIZE
    slim = SLIM_TYPE_OFFSET + type_count * TYPE_RECORD_SIZE
    entries_off = (slim if type_count and
                   _score(d, SLIM_TYPE_OFFSET, slim, type_count, file_count)
                   > _score(d, LEGACY_TYPE_OFFSET, legacy, type_count, file_count)
                   else legacy)
    out = []
    for i in range(file_count):
        o = entries_off + i * ENTRY_RECORD_SIZE
        out.append(dict(
            index=i, file_id=_u64(d, o), type_id=_u64(d, o + 8),
            toc_offset=_u64(d, o + 16), stream_offset=_u64(d, o + 24),
            gpu_offset=_u64(d, o + 32), unknown1=_u64(d, o + 40), unknown2=_u64(d, o + 48),
            toc_size=_u32(d, o + 56), stream_size=_u32(d, o + 60),
            gpu_size=_u32(d, o + 64), unknown3=_u32(d, o + 68),
            unknown4=_u32(d, o + 72)))
    return out, d


def read_range(path, offset, size, sidecar=False):
    if size == 0:
        return b''
    try:
        with open(path, 'rb') as f:
            f.seek(offset)
            return f.read(size)
    except FileNotFoundError:
        return b''


def entry_payload(entry, toc_path):
    return dict(
        toc=read_range(toc_path, entry['toc_offset'], entry['toc_size']),
        stream=read_range(toc_path + '.stream', entry['stream_offset'], entry['stream_size'], True),
        gpu=read_range(toc_path + '.gpu_resources', entry['gpu_offset'], entry['gpu_size'], True))


# ---------------------------------------------------------------- Unit TocData
def read_transform_info(toc):
    """Bone rest transforms from a Unit TocData -- same layout as the avatar toc.

    Verified against `UnitMeshReader.ReadTransformInfo`:
        count      @ transformInfoOffset            (u32)
        locals     @ +16                count*64
        matrices   @ +16 + count*64     count*64    <- row-major, translation flat 12..14
        entries    @ +16 + 2*count*64   count*4     (u16 x2 per bone: parent is the 2nd)
        nameHashes @ +16 + 2*count*64 + count*4   count*4
    """
    tio = _u32(toc, 0x34)
    if tio == 0 or tio >= len(toc):
        return dict(count=0, matrices=[], parents=[], hashes=[], transform_info_offset=tio)
    n = _u32(toc, tio)
    mo = tio + 16 + n * 64
    eo = mo + n * 64
    ho = eo + n * 4
    if ho + n * 4 > len(toc):
        raise ValueError('transform info runs past the payload (n=%d)' % n)
    matrices = []
    for i in range(n):
        raw = list(struct.unpack_from('<16f', toc, mo + i * 64))
        matrices.append([raw[0], raw[4], raw[8], raw[12],
                         raw[1], raw[5], raw[9], raw[13],
                         raw[2], raw[6], raw[10], raw[14],
                         raw[3], raw[7], raw[11], raw[15]])
    parents = [struct.unpack_from('<H', toc, eo + i * 4 + 2)[0] for i in range(n)]
    hashes = [_u32(toc, ho + i * 4) for i in range(n)]
    return dict(count=n, matrices=matrices, parents=parents, hashes=hashes,
                transform_info_offset=tio, version=_u32(toc, 0x2c),
                bones_ref=_u64(toc, 8), composite_ref=_u64(toc, 16),
                bone_info_offset=_u32(toc, 0x58), stream_info_offset=_u32(toc, 0x5c),
                mesh_info_offset=_u32(toc, 0x64))


def origin(m):
    return (m[3], m[7], m[11])


def bone_hash_file(path):
    names = {}
    for line in open(path, encoding='utf-8', errors='replace'):
        p = line.split()
        if len(p) >= 2:
            try:
                names[int(p[0])] = p[1]
            except ValueError:
                pass
    return names


def main():
    ap = sys.argv
    if len(ap) < 2:
        print('usage: hd2_patch.py <dir-with-patch_0> [bonehash.txt]')
        return 1
    import glob
    import os
    d = ap[1]
    tocs = glob.glob(os.path.join(d, '*.patch_*'))
    tocs = [t for t in tocs if not t.endswith('.stream') and not t.endswith('.gpu_resources')]
    if not tocs:
        print('no patch file found in %s' % d)
        return 1
    toc_path = tocs[0]
    print('patch: %s (%d bytes)' % (toc_path, os.path.getsize(toc_path)))
    entries, _raw = scan_entries(toc_path)
    print('entries: %d' % len(entries))
    for e in entries:
        tn = TYPE_NAMES.get(e['type_id'], '?')
        print('  [%2d] %-14s type=0x%016x file=0x%016x  toc %d..%d (%d B)  gpu %d..%d (%d B)'
              % (e['index'], tn, e['type_id'], e['file_id'],
                 e['toc_offset'], e['toc_offset'] + e['toc_size'], e['toc_size'],
                 e['gpu_offset'], e['gpu_offset'] + e['gpu_size'], e['gpu_size']))

    units = [e for e in entries if e['type_id'] == UNIT_TYPE_ID]
    names = bone_hash_file(ap[2]) if len(ap) > 2 else {}
    for e in units:
        pl = entry_payload(e, toc_path)
        print('\n=== Unit 0x%016x  toc %d B  gpu %d B ===' % (e['file_id'], len(pl['toc']), len(pl['gpu'])))
        ti = read_transform_info(pl['toc'])
        print('  version %d  bones(transformInfo) %d  boneInfo@0x%x streamInfo@0x%x meshInfo@0x%x'
              % (ti['version'], ti['count'], ti['bone_info_offset'],
                 ti['stream_info_offset'], ti['mesh_info_offset']))
        for i in range(ti['count']):
            nm = names.get(ti['hashes'][i])
            if nm:
                o = origin(ti['matrices'][i])
                print('    %3d %-22s parent=%3d  pos (%+.4f, %+.4f, %+.4f)'
                      % (i, nm, ti['parents'][i], o[0], o[1], o[2]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
