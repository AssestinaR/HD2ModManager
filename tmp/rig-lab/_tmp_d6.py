# -*- coding: utf-8 -*-
import os, sys, hashlib, tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
import export_obj as E, hd2_source as S, hd2_vtf as V, fix_appearance as FA
for tag in ['D1发色白','D2发色紫','D3发色浅灰','D4发色黄']:
    p = r'E:/tmp/pkg8/Lunalice基础材质%s.vpk' % tag
    _v, do, ents = E.parse_vpk_tree(p)
    out=[]
    for f,a,o,l in ents:
        b=os.path.basename(f.replace('/','\\'))
        if not b.lower().endswith('.vtf'): continue
        blob=E.read_range(p,do,a,o,l)
        v=V.VTF.read(blob)
        px=v.mips[0]
        out.append('%s %dx%d %s sha=%s mip0sha=%s' % (b, v.width, v.height, v.format_name,
                    hashlib.sha256(blob).hexdigest()[:12], hashlib.sha256(px).hexdigest()[:12]))
    print('%-10s %s' % (tag, ' | '.join(out)))
print()
for tag in ['C1金属装饰品色金','C2金属装饰品色银']:
    p = r'E:/tmp/pkg8/Lunalice基础材质%s.vpk' % tag
    _v, do, ents = E.parse_vpk_tree(p)
    for f,a,o,l in ents:
        b=os.path.basename(f.replace('/','\\'))
        if not b.lower().endswith('.vtf'): continue
        blob=E.read_range(p,do,a,o,l)
        v=V.VTF.read(blob)
        print('%-14s %-12s %dx%d %s sha=%s' % (tag, b, v.width, v.height, v.format_name, hashlib.sha256(blob).hexdigest()[:12]))
