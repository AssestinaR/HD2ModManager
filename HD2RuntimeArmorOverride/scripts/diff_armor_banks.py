# -*- coding: utf-8 -*-
"""对比两份 `peek_armor_banks.py --save` 的快照，找出 bank 真正变化的记录。

为什么需要它：`rec[0]` 一开始看起来很像护甲实例（16 个连号小整数），
但切换 Piece 配置后它逐字节不变 —— 说明那只是巧合。判据不能靠"看起来像"，
只能靠**跨配置差分**：只有真正承载护甲实例的那条记录会随配置改变。
"""
import argparse
import json
import sys

BANK_LABELS = {'546b4c': 'A(type0)', '546b74': 'B(type1)', '546b9c': 'C(type>=2)'}


def load(path):
    with open(path, encoding='utf-8') as fh:
        data = json.load(fh)
    records = {}
    for record in data['records']:
        records[record['index']] = record
    return data, records


def describe(banks):
    parts = []
    for offset in ('546b4c', '546b74', '546b9c'):
        values = banks.get(offset)
        if not values:
            continue
        used = [i for i, v in enumerate(values) if v]
        if used:
            parts.append('%s槽=%s' % (BANK_LABELS.get(offset, offset), used))
    return ' '.join(parts) if parts else '(无)'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--before', required=True)
    ap.add_argument('--after', required=True)
    ap.add_argument('--limit', type=int, default=20)
    args = ap.parse_args()

    before_meta, before = load(args.before)
    after_meta, after = load(args.after)
    print('before: %s（%d 条非零）' % (before_meta.get('label'), len(before)))
    print('after : %s（%d 条非零）' % (after_meta.get('label'), len(after)))
    if before_meta.get('arrayBase') != after_meta.get('arrayBase'):
        print('⚠ 两次快照的数组基址不同：0x%x vs 0x%x —— 进程可能重启过，'
              '差分不可直接比。'
              % (before_meta.get('arrayBase', 0), after_meta.get('arrayBase', 0)))

    changed = []
    for index in sorted(set(before) | set(after)):
        old = before.get(index, {}).get('banks', {})
        new = after.get(index, {}).get('banks', {})
        if old == new:
            continue
        delta = 0
        for offset in set(old) | set(new):
            o = old.get(offset) or []
            n = new.get(offset) or []
            for i in range(max(len(o), len(n))):
                ov = o[i] if i < len(o) else 0
                nv = n[i] if i < len(n) else 0
                if ov != nv:
                    delta += 1
        changed.append((delta, index, old, new))

    changed.sort(reverse=True)
    print('\n变化的记录 %d 条；按改动槽数排序取前 %d：'
          % (len(changed), min(args.limit, len(changed))))
    for delta, index, old, new in changed[:args.limit]:
        print('\n  rec[%d]  改动 %d 个槽' % (index, delta))
        print('    before %s' % describe(old))
        print('    after  %s' % describe(new))
        for offset in ('546b4c', '546b74', '546b9c'):
            o = old.get(offset) or []
            n = new.get(offset) or []
            if o == n:
                continue
            print('      %s' % BANK_LABELS.get(offset, offset))
            print('        before %s' % ' '.join('%08x' % v if v else '    .   ' for v in o))
            print('        after  %s' % ' '.join('%08x' % v if v else '    .   ' for v in n))
    return 0


if __name__ == '__main__':
    sys.exit(main())
