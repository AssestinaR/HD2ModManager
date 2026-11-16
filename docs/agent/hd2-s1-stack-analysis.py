"""Read-only dump/PE analysis for S1. Outputs only JSON to stdout; never loads target code."""
import pathlib, struct, json, collections
import pefile, capstone
DUMP=pathlib.Path(r'C:\Users\Administrator\AppData\Local\Temp\PACKER_665835500_22488.dmp')
data=DUMP.read_bytes()
def u32(off): return struct.unpack_from('<I',data,off)[0]
def u64(off): return struct.unpack_from('<Q',data,off)[0]
streams={u32(u32(12)+12*i):(u32(u32(12)+12*i+4),u32(u32(12)+12*i+8)) for i in range(u32(8))}
regions=[]
if 5 in streams:
    p=streams[5][1]
    for i in range(u32(p)):
        q=p+4+16*i; regions.append((u64(q),u32(q+8),u32(q+12)))
if 9 in streams:
    p=streams[9][1]; r=u64(p+8)
    for i in range(u64(p)):
        q=p+16+16*i; n=u64(q+8); regions.append((u64(q),n,r)); r+=n
regions.sort()
def mem(va,n):
    out=bytearray()
    while len(out)<n:
        cur=va+len(out)
        found=next(((a,s,r) for a,s,r in regions if a<=cur<a+s),None)
        if found is None: raise ValueError(f'missing dump memory 0x{cur:X}')
        a,s,r=found; take=min(n-len(out),a+s-cur); out+=data[r+cur-a:r+cur-a+take]
    return bytes(out)
def mq(va): return struct.unpack('<Q',mem(va,8))[0]
modules=[]; p=streams[4][1]
for i in range(u32(p)):
    q=p+4+i*108; nr=u32(q+20); name=data[nr+4:nr+4+u32(nr)].decode('utf-16-le')
    modules.append(dict(base=u64(q),size=u32(q+8),checksum=u32(q+12),timestamp=u32(q+16),path=name,name=pathlib.PureWindowsPath(name).name))
def mod(va): return next((m for m in modules if m['base']<=va<m['base']+m['size']),None)
def label(va):
    m=mod(va); return f"{m['name']}+0x{va-m['base']:X}" if m else f'0x{va:X}'
pe_cache={}; reports={}
def getpe(m):
    name=m['path'].lower()
    if name not in pe_cache:
        path=pathlib.Path(m['path'])
        if not path.is_file(): raise ValueError('image missing: '+str(path))
        pe=pefile.PE(str(path),fast_load=False)
        check=dict(path=str(path),dump_timestamp=m['timestamp'],disk_timestamp=pe.FILE_HEADER.TimeDateStamp,dump_size=m['size'],disk_size=pe.OPTIONAL_HEADER.SizeOfImage,dump_checksum=m['checksum'],disk_checksum=pe.OPTIONAL_HEADER.CheckSum)
        check['match']=check['dump_timestamp']==check['disk_timestamp'] and check['dump_size']==check['disk_size'] and (not check['dump_checksum'] or check['dump_checksum']==check['disk_checksum'])
        reports[name]=check
        if not check['match']: raise ValueError('image metadata mismatch: '+str(check))
        pe_cache[name]=pe
    return pe_cache[name]
md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64)
def disasm(va,n=48):
    source='dump'
    try: b=mem(va,n)
    except ValueError:
        m=mod(va); b=getpe(m).get_data(va-m['base'],n); source='matching disk image'
    return dict(source=source,instructions=[f'0x{x.address:X} {x.mnemonic} {x.op_str}' for x in md.disasm(b,va)])
def function(m,va):
    pe=getpe(m); rva=va-m['base']
    return next((f.struct for f in getattr(pe,'DIRECTORY_ENTRY_EXCEPTION',[]) if f.struct.BeginAddress<=rva<f.struct.EndAddress),None)
exc=streams[6][1]; ctx=u32(exc+164)
regs={name:u64(ctx+off) for name,off in [('rax',120),('rcx',128),('rdx',136),('rbx',144),('rsp',152),('rbp',160),('rsi',168),('rdi',176),('r8',184),('r9',192),('r10',200),('r11',208),('r12',216),('r13',224),('r14',232),('r15',240),('rip',248)]}
regnames=['rax','rcx','rdx','rbx','rsp','rbp','rsi','rdi','r8','r9','r10','r11','r12','r13','r14','r15']

