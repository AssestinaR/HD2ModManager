import os, sys, subprocess, tempfile
from pathlib import Path
sys.path.insert(0, r'E:/Data/source/repos/WpfApp1/tmp/rig-lab')
import hd2_asset as A, hd2_patch as P
from PIL import Image
SDK = r'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
TEXCONV = SDK + r'\deps\texconv.exe'
PATCH = r'E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0'
OUT = r'E:/Data/source/repos/WpfApp1/tmp/rig-lab/out/uv-crops'
tmp = tempfile.mkdtemp()
entries,_ = P.scan_entries(PATCH)
mats = {e['file_id']: e for e in entries if e['type_id']==A.MATERIAL_TYPE_ID}
tex  = {e['file_id']: e for e in entries if e['type_id']==A.TEXTURE_TYPE_ID}
want = [0x0d04d273fa5a4646, 0x0228cdef42ee1e6e]
tiles=[]
for i,mid in enumerate(want):
    m = A.Material.read(P.entry_payload(mats[mid], PATCH)['toc'])
    print('material 0x%016x  ntex=%d nvar=%d' % (mid, len(m.tex_ids), len(m.var_names) if hasattr(m,'var_names') else -1))
    for slot,tid in enumerate(m.tex_ids):
        if tid == 0: continue
        e = tex.get(tid)
        if e is None: continue
        tp = P.entry_payload(e, PATCH)
        t = A.Texture.read(tp['toc'], tp['gpu'])
        dds = os.path.join(tmp, 'r%d_%d.dds'%(i,slot))
        open(dds,'wb').write(bytes(t.dds_header)+bytes(tp['gpu']))
        subprocess.run([TEXCONV,'-nologo','-y','-ft','png','-f','R8G8B8A8_UNORM','-m','1','-o',tmp,dds],capture_output=True)
        png = os.path.join(tmp, 'r%d_%d.png'%(i,slot))
        if not os.path.exists(png): continue
        im = Image.open(png).convert('RGB')
        print('   slot %2d  tex 0x%016x  %dx%d' % (slot, tid, im.width, im.height))
        if slot in (5, 3):
            tiles.append(('m%d slot%d'%(i,slot), im))
CELL=460
sheet = Image.new('RGB',(CELL*len(tiles), CELL+26),(28,32,38))
from PIL import ImageDraw
dr = ImageDraw.Draw(sheet)
for k,(lab,im) in enumerate(tiles):
    th = im.copy(); th.thumbnail((CELL-6,CELL-6)); sheet.paste(th,(k*CELL+3,3))
    dr.text((k*CELL+4, CELL+4), lab, fill=(235,240,246))
p = OUT + r'\ref-textures.png'
sheet.save(p); print('->', p)
