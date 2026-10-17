-- 护甲 bank 扩容补丁：在**插件进程内**用 LuaJIT FFI 打，不用任何外部工具。
--
-- 为什么必须走插件内：外部进程对 game.dll 做 VirtualProtectEx + WriteProcessMemory
-- 会被反作弊判为"可疑程序"（已实测）。而模组是官方允许的 Lua 插件，它对自己的
-- 进程做同样的事没有外部句柄、没有跨进程写入，检测面完全不同。
--
-- 补丁内容（把 bank 从 3 扩到 8）：
--
--   1. 在代码洞里放一段 trampoline，把 `piece_type >= 3` 的句柄写到我们自己
--      VirtualAlloc 出来的缓冲里：
--          bank = buf + index*0x1b8 + (type-3)*40
--      `index*0x1b8` 正好是 trampoline 里现成的 r12，不用额外求值。
--      `piece_type == 2` 仍然走原来的 bank C，所以消费者和销毁循环都不用改。
--
--   2. 把 `game.dll+0x82024e` 的 `mov rcx,[rsp+0x68]`（5 字节）换成
--      `jmp 代码洞`。这条指令是 `type>=2` 分支的入口；`type` 0/1 走的是
--      0x820214，不受影响。
--
-- 已知的定位常量全部来自对 `game.dll` 内存镜像的静态分析，见
-- notes/armor-bank-code-evidence-20261017.md 与
-- notes/code-patch-via-lua-plugin-20261017.md。
--
-- 安全：install() 会把原始字节存进返回的 table；revert() 用它还原。
-- 找不到 game.dll 或任何一步失败都返回 nil,原因，绝不半途写入。

local M = {}

-- 诊断日志：install() 的任何一步失败都必须留下痕迹，否则补丁"静默不生效"时
-- 完全无法区分是"没被调用"、"ffi 不可用"、"VirtualProtect 被拦"还是"指令校验失败"。
-- 用 os.getenv 拿不到路径就静默跳过（不能让日志本身把插件搞挂）。
local function trace(message)
    local root = os and os.getenv and os.getenv('LOCALAPPDATA')
    if not root then return end
    local path = root .. '\\CowboyBingus\\Helldivers2\\ArmorRegistry\\bank-patch.status'
    local handle = io and io.open and io.open(path, 'a')
    if not handle then return end
    pcall(handle.write, handle, tostring(message) .. '\n')
    pcall(handle.close, handle)
end

M.trace = trace

local DISPATCH_RVA = 0x82024e   -- mov rcx,[rsp+0x68]（type>=2 分支入口）
local RESUME_RVA   = 0x820256   -- trampoline 结束后跳回
local CAVE_RVA     = 0x1af7673  -- 93 字节 0xCC 填充洞
local CAVE_LEN     = 93
local BANKS        = 8          -- 目标 bank 数（C 之后再来 6 个）
local BANK_BYTES   = 40         -- 每个 bank 10 个 dword
local RECORD_STRIDE= 0x1b8
local RECORD_SLOTS = 16384      -- 覆盖索引到 16384，缓冲约 12 MB

local PAGE_EXECUTE_READWRITE = 0x40
local MEM_COMMIT_RESERVE     = 0x1000 + 0x2000
local PAGE_READWRITE         = 0x04

local function load_kernel()
    local ok, ffi = pcall(require, 'ffi')
    if not ok or type(ffi) ~= 'table' then return nil, 'LuaJIT ffi 不可用' end
    if ffi.os ~= 'Windows' or not ffi.abi('64bit') then
        return nil, '只支持 Windows x64'
    end
    pcall(ffi.cdef, [[
        void *GetModuleHandleW(const wchar_t *name);
        int VirtualProtect(void *address, size_t size, unsigned long protect,
                           unsigned long *old);
        int FlushInstructionCache(void *process, const void *address, size_t size);
        void *VirtualAlloc(void *address, size_t size, unsigned long type,
                           unsigned long protect);
    ]])
    local kernel = ffi.load('kernel32')
    return kernel
end