def unwind_one(regs):
    r=dict(regs); pc=r['rip']; m=mod(pc)
    if not m: raise ValueError('pc outside modules')
    pe=getpe(m); f=function(m,pc); notes=[]
    # Epilogue simulation deliberately unsupported: detect common epilog instructions.
    ins=list(md.disasm(pe.get_data(pc-m['base'],32),pc))
    if ins and (ins[0].mnemonic in ('ret','pop','leave') or (ins[0].mnemonic in ('add','lea') and ins[0].op_str.startswith('rsp,'))):
        raise ValueError('epilogue needs explicit emulation at '+label(pc))
    if f is None:
        if m['name'].lower()=='dxilconv.dll' and pc-m['base']==0x44414:
            expected=bytes.fromhex('8b41548902488bc2c3')
            actual=pe.get_data(pc-m['base'],len(expected))
            if actual!=expected: raise ValueError('leaf code verification failed: '+actual.hex())
            notes.append('no pdata: verified leaf mov eax,[rcx+54h]; mov [rdx],eax; mov rax,rdx; ret')
        else:
            raise ValueError('no pdata; no verified leaf, stop (packed game disk code is not reliable)')
    else:
        ui=f.UnwindData; visited=set()
        while True:
            if ui in visited: raise ValueError('cyclic chain')
            visited.add(ui); hdr=pe.get_data(ui,4)
            ver=hdr[0]&7; flags=hdr[0]>>3; prolog=hdr[1]; count=hdr[2]; fr=hdr[3]&15; fo=hdr[3]>>4
            if ver!=1: raise ValueError('unsupported unwind version '+str(ver))
            notes.append(dict(begin=hex(f.BeginAddress),end=hex(f.EndAddress),unwind=hex(ui),flags=flags,frame_register=regnames[fr] if fr else None,frame_offset=fo*16))
            codes=pe.get_data(ui+4,count*2); i=0
            while i<count:
                offset=codes[i*2]; op=codes[i*2+1]&15; info=codes[i*2+1]>>4; slots=1
                if op==1: slots=2 if info==0 else 3
                if op in (4,8): slots=2
                if op in (5,9): slots=3
                val=struct.unpack_from('<H',codes,(i+1)*2)[0] if slots==2 else (struct.unpack_from('<I',codes,(i+1)*2)[0] if slots==3 else None)
                apply=(pc-m['base']-f.BeginAddress>=offset)
                if apply:
                    if op==0: r[regnames[info]]=mq(r['rsp']); r['rsp']+=8
                    elif op==1: r['rsp']+=val*8 if info==0 else val
                    elif op==2: r['rsp']+=info*8+8
                    elif op==3: r['rsp']=r[regnames[fr]]-fo*16
                    elif op in (4,5): r[regnames[info]]=mq(r['rsp']+(val*8 if op==4 else val))
                    elif op in (8,9): pass # SIMD restoration irrelevant to control flow
                    else: raise ValueError('unsupported unwind opcode '+str(op))
                i+=slots
            if flags&4:
                chained=pe.get_data(ui+4+((count+1)&~1)*2,12)
                begin,end,ui=struct.unpack('<III',chained)
                f=type('RuntimeFunction',(),dict(BeginAddress=begin,EndAddress=end,UnwindData=ui))()
            else: break
    ret_slot=r['rsp']; r['rip']=mq(ret_slot); r['rsp']+=8
    return r,dict(return_slot=hex(ret_slot),return_to=label(r['rip']),unwind=notes)
frames=[]; r=dict(regs)
for i in range(100):
    entry=dict(index=i,pc=hex(r['rip']),location=label(r['rip']),rsp=hex(r['rsp']))
    try:
        r,detail=unwind_one(r); entry.update(detail); frames.append(entry)
        if not r['rip']: break
    except Exception as e:
        entry['stop']=str(e); frames.append(entry); break

import re
stack_path=pathlib.Path(r'E:\Data\source\repos\WpfApp1\.dsh\analysis\dump\stack.txt')
candidates=[]
for line in stack_path.read_text(encoding='utf-8').splitlines():
    match=re.match(r'\s*(0x[0-9A-Fa-f]+)\s+(0x[0-9A-Fa-f]+)\s+(\S+)\s+\+\s+(0x[0-9A-Fa-f]+)',line)
    if match:
        a,v,n,o=match.groups(); va=int(v,16); m=mod(va)
        ent=dict(stack=hex(int(a,16)),delta=hex(int(a,16)-regs['rsp']),value=hex(va),module=n,offset=o,recomputed=label(va))
        if m:
            try:
                pe=getpe(m); sec=next((s for s in pe.sections if s.VirtualAddress<=va-m['base']<s.VirtualAddress+max(s.Misc_VirtualSize,s.SizeOfRawData)),None)
                ent['section']=sec.Name.rstrip(b'\0').decode() if sec else '<headers>'
                ent['executable']=bool(sec and sec.Characteristics&0x20000000)
            except Exception as e: ent['section_error']=str(e)
        candidates.append(ent)
