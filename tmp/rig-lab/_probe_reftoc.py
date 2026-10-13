"""Dump the full legacy patch TOC of every reference patch we have.

Goal: confirm the header layout (16B + 56B unk4Data + type table @0x48 +
entry table @0x48+numTypes*32), and list every entry so we know which asset
IDs a working patch has to carry.
"""
import os
import struct
import sys
import glob

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as HP
import hd2_hash as H

MAGIC = 4026531857
TYPE_SIZE = 32
ENTRY_SIZE = 80


def u32(b, o):
    return struct.unpack_from('<I', b, o)[0]


def u64(b, o):
    return struct.unpack_from('<Q', b, o)[0]


def dump(path):
    raw = open(path, 'rb').read()
    print('=' * 100)
    print('%s   (%d bytes)' % (os.path.basename(path), len(raw)))
    magic, ntypes, nfiles, unk = struct.unpack_from('<4I', raw, 0)
    print('  magic=%d (0x%x) numTypes=%d numFiles=%d unknown=%d' % (magic, magic, ntypes, nfiles, unk))
    print('  unk4Data[16..72] =', raw[16:72].hex())
    # split into u32 words for readability
    print('  unk4Data words   =', [hex(u32(raw, 16 + 4 * i)) for i in range(14)])

    types = []
    for i in range(ntypes):
        o = 72 + i * TYPE_SIZE
        t = struct.unpack_from('<3Q2I', raw, o)
        types.append(t)
        print('  type[%d] @0x%x: unk1=%d typeID=0x%x numFiles=%d unk2=%d unk3=%d'
              % (i, o, t[0], t[1], t[2], t[3], t[4]))

    eo = 72 + ntypes * TYPE_SIZE
    print('  entry table @0x%x (%d)' % (eo, eo))
    print('  %-4s %-18s %-6s %-9s %-9s %-9s %-9s %-9s %-9s %-9s %-9s %-5s %-5s %-5s'
          % ('#', 'FileID', 'TypeID', 'TocOff', 'StrOff', 'GpuOff', 'Unk1', 'Unk2',
             'TocSize', 'StrSize', 'GpuSize', 'U3', 'U4', 'Idx'))
    per_type = {}
    for i in range(nfiles):
        o = eo + i * ENTRY_SIZE
        fid, tid, tocoff, stroff, gpuoff, u1, u2, tocsz, strsz, gpusz, u3, u4, idx = \
            struct.unpack_from('<7Q6I', raw, o)
        per_type.setdefault(tid, []).append(i)
        print('  %-4d %-18x %-6d %-9d %-9d %-9d %-9d %-9d %-9d %-9d %-9d %-5d %-5d %-5d'
              % (i, fid, tid, tocoff, stroff, gpuoff, u1, u2, tocsz, strsz, gpusz, u3, u4, idx))
    tmap = {t[1]: t for t in types}
    print('  --- per type ---')
    for tid, idxs in per_type.items():
        t = tmap.get(tid)
        print('   type %d (0x%x): %d entries, header numFiles=%s'
              % (tid, tid, len(idxs), t[2] if t else '?'))

    # first TocData start
    if nfiles:
        first = min(struct.unpack_from('<Q', raw, eo + i * ENTRY_SIZE + 16)[0] for i in range(nfiles))
        print('  first TocDataOffset = 0x%x (%d), gap from entry-table end 0x%x = %d'
              % (first, first, eo + nfiles * ENTRY_SIZE, first - (eo + nfiles * ENTRY_SIZE)))


if __name__ == '__main__':
    if len(sys.argv) > 1:
        for p in sys.argv[1:]:
            dump(p)
    else:
        pats = [
            r'E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310\*.patch_0',
            r'E:\mod\*\*.patch_0',
        ]
        seen = set()
        for pat in pats:
            for p in glob.glob(pat):
                if p not in seen:
                    seen.add(p)
                    dump(p)
