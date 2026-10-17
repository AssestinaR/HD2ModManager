# -*- coding: utf-8 -*-
"""把当前自定义素体的 Piece 组合切成一个预设，原子替换运行时协议。

为什么需要它
------------
原生消费者每个 slot 只有 **A/B/C 三个句柄 bank**，`pieceType` 到 bank 的映射是

    bank = 0 if kind == 0 else 1 if kind == 1 else 2

（逐字取自 `analyze_registry_slots.py:59-61` 与 `review_armory_report.py:37-41`）。
同一个 `(body 变体, slot, bank)` 里放两个以上 Piece 就是别名，**后写的句柄盖掉先写的**。

管理器把配件按"每槽第几个"编号，于是第 3 个配件拿到 `pieceType=2`、第 4 个拿到 3……
**从第 3 个起全部落进 bank C**。所以"到底能显示几个"完全由 pieceType 决定，
而这正是要在游戏里做单变量对照的东西。

协议约 1 秒轮询一次，所以改这个文件**不需要重启游戏、也不需要开管理器**。

预设
----
    C1  只留 pieceType<=2          -> 25 个 Piece，0 别名（预期：全部正常）
    C2  本体 + 左腿 type 1/2/3      -> 10 个 Piece，1 个别名组（比 C1 少，只多一个 type=3）
    C3  全部 43 个                  -> 复现故障

C1 与 C2 只差"有没有一个 pieceType>=3 的 Piece"，而且 C2 的 Piece 数还**更少**，
所以"总数太多"这个解释被排除掉了。

用法
----
    python use_armor_piece_config.py --config C1 [--dry-run]
"""
import argparse
import json
import os
import sys
from collections import defaultdict

SLOT_NAMES = {0: 'Head', 1: 'Cape', 2: 'Chest', 3: 'Hips', 4: 'LeftLeg',
              5: 'RightLeg', 6: 'LeftArm', 7: 'RightArm', 8: 'slot8', 9: 'slot9'}


def bank_of(piece_type):
    return 0 if piece_type == 0 else 1 if piece_type == 1 else 2


def select(pieces, config):
    if config == 'C1':
        return [p for p in pieces if p['pieceType'] <= 2]
    if config == 'C2':
        return [p for p in pieces
                if p['pieceType'] == 0 or (p['slot'] == 4 and p['pieceType'] <= 3)]
    if config == 'C3':
        return list(pieces)
    raise SystemExit('unknown config %r' % config)


def analyze(pieces):
    """按原生规则算别名。body=3 是 Any，两个原生变体都会看到它。"""
    for body in (0, 1):
        active = [p for p in pieces if p['body'] in (body, 3)]
        banks = defaultdict(list)
        for p in active:
            if 1 <= p['slot'] <= 9:
                banks[(p['slot'], bank_of(p['pieceType']))].append(p)
        groups = {k: v for k, v in banks.items() if len(v) > 1}
        held = sum(len(v) for v in groups.values())
        print('  body=%d：生效 %d 个 Piece，别名组 %d 个，被压住 %d 个'
              % (body, len(active), len(groups), held))
        for (slot, bank), entries in sorted(groups.items()):
            types = ','.join(str(p['pieceType']) for p in sorted(entries, key=lambda x: x['pieceType']))
            print('      %-9s slot=%d bank=%s  挤了 %d 个，pieceType=[%s]'
                  % (SLOT_NAMES.get(slot, '?'), slot, 'ABC'[bank], len(entries), types))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--config', required=True, choices=['C1', 'C2', 'C3'])
    ap.add_argument('--model', default=r'E:\mod\M3-verify-Unaligned\custom-model.json')
    ap.add_argument('--registry-root',
                    default=os.path.join(os.environ.get('LOCALAPPDATA', ''),
                                         'CowboyBingus', 'Helldivers2', 'ArmorRegistry'))
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    protocol_path = os.path.join(args.registry_root, 'armor-registry.protocol')
    if not os.path.isfile(protocol_path):
        raise SystemExit('找不到协议文件：%s' % protocol_path)
    if not os.path.isfile(args.model):
        raise SystemExit('找不到素体定义：%s' % args.model)

    with open(args.model, encoding='utf-8') as handle:
        model = json.load(handle)
    key = model['key']
    all_pieces = model['pieces']
    chosen = select(all_pieces, args.config)

    # 映射行原样保留（293 条 map_model），只换 model_piece。
    revision = 0
    mapping_lines = []
    with open(protocol_path, encoding='utf-8') as handle:
        for line in handle:
            line = line.rstrip('\n')
            if line.startswith('revision='):
                revision = int(line.split('=', 1)[1])
            elif line.startswith('map_model=') or line.startswith('map='):
                mapping_lines.append(line)
    new_revision = revision + 1

    lines = ['schema=2', 'revision=%d' % new_revision, 'enabled=true', 'model=%s,' % key]
    for piece in sorted(chosen, key=lambda p: (p['slot'], p['pieceType'], p['body'])):
        # 6/7/8 个字段都合法；显式写 true，与管理器自己的输出一致。
        lines.append('model_piece=%s,%d,%s,%d,%d,%d,true'
                     % (key, piece['body'], piece['unit'], piece['slot'], piece['pieceType'],
                        piece.get('tone', 0)))
    lines.extend(mapping_lines)

    print('配置 %s：%d 个 Piece（原 %d 个），revision %d -> %d，映射 %d 条'
          % (args.config, len(chosen), len(all_pieces), revision, new_revision, len(mapping_lines)))
    analyze(chosen)

    if args.dry_run:
        print('（--dry-run：没有写文件）')
        return 0

    # 原子替换：同目录临时文件 + os.replace。Lua 轮询读到的一定是完整文件。
    temp = os.path.join(args.registry_root, '.piece-config-%d.tmp' % os.getpid())
    with open(temp, 'w', encoding='utf-8', newline='\n') as handle:
        handle.write('\n'.join(lines) + '\n')
    os.replace(temp, protocol_path)
    print('已写入 %s（%d 行）。Lua 约 1 秒内生效。' % (protocol_path, len(lines)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