candidates.sort(key=lambda x:int(x['stack'],16))
result=dict(registers={k:hex(v) for k,v in regs.items()},image_checks=reports,frames=frames,code={label(regs['rip']):disasm(regs['rip']-13,50),label(mod(regs['rip'])['base']+0x9F335D):disasm(mod(regs['rip'])['base']+0x9F3340,45)},candidate_counts=dict(collections.Counter(c['module'] for c in candidates)),candidates=candidates)
def recovered_context():
    address=0x1A164F70C0
    b=mem(address,256)
    assert struct.unpack_from('<I',b,48)[0]==0x10005F
    rr={name:struct.unpack_from('<Q',b,120+8*i)[0] for i,name in enumerate(['rax','rcx','rdx','rbx','rsp','rbp','rsi','rdi','r8','r9','r10','r11','r12','r13','r14','r15','rip'])}
    eb=mem(address+0x4F0,152)
    er=dict(address=hex(address+0x4F0),code=hex(struct.unpack_from('<I',eb,0)[0]),flags=hex(struct.unpack_from('<I',eb,4)[0]),nested=hex(struct.unpack_from('<Q',eb,8)[0]),pc=label(struct.unpack_from('<Q',eb,16)[0]),count=struct.unpack_from('<I',eb,24)[0],parameters=[hex(x) for x in struct.unpack_from('<2Q',eb,32)])
    fs=[]; r=dict(rr)
    for i in range(60):
        e=dict(index=i,pc=hex(r['rip']),location=label(r['rip']),rsp=hex(r['rsp']))
        try:
            r,detail=unwind_one(r); e.update(detail);fs.append(e)
            if not r['rip']: break
        except Exception as ex:
            e['stop']=str(ex);fs.append(e);break
    return dict(address=hex(address),registers={k:hex(v) for k,v in rr.items()},exception=er,frames=fs,code=disasm(rr['rip'],16))
result['original_exception']=recovered_context()
# Disk game image is packed: matching metadata is not a license to disassemble its runtime code.
result['code'].pop('helldivers2.exe+0x9F335D',None)
result['code_warning']='Game disk .text differs from runtime (packed/protected); no game unwind metadata available. Only dump-resident game instruction bytes are used.'
result['candidate_section_counts']={name:dict(collections.Counter(c.get('section','?') for c in candidates if c['module']==name)) for name in result['candidate_counts']}

