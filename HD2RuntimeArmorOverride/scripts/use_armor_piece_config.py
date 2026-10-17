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
    C1      只留 pieceType<=2          -> 25 个 Piece，0 别名（预期：全部正常）
    C2      本体 + 左腿 type 1/2/3      -> 16 个 Piece，1 个别名组（= 阶梯的 N=3）
    C3      全部 43 个                  -> 复现故障

    S3..S8  单变量阶梯：**只有本体 + slot 4 的配件**，把 slot 4 的 pieceType 上限抬到 N。
            总数 16..21，全部远低于原版上限 29 —— 于是"总数太多"被排除，
            量出来的纯粹是"一个槽能装几个"。S3 就是 C2（实测身体正常）。
    M2..M5  互补的"总数阶梯"：本体 + slot 2/3/4/5/6 各 type<=N。
            每槽 3..6 个，总数 19..37。用来在"每槽几个"之外单独量"总数多少"。
            M2 就是 C1（实测身体正常），M5 的有效 Piece 数正好等于 C3。

    判读：先跑 S 阶梯，**遇到第一个身体消失的那一级就停**，记下 N。
    如果 S8（9 个挤在一槽、总数只有 21）仍然正常，再跑 M 阶梯找总数阈值。

    K1..K5  深度固定（每槽 4 个 = 超 1 个），只改**有几个槽超容量**。K5 就是 M3。
    X6      单变量验证"溢出到下一个槽"：M3 但 slot 6 压回 3 个。若右臂回来即证实。

    实机已测（2026-10-16）：
        S4..S8（1 个槽超容量，最多塞 9 个）  -> **身体从不消失**  => "每槽几个"不是触发点
        M2（0 个槽超容量）                   -> 正常
        M3（5 个槽各超 1 个）                -> **掉右臂（slot 7，它自己没超）**
        M4（5 个槽各超 2 个）                -> +掉左臂、胯、双腿
        M5（5 个槽各超 3 个）                -> +掉胸
    所以变量是"超容量的槽有几个"，而且被打掉的不是超容量的那些 —— 像是溢出到下一个槽。

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
    if config.startswith('S'):
        # 单变量阶梯：**只有本体 + slot 4 的配件**，把 slot 4 的 pieceType 上限抬到 N。
        # 规则与 C2 完全相同，所以 S3 就是 C2（实测身体正常）。
        #
        # 为什么不用"其他槽也带 type<=2"那种写法：那样总数会到 29..31，正好撞上原版
        # 每 Kit 的观测上限 29，于是"每槽几个"和"总数多少"两个变量缠在一起。
        # 现在总数只有 16..21，**远低于 29**，量出来的就纯粹是"一个槽能装几个"。
        depth = int(config[1:])
        return [p for p in pieces
                if p['pieceType'] == 0 or (p['slot'] == 4 and p['pieceType'] <= depth)]
    if config.startswith('M'):
        # 互补的"总数阶梯"：本体 + slot 2/3/4/5/6 各 type<=N。
        # 每槽个数从 3 抬到 6，总数从 19 抬到 37 —— 用来在"每槽几个"之外单独量"总数多少"。
        depth = int(config[1:])
        return [p for p in pieces
                if p['pieceType'] == 0 or (p['slot'] in (2, 3, 4, 5, 6) and p['pieceType'] <= depth)]
    if config.startswith('K'):
        # "超容量槽个数"阶梯：slot 2..(1+N) 各 type<=3（也就是每槽 4 个 = 超 1 个），
        # 深度固定、只改**有几个槽超容量**。总数 10..22，把"几个槽超"和"总数多少"尽量分开。
        # K5 就是 M3（实测掉右臂）。
        count = int(config[1:])
        slots = set(range(2, 2 + count))
        return [p for p in pieces
                if p['pieceType'] == 0 or (p['slot'] in slots and p['pieceType'] <= 3)]
    if config == 'X6':
        # 单变量验证"溢出到下一个槽"：M3（slot 2..6 各 type<=3）但把 **slot 6 压回 type<=2**。
        # 其余一字不改。若右臂（slot 7）回来，就说明是 slot 6 的溢出打掉了 slot 7。
        return [p for p in pieces
                if p['pieceType'] == 0
                or (p['slot'] in (2, 3, 4, 5) and p['pieceType'] <= 3)
                or (p['slot'] == 6 and p['pieceType'] <= 2)]
    if config == 'Y':
        # 把"超容量的槽有几个"和"总数多少"分开的那一格。
        #
        # 组成：本体 + slot 2/3/5/6 各 type<=2（C1 那批）+ slot 4 挖深到 type 8。
        # 于是 **只有 slot 4 一个槽超容量**，但总数 29、生效 23 ——
        # 正好压过 M3（总数 28、生效 22、5 个槽超容量）那条线。
        #
        # 判读：Y 正常 -> 触发点是"几个槽超容量"（1 个随便深都行）；
        #       Y 也坏 -> 触发点是总数，与几个槽超容量无关。
        return [p for p in pieces
                if (p['pieceType'] <= 2 and p['slot'] != 0)
                or (p['slot'] == 4 and p['pieceType'] <= 8)]
    raise SystemExit('unknown config %r' % config)


