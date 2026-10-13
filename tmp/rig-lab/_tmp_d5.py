# -*- coding: utf-8 -*-
import os, sys, tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
from PIL import Image
import hd2_asset as A, hd2_patch as HP, hd2_source as S, hd2_texpack as TP, fix_appearance as FA
import split_materials as SM

src = r'E:/mod/Lunalice-RE2310-M8/9ba626afa44a3aa3.patch_0'
entries,_ = HP.scan_entries(src)
m8tex = {e['file_id']: e for e in entries if e['type_id']==A.TEXTURE_TYPE_ID}
packs = S.Packs()
stem = 'shinano_body'
cid = FA.make_id(FA.TEX_ID_PREFIX, 'tex/base/'+stem)
e = m8tex[cid]
pl = HP.entry_payload(e, src)
t8 = A.Texture.read(pl['toc'], pl['gpu'])
print('M8 entry: toc=%d gpu=%d' % (len(pl['toc']), len(pl['gpu'])))
print('  m8 header: dims', t8.dds_header[12:20].hex(), 'dxgi', int.from_bytes(t8.dds_header[128:132],'little'), 'mips', int.from_bytes(t8.dds_header[28:32],'little'))
raw = packs.vtf_bytes(stem)
import hd2_vtf as V
v = V.VTF.read(raw)
print('  src VTF: %dx%d fmt=%s mips=%d' % (v.width, v.height, v.format_name, len(v.mips)))
tmp = tempfile.mkdtemp()
t2, px2 = TP.build_texture(raw, TP.DXGI_BC7_SRGB, cid, tmpdir=tmp, tag='base_'+stem)
print('  built: toc=%d gpu=%d dxgi=%d' % (len(t2.write_toc()), len(px2), int.from_bytes(t2.dds_header[128:132],'little')))
print('  gpu equal:', px2 == pl['gpu'])
arr_ref = FA.decode_patch_texture(src, e, tmp, 'ref')[0]
arr_new = SM.decode_asset(t2, tmp, 'new')
print('  ref  mean per ch', arr_ref.reshape(-1,4).mean(0).round(1), 'shape', arr_ref.shape)
print('  new  mean per ch', arr_new.reshape(-1,4).mean(0).round(1), 'shape', arr_new.shape)
Image.fromarray(arr_ref,'RGBA').save(os.path.join(HERE:=os.path.dirname(os.path.abspath(__file__)),'out','_cmp_ref.png'))
Image.fromarray(arr_new,'RGBA').save(os.path.join(HERE,'out','_cmp_new.png'))
# also: what did fix_textures do? check header diff of m8 toc vs built toc
print('  m8 toc head', pl['toc'][:24].hex())
print('  new toc head', t2.write_toc()[:24].hex())
print('  m8 toc == new toc:', pl['toc'] == t2.write_toc())