if __name__=='__main__':
    import sys
    if '--save' in sys.argv:
        root=pathlib.Path(__file__).parent
        jpath=root/'hd2-s1-stack-evidence.json'
        report=root/'hd2-s1-stack-report.md'
        assert not jpath.exists() and not report.exists(), 'Do not overwrite reports without reviewing them first.'
        jpath.write_text(json.dumps(result,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
        header='''# S1：崩溃调用方向与原始异常恢复

## 结论

当前记录的 `helldivers2.exe+0x9F616D / read 0x58` 是异常处理路径中的二次访问违例；栈上保存的原始异常发生于 `dxilconv.dll+0x44414 / read 0x54`。dxilconv 的保存现场在当前 game 崩溃函数的更外层，不是 game+9F616D 正在调用的更内层。这里包含异常分发/回调重入，不能将所有模块地址直接串为正常 call 链。

## 输入与方法

- 原始扫描：[stack.txt](<../../.dsh/analysis/dump/stack.txt>)，152 条，已按栈地址升序。
- 模块范围：[modules.txt](<../../.dsh/analysis/dump/modules.txt>)。
- 转储：[PACKER_665835500_22488.dmp](<C:/Users/Administrator/AppData/Local/Temp/PACKER_665835500_22488.dmp>)。
- 可复核脚本：[hd2-s1-stack-analysis.py](<hd2-s1-stack-analysis.py>)；机器可读证据：[hd2-s1-stack-evidence.json](<hd2-s1-stack-evidence.json>)。
- 用 dump 内存读 CONTEXT、EXCEPTION_RECORD 和栈；只读本机 PE，核对时间戳、映像大小、校验和值；对系统 DLL 使用 `.pdata/.xdata` 展开并检查关键 CALL 返回位置。没有运行或修改游戏模块。
- 游戏磁盘映像受保护/加壳，元数据相同不代表其磁盘代码等于运行时代码；游戏部分没有可靠 `.pdata`，因此停止展开，而不是把它当无栈帧叶函数。其当前故障指令直接取自 dump。

## 1. 方向判据与当前内层

Windows x64 栈向低地址增长：CALL 压入返回地址使 RSP 减 8，被调用函数再向低地址分配局部变量；正常向调用者展开时 RSP 递增。因此低栈地址一般是新/内层，高地址一般是旧/外层。异常保存现场、局部变量和旧数据不是普通帧，必须另外识别。

当前 `RSP=0x1A164F6400`，`RIP=game+0x9F616D`。dump 中实际指令是 `mov rcx,[rax+0x58]`，且 `RAX=0`。后续尾部为 `add rsp,0x20; pop rdi; jmp [rax+0x10]`，因此当前返回槽位应在 `RSP+0x28=0x1A164F6428`，那里恰为 `game+0x9F335D`。`RSP+0x20` 上的 `game+0x1A2C9B0` 落在 `.data`，不是更近的一帧。RBP 落在堆区，不能作为传统链式帧指针追溯。

所有 26 条 game 候选都位于 `0x1A164F6420..0x1A164F6748`；第一条 dxilconv 指针位于 `0x1A164F6760`（RSP+0x360），大量 dxilconv 指针在更高地址。只看扫描已经说明 dxilconv 在更外层方向；下面的异常记录把这一点落实为具体机制。

## 2. 直接读到的原始异常证据

- 栈 `0x1A164F6778`：`KERNELBASE+0x116083`，导出范围确认是 `UnhandledExceptionFilter+0x1F3`；前一条调用将控制交给过滤器回调。
- 栈 `0x1A164F70B8`：`ntdll+0x165D3E`，导出精确确认是 `KiUserExceptionDispatcher+0x2E`，处于调用异常分发函数后的返回位置。
- 与该分发器布局吻合的 CONTEXT 在 `0x1A164F70C0`：`ContextFlags=0x10005F`、`CS=0x33`、`RIP=dxilconv+0x44414`、`RSP=0x1A164F7E38`、`RCX=0`。
- CONTEXT+0x4F0 的 `0x1A164F75B0` 是 EXCEPTION_RECORD：`code=0xC0000005`、`flags=0`、`nested=0`、`address=dxilconv+0x44414`、`NumberParameters=2`、`info[0]=0`、`info[1]=0x54`。
- 元数据匹配的 dxilconv 映像在 `+0x44414` 的完整叶函数：`mov eax,[rcx+0x54]; mov [rdx],eax; mov rax,rdx; ret`。它与保存的 `RCX=0 / read 0x54` 完全吻合。
- 原始返回槽 `0x1A164F7E38` 存 `dxilconv+0x1E4A6`，且 `dxilconv+0x1E4A1` 的 CALL 精确指向 `+0x44414`。这比“模块候选很多”的证据更强。

因此当前 game+9F616D 不是第一现场；更可能是异常过滤/崩溃处理过程中又触发空指针。无需推翻 SHEX 整段替换有效的实机事实。

## 3. 最可能的时间/调用序列

下列模块偏移为帧中的当前/返回 PC，不全是函数入口。箭头方向为旧调用者到新被调用者；异常边界用 ⇒ 区分：

```text
game+736F84
 → D3D12Core+7BC34 → D3D12Core+16C77 → D3D12+1707
 → D3D12Core+50FDB → D3D12Core+7D246 → D3D12Core+158CD
 → D3D12Core+15A87C
 → dxilconv+B3A2 → dxilconv+2575 → dxilconv+CAD7
 → dxilconv+EB40 → dxilconv+B633 → dxilconv+1E4A6
 → dxilconv+44414  [原始 AV：RCX=0，读 0x54]
 ⇒ ntdll!KiUserExceptionDispatcher / 异常分发与过滤
 ⇒ KERNELBASE!UnhandledExceptionFilter
 ⇒ game 的过滤/崩溃处理链（中间 game 帧尚不能完整验证）
 → game+9F335D → game+9F616D  [当前转储 AV：RAX=0，读 0x58]
```

原始 CONTEXT 的系统 DLL 段展开如下（新→旧，RSP 递增）；停止于 game，不伪造更外层。原扫描到 `0x1A164F80F8` 为止，但转储实际保留了更高地址栈内存，所以能恢复原扫描之外的 D3D12/game 调用者。

| 序号 | 当前/返回 PC | 此帧 RSP | 下一返回槽 |
|---:|---|---|---|
'''
        lines=[header]
        for f in result['original_exception']['frames']:
            lines.append(f"| {f['index']} | `{f['location']}` | `{f['rsp']}` | `{f.get('return_slot','停止：无可靠 game 展开信息')}` |\n")
        lines.append('''
## 4. 不能把 152 个命中称为 152 帧

69 条 dxilconv 命中中，55 条在 `.text`、7 条在 `.pdata`、7 条是映像基址；14 条 D3D12Core 命中中，只有 3 条在 `.text`，6 条在 `.pdata`、5 条为基址。代码段候选也不自动成为活动返回帧：保存的 RIP、旧调用残留、函数指针同样落在代码段。`0x1A164F6FA8..0x1A164F7060` 连续的模块基址/`.pdata` 指针对与异常展开数据吻合，不能读成跨模块调用链。runtime 的 6 个 `0xFFFFFFFF` 命中也高度疑似哨兵值（模块表低基址范围恰好包含该值）。

本结果保留“崩溃发生于 DXBC→DXIL/着色器创建路径”的判断，但将“69/14 帧”精确化为“69/14 个模块内指针命中”。

## 5. 最小实验与可执行结论

1. **最小定位复核，无需改 mod：** 用 WinDbg 打开同一 dump，`.ecxr; r; .cxr 0000001a164f70c0; .exr 0000001a164f75b0; r; kv; u 00007ff990144414 L4`。必要时把本机对应系统映像加入 `.exepath`。应看到原始 `dxilconv+44414 / read 54 / RCX=0`，区别于 `.ecxr` 的 game/read58。
2. **下一轮只做一个 mod 对照：** 保持当前 rough7/t2/v7.xy SHEX 和全部 shader 字节不变，仅将 RoughnessMap 条目逐字节恢复为已知正常原始条目，其余同当前失败样本。它能直接区分“当前 shader 单独是否足以复现”；若仍崩，优先抓 dxilconv 原始异常，而不是追二次 game/read58。若不崩，只能说明贴图替换或与 shader 的交互必要，不能直接宣布 BC7 载荷本身无效。
3. 若能在游戏策略允许的调试环境复现，用首机会访问违例断点先截获 dxilconv 原始现场，保留 `.exr -1; .ecxr; r; kv`；不要只取 UnhandledExceptionFilter 之后的最终崩溃。

仅由 S1 不能确定原始 null 对象究竟是采样输入/资源元数据/着色器声明不一致引起，还是纹理替换影响资产加载后间接引起；也不能因 fxc 能反汇编就排除转换器的语义问题。

## 附录：152 个候选按栈地址升序

**G**=helldivers2.exe，**X**=dxilconv.dll，**C**=D3D12Core.dll；其余保留全名。`代码候选` 不等于已展开帧。原始行号=序号+3。

| 序号 | 栈地址 | 相对当前 RSP | 模块 | 偏移 | PE节 / 分类 |
|---:|---|---|---|---|---|
''')
        for i,c in enumerate(candidates,1):
            name={'helldivers2.exe':'**G**','dxilconv.dll':'**X**','D3D12Core.dll':'**C**'}.get(c['module'],c['module'])
            section=c.get('section','?')
            state='代码候选' if c.get('executable') else '非代码指针'
            if int(c['value'],16)==0xFFFFFFFF: state='疑似哨兵/假命中'
            if int(c['stack'],16)==0x1A164F71B8: state='保存 CONTEXT.Rip'
            if int(c['stack'],16)==0x1A164F75C0: state='原始 EXCEPTION_RECORD.Address'
            if int(c['stack'],16) in (0x1A164F7E38,0x1A164F7FF8,0x1A164F80F8): state='已展开原始返回槽'
            if int(c['stack'],16)==0x1A164F6428: state='当前 game 返回槽，尾部代码支持'
            lines.append(f"| {i} | `{c['stack']}` | `{c['delta']}` | {name} | `{c['offset']}` | `{section}`；{state} |\n")
        report.write_text(''.join(lines),encoding='utf-8')
        print(json.dumps(dict(report=str(report),evidence=str(jpath),candidate_counts=result['candidate_counts'],section_counts=result['candidate_section_counts'],original_exception=result['original_exception']['exception'],original_frames=result['original_exception']['frames']),indent=2,ensure_ascii=False))
    else:
        if '--brief' in sys.argv: result.pop('candidates',None)
        print(json.dumps(result,indent=2,ensure_ascii=False))
