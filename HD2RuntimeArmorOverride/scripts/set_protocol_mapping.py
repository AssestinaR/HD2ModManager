# -*- coding: utf-8 -*-
"""增删运行时协议里的 `map_model` 行，并 bump revision。

为什么需要它：`use_armor_piece_config.py` 只替换 `model_piece`，**原样保留**
`map_model`（见该脚本 193 行注释）。所以当协议被清成 0 条映射时，
只改 `model_piece` 是**完全惰性**的 —— 实测切换 S8/Y 后 bank 结构一字未变。

格式（`src/armor_registry_protocol.lua:139`）：`map_model=<target_item_id>,<model_key>`

用法：
    # 让 RE-2310（itemId 820333399）走自定义模型
    python set_protocol_mapping.py --add 820333399:custom-eaf880703fa94f398d620d2288b25d1f

    # 清空所有映射（回到干净状态）
    python set_protocol_mapping.py --clear

    # 只看现状
    python set_protocol_mapping.py --list
"""
import argparse
import os
import sys

DEFAULT_PATH = os.path.join(
    os.environ.get('LOCALAPPDATA', ''), 'CowboyBingus', 'Helldivers2',
    'ArmorRegistry', 'armor-registry.protocol')


def read_lines(path):
    with open(path, encoding='utf-8') as fh:
        return [line.rstrip('\r\n') for line in fh]


def write_lines(path, lines):
    tmp = path + '.tmp'
    with open(tmp, 'w', encoding='utf-8', newline='\n') as fh:
        fh.write('\n'.join(lines) + '\n')
    os.replace(tmp, path)


def get_revision(lines):
    for line in lines:
        if line.startswith('revision='):
            return int(line.split('=', 1)[1])
    return 0


def set_revision(lines, revision):
    for index, line in enumerate(lines):
        if line.startswith('revision='):
            lines[index] = 'revision=%d' % revision
            return lines
    lines.insert(0, 'revision=%d' % revision)
    return lines


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--protocol', default=DEFAULT_PATH)
    ap.add_argument('--add', action='append', default=[],
                    help='target_item:model_key，可多次给')
    ap.add_argument('--clear', action='store_true', help='删掉所有 map_model/map 行')
    ap.add_argument('--list', action='store_true', help='只打印现状')
    ap.add_argument('--no-bump', action='store_true', help='不改 revision（调试用）')
    args = ap.parse_args()

    if not os.path.exists(args.protocol):
        raise SystemExit('协议不存在：%s' % args.protocol)

    lines = read_lines(args.protocol)
    mappings = [line for line in lines
                if line.startswith('map_model=') or line.startswith('map=')]
    others = [line for line in lines
              if not (line.startswith('map_model=') or line.startswith('map='))]

    print('协议 %s' % args.protocol)
    print('  revision=%d  现有映射 %d 条' % (get_revision(lines), len(mappings)))
    if args.list:
        for line in mappings[:20]:
            print('    ' + line)
        if len(mappings) > 20:
            print('    … 另有 %d 条' % (len(mappings) - 20))
        return 0

    wanted = list(mappings)
    if args.clear:
        wanted = []
    for spec in args.add:
        if ':' not in spec:
            raise SystemExit('--add 需要 target_item:model_key，收到 %r' % spec)
        target, model = spec.split(':', 1)
        target = target.strip()
        model = model.strip()
        if not target.isdigit():
            raise SystemExit('target_item 必须是十进制 itemId，收到 %r' % target)
        if not model:
            raise SystemExit('model_key 不能为空')
        entry = 'map_model=%s,%s' % (target, model)
        wanted = [line for line in wanted if not line.startswith('map_model=%s,' % target)]
        wanted.append(entry)
        print('  + %s' % entry)

    revision = get_revision(lines)
    if not args.no_bump:
        revision += 1
    out = set_revision(list(others), revision)
    out.extend(wanted)
    write_lines(args.protocol, out)
    print('  写入 %d 行，revision -> %d，映射 %d 条' % (len(out), revision, len(wanted)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
