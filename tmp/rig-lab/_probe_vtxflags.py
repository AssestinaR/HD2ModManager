"""Dump the VTX StripGroup headers of the source model.

`export_obj._read_vtx_parts` reads `numVerts / vertOffset / numIndices /
indexOffset` and then treats the index block as a flat triangle LIST.  VTX
strip groups are triangle STRIPS though (alternating winding), and the group
header carries a `flags` byte that says which.  If that byte says "strip", every
other triangle we emit is inside out -- which is exactly the 70/30 split
`_probe_winding.py` measured.
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_source as S
import export_obj as E

MODEL = 'survivor_teenangst'


def main():
    vpk = S.ADDONS + S.MODEL_VPK
    ver, do, ents = E.parse_vpk_tree(vpk)
    print('vpk version=%d dataOffset=%d entries=%d' % (ver, do, len(ents)))
    vtx = None
    for nm, a, o, l in ents:
        if nm.lower().endswith('survivor_teenangst.dx90.vtx'):
            vtx = E.read_range(vpk, do, a, o, l)
            print('vtx: %s (%d bytes)' % (nm, len(vtx)))
    if vtx is None:
        raise SystemExit('no .dx90.vtx found')

    v, = struct.unpack_from('<i', vtx, 0)
    lod_count, = struct.unpack_from('<i', vtx, 20)
    bpc, = struct.unpack_from('<i', vtx, 28)
    bpo, = struct.unpack_from('<i', vtx, 32)
    print('vtx version=%d lod_count=%d bodyparts=%d' % (v, lod_count, bpc))

    hist = {}
    n_groups = 0
    for i in range(bpc):
        e = bpo + i * 8
        mc, = struct.unpack_from('<i', vtx, e)
        mo, = struct.unpack_from('<i', vtx, e + 4)
        mstart = e + mo
        for mi in range(mc):
            me = mstart + mi * 8
            mlc, = struct.unpack_from('<i', vtx, me)
            mlo, = struct.unpack_from('<i', vtx, me + 4)
            lstart = (me + mlo) if mlc else 0
            for li in range(mlc):
                le = lstart + li * 12
                xc, = struct.unpack_from('<i', vtx, le)
                xo, = struct.unpack_from('<i', vtx, le + 4)
                xstart = (le + xo) if xc else 0
                for xi in range(xc):
                    xe = xstart + xi * 9
                    gc, = struct.unpack_from('<i', vtx, xe)
                    go, = struct.unpack_from('<i', vtx, xe + 4)
                    gstart = (xe + go) if gc else 0
                    for gi in range(gc):
                        ge = gstart + gi * 25
                        vc, = struct.unpack_from('<i', vtx, ge)
                        vo, = struct.unpack_from('<i', vtx, ge + 4)
                        ic, = struct.unpack_from('<i', vtx, ge + 8)
                        io, = struct.unpack_from('<i', vtx, ge + 12)
                        fl = vtx[ge + 16]
                        u17, = struct.unpack_from('<i', vtx, ge + 17)
                        u21, = struct.unpack_from('<i', vtx, ge + 21)
                        hist[fl] = hist.get(fl, 0) + 1
                        n_groups += 1
                        if n_groups <= 15:
                            print('  bp%d m%d lod%d mesh%d g%-2d: vc=%-6d ic=%-7d flags=0x%02x '
                                  'u17=%-8d u21=%-8d' % (i, mi, li, xi, gi, vc, ic, fl, u17, u21))
    print('groups=%d  flags histogram=%s' % (n_groups, hist))


if __name__ == '__main__':
    main()
