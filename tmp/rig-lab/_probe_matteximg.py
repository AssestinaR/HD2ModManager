# -*- coding: utf-8 -*-
import os, struct, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as P
MAT=0xEAC0B497876ADEDF; TEX=0xCD4238C6A0C69E32
D='E:/mod/Lunalice-Visible-Virtual-Model'
tocs=[os.path.join(D,f) for f in os.listdir(D) if '.patch_' in f and not f.endswith(('.stream','.gpu_resources'))]
entries,_=P.scan_entries(tocs[0])
def u32(d,o): return struct.unpack_from('<I',d,o)[0]
def u64(d,o): return struct.unpack_from('<Q',d,o)[0]
def f32(d,o): return struct.unpack_from('<f',d,o)[0]
print('=== MATERIALS ===')
for e in entries:
    if e['type_id']!=MAT: continue
    pl=P.entry_payload(e,tocs[0]); t=pl['toc']
    print('\nmaterial 0x%016x  toc %d B' % (e['file_id'], len(t)))
    print('  raw[0:64] %s' % t[:64].hex())
    p=0
    undat1=t[0:12]; p=12
    endoffset=u32(t,p); p+=4
    undat2=u64(t,p); p+=8
    parent=u64(t,p); p+=8
    undat3=t[p:p+32]; p+=32
    ntex=u32(t,p); p+=4
    undat4=t[p:p+36]; p+=36
    nvar=u32(t,p); p+=4
    undat5=t[p:p+12]; p+=12
    vdsize=u32(t,p); p+=4
    undat6=t[p:p+12]; p+=12
    print('  undat1 %s EndOffset %d undat2 0x%x ParentMaterialID 0x%x' % (undat1.hex(), endoffset, undat2, parent))
    print('  undat3 %s' % undat3.hex())
    print('  NumTextures %d  undat4 %s' % (ntex, undat4.hex()))
    print('  NumVariables %d undat5 %s VariableDataSize %d undat6 %s' % (nvar, undat5.hex(), vdsize, undat6.hex()))
    tex=[]
    for i in range(ntex):
        tu=u32(t,p); tid=u64(t,p+4); p+=12
        tex.append((tu,tid))
        print('    tex[%d] TexUnk %d TexID 0x%016x' % (i, tu, tid))
    vars_=[]
    for i in range(nvar):
        klass=u32(t,p); elems=u32(t,p+4); vid=u32(t,p+8); off=u32(t,p+12); stride=u32(t,p+16); p+=20
        vars_.append((klass,elems,vid,off,stride))
        print('    var[%d] klass %d elems %d ID 0x%08x off %d stride %d' % (i, klass, elems, vid, off, stride))
    print('  header end at %d  remaining %d bytes: %s' % (p, len(t)-p, t[p:p+96].hex()))
    for i,(klass,elems,vid,off,stride) in enumerate(vars_):
        vals=[f32(t,p+off+4*k) for k in range(klass+1)]
        print('    var[%d] 0x%08x values %s' % (i, vid, [round(v,5) for v in vals]))
print('\n=== TEXTURES ===')
for e in entries:
    if e['type_id']!=TEX: continue
    pl=P.entry_payload(e,tocs[0]); t=pl['toc']
    unkid=u32(t,0); u1=u32(t,4); u2=u32(t,8)
    mips=[]
    p=12
    for i in range(15):
        st=u32(t,p); bl=u32(t,p+4); h=struct.unpack_from('<H',t,p+8)[0]; w=struct.unpack_from('<H',t,p+10)[0]
        mips.append((st,bl,h,w)); p+=12
    dds=t[148:296]
    magic=dds[:4]; size=u32(dds,4); flags=u32(dds,8)
    hgt=u32(dds,12); wid=u32(dds,16); pitch=u32(dds,20); depth=u32(dds,24); mipcount=u32(dds,28)
    pf=dds[76:76+32]
    fourcc=dds[84:88]
    dxgi=u32(dds,128); arr=u32(dds,140)
    print('\ntexture 0x%016x toc %d gpu %d' % (e['file_id'], len(t), len(pl['gpu'])))
    print('  UnkID 0x%x Unk1 %d Unk2 0x%x' % (unkid,u1,u2))
    print('  mips %s' % mips[:4])
    print('  dds magic %s size %d flags 0x%x h %d w %d pitch %d depth %d mipcount %d fourcc %s dxgi %d arr %d' % (magic,size,flags,hgt,wid,pitch,depth,mipcount,fourcc,dxgi,arr))
    print('  pf %s' % pf.hex())
    print('  gpu first 32: %s' % pl['gpu'][:32].hex())
