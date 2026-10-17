# -*- coding: utf-8 -*-
"""按**精确布局**生成运行时协议里的 model_piece 行，用于容量实验。

为什么需要它：`use_armor_piece_config.py` 只能套预设。要验证
"每部位 3 层 = 18 件"这条容量边界，必须能精确指定
"slot 2..7 各 3 层、type 分别是 0/1/2"，并且能只多一件去撞边界。

保留协议里的 `model=` 与 `map_model=`，只重写 `model_piece=`，并 bump revision。

用法：
    # 6 个身体部位 x 3 层 = 18 件（body 3 = Any）
    python write_capacity_protocol.py --slots 2-7 --layers 3

    # 同上但每部位 4 层 = 24 件（必然出现别名，因为每槽只有 3 个 bank）
    python write_capacity_protocol.py --slots 2-7 --layers 4

    # 只多一件，去撞 18 的边界
    python write_capacity_protocol.py --slots 2-7 --layers 3 --extra 2:3
"""
import argparse
import os
import sys

DEFAULT_PATH = os.path.join(
    os.environ.get('LOCALAPPDATA', ''), 'CowboyBingus', 'Helldivers2',
    'ArmorRegistry', 'armor-registry.protocol')


def parse_slots(text):
    out = []
    for part in text.split(','):
        part = part.strip()
        if not part:
            continue
        if '-' in part:
            lo, hi = part.split('-', 1)
            out.extend(range(int(lo, 0), int(hi, 0) + 1))
        else:
            out.append(int(part, 0))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--protocol', default=DEFAULT_PATH)
    ap.add_argument('--slots', default='2-7', help='身体槽位，默认 2-7')
    ap.add_argument('--layers', type=int, default=3, help='每个部位几层（type 0..layers-1）')
    ap.add_argument('--body', type=int, default=3, help='body 变体，3 = Any')
    ap.add_argument('--extra', action='append', default=[],
                    help='额外追加一件，格式 slot:type，可多次给')
    ap.add_argument('--tone', type=int, default=0)
    ap.add_argument('--dry-run', action='store_true', help='只打印，不写文件')
    args = ap.parse_args()

    if not os.path.exists(args.protocol):
        raise SystemExit('协议不存在：%s' % args.protocol)

    with open(args.protocol, encoding='utf-8') as fh:
        lines = [line.rstrip('\r\n') for line in fh]

    model_key = None
    pool = []
    mappings = []
    for line in lines:
        if line.startswith('model='):
            model_key = line.split('=', 1)[1].split(',')[0].strip()
        elif line.startswith('model_piece='):
            fields = line.split('=', 1)[1].split(',')
            if len(fields) >= 3 and fields[2].strip():
                pool.append(fields[2].strip())
        elif line.startswith('map_model='):
            mappings.append(line)

    if not model_key:
        raise SystemExit('协议里没有 model= 行')
    if not pool:
        raise SystemExit('协议里没有可复用的 unit（model_piece 行）')

    slots = parse_slots(args.slots)
    plan = []
    cursor = 0
    for slot in slots:
        for layer in range(args.layers):
            unit = pool[cursor % len(pool)]
            cursor += 1
            plan.append((args.body, unit, slot, layer))
    for spec in args.extra:
        if ':' not in spec:
            raise SystemExit('--extra 需要 slot:type，收到 %r' % spec)
        slot_text, type_text = spec.split(':', 1)
        unit = pool[cursor % len(pool)]
        cursor += 1
        plan.append((args.body, unit, int(slot_text, 0), int(type_text, 0)))

    keys = [(body, slot, piece_type) for body, _unit, slot, piece_type in plan]
    duplicates = len(keys) - len(set(keys))

    revision = 0
    for line in lines:
        if line.startswith('revision='):
            revision = int(line.split('=', 1)[1])
    revision += 1

    head = ['schema=2', 'revision=%d' % revision, 'enabled=true', 'model=%s,' % model_key]
    body_lines = ['model_piece=%s,%d,%s,%d,%d,%d,true'
                  % (model_key, body, unit, slot, piece_type, args.tone)
                  for body, unit, slot, piece_type in plan]
    out = head + body_lines + mappings

    print('布局：%d 个槽 x %d 层 + %d 件额外 = **%d 件**'
          % (len(slots), args.layers, len(args.extra), len(plan)))
    print('  (body, slot, type) 唯一键 %d 个，重复 %d 个 —— %s'
          % (len(set(keys)), duplicates,
             '没有别名，全部应该拿到句柄' if duplicates == 0
             else '必然有 %d 件被别名压掉（每槽只有 3 个 bank）' % duplicates))
    per_slot = {}
    for _body, slot, piece_type in keys:
        per_slot.setdefault(slot, []).append(piece_type)
    for slot in sorted(per_slot):
        print('  slot %d: type=%s' % (slot, sorted(per_slot[slot])))
    print('  revision -> %d，映射 %d 条' % (revision, len(mappings)))

    if args.dry_run:
        print('\n--- 预览 ---')
        print('\n'.join(out))
        return 0

    tmp = args.protocol + '.tmp'
    with open(tmp, 'w', encoding='utf-8', newline='\n') as fh:
        fh.write('\n'.join(out) + '\n')
    os.replace(tmp, args.protocol)
    print('已写入 %s' % args.protocol)
    return 0


if __name__ == '__main__':
    sys.exit(main())
