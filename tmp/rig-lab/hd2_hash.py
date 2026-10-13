# -*- coding: utf-8 -*-
"""murmur64/murmur32 exactly as the SDK's `utils/hashing.py` implements them.

The engine names every bone/transform by the low 32 bits of a MurmurHash64A of
the name, so writing a TransformInfo entry for a bone means reproducing this
function bit for bit.
"""
M64 = 0xC6A4A7935BD1E995
R = 47
MASK = (1 << 64) - 1


def murmur64_hash(data, seed=0):
    b = bytearray(data)
    h = seed ^ ((M64 * len(b)) & MASK)
    off = (len(b) // 8) * 8
    for ll in range(0, off, 8):
        k = sum((b[ll + i] << (i * 8)) for i in range(8))
        k = (k * M64) & MASK
        k ^= (k >> R) & MASK
        k = (k * M64) & MASK
        h ^= k
        h = (h * M64) & MASK
    l = len(b) & 7
    if l >= 7:
        h ^= (b[off + 6] << 48)
    if l >= 6:
        h ^= (b[off + 5] << 40)
    if l >= 5:
        h ^= (b[off + 4] << 32)
    if l >= 4:
        h ^= (b[off + 3] << 24)
    if l >= 3:
        h ^= (b[off + 2] << 16)
    if l >= 2:
        h ^= (b[off + 1] << 8)
    if l >= 1:
        h ^= b[off]
        h = (h * M64) & MASK
    h ^= (h >> R) & MASK
    h = (h * M64) & MASK
    h ^= (h >> R) & MASK
    return h


def murmur32_hash(data, seed=0):
    return murmur64_hash(data, seed) >> 32


def name_hash(name):
    """Hash of a bone / node name exactly the way the engine computes it."""
    if isinstance(name, str):
        name = name.encode('utf-8')
    return murmur32_hash(name)


def load_bonehash(path):
    out = {}
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            p = line.split()
            if len(p) >= 2:
                try:
                    out[int(p[0])] = p[1]
                except ValueError:
                    pass
    return out


def load_typehash(path):
    out = {}
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            p = line.split()
            if len(p) >= 2:
                out[int(p[0], 16)] = p[1]
    return out
