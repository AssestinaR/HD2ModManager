import os, sys, subprocess, tempfile
import numpy as np
sys.path.insert(0, r'E:/Data/source/repos/WpfApp1/tmp/rig-lab')
import hd2_asset as A, hd2_patch as P
from PIL import Image
SDK = r'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
TEXCONV = SDK + r'\deps\texconv.exe'
tmp = tempfile.mkdtemp()
PATCH = r'E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0'
entries,_ = P.scan_entries(PATCH)
tex = {e['file_id']: e for e in entries if e['type_id']==A.TEXTURE_TYPE_ID}
for fid,e in tex.items():
    tp = P.entry_payload(e, PATCH)
    t = A.Texture.read(tp['toc'], tp['gpu'])
    dds = os.path.join(tmp,'r.dds'); open(dds,'wb').write(bytes(t.dds_header)+bytes(tp['gpu']))
    subprocess.run([TEXCONV,'-nologo','-y','-ft','png','-f','R8G8B8A8_UNORM','-m','1','-o',tmp,dds],capture_output=True)
    p = os.path.join(tmp,'r.png')
    if not os.path.exists(p): continue
    a = np.asarray(Image.open(p).convert('RGBA'))
    al = a[:,:,3]
    print('tex 0x%016x  %4dx%-5d alpha min %3d max %3d mean %6.1f  frac==0 %.3f  rgbMean %s'
          % (fid, a.shape[1], a.shape[0], al.min(), al.max(), al.mean(), (al==0).mean(),
             np.round(a[:,:,:3].reshape(-1,3).mean(axis=0),1)))
