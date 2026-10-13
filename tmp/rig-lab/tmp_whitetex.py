import os, sys, subprocess, tempfile
sys.path.insert(0, r'E:/Data/source/repos/WpfApp1/tmp/rig-lab')
import hd2_asset as A, hd2_patch as P
from PIL import Image
SDK = r'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
TEXCONV = SDK + r'\deps\texconv.exe'
OUT = r'E:/Data/source/repos/WpfApp1/tmp/rig-lab/out/uv-crops'
tmp = tempfile.mkdtemp()
for tag, PATCH, ids in (
    ('ours', r'E:/tmp/rig-lab/cand-m4w/9ba626afa44a3aa3.patch_0',
     {'white':0xbe569f23c33c3083,'face':0xb9f84331454d8282}),
):
    entries,_ = P.scan_entries(PATCH)
    tex = {e['file_id']: e for e in entries if e['type_id']==A.TEXTURE_TYPE_ID}
    for nm, fid in ids.items():
        e = tex[fid]; tp = P.entry_payload(e, PATCH)
        t = A.Texture.read(tp['toc'], tp['gpu'])
        dds = os.path.join(tmp, nm+'.dds'); open(dds,'wb').write(bytes(t.dds_header)+bytes(tp['gpu']))
        subprocess.run([TEXCONV,'-nologo','-y','-ft','png','-f','R8G8B8A8_UNORM','-m','1','-o',tmp,dds],capture_output=True)
        im = Image.open(os.path.join(tmp, nm+'.png')).convert('RGB')
        print('%s %s %dx%d' % (tag, nm, im.width, im.height))
        im.thumbnail((1100,1100))
        im.save(OUT + '\\' + tag + '-' + nm + '-tex.png')
print('done')
