"""Identify the stray `9ba626afa44a3aa3.patch_1` sitting in the game data dir.

Why: after deploying M1 (patch_0) the user reported *zero* visual change --
"completely identical to before".  A null result is itself evidence.  Before
escalating to M2/M3 we must rule out "our bytes never reached the renderer".

The game data dir currently holds BOTH
    9ba626afa44a3aa3.patch_0            -> E:/mod/Lunalice-RE2310-M1   (ours, 563900 B)
    9ba626afa44a3aa3.patch_1            -> E:/mod/.runtime/lua/<sha>/  (824903 B, 12:24)
and library.json says *every* node owns patchIndex 0 -- nobody owns patch_1.

Read-only.  Prints the TOC inventory of each package and the file_id overlap.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_patch as HP

GAME = Path(r'D:\SteamLibrary\steamapps\common\Helldivers 2\data')
TAG = '9ba626afa44a3aa3'
P0 = GAME / (TAG + '.patch_0')
P1 = GAME / (TAG + '.patch_1')

TYPE_NAMES = {v: k for k, v in HP.__dict__.items() if isinstance(v, int)} if False else {}


def inventory(label, path):
    try:
        entries, d = HP.scan_entries(str(path))
    except Exception as e:  # noqa: BLE001
        print('  %-28s UNREADABLE: %s' % (label, e))
        return None
    print('  %-28s %d bytes, %d entries' % (label, len(d), len(entries)))
    types = {}
    for e in entries:
        types.setdefault(e['type_id'], []).append(e)
    for t, lst in sorted(types.items(), key=lambda kv: -len(kv[1])):
        gpu = sum(x['gpu_size'] for x in lst)
        stream = sum(x['stream_size'] for x in lst)
        toc = sum(x['toc_size'] for x in lst)
        print('      type 0x%016x  n=%-4d toc=%-9d stream=%-9d gpu=%d'
              % (t, len(lst), toc, stream, gpu))
    return entries


def main():
    print('=== inventory ===')
    e0 = inventory('patch_0 (M1, ours)', P0)
    print()
    e1 = inventory('patch_1 (.runtime/lua)', P1)
    if e0 is None or e1 is None:
        return
    s0 = {e['file_id'] for e in e0}
    s1 = {e['file_id'] for e in e1}
    both = s0 & s1
    print()
    print('=== file_id overlap ===')
    print('  only in patch_0 : %d' % len(s0 - s1))
    print('  only in patch_1 : %d' % len(s1 - s0))
    print('  in BOTH         : %d   <-- patch_1 can override these' % len(both))
    t0 = {e['file_id']: e['type_id'] for e in e0}
    t1 = {e['file_id']: e['type_id'] for e in e1}
    conflicts = [f for f in both if t0[f] != t1[f]]
    print('  same id, DIFFERENT type : %d' % len(conflicts))
    for f in sorted(both)[:40]:
        same = 'same-type' if t0[f] == t1[f] else 'TYPE-MISMATCH'
        print('      0x%016x  p0_type=0x%016x p1_type=0x%016x  %s'
              % (f, t0[f], t1[f], same))
    if len(both) > 40:
        print('      ... %d more' % (len(both) - 40))
    # byte-level: are the overlapping entries identical?
    m0 = {e['file_id']: e for e in e0}
    m1 = {e['file_id']: e for e in e1}
    diff = 0
    for f in sorted(both):
        a, b = m0[f], m1[f]
        if (a['toc_size'], a['stream_size'], a['gpu_size']) != \
           (b['toc_size'], b['stream_size'], b['gpu_size']):
            diff += 1
    print('  overlapping entries with DIFFERENT sizes : %d' % diff)


if __name__ == '__main__':
    main()
