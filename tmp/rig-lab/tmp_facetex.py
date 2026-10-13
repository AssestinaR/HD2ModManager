import os, sys, subprocess, tempfile
sys.path.insert(0, r'E:/Data/source/repos/WpfApp1/tmp/rig-lab')
import hd2_asset as A, hd2_patch as P
from PIL import Image
SDK = r'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
TEXCONV = SDK + r'\deps\texconv.exe'
PATCH = r'E:/tmp/rig-lab/cand-m4w/9ba626afa44a3aa3.patch_0'
OUT = r'E:/Data/source/repos/WpfApp1/tmp/rig-lab/out/uv-crops'
tmp = tempfile.mkdtemp()
entries,_ = P.scan_entries(PATCH)
tex = {e['file_id']: e for e in entries if e['type_id']==A.TEXTURE_TYPE_ID}
FACE = 0xb9f84331454d8282
e = tex[FACE]; tp = P.entry_payload(e, PATCH)
t = A.Texture.read(tp['toc'], tp['gpu'])
dds = os.path.join(tmp,'face.dds'); open(dds,'wb').write(bytes(t.dds_header)+bytes(tp['gpu']))
subprocess.run([TEXCONV,'-nologo','-y','-ft','png','-f','R8G8B8A8_UNORM','-m','1','-o',tmp,dds],capture_output=True)
im = Image.open(os.path.join(tmp,'face.png')).convert('RGB')
im.thumbnail((1000,1000))
im.save(OUT + r'\face-atlas.png')
print('face texture ->', OUT + r'\face-atlas.png', im.size)