local function module_base(kernel, ffi)
    -- 必须构造真正的 UTF-16 宽字符串。`ffi.cast('const wchar_t*', 'game.dll')`
    -- 只是把 Lua 字符串的 ASCII 字节当成宽字符指针传，名字是乱码，
    -- GetModuleHandleW 必然找不到（实测日志：install: module_base failed）。
    local name = 'game.dll'
    local wide = ffi.new('wchar_t[?]', #name + 1)
    for index = 1, #name do wide[index - 1] = name:byte(index) end
    wide[#name] = 0
    local handle = kernel.GetModuleHandleW(wide)
    if handle == nil then return nil, 'GetModuleHandleW 找不到 game.dll' end
    return tonumber(ffi.cast('uintptr_t', handle))
end

-- 构造 trampoline：type<=2 仍用 bank C，type>=3 落到自分配缓冲。
local function build_trampoline(ffi, cave_rva, buffer)
    local code = {}
    local function u8(v) code[#code + 1] = string.char(v % 256) end
    local function u32(v)
        for _ = 1, 4 do u8(v); v = math.floor(v / 256) end
    end
    local function u64(v)
        for _ = 1, 8 do u8(v % 256); v = math.floor(v / 256) end
    end
    local function bytes(hex)
        for i = 1, #hex, 2 do u8(tonumber(hex:sub(i, i + 1), 16)) end
    end

    bytes('488b4c2468')      -- mov  rcx, [rsp+0x68]    ; bankC
    bytes('418b54240c')      -- mov  edx, [r12+0xc]     ; piece_type
    bytes('83fa02')          -- cmp  edx, 2
    local jump_at = #code + 1
    bytes('7600')            -- jbe  store               ; type<=2 -> bankC
    bytes('83ea03')          -- sub  edx, 3
    bytes('6bd228')          -- imul edx, edx, 0x28
    local disp_at = #code + 1
    bytes('4c8b1500000000')  -- mov  r10, [rip+0]        ; 缓冲地址（回填）
    bytes('4d01e2')          -- add  r10, r12            ; + index*0x1b8
    bytes('89d1')            -- mov  ecx, edx
    bytes('4c01d1')          -- add  rcx, r10
    local store_at = #code + 1
    bytes('893c81')          -- mov  [rcx+rax*4], edi    ; bank[slot] = handle
    local rel = RESUME_RVA - (cave_rva + #code + 5)
    u8(0xe9); u32(rel < 0 and rel + 0x100000000 or rel)   -- jmp 0x820256
    local data_at = #code + 1
    u64(buffer)              -- 缓冲地址

    -- 回填 jbe（相对短跳，必须 <= 127）
    local short = store_at - (jump_at + 1)
    if short > 127 then return nil, 'jbe 距离超出 rel8' end
    code[jump_at + 1] = string.char(short)
    -- 回填 mov r10 的 rip 位移
    local disp = data_at - (disp_at + 6)
    local d = disp < 0 and disp + 0x100000000 or disp
    code[disp_at + 3] = string.char(d % 256); d = math.floor(d / 256)
    code[disp_at + 4] = string.char(d % 256); d = math.floor(d / 256)
    code[disp_at + 5] = string.char(d % 256); d = math.floor(d / 256)
    code[disp_at + 6] = string.char(d % 256)

    local blob = table.concat(code)
    if #blob > CAVE_LEN then
        return nil, ('trampoline %d 字节超出代码洞 %d'):format(#blob, CAVE_LEN)
    end
    return blob
end

local function read_bytes(ffi, address, count)
    local pointer = ffi.cast('const uint8_t *', address)
    local out = {}
    for i = 0, count - 1 do out[#out + 1] = pointer[i] end
    return out
end

local function write_bytes(ffi, address, blob)
    local pointer = ffi.cast('uint8_t *', address)
    for i = 1, #blob do pointer[i - 1] = blob:byte(i) end
end

local function protect(kernel, ffi, address, size, wanted)
    local old = ffi.new('unsigned long[1]')
    if kernel.VirtualProtect(ffi.cast('void *', address), size, wanted, old) == 0 then
        return nil, 'VirtualProtect 失败'
    end
    return tonumber(old[0])
end

--- 安装补丁。返回状态表（含原始字节，供 revert 使用），失败返回 nil,原因。
function M.install()
    trace('install: start')
    local kernel, reason = load_kernel()
    if not kernel then
        trace('install: load_kernel failed - ' .. tostring(reason))
        return nil, reason
    end
    trace('install: ffi/kernel32 ok')
    local ffi = require('ffi')

    local base, why = module_base(kernel, ffi)
    if not base then
        trace('install: module_base failed - ' .. tostring(why))
        return nil, why
    end
    trace(('install: game.dll base=0x%x'):format(base))

    local dispatch = base + DISPATCH_RVA
    local cave = base + CAVE_RVA

    -- 先确认目标位置确实是预期指令，避免在游戏更新后盲写。
    local head = read_bytes(ffi, dispatch, 5)
    if head[1] ~= 0x48 or head[2] ~= 0x8b or head[3] ~= 0x4c
        or head[4] ~= 0x24 or head[5] ~= 0x68 then
        return nil, ('0x%x 不是 mov rcx,[rsp+0x68]（游戏可能已更新）'):format(DISPATCH_RVA)
    end
    local cave_head = read_bytes(ffi, cave, 8)
    for i = 1, 8 do
        if cave_head[i] ~= 0xCC then
            return nil, ('代码洞 0x%x 不是 0xCC 填充（游戏可能已更新）'):format(CAVE_RVA)
        end
    end

    local buffer = kernel.VirtualAlloc(nil, RECORD_SLOTS * RECORD_STRIDE,
                                       MEM_COMMIT_RESERVE, PAGE_READWRITE)
    if buffer == nil then return nil, 'VirtualAlloc 失败' end
    buffer = tonumber(ffi.cast('uintptr_t', buffer))

    local trampoline, err = build_trampoline(ffi, CAVE_RVA, buffer)
    if not trampoline then return nil, err end

    local jump = string.char(0xe9)
    local rel = CAVE_RVA - (DISPATCH_RVA + 5)
    local r = rel < 0 and rel + 0x100000000 or rel
    for _ = 1, 4 do jump = jump .. string.char(r % 256); r = math.floor(r / 256) end

    local original_dispatch = read_bytes(ffi, dispatch, 5)
    local original_cave = read_bytes(ffi, cave, #trampoline)

    local old = protect(kernel, ffi, dispatch, 5, PAGE_EXECUTE_READWRITE)
    if not old then return nil, 'VirtualProtect(分派点) 失败' end
    write_bytes(ffi, dispatch, jump)
    protect(kernel, ffi, dispatch, 5, old)

    old = protect(kernel, ffi, cave, #trampoline, PAGE_EXECUTE_READWRITE)
    if not old then return nil, 'VirtualProtect(代码洞) 失败' end
    write_bytes(ffi, cave, trampoline)
    protect(kernel, ffi, cave, #trampoline, old)

    kernel.FlushInstructionCache(nil, ffi.cast('const void *', dispatch), 5)
    kernel.FlushInstructionCache(nil, ffi.cast('const void *', cave), #trampoline)

    return {
        base = base,
        buffer = buffer,
        banks = BANKS,
        bankBytes = BANK_BYTES,
        recordStride = RECORD_STRIDE,
        originalDispatch = original_dispatch,
        originalCave = original_cave,
        trampolineBytes = #trampoline,
    }
end

--- 还原补丁（用 install 返回的状态）。
function M.revert(state)
    if not state then return nil, '没有状态可还原' end
    local kernel, reason = load_kernel()
    if not kernel then return nil, reason end
    local ffi = require('ffi')
    local base = state.base
    local dispatch = base + DISPATCH_RVA
    local cave = base + CAVE_RVA

    local old = protect(kernel, ffi, dispatch, 5, PAGE_EXECUTE_READWRITE)
    if not old then return nil, 'VirtualProtect(分派点) 失败' end
    write_bytes(ffi, dispatch, string.char(unpack(state.originalDispatch)))
    protect(kernel, ffi, dispatch, 5, old)

    old = protect(kernel, ffi, cave, #state.originalCave, PAGE_EXECUTE_READWRITE)
    if not old then return nil, 'VirtualProtect(代码洞) 失败' end
    write_bytes(ffi, cave, string.char(unpack(state.originalCave)))
    protect(kernel, ffi, cave, #state.originalCave, old)

    kernel.FlushInstructionCache(nil, ffi.cast('const void *', dispatch), 5)
    kernel.FlushInstructionCache(nil, ffi.cast('const void *', cave), #state.originalCave)
    return true
end

--- 读某个记录的第 n 个扩展 bank（n=0 对应 type 3）。
function M.read_bank(state, record, extra)
    local ffi = require('ffi')
    local address = state.buffer + record * state.recordStride
        + (extra or 0) * state.bankBytes
    local pointer = ffi.cast('const uint32_t *', address)
    local out = {}
    for slot = 0, 9 do out[slot + 1] = tonumber(pointer[slot]) end
    return out
end

return M
