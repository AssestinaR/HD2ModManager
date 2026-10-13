# -*- coding: utf-8 -*-
"""Source side of the transparency question.

For every material slot the model uses, print the raw VMT text (looking for
$alphatest / $translucent / $alphatestreference / $nocull / $outline) and the
alpha content of the referenced VTF.

usage: _probe_srcvmt.py
"""
import os
import re
import struct
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_source as S
import hd2_vtf as V

KEYS = ('$alphatest', '$translucent', '$alphatestreference', '$nocull',
        '$outline', '$blendtintbybasealpha', '$vertexalpha', '$vertexcolor',
        '$basetexture', '$bumpmap', '$surfaceprop', '$mostlyopaque',
        '$allowalphatocoverage', '$alpha')


def alpha_stats(raw):
    """alpha stats of mip0 without texconv: DXT5 -> 8-bit, DXT1 -> 1-bit."""
    v = V.VTF.read(raw)
    m0 = v.mips[0]
    w, h = v.width, v.height
    if v.hi_format in (15, 14):                      # DXT5 / DXT3
        bw, bh = (w + 3) // 4, (h + 3) // 4
        a = np.frombuffer(m0[:bw * bh * 16], np.uint8).reshape(bh, bw, 16)
        if v.hi_format == 15:                        # BC3: alpha = 2 endpoints + 3-bit indices
            e0 = a[:, :, 0].astype(np.int32)
            e1 = a[:, :, 1].astype(np.int32)
            bits = (a[:, :, 2].astype(np.int32)
                    | (a[:, :, 3].astype(np.int32) << 8)
                    | (a[:, :, 4].astype(np.int32) << 16)
                    | (a[:, :, 5].astype(np.int32) << 24)
                    | (a[:, :, 6].astype(np.int32) << 32)
                    | (a[:, :, 7].astype(np.int32) << 40))
            out = np.zeros((bh, bw, 16), np.uint8)
            for i in range(16):
                idx = (bits >> (3 * i)) & 7
                v0, v1 = e0, e1
                val = np.where(idx == 0, v0,
                               np.where(idx == 1, v1,
                                        np.where(v0 > v1,
                                                 ((7 - idx) * v0 + (idx - 1) * v1) // 7,
                                                 ((5 - idx) * v0 + (idx - 1) * v1) // 5)))
                out[:, :, i] = np.clip(val, 0, 255).astype(np.uint8)
            al = out.transpose(0, 2, 1).reshape(bh, 4, bw, 4).transpose(0, 2, 1, 3)
            al = al[:h // 4 * 4 if h % 4 == 0 else h, :w // 4 * 4 if w % 4 == 0 else w]
            flat = al.reshape(-1)
        else:                                        # DXT3: 4-bit explicit alpha
            nib = a[:, :, :8]
            al = np.zeros((bh, bw, 16), np.uint8)
            for i in range(16):
                byte = nib[:, :, i // 2].astype(np.int32)
                v4 = (byte >> 4) if i % 2 == 0 else (byte & 0xF)
                al[:, :, i] = (v4 * 17).astype(np.uint8)
            flat = al.reshape(-1)
        return ('%s  mean=%.1f  ==0:%.1f%%  ==255:%.1f%%  mid:%.1f%%'
                % (v.format_name, flat.mean(), 100.0 * (flat == 0).mean(),
                   100.0 * (flat == 255).mean(),
                   100.0 * ((flat > 0) & (flat < 255)).mean()))
    if v.hi_format in (13, 20):                      # DXT1: 1-bit alpha if c0<=c1
        bw, bh = (w + 3) // 4, (h + 3) // 4
        blk = np.frombuffer(m0[:bw * bh * 8], np.uint8).reshape(bh, bw, 8)
        c0 = blk[:, :, 0].astype(np.int32) | (blk[:, :, 1].astype(np.int32) << 8)
        c1 = blk[:, :, 2].astype(np.int32) | (blk[:, :, 3].astype(np.int32) << 8)
        bits = (blk[:, :, 4].astype(np.uint32)
                | (blk[:, :, 5].astype(np.uint32) << 8)
                | (blk[:, :, 6].astype(np.uint32) << 16)
                | (blk[:, :, 7].astype(np.uint32) << 24))
        onebit = (c0 <= c1)
        trans = 0
        for i in range(16):
            idx = (bits >> (2 * i)) & 3
            trans += int(np.sum(onebit & (idx == 3)))
        nblk = bw * bh
        return ('%s  1-bit-alpha blocks: %d/%d (%.1f%%)   fully-transparent texels: %d/%d (%.2f%%)'
                % (v.format_name, int(onebit.sum()), nblk,
                   100.0 * onebit.mean(), trans, nblk * 16, 100.0 * trans / (nblk * 16)))
    return '%s (no alpha channel)' % v.format_name


def main():
    packs = S.Packs()
    # map basetexture basename -> vmt path
    by_base = {}
    for path, blob in packs.vmt.items():
        txt = blob.decode('latin-1', 'replace')
        m = re.search(r'"?\$basetexture"?\s+"?([^"\r\n]+)"?', txt, re.I)
        if m:
            by_base.setdefault(os.path.basename(m.group(1).strip().replace('\\', '/')).lower(),
                               (path, txt))
    print('%d VMTs indexed' % len(by_base))
    stems = sorted({os.path.basename(k)[:-4] for k in packs.vtf})
    seen = set()
    for stem in stems:
        hit = by_base.get(stem.lower())
        if not hit:
            continue
        path, txt = hit
        if path in seen:
            continue
        seen.add(path)
        print('=' * 78)
        print('VMT %s   (basetexture basename = %s)' % (path, stem))
        for line in txt.splitlines():
            s = line.strip()
            if not s:
                continue
            low = s.lower()
            if any(low.startswith(k) or (' ' + k) in low for k in KEYS):
                print('   %s' % s)
        raw = packs.vtf_bytes(stem)
        if raw:
            v = V.VTF.read(raw)
            print('   VTF %dx%d  mips=%d  %s' % (v.width, v.height, v.mip_count, v.summary()))
            print('   alpha: %s' % alpha_stats(raw))
    return 0


if __name__ == '__main__':
    sys.exit(main())
