#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""给"教学补丁"生成着色器：只用 SV_Position + 时间，做出屏幕空间的镂空/透明/发光/动画。

为什么用屏幕空间：不同记录组的 ISGN 语义不一样（60,412 那组和 35,576 那组的
UV/法线/世界坐标分别在 v3/v2/v1），一旦猜错就是 dxilconv 崩溃。
只读 `SV_Position`（永远是 v0、永远存在）就绕开了全部不确定性，任何组都能用。

用法（内部脚本，被 build_teaching_patch 调用）：
    python teaching_fx_build.py --payload <本体根载荷> --out <输出载荷> 
"""
import argparse
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import payload_probe_build as ppb
import payload_map as pm

EMERALD = (0.00, 0.55, 0.32)


def hlsl(targets, color):
    """targets: [(语义序号, 分量数)]。"""
    L = ['// 教学用：屏幕空间条纹镂空 + 透明度呼吸 + 发光呼吸 + 祖母绿',
         '// 只读 SV_Position，所以对任何 ISGN 布局都安全。',
         'cbuffer VP : register(b0) { float4 vp[51]; };',
         'struct PSIn { float4 pos : SV_Position; };',
         'struct PSOut {']
    for k, (ti, n) in enumerate(targets):
        ty = {1: 'float', 2: 'float2', 3: 'float3', 4: 'float4'}[n]
        L.append('    %-6s o%d : SV_Target%d;' % (ty, k, ti))
    L += ['};', '',
          'PSOut main(PSIn i)', '{',
          '    float  t = vp[43].w;              // 游戏时间（秒）',
          '    float2 p = i.pos.xy;              // 屏幕像素坐标',
          '',
          '    // ---- 镂空：屏幕空间斜条纹，35% 被丢弃 ----',
          '    float s = frac((p.y + p.x * 0.6) * 0.020 - t * 0.25);',
          '    if (s < 0.35) discard;',
          '',
          '    // ---- 半透明：沿屏幕纵向来回摆的透明带 ----',
          '    float a = 0.20 + 0.80 * (0.5 + 0.5 * sin(t * 2.0 - p.y * 0.010));',
          '',
          '    // ---- 自发光（呼吸）+ 颜色 ----',
          '    float  g = 0.35 + 0.65 * (0.5 + 0.5 * sin(t * 3.0));',
          '    float3 c = float3(%.3ff, %.3ff, %.3ff) * g;' % color,
          '',
          '    PSOut o;']
    for k, (ti, n) in enumerate(targets):
        vals = ['c.x', 'c.y', 'c.z', 'a'][:n]
        L.append('    o.o%d = %s(%s);' % (k, {1: 'float', 2: 'float2', 3: 'float3', 4: 'float4'}[n],
                                          ', '.join(vals)))
    L += ['    return o;', '}', '']
    return '\n'.join(L)


def targets_of(rec):
    sem = pm.sig_semantics(rec['chunks'].get('OSGN') or b'')
    t = [s for s in sem if s['name'].upper().replace('_', '') == 'SVTARGET']
    t.sort(key=lambda s: s['index'])
    out = []
    for s in t:
        n = bin(s['mask']).count('1') or 4
        out.append((s['index'], min(n, 4)))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--payload', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--groups', default=None, help='逗号分隔的组号；省略=所有像素组')
    ap.add_argument('--keep-hlsl', default=None)
    args = ap.parse_args()

    pay = bytearray(open(args.payload, 'rb').read())
    recs = ppb.records(pay)
    groups = ppb.groups_of(pay)
    want = None
    if args.groups:
        want = set(int(x) for x in args.groups.split(','))

    for gi, (key, idxs) in enumerate(groups.items()):
        stage = key[0]
        if stage != 'ps':
            continue
        if want is not None and gi not in want:
            continue
        first = recs[idxs[0]]
        tg = targets_of(first)
        if not tg:
            continue
        src = hlsl(tg, EMERALD)
        if args.keep_hlsl:
            os.makedirs(args.keep_hlsl, exist_ok=True)
            open(os.path.join(args.keep_hlsl, 'g%d.hlsl' % gi), 'w', encoding='utf-8').write(src)
        blob = ppb.compile_ps(src, 'teach_g%d' % gi)
        ours, _ = ppb.chunks_of(open(blob, 'rb').read())
        donor = bytes(pay[first['off']:first['off'] + first['size']])
        rec, nnop = ppb.rebuild_record(donor, ours, 'ours')
        ok, text = ppb.verify_record(rec, 'teach_g%d' % gi)
        print('组 #%-3d x%-3d 记录 %-6d B  %d 个 SV_Target %s  NOP %-5d fxc %s' % (
            gi, len(idxs), first['size'], len(tg), [t[0] for t in tg], nnop, '✔' if ok else '✗'))
        if not ok:
            raise SystemExit('记录非法，停止')
        for i in idxs:
            r = recs[i]
            pay[r['off']:r['off'] + r['size']] = rec

    open(args.out, 'wb').write(bytes(pay))
    print('写出 %s（%d B，等长 %s）' % (args.out, len(pay),
                                       len(pay) == os.path.getsize(args.payload)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
