import sys, struct
sys.path.insert(0, r'E:/Data/source/repos/WpfApp1/tmp/rig-lab')
import hd2_asset as A, hd2_patch as P

def scan(tag, path):
    entries,_ = P.scan_entries(path)
    print('=== %s ===' % tag)
    tex = [e for e in entries if e['type_id']==A.TEXTURE_TYPE_ID]
    print('textures: %d' % len(tex))
    for e in tex:
        tp = P.entry_payload(e, path)
        t = A.Texture.read(tp['toc'], tp['gpu'])
        h = bytes(t.dds_header)
        dds_h = struct.unpack_from('<I', h, 12)[0]
        dds_w = struct.unpack_from('<I', h, 16)[0]
        mips  = struct.unpack_from('<I', h, 28)[0]
        dxgi  = struct.unpack_from('<I', h, 128)[0]
        # StingrayMipmapInfo occupies the first 15*... bytes before the DDS header
        mi = bytes(tp['toc'])[:A.DDS_HEADER_SIZE-148] if A.DDS_HEADER_SIZE>148 else b''
        print('  0x%016x  %4dx%-5d ddsMips=%-3d dxgi=%-3d  gpu=%9d  tocPrefix=%d'
              % (e['file_id'], dds_w, dds_h, mips, dxgi, len(tp['gpu']), len(tp['toc'])))
    print()

scan('REFERENCE 【4.28】Chiffon-LUNALICE替换RE-2310',
     r'E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0')
scan('OURS M4W', r'E:/tmp/rig-lab/cand-m4w/9ba626afa44a3aa3.patch_0')
