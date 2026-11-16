#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""按「记录分组」改菲涅尔阈值，用于二分定位真正生效的那条 pass。

分组来自实测（见 docs/agent/hd2-custom-material-pipeline-plan-20261114.md）：
    46k  4 条   @281216,337792,394032,449328           （46 KB 家族）
    60k  20 条  @517344 … 2406072                        （60 KB 家族）
    mat  10 条  @2592520 … 3974200                       （材质 pass，62,652 B）

用法：
    python payload_alpha_group.py <payload> --show
    python payload_alpha_group.py <payload> --group 46k --threshold 0.5 --apply
    python payload_alpha_group.py <payload> --records 281216,337792 --threshold 0.5 --apply
    python payload_alpha_group.py <payload> --restore     # 从最近一次备份还原
"""
import struct, os, sys, glob, shutil, datetime, argparse

GROUPS = {
    '46k': [281216, 337792, 394032, 449328],
    '60k': [517344, 620328, 722232, 822928, 921752, 1018800, 1119256, 1218624, 1316792, 1413120,
            1510296, 1613280, 1715184, 1815880, 1914704, 2011752, 2112208, 2211576, 2309744, 2406072],
    'mat': [2592520, 2748816, 2903832, 3057216, 3204672, 3362048, 3518344, 3673360, 3826744, 3974200],
}
MUL_TOKEN = 0x07000038


def alpha_block(d, rec):
    nch = struct.unpack_from('<I', d, rec + 28)[0]
    offs = struct.unpack_from('<%dI' % nch, d, rec + 32)
    shex = None
    for o in offs:
        if d[rec + o:rec + o + 4] == b'SHEX':
            shex = rec + o
            size = struct.unpack_from('<I', d, rec + o + 4)[0]
            break
    if not shex:
        return None
    tk, end = shex + 16, shex + 8 + size
    for k in range(tk, end - 160, 4):
        b = struct.unpack_from('<f', d, k)[0]
        m = struct.unpack_from('<f', d, k + 28)[0]
        if -1.0 < b < 0.0 and abs(m * (1.0 + b) - 1.0) < 1e-5 and struct.unpack_from('<I', d, k + 96)[0] == MUL_TOKEN:
            return k
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('payload')
    ap.add_argument('--group', choices=sorted(GROUPS))
    ap.add_argument('--records')
    ap.add_argument('--threshold', type=float)
    ap.add_argument('--show', action='store_true')
    ap.add_argument('--restore', action='store_true')
    ap.add_argument('--apply', action='store_true')
    a = ap.parse_args()

    d = bytearray(open(a.payload, 'rb').read())

    if a.restore:
        baks = sorted(glob.glob(os.path.join(os.path.dirname(a.payload), '.abtest-backup', '*', os.path.basename(a.payload))))
        if not baks:
            raise SystemExit('没有备份可还原')
        shutil.copy2(baks[-1], a.payload)
        print('已从备份还原：', baks[-1])
        return 0

    found = {}
    for name, recs in GROUPS.items():
        ok = [r for r in recs if alpha_block(d, r) is not None]
        found[name] = ok
        vals = []
        for r in ok[:2]:
            k = alpha_block(d, r)
            vals.append((round(struct.unpack_from('<f', d, k)[0], 4), round(struct.unpack_from('<f', d, k + 28)[0], 6)))
        print("%-4s 组：%2d/%2d 条命中 alpha 块；样本 bias/mult=%s" % (name, len(ok), len(recs), vals))

    if a.show or (a.threshold is None):
        return 0

    targets = []
    if a.group:
        targets = found[a.group]
    if a.records:
        targets += [int(x) for x in a.records.split(',') if x.strip()]

    n = 0
    for rec in targets:
        k = alpha_block(d, rec)
        if k is None:
            print('  ⚠ 记录 @%d 没有 alpha 块' % rec)
            continue
        d[k:k + 4] = struct.pack('<f', -a.threshold)
        d[k + 28:k + 32] = struct.pack('<f', 1.0 / (1.0 - a.threshold))
        n += 1
    print("改 %d 条记录，T=%.4g" % (n, a.threshold))

    if not a.apply:
        print('[dry-run] 未写盘')
        return 0
    bak = os.path.join(os.path.dirname(a.payload), '.abtest-backup',
                       datetime.datetime.now().strftime('%Y%m%d-%H%M%S') + "-group")
    os.makedirs(bak, exist_ok=True)
    shutil.copy2(a.payload, os.path.join(bak, os.path.basename(a.payload)))
    tmp = a.payload + '.g.tmp'
    open(tmp, 'wb').write(d)
    os.replace(tmp, a.payload)
    print('已写盘；备份 ->', bak)
    print('复核 -0.21 残留 %d 处' % open(a.payload, 'rb').read().count(struct.pack('<f', -0.21)))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
