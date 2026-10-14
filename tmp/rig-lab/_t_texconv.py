# -*- coding: utf-8 -*-
"""Manual texconv smoke test."""
import os, sys, subprocess
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_vtf as V, hd2_texpack as TP, hd2_source as S

TEXCONV = TP.TEXCONV
ty = os.path.join(os.environ['TEMP'], 'cgtest')
os.makedirs(ty, exist_ok=True)
p = S.Packs()
raw = p.vtf_bytes('dress_white01_dress_corset')
v = V.VTF.read(raw)
print('src', v.format_name, v.width, v.height, 'hi_format', v.hi_format)
hdr = TP._wrapper_dds(v, TP.VTF_TO_WRAPPER_DXGI[v.hi_format], v.width, v.height, v.mips[0], None)
dds = os.path.join(ty, 't.dds')
open(dds, 'wb').write(hdr)
print('wrote', len(hdr), '->', dds)
cmd = [TEXCONV, '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM', '-m', '1', '-o', ty, dds]
print('cmd', cmd)
r = subprocess.run(cmd, capture_output=True)
print('rc', r.returncode)
print('OUT:', r.stdout.decode('utf-8', 'replace'))
print('ERR:', r.stderr.decode('utf-8', 'replace'))
print('files:', sorted(os.listdir(ty)))
