#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""通用 SM5 反汇编器（着色器 → 指令流）+ RDEF 资源清单。

只用于**离线阅读**：判断某个像素着色器采样了哪些贴图、alpha 是怎么算出来的、
以及尾部有没有可改写的指令。注意：操作数解码是尽力而为（历史上有过误读），
关键结论应交叉验证。

用法：
    python tools/shex_disasm.py <记录.dxbc 或 载荷.bin> [--size <记录字节数>]
    python tools/shex_disasm.py <载荷.bin> --size 3340 --list     # 只列记录
"""
import argparse
import os
import struct
import sys

CH = set(b'RDEF ISGN OSGN SHEX STAT PCSG ILDN SPDB IFCE RD11 PRIV'.split())

OPCODES = {
    0: 'add', 1: 'and', 2: 'break', 3: 'breakc', 4: 'call', 5: 'callc', 6: 'case', 7: 'continue',
    8: 'continuec', 9: 'cut', 10: 'default', 11: 'deriv_rtx', 12: 'deriv_rty', 13: 'discard',
    14: 'div', 15: 'dp2', 16: 'dp3', 17: 'dp4', 18: 'else', 19: 'emit', 20: 'emit_then_cut',
    21: 'endif', 22: 'endloop', 23: 'endswitch', 24: 'eq', 25: 'exp', 26: 'frc', 27: 'ftoi',
    28: 'ftou', 29: 'ge', 30: 'iadd', 31: 'if', 32: 'ieq', 33: 'ige', 34: 'ilt', 35: 'imad',
    36: 'imax', 37: 'imin', 38: 'imul', 39: 'ine', 40: 'ineg', 41: 'ishl', 42: 'ishr', 43: 'itof',
    44: 'label', 45: 'ld', 46: 'ld_ms', 47: 'log', 48: 'loop', 49: 'lt', 50: 'mad', 51: 'min',
    52: 'mov', 53: 'movc', 54: 'mul', 55: 'ne', 56: 'nop', 57: 'not', 58: 'or', 59: 'popc',
    60: 'resinfo', 61: 'ret', 62: 'retc', 63: 'round_ne', 64: 'round_ni', 65: 'round_pi',
    66: 'round_z', 67: 'rsq', 68: 'sample', 69: 'sample_c', 70: 'sample_c_lz', 71: 'sample_l',
    72: 'sample_d', 73: 'sample_b', 74: 'sqrt', 75: 'switch', 76: 'sincos', 77: 'udiv', 78: 'ult',
    79: 'uge', 80: 'umul', 81: 'umad', 82: 'umax', 83: 'umin', 84: 'usne', 85: 'xor',
    86: 'dcl_resource', 87: 'dcl_constantbuffer', 88: 'dcl_sampler', 89: 'dcl_indexrange',
    90: 'dcl_outputtopology', 91: 'dcl_inputprimitive', 92: 'dcl_maxout', 93: 'dcl_input',
    94: 'dcl_input_sgv', 95: 'dcl_input_siv', 96: 'dcl_input_ps', 97: 'dcl_input_ps_sgv',
    98: 'dcl_input_ps_siv', 99: 'dcl_output', 100: 'dcl_output_siv', 101: 'dcl_temps',
    102: 'dcl_output_sgv', 103: 'dcl_output_siv', 104: 'dcl_temps', 105: 'dcl_indexabletemp',
    106: 'dcl_globalflags', 108: 'lod', 109: 'gather4',
}
REGS = {0: 'r', 1: 'v', 2: 'o', 3: 'x', 4: 'l', 5: 'l64', 6: 's', 7: 't', 8: 'cb', 9: 'icb',
        10: 'label', 11: 'vprim', 12: 'oprim', 13: 'gsinstance', 14: 'null', 15: 'rasterizer'}
SWZ = 'xyzw'


def decode_operand(t, idx, toks):
    ncomp = t & 0x3
    sel = (t >> 2) & 0x3
    op_type = (t >> 12) & 0xFF
    idim = (t >> 20) & 0x3
    ext = (t >> 31) & 1
    consumed = 1
    neg = absv = False
    if ext and idx + consumed < len(toks):
        e = toks[idx + consumed]
        consumed += 1
        neg = ((e >> 6) & 1) != 0
        absv = ((e >> 7) & 1) != 0
    comp = ''
    if ncomp == 2 and sel == 0:
        mask = (t >> 4) & 0xF
        comp = ''.join(SWZ[i] for i in range(4) if mask & (1 << i))
    elif ncomp == 2 and sel == 1:
        comp = ''.join(SWZ[(t >> (4 + i * 2)) & 0x3] for i in range(4))
    index = None
    imms = []
    if idim == 0 and op_type == 4:
        cnt = {0: 0, 1: 1}.get(ncomp, 4)
        imms = list(toks[idx + consumed:idx + consumed + cnt])
        consumed += len(imms)
    elif idim == 1:
        repr0 = (t >> 22) & 0x7
        if repr0 == 0 and idx + consumed < len(toks):
            index = toks[idx + consumed]
            consumed += 1
        elif repr0 == 2 and idx + consumed < len(toks):
            subt, sc = decode_operand(toks[idx + consumed], idx + consumed, toks)
            index = subt
            consumed += sc
    name = REGS.get(op_type, 'reg%d' % op_type)
    if op_type == 4:
        body = 'l(' + ', '.join('%.6g' % struct.unpack('<f', struct.pack('<I', w))[0] for w in imms) + ')'
    else:
        body = name + ('' if index is None else '[%s]' % index)
        if comp:
            body += '.' + comp
    if absv:
        body = 'abs(' + body + ')'
    if neg:
        body = '-' + body
    return body, consumed


def disasm(shex, start=0, end=None):
    toks = struct.unpack_from('<%dI' % (len(shex) // 4), shex, 0)
    out = []
    i = start // 4
    limit = (end // 4) if end else len(toks)
    while i < limit:
        t = toks[i]
        op = t & 0x7FF
        ln = (t >> 24) & 0x7F
        if ln == 0:
            break
        parts = []
        j = i + 1
        while j < i + ln:
            txt, c = decode_operand(toks[j], j, toks)
            parts.append(txt)
            j += max(1, c)
        out.append((i * 4, OPCODES.get(op, 'op%d' % op), parts))
        i += ln
    return out


def rdef_resources(blob):
    if 'RDEF' not in blob:
        return [], []
    r = blob['RDEF']
    try:
        cb_n, cb_off = struct.unpack_from('<2I', r, 0)
        bd_n, bd_off = struct.unpack_from('<2I', r, 8)
    except struct.error:
        return [], []
    def cstr(o):
        z = r.find(b'\x00', o)
        return r[o:z].decode('ascii', 'replace') if 0 < o < len(r) and z > 0 else None
    binds, cbs = [], []
    for k in range(min(bd_n, 128)):
        e = bd_off + k * 32
        if e + 32 > len(r):
            break
        o, = struct.unpack_from('<I', r, e)
        s = cstr(o)
        if s:
            binds.append(s)
    for k in range(min(cb_n, 64)):
        e = cb_off + k * 24
        if e + 24 > len(r):
            break
        o, = struct.unpack_from('<I', r, e)
        s = cstr(o)
        if s:
            cbs.append(s)
    return binds, cbs


def load_record(path, size):
    d = open(path, 'rb').read()
    if d[:4] == b'DXBC':
        return d
    i = 0
    while True:
        s = d.find(b'DXBC', i)
        if s < 0:
            raise SystemExit('载荷里找不到记录')
        i = s + 4
        if s + 32 > len(d):
            continue
        total, nch = struct.unpack_from('<2I', d, s + 24)
        if size is not None and total != size:
            continue
        if not (32 < total < (1 << 23)) or not (1 <= nch <= 16) or s + total > len(d):
            continue
        offs = sorted(struct.unpack_from('<%dI' % nch, d, s + 32))
        if not all(bytes(d[s + o:s + o + 4]) in CH for o in offs):
            continue
        return bytes(d[s:s + total])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('path')
    ap.add_argument('--size', type=int, default=None)
    ap.add_argument('--list', action='store_true')
    args = ap.parse_args()

    rec = load_record(args.path, args.size)
    total, nch = struct.unpack_from('<2I', rec, 24)
    offs = sorted(struct.unpack_from('<%dI' % nch, rec, 32))
    blob = {}
    for k, o in enumerate(offs):
        end = offs[k + 1] if k + 1 < len(offs) else total
        blob[bytes(rec[o:o + 4]).decode()] = rec[o + 8:end]
    print('记录 %d B，块: %s' % (total, '+'.join('%s(%d)' % (k, len(v)) for k, v in blob.items())))

    binds, cbs = rdef_resources(blob)
    if cbs:
        print('  cbuffer: %s' % ', '.join(cbs))
    if binds:
        print('  绑定   : %s' % ', '.join(binds))

    if args.list or 'SHEX' not in blob:
        return
    shex_size = struct.unpack_from('<I', rec, offs[[i for i, o in enumerate(offs) if bytes(rec[o:o + 4]) == b'SHEX'][0]] + 4)[0]
    idx = [o for o in offs if bytes(rec[o:o + 4]) == b'SHEX'][0]
    shex = rec[idx + 16:idx + 8 + shex_size]
    print('  SHEX %d B（指令区）' % len(shex))
    for off, name, parts in disasm(shex):
        print('  +%-5d %-16s %s' % (off, name, ', '.join(parts)))


if __name__ == '__main__':
    sys.exit(main())
