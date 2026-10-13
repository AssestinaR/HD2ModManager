"""Round-trip check: read every Unit of the reference patch, re-serialize it
with the new writer, and require the TocData (and GpuData) to be byte-identical.

Any difference means the writer's section order / offsets / padding differ from
the SDK's, which would produce a patch the game rejects.
"""
import glob
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as HP
import hd2_unit as HU
import hd2_unitw as UW


def find_ref():
    cand = glob.glob(r'E:\mod\*\9ba626afa44a3aa3.patch_0')
    for c in cand:
        if 'Chiffon' in c or '4.28' in c:
            return c
    return cand[0]


def first_diff(a, b):
    n = min(len(a), len(b))
    for i in range(n):
        if a[i] != b[i]:
            return i, a[i], b[i]
    if len(a) != len(b):
        return n, 'len=%d' % len(a), 'len=%d' % len(b)
    return None


def main():
    ref = find_ref()
    entries, _ = HP.scan_entries(ref)
    units = [e for e in entries if e['type_id'] == HP.UNIT_TYPE_ID]
    print('patch: %s  (%d units)' % (os.path.basename(ref), len(units)))
    ok = bad = 0
    for e in units:
        pay = HP.entry_payload(e, ref)
        u = HU.Unit.read(pay['toc'], pay['gpu'])
        gpu2 = UW.serialize_gpu(u)
        toc2 = UW.serialize_unit(u)
        dt = first_diff(pay['toc'], toc2)
        dg = first_diff(pay['gpu'], gpu2)
        if dt is None and dg is None:
            ok += 1
            print('  OK   0x%016x  toc=%d gpu=%d' % (e['file_id'], len(toc2), len(gpu2)))
        else:
            bad += 1
            print('  FAIL 0x%016x' % e['file_id'])
            if dt:
                print('        toc diff @%s: %r vs %r' % dt)
                i = dt[0]
                lo = max(0, i - 32)
                print('        src[%d:%d] = %s' % (lo, i + 32, pay['toc'][lo:i + 32].hex()))
                print('        new[%d:%d] = %s' % (lo, i + 32, toc2[lo:i + 32].hex()))
            if dg:
                print('        gpu diff @%s' % (dg,))
    print('ok=%d bad=%d' % (ok, bad))
    return 0 if bad == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
