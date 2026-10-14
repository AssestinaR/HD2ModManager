# -*- coding: utf-8 -*-
"""Minimal read-only reader for HD2 bundled ("slim") game data.

Port of the parts of HD2ModCore/Infrastructure/GameDataPackageResolver.cs that we
need: parse bundles.nxa, resolve a virtual archive id to its byte range, and pull
DSAR/LZ4 chunks out of bundles.NN.nxa.  Nothing here writes to the game.

This is what lets a probe read thousands of assets quickly instead of shelling
out to the CLI once per asset.
"""
import os
import struct

try:
    import lz4.block as _lz4
except Exception:  # pragma: no cover
    _lz4 = None

TOC_MAGIC = 4026531857
DSAR_MAGIC = 0x52415344
CHUNK_START = 0x02
COMP_NONE = 0x00
COMP_LZ4 = 0x03


def _u32(b, o):
    return struct.unpack_from('<I', b, o)[0]


def _i32(b, o):
    return struct.unpack_from('<i', b, o)[0]


def _i64(b, o):
    return struct.unpack_from('<q', b, o)[0]


def _u64(b, o):
    return struct.unpack_from('<Q', b, o)[0]


class _Dsar:
    """A DSAR container: chunk table + (optionally LZ4) payloads."""

    _CACHE_MAX = 96

    def __init__(self, path):
        self.path = path
        self.chunk_count = 0
        self._chunk_index = {}   # key (u64 at header start) -> chunk index
        self._cache = {}         # chunk index -> (chunk_type, data)
        self._cache_order = []
        with open(path, 'rb') as f:
            head = f.read(0x20)
            if len(head) < 0x20 or _u32(head, 0) != DSAR_MAGIC:
                raise ValueError('not a DSAR file: %s' % path)
            self.chunk_count = _u32(head, 8)
            f.seek(0x20)
            table = f.read(0x20 * self.chunk_count)
        for i in range(self.chunk_count):
            o = i * 0x20
            self._chunk_index[_u64(table, o)] = i

    def _chunk(self, f, idx):
        hit = self._cache.get(idx)
        if hit is not None:
            return hit
        f.seek(0x20 + idx * 0x20)
        h = f.read(0x20)
        comp_off = _u64(h, 8)
        unc_size = _u32(h, 16)
        comp_size = _u32(h, 20)
        comp_type = h[24]
        chunk_type = h[25]
        f.seek(comp_off)
        raw = f.read(comp_size)
        if comp_type == COMP_NONE:
            out = (chunk_type, raw)
        elif comp_type != COMP_LZ4:
            raise ValueError('unsupported DSAR compression %d' % comp_type)
        else:
            if _lz4 is None:
                raise RuntimeError('lz4 module required to read %s' % self.path)
            out = (chunk_type, _lz4.decompress(raw, uncompressed_size=unc_size))
        self._cache[idx] = out
        self._cache_order.append(idx)
        if len(self._cache_order) > self._CACHE_MAX:
            self._cache.pop(self._cache_order.pop(0), None)
        return out

    def read_at(self, offset):
        """Resource starting exactly at `offset`; concatenate chunks until the
        next chunk flagged as a start."""
        idx = self._chunk_index.get(offset)
        if idx is None:
            return b''
        out = []
        f = self._fh()
        while idx < self.chunk_count:
            ctype, data = self._chunk(f, idx)
            if (ctype & CHUNK_START) and out:
                break
            out.append(data)
            idx += 1
        return b''.join(out)

    def _fh(self):
        if getattr(self, '_handle', None) is None:
            self._handle = open(self.path, 'rb')
        return self._handle

    def read_whole(self):
        out = []
        f = self._fh()
        for i in range(self.chunk_count):
            out.append(self._chunk(f, i)[1])
        return b''.join(out)


class GameData:
    def __init__(self, root):
        self.root = os.path.abspath(root)
        self._packages = {}       # name -> (size, [(origOff, startOff, bundleIdx)])
        self._dsar = {}
        self._load_bundles()

    # ---------------------------------------------------------------- bundles
    def _load_bundles(self):
        path = os.path.join(self.root, 'bundles.nxa')
        if not os.path.isfile(path):
            return
        blob = _Dsar(path).read_whole()
        if len(blob) < 0x18:
            return
        count = _i32(blob, 0x10)
        for n in range(count):
            po = 0x18 + n * 0x18
            if po + 0x18 > len(blob):
                break
            size = _i64(blob, po)
            name_off = _i32(blob, po + 8)
            items = _i32(blob, po + 12)
            items_off = _i32(blob, po + 16)
            end = blob.find(b'\x00', name_off)
            name = blob[name_off:end].decode('utf-8', 'replace') if end > 0 else ''
            if not name:
                continue
            entries = []
            for i in range(items):
                io = items_off + 0x10 * i
                if io + 0x10 > len(blob):
                    break
                entries.append((_i64(blob, io), _u32(blob, io + 8), blob[io + 0x0F]))
            self._packages[name] = (size, entries)

    def has_package(self, name):
        return name in self._packages or os.path.isfile(os.path.join(self.root, name))

    def _dsar_for(self, name):
        if name not in self._dsar:
            self._dsar[name] = _Dsar(os.path.join(self.root, name))
        return self._dsar[name]

    # ------------------------------------------------------------------ read
    def read_range(self, package, offset, size):
        """Bytes [offset, offset+size) of a logical package."""
        if size <= 0:
            return b''
        direct = os.path.join(self.root, package)
        if os.path.isfile(direct):
            with open(direct, 'rb') as f:
                head = f.read(4)
            if len(head) == 4 and _u32(head, 0) == DSAR_MAGIC:
                return self._dsar_for(package).read_at(offset)[:size]
            with open(direct, 'rb') as f:
                f.seek(offset)
                return f.read(size)

        if package not in self._packages:
            return b''
        total, entries = self._packages[package]
        requested_end = min(total, offset + size)
        out = bytearray(max(0, requested_end - offset))
        ordered = sorted(entries, key=lambda e: e[0])
        for i, (orig, start, bundle) in enumerate(ordered):
            nxt = ordered[i + 1][0] if i + 1 < len(ordered) else total
            copy_start = max(offset, orig)
            copy_end = min(requested_end, nxt)
            if copy_end <= copy_start:
                continue
            before = copy_start - orig
            need = copy_end - orig
            blob = self._dsar_for('bundles.%02d.nxa' % bundle).read_at(start)
            if len(blob) <= before:
                continue
            take = min(len(blob) - before, copy_end - copy_start)
            out[copy_start - offset:copy_start - offset + take] = blob[before:before + take]
        return bytes(out)


if __name__ == '__main__':
    import sys
    gd = GameData(sys.argv[1])
    print('packages in bundles.nxa: %d' % len(gd._packages))
    print('sample:', list(gd._packages)[:5])
