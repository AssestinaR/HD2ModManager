# -*- coding: utf-8 -*-
"""Byte-exact *writer* for a legacy HD2 patch container.

Mirrors `HD2ModAdaptation/PatchReconstruction/PatchArchiveWriter.cs` (read line
by line) and is validated against the shipping Chiffon patch:

    header   = template[0 : typeOffset]  (magic + counts + 56B unk4Data copied
               verbatim)  followed by `numTypes * 32`-byte type records
    entries  = numFiles * 80 bytes, starting at `len(header)`
    TocData  = appended back to back, NO padding (the reference's sizes are all
               multiples of 4, so nothing needs aligning)
    stream   = 64-byte aligned, deduped by content; skipped entirely when empty
    gpu      = 64-byte aligned, deduped by `len:sha256`
    the TOC file is padded to at least `numFiles * 256` bytes

Type ordering follows `OrderSources`: types already present in the template keep
their template order; brand new types are appended sorted by type id.

The four type ids are stable across every patch we have:
    Unit     0xe0a48d0be9a7453f
    Bone     0x18dead01056b72e9
    Material 0xeac0b497876adedf
    Texture  0xcd4238c6a0c69e32
"""
import hashlib
import os
import struct

MAGIC = 4026531857
TYPE_OFFSET_SLIM = 72
TYPE_OFFSET_LEGACY = 60
TYPE_RECORD_SIZE = 32
ENTRY_RECORD_SIZE = 80
ALIGNMENT = 64


class Entry:
    """One asset inside the patch."""

    def __init__(self, type_id, file_id, toc=b'', stream=b'', gpu=b'',
                 unknown1=0, unknown2=0, unknown3=16, unknown4=64, note=''):
        self.type_id = type_id
        self.file_id = file_id
        self.toc = toc
        self.stream = stream
        self.gpu = gpu
        self.unknown1 = unknown1
        self.unknown2 = unknown2
        self.unknown3 = unknown3
        self.unknown4 = unknown4
        self.note = note

    @property
    def key(self):
        return (self.type_id, self.file_id)

    def __repr__(self):
        return ('Entry(type=0x%x id=0x%x toc=%d gpu=%d %s)'
                % (self.type_id, self.file_id, len(self.toc), len(self.gpu), self.note))


def _u32(d, o):
    return struct.unpack_from('<I', d, o)[0]


def _u64(d, o):
    return struct.unpack_from('<Q', d, o)[0]


def read_template_types(template):
    """-> (type_offset, [(type_id, raw32bytes), ...]) from a reference patch."""
    if _u32(template, 0) != MAGIC:
        raise ValueError('template is not a patch TOC')
    n = _u32(template, 4)
    # both candidate layouts are tried; the one whose entry table is consistent
    # wins (exactly the `ResolveLayout` scoring of the C# writer).
    files = _u32(template, 8)

    def score(type_off):
        ent = type_off + n * TYPE_RECORD_SIZE
        if len(template) < ent + files * ENTRY_RECORD_SIZE:
            return -(1 << 30)
        ids, declared = set(), 0
        for i in range(n):
            o = type_off + i * TYPE_RECORD_SIZE
            ids.add(_u64(template, o + 8))
            declared += _u64(template, o + 16)
        s = 1000 if declared == files else 0
        for i in range(files):
            o = ent + i * ENTRY_RECORD_SIZE
            if _u64(template, o):
                s += 1
            if _u64(template, o + 8) in ids:
                s += 10
        return s

    slim = score(TYPE_OFFSET_SLIM)
    legacy = score(TYPE_OFFSET_LEGACY)
    off = TYPE_OFFSET_SLIM if (n and slim >= legacy) else TYPE_OFFSET_LEGACY
    types = [(struct.unpack_from('<Q', template, off + i * TYPE_RECORD_SIZE + 8)[0],
              bytes(template[off + i * TYPE_RECORD_SIZE:off + (i + 1) * TYPE_RECORD_SIZE]))
             for i in range(n)]
    return off, types


def build_header(template, type_ids_in_order):
    """Copy the template's first `typeOffset` bytes, then write the type table."""
    off, tpl_types = read_template_types(template)
    known = {tid: raw for tid, raw in tpl_types}
    order = [tid for tid, _raw in tpl_types]
    for tid in type_ids_in_order:
        if tid not in known:
            known[tid] = None
            order.append(tid)
    head = bytearray(template[:off])
    struct.pack_into('<I', head, 4, len(order))
    counts = {}
    for tid in type_ids_in_order:
        counts[tid] = counts.get(tid, 0) + 1
    struct.pack_into('<I', head, 8, len(type_ids_in_order))
    for tid in order:
        raw = known[tid]
        if raw is None:                     # brand new type: copy the first
            raw = bytearray(next(r for _t, r in tpl_types if any(r)))  # template
            raw = bytearray(raw)
            struct.pack_into('<Q', raw, 8, tid)
            struct.pack_into('<Q', raw, 16, 0)
            struct.pack_into('<I', raw, 24, 16)
            struct.pack_into('<I', raw, 28, 64)
        else:
            raw = bytearray(raw)
        struct.pack_into('<Q', raw, 16, counts.get(tid, 0))
        head += raw
    return bytes(head), order


def _pad(buf, n=ALIGNMENT):
    extra = (-len(buf)) % n
    if extra:
        buf += b'\x00' * extra
    return buf


