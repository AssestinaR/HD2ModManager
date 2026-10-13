import os, sys, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as P, hd2_unit as U
D='E:/mod/Lunalice-Visible-Virtual-Model'
tocs=[os.path.join(D,f) for f in os.listdir(D) if '.patch_' in f and not f.endswith(('.stream','.gpu_resources'))]
entries,_=P.scan_entries(tocs[0])
e=[x for x in entries if x['type_id']==U.UNIT_ID][6]
pl=P.entry_payload(e,tocs[0]); toc=pl['toc']
b=U.Buf(toc); u=U.Unit.read(toc,pl['gpu'])
bio=u.bone_info_offset
n=struct.unpack_from('<I',toc,bio)[0]
offs=[struct.unpack_from('<I',toc,bio+4+4*i)[0] for i in range(n)]
print('bone_info_offset',hex(bio),'num',n,'offsets',offs)
for bi_i,o in enumerate(offs):
    s=bio+o
    nb,mo,rio,fio=struct.unpack_from('<4I',toc,s)
    print(' bi[%d] at 0x%x num_bones %d matrix_off %d real_off %d fake_off %d' % (bi_i,s,nb,mo,rio,fio))
    fs=s+fio
    nr=struct.unpack_from('<I',toc,fs)[0]
    ro=[struct.unpack_from('<I',toc,fs+4+4*i)[0] for i in range(nr)]
    rc=[struct.unpack_from('<I',toc,fs+4+4*nr+4*i)[0] for i in range(nr)]
    print('    num_remaps %d offsets %s counts %s   (header bytes: %s)' % (nr,ro,rc,toc[fs:fs+4+8*nr].hex()))
    raw=[struct.unpack_from('<I',toc,fs+4*i)[0] for i in range(min(40,(len(toc)-fs)//4))]
    print('    first 40 u32 from RemapStart: %s' % raw)
    for i in range(nr):
        vals=[struct.unpack_from('<I',toc,fs+ro[i]+4*k)[0] for k in range(rc[i])]
        print('    remap[%d] at +%d count %d -> %s' % (i,ro[i],rc[i],vals[:24]))
    break
