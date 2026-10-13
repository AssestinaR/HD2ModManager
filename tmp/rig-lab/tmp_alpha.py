import os, sys, subprocess, tempfile
import numpy as np
sys.path.insert(0, r'E:/Data/source/repos/WpfApp1/tmp/rig-lab')
import hd2_asset as A, hd2_patch as P, hd2_source as S, hd2_texpack as TP, hd2_vtf as V
from PIL import Image
SDK = r'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
TEXCONV = SDK + r'\deps\texconv.exe'
tmp = tempfile.mkdtemp()

def png_from(hdr, px, tag):
    dds = os.path.join(tmp, tag+'.dds'); open(dds,'wb').write(bytes(hdr)+bytes(px))
    subprocess.run([TEXCONV,'-nologo','-y','-ft','png','-f','R8G8B8A8_UNORM','-m','1','-o',tmp,dds],capture_output=True)
    p = os.path.join(tmp, tag+'.png')
    return Image.open(p).convert('RGBA') if os.path.exists(p) else None

print('=== SOURCE VTF (raw mip0 alpha) ===')
packs = S.Packs()
for nm in ('white','shinano_hair_ear','shinano_face','shinano_body',
           'headdress_white01_catears','lace_white01_catears',
           'bra_white01_bra_inner','glove_white01_wristcuffs',
           'dress_white01_dress_corset','lower_white01_underwear'):
    raw = packs.vtf_bytes(nm)
    if raw is None:
        print('%-32s MISSING' % nm); continue
    v = V.VTF.read(raw)
    hdr, px = TP.vtf_to_dds(raw, TP.DXGI_BC7_SRGB, tag='a_'+nm)
    im = png_from(hdr, px, 'a_'+nm)
    if im is None: continue
    a = np.asarray(im)[:,:,3]
    print('%-32s fmt=%-2d %4dx%-5d alpha: min %3d max %3d mean %6.1f  frac==0 %.3f'
          % (nm, v.hi_format, v.width, v.height, a.min(), a.max(), a.mean(), (a==0).mean()))

print()
print('=== OURS (shipped M4W) ===')
PATCH = r'E:/tmp/rig-lab/cand-m4w/9ba626afa44a3aa3.patch_0'
entries,_ = P.scan_entries(PATCH)
tex = {e['file_id']: e for e in entries if e['type_id']==A.TEXTURE_TYPE_ID}
NAMES = {'white':0xbe569f23c33c3083,'hair_ear':0xb66b54bafd3dd768,
         'face':0xb9f84331454d8282,'body':0xb6e760aee076359d,
         'headdress':0xbbd058dd84a87e8c,'lace':0xbea2867f775bc39b,
         'bra_inner':0xbb106a56c4d48a2b,'glove':0xb3ba47e7968a0ab5,
         'dress_corset':0xb8f74de60c5f8803,'lower_underwear':0xbc9d1471d87f8337}
for nm,fid in NAMES.items():
    e = tex.get(fid)
    if e is None: print('%-32s MISSING' % nm); continue
    tp = P.entry_payload(e, PATCH)
    t = A.Texture.read(tp['toc'], tp['gpu'])
    im = png_from(t.dds_header, tp['gpu'], 'b_'+nm)
    if im is None: continue
    a = np.asarray(im)[:,:,3]
    print('%-32s      %4dx%-5d alpha: min %3d max %3d mean %6.1f  frac==0 %.3f'
          % (nm, im.width, im.height, a.min(), a.max(), a.mean(), (a==0).mean()))
