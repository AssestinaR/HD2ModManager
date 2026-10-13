# -*- coding: utf-8 -*-
"""Decide the mip chain order objectively, with no magic threshold.

Idea: neighbouring mip levels describe the same picture.  If we read the chain
in the *correct* order, mip0 downsampled 2x (box) must agree closely with mip1,
and mip1 with mip2.  If we read it in the wrong order we are pairing unrelated
byte ranges and the agreement collapses.

For every texture we build the two competing layouts:

  largest-first (what hd2_vtf.VTF.read does today)
      mip m lives at  off = sum(mip_size(0..m-1))
  smallest-first
      mip m lives at  off = sum(mip_size(m+1..N-1))

and score each by  mean|box2(mip_m) - mip_{m+1}|  averaged over all adjacent
pairs.  The layout with the far smaller score wins.  No thresholds involved --
we only compare two numbers against each other.

usage: _probe_miporder2.py [texture ...]
"""
import os
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_source as S
import hd2_vtf as V

TC = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0\deps\texconv.exe')
NAMES = ['shinano_face', 'white', '_4', 'ornament_metal_underwear', 'shinano_body',
         'shinano_hair_ear', 'headdress_white01_catears', 'lace_white01_catears',
         'bra_white01_bra_inner', 'glove_white01_wristcuffs',
         'dress_white01_dress_corset', 'lower_white01_underwear',
         'shoes_white01_highheels', 'shoes_02']
DXGI = {13: 71, 15: 77, 12: 70, 37: 83, 38: 80}


def decode(payload, w, h, fmt, tmp, tag):
    dxgi = DXGI.get(fmt, 71)
    dds = os.path.join(tmp, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(V.build_dds_dx10(w, h, dxgi, [payload]))
    subprocess.run([str(TC), '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
                    '-m', '1', '-o', tmp, dds], capture_output=True)
    p = os.path.join(tmp, tag + '.png')
    if not os.path.exists(p):
        return None
    from PIL import Image
    im = Image.open(p).convert('RGB')
    if im.size != (w, h):
        im = im.resize((w, h))
    return np.asarray(im, np.float64)


def box2(a):
    h, w, _ = a.shape
    h2, w2 = h // 2, w // 2
    a = a[:h2 * 2, :w2 * 2]
    return a.reshape(h2, 2, w2, 2, 3).mean(axis=(1, 3))


def offsets(v, order):
    """Return list of byte offsets of mip0..mipN-1 for the given chain order."""
    sz = []
    for m in range(v.mip_count):
        w = max(1, v.width >> m)
        h = max(1, v.height >> m)
        sz.append(V.mip_size(w, h, v.hi_format) * v.frames * v.depth)
    off = []
    if order == 'largest':
        acc = 0
        for m in range(v.mip_count):
            off.append(acc)
            acc += sz[m]
    else:                       # smallest-first: mipN-1 first
        acc = 0
        tmp = [0] * v.mip_count
        for m in range(v.mip_count - 1, -1, -1):
            tmp[m] = acc
            acc += sz[m]
        off = tmp
    return off, sz


def score(v, data, order, tmp, tag):
    """mean over adjacent pairs of  mean|box2(mip_m) - mip_{m+1}| ; lower = better."""
    if v.mip_count < 2:
        return None
    off, sz = offsets(v, order)
    vals = []
    for m in range(min(v.mip_count - 1, 4)):        # top 5 levels are plenty
        w0 = max(1, v.width >> m)
        h0 = max(1, v.height >> m)
        w1 = max(1, v.width >> (m + 1))
        h1 = max(1, v.height >> (m + 1))
        if w1 < 8 or h1 < 8:
            break
        o0, o1 = off[m], off[m + 1]
        if o0 + sz[m] > len(data) or o1 + sz[m + 1] > len(data):
            return None
        a = decode(data[o0:o0 + sz[m]], w0, h0, v.hi_format, tmp, '%s_%s_%d' % (tag, order, m))
        b = decode(data[o1:o1 + sz[m + 1]], w1, h1, v.hi_format, tmp, '%s_%s_%d' % (tag, order, m + 1))
        if a is None or b is None:
            return None
        vals.append(float(np.abs(box2(a) - b).mean()))
    return float(np.mean(vals)) if vals else None


def main():
    want = sys.argv[1:] or NAMES
    packs = S.Packs()
    tmp = tempfile.mkdtemp(prefix='mipord')
    print('%-28s %-10s %-3s %-4s %-9s | %-10s %-10s | winner'
          % ('texture', 'size', 'mp', 'lo', 'mipbytes', 'largest', 'smallest'))
    tally = {'largest': 0, 'smallest': 0, 'tie': 0, 'skip': 0}
    for nm in want:
        b = packs.vtf_bytes(nm)
        if b is None:
            print('%-28s MISSING' % nm)
            tally['skip'] += 1
            continue
        v = V.VTF.read(b)
        hdr = struct.unpack_from('<I', b, 12)[0]
        data = b[hdr:]
        offL, sz = offsets(v, 'largest')
        mipbytes = sum(sz)
        lo_sz = V.mip_size(v.lo_width, v.lo_height, v.lo_format) if (v.lo_width and v.lo_height) else 0
        sL = score(v, data, 'largest', tmp, nm)
        sS = score(v, data, 'smallest', tmp, nm)
        if sL is None or sS is None:
            win = 'skip (1 mip / 1x1)'
            tally['skip'] += 1
        elif abs(sL - sS) < 1e-9:
            win = 'tie'
            tally['tie'] += 1
        elif sS < sL:
            win = 'SMALLEST-FIRST  (x%.2f better)' % (sL / max(sS, 1e-9))
            tally['smallest'] += 1
        else:
            win = 'largest-first   (x%.2f better)' % (sS / max(sL, 1e-9))
            tally['largest'] += 1
        print('%-28s %-10s %-3d %-4d %-9d | %-10s %-10s | %s'
              % (nm, '%dx%d' % (v.width, v.height), v.mip_count,
                 v.lo_width or 0, mipbytes,
                 ('%.3f' % sL) if sL is not None else '-',
                 ('%.3f' % sS) if sS is not None else '-', win))
        print('   block=%d  mips=%d  lowres=%dB  trailing=%dB  fmt=%s'
              % (len(data) - mipbytes - lo_sz, v.mip_count, lo_sz,
                 len(data) - mipbytes - lo_sz, v.format_name))
    print('\ntally:', tally)
    return 0


if __name__ == '__main__':
    sys.exit(main())