def write_patch(entries, template_path, out_dir, patch_name='9ba626afa44a3aa3.patch_0'):
    """Write `entries` as a legacy patch next to a copy of `template_path`.

    Returns a dict with the three output paths and a per-entry report.
    """
    template = open(template_path, 'rb').read()
    _off, tpl_types = read_template_types(template)
    type_order = [tid for tid, _raw in tpl_types]

    # ---- order the entries: template type order, then insertion order -------
    def type_rank(tid):
        return type_order.index(tid) if tid in type_order else len(type_order)
    ordered = sorted(range(len(entries)), key=lambda i: (type_rank(entries[i].type_id), i))
    ordered = [entries[i] for i in ordered]

    dup = {}
    for e in ordered:
        if e.key in dup:
            raise ValueError('duplicate entry %r' % (e.key,))
        dup[e.key] = True

    header, order = build_header(template, [e.type_id for e in ordered])
    entry_offset = len(header)
    toc_pos = entry_offset + len(ordered) * ENTRY_RECORD_SIZE

    toc = bytearray(header)
    toc.extend(b'\x00' * (len(ordered) * ENTRY_RECORD_SIZE))
    stream = bytearray()
    gpu = bytearray()
    gpu_seen = {}
    stream_seen = {}
    report = []

    for i, e in enumerate(ordered):
        toc_off = toc_pos
        toc.extend(e.toc)
        toc_pos += len(e.toc)

        if e.stream:
            if e.stream in stream_seen:
                stream_off = stream_seen[e.stream]
            else:
                _pad(stream)
                stream_off = len(stream)
                stream.extend(e.stream)
                stream_seen[e.stream] = stream_off
        else:
            stream_off = 0

        if e.gpu:
            ident = '%d:%s' % (len(e.gpu), hashlib.sha256(e.gpu).hexdigest())
            if ident in gpu_seen:
                gpu_off = gpu_seen[ident]
            else:
                _pad(gpu)
                gpu_off = len(gpu)
                gpu.extend(e.gpu)
                gpu_seen[ident] = gpu_off
        else:
            gpu_off = 0

        rec = struct.pack('<7Q6I',
                          e.file_id, e.type_id, toc_off, stream_off, gpu_off,
                          e.unknown1, e.unknown2,
                          len(e.toc), len(e.stream), len(e.gpu),
                          e.unknown3, e.unknown4, i + 1)
        toc[entry_offset + i * ENTRY_RECORD_SIZE:
            entry_offset + (i + 1) * ENTRY_RECORD_SIZE] = rec
        report.append(dict(index=i + 1, type_id=e.type_id, file_id=e.file_id,
                           toc_off=toc_off, toc_size=len(e.toc),
                           gpu_off=gpu_off, gpu_size=len(e.gpu), note=e.note))

    if len(toc) < len(ordered) * 256:
        toc.extend(b'\x00' * (len(ordered) * 256 - len(toc)))

    os.makedirs(out_dir, exist_ok=True)
    toc_path = os.path.join(out_dir, patch_name)
    gpu_path = toc_path + '.gpu_resources'
    stream_path = toc_path + '.stream'
    with open(toc_path, 'wb') as f:
        f.write(bytes(toc))
    with open(gpu_path, 'wb') as f:
        f.write(bytes(gpu))
    if stream:
        with open(stream_path, 'wb') as f:
            f.write(bytes(stream))
    elif os.path.exists(stream_path):
        os.remove(stream_path)
    return dict(toc=toc_path, gpu=gpu_path,
                stream=stream_path if stream else None,
                entries=report, type_order=order,
                toc_size=len(toc), gpu_size=len(gpu), stream_size=len(stream))


# ---------------------------------------------------------------- self check
def roundtrip_check(template_path):
    """Re-emit every entry of `template_path` untouched and require the TOC to
    come back byte for byte (the GPU sidecar must be identical too)."""
    import hd2_patch as HP
    entries, _raw = HP.scan_entries(template_path)
    src = open(template_path, 'rb').read()
    out = []
    for e in entries:
        pl = HP.entry_payload(e, template_path)
        out.append(Entry(e['type_id'], e['file_id'], pl['toc'], pl['stream'], pl['gpu'],
                         e['unknown1'], e['unknown2'], e['unknown3'], e['unknown4']))
    import tempfile
    d = tempfile.mkdtemp(prefix='patchw')
    res = write_patch(out, template_path, d, os.path.basename(template_path))
    new = open(res['toc'], 'rb').read()
    same_toc = new == src
    same_gpu = open(res['gpu'], 'rb').read() == open(template_path + '.gpu_resources', 'rb').read()
    return same_toc, same_gpu, res, d


if __name__ == '__main__':
    import glob
    import sys
    cand = glob.glob(r'E:\mod\*\9ba626afa44a3aa3.patch_0')
    tgt = sys.argv[1] if len(sys.argv) > 1 else \
        [c for c in cand if 'Chiffon' in c or '4.28' in c][0]
    ok_toc, ok_gpu, res, d = roundtrip_check(tgt)
    print('template : %s' % tgt)
    print('toc identical : %s (%d bytes)' % (ok_toc, res['toc_size']))
    print('gpu identical : %s (%d bytes)' % (ok_gpu, res['gpu_size']))
    print('type order    : %s' % ['0x%x' % t for t in res['type_order']])
    print('wrote to      : %s' % d)
