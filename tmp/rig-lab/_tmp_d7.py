# -*- coding: utf-8 -*-
import os, sys, tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
from PIL import Image
import export_obj as E, hd2_vtf as V, fix_appearance as FA, hd2_texpack as TP
tmp = tempfile.mkdtemp()
tiles=[]
for tag in ['C1金属装饰品色金','C2金属装饰品色银']:
    p = r'E:/tmp/pkg8/Lunalice基础材质%s.vpk' % tag
    _v, do, ents = E.parse_vpk_tree(p)
    for f,a,o,l in ents:
        b=os.path.basename(f.replace('/','\\'))
        if not b.lower().endswith('.vtf'): continue
        blob=E.read_range(p,do,a,o,l)
        v=V.VTF.read(blob)
        src=os.path.join(tmp,'%s.dds'%tag)
        open(src,'wb').write(TP._wrapper_dds(v, TP.VTF_TO_WRAPPER_DXGI[v.hi_format], v.width, v.height, v.mips[0], None))
        FA._run([FA.TEXCONV,'-nologo','-y','-ft','png','-f','R8G8B8A8_UNORM','-m','1','-o',tmp,src])
        im=Image.open(os.path.join(tmp,'%s.png'%tag)).convert('RGB')
        arr=np.asarray(im)
        print('%-14s %dx%d mean=%s  p90=%s' % (tag, v.width, v.height, arr.reshape(-1,3).mean(0).round(1), np.percentile(arr.reshape(-1,3),90,axis=0).round(1)))
        tiles.append((tag,im))
# M8 default (from A1: matcap1/matcap8/6) for reference
p = r'E:/tmp/pkg8/Lunalice基础材质A1主体.vpk'
_v, do, ents = E.parse_vpk_tree(p)
for f,a,o,l in ents:
    b=os.path.basename(f.replace('/','\\'))
    if b.lower() not in ('matcap1.vtf','matcap8.vtf','6.vtf','11.vtf'): continue
    blob=E.read_range(p,do,a,o,l)
    v=V.VTF.read(blob)
    src=os.path.join(tmp,'%s.dds'%b)
    open(src,'wb').write(TP._wrapper_dds(v, TP.VTF_TO_WRAPPER_DXGI[v.hi_format], v.width, v.height, v.mips[0], None))
    FA._run([FA.TEXCONV,'-nologo','-y','-ft','png','-f','R8G8B8A8_UNORM','-m','1','-o',tmp,src])
    im=Image.open(os.path.join(tmp,'%s.png'%b)).convert('RGB')
    arr=np.asarray(im)
    print('%-14s %dx%d mean=%s' % (b, v.width, v.height, arr.reshape(-1,3).mean(0).round(1)))
    tiles.append((b,im))
S=300
sheet=Image.new('RGB',(S*len(tiles), S+16),(24,24,28))
from PIL import ImageDraw
d=ImageDraw.Draw(sheet)
for i,(n,im) in enumerate(tiles):
    sheet.paste(im.resize((S,S), Image.LANCZOS),(i*S,16))
    d.text((i*S+4,3), n, fill=(230,230,230))
sheet.save('out/matcap_compare.png'); print('-> out/matcap_compare.png')