def analyze(pieces):
    """按原生规则算别名。body=3 是 Any，两个原生变体都会看到它。"""
    for body in (0, 1):
        active = [p for p in pieces if p['body'] in (body, 3)]
        banks = defaultdict(list)
        per_slot = defaultdict(int)
        for p in active:
            per_slot[p['slot']] += 1
            if 1 <= p['slot'] <= 9:
                banks[(p['slot'], bank_of(p['pieceType']))].append(p)
        groups = {k: v for k, v in banks.items() if len(v) > 1}
        held = sum(len(v) for v in groups.values())
        worst_slot = max(per_slot, key=lambda s: per_slot[s]) if per_slot else None
        print('  body=%d：生效 %d 个 Piece，最大每槽 %d 个（%s），别名组 %d 个，被压住 %d 个'
              % (body, len(active), per_slot[worst_slot] if worst_slot is not None else 0,
                 SLOT_NAMES.get(worst_slot, '?'), len(groups), held))
        for (slot, bank), entries in sorted(groups.items()):
            types = ','.join(str(p['pieceType']) for p in sorted(entries, key=lambda x: x['pieceType']))
            print('      %-9s slot=%d bank=%s  挤了 %d 个，pieceType=[%s]'
                  % (SLOT_NAMES.get(slot, '?'), slot, 'ABC'[bank], len(entries), types))


def list_slots(pieces):
    """逐槽列出会生效的 Piece（body=3 与本体都算），方便对照"应该看到什么"。"""
    by_slot = defaultdict(list)
    for p in pieces:
        by_slot[p['slot']].append(p)
    print('  逐槽清单（body=3 的 Piece 两个原生变体都生效）：')
    for slot in sorted(by_slot):
        entries = sorted(by_slot[slot], key=lambda x: (x['pieceType'], x['body']))
        desc = '  '.join('%s/type%d/%s' % (SLOT_NAMES.get(slot, '?'), p['pieceType'],
                                           'Any' if p['body'] == 3 else 'body%d' % p['body'])
                         for p in entries)
        print('    slot %-9s %s' % (slot, desc))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--config', required=True,
                    choices=['C1', 'C2', 'C3',
                             'S3', 'S4', 'S5', 'S6', 'S7', 'S8',
                             'M2', 'M3', 'M4', 'M5',
                             'K1', 'K2', 'K3', 'K4', 'K5', 'X6', 'Y'])
    ap.add_argument('--disable', action='append', default=[], metavar='SLOT:TYPE',
                    help='在预设基础上再关掉某个 Piece，例如 --disable 4:8。可重复。')
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

    # 在预设基础上再关掉单个 Piece —— 这是"关掉一个反而多显示一个"那条症状的直接对照。
    for item in args.disable:
        try:
            slot, piece_type = (int(part) for part in item.split(':'))
        except ValueError:
            raise SystemExit('--disable 的格式是 SLOT:TYPE，例如 4:8；收到 %r' % item)
        before = len(chosen)
        chosen = [p for p in chosen if not (p['slot'] == slot and p['pieceType'] == piece_type)]
        if len(chosen) == before:
            print('  ⚠️ --disable %s 没有匹配到任何 Piece' % item)

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
    list_slots(chosen)

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
