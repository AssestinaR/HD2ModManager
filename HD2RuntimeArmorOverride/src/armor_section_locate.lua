-- 在游戏进程里定位某个 unit 的 TOC 副本。
--
-- 特征：TOC 起点前 8 字节是 unit 自己的 FileId，紧接 8 个零字节。
-- 依据见 notes/section-toc-memory-header-differs-only-in-unit-id-20261019.md：
-- 内存副本与文件逐字节相同，唯一差异就是 +0 处被填上了 FileId。
--
-- 为什么不用交接文档里的"节记录 48 字节签名 + 固定偏移"：那个固定偏移属于某一次
-- 构建，换个包就失效（本次实测旧签名一个都没命中）。FileId 与构建无关。
--
-- 光凭 8 个字节会误命中，所以命中后必须用结构走查验证：mesh 表数量、节表边界、
-- 以及 +0 的 FileId 回读。验证不过就继续扫，不当成命中。
--
-- 内存访问全部由调用方注入（read / regions），所以能离线用"假内存"测。
local M={}

local MAX_REGION_BYTES=0x800000

-- FileId 在内存里是小端字节序，后面跟 8 个零字节。
function M.pattern(unit_id)
    assert(type(unit_id)=='string' and #unit_id==16,'unit id must be 16 hex digits')
    local bytes={}
    for index=15,1,-2 do
        bytes[#bytes+1]=string.char(tonumber(unit_id:sub(index,index+1),16))
    end
    return table.concat(bytes)..string.rep('\0',8)
end

-- read(address,size) -> string|nil ; regions() -> 迭代器，产出 {base=,size=}
function M.new(read,regions,layout)
    assert(type(read)=='function','read is required')
    assert(type(regions)=='function','regions is required')
    assert(layout and type(layout.parse)=='function','layout module is required')

    local cache={}

    local function scan(unit_id,pattern,checkpoint)
        local scanned=0
        for region in regions() do
            local base=region.base
            local size=region.size or 0
            if base and size>0 and size<=MAX_REGION_BYTES then
                local chunk=read(base,size)
                if chunk then
                    local at=1
                    while true do
                        local found=string.find(chunk,pattern,at,true)
                        if not found then break end
                        local candidate=base+found-1
                        -- 命中只是候选：结构走查通过、且 FileId 回读一致，才算找到。
                        local parsed=layout.parse(read,candidate)
                        if parsed and layout.unit_id_at(read,candidate)==unit_id then
                            return candidate,parsed
                        end
                        at=found+1
                    end
                end
                scanned=scanned+size
            end
            if checkpoint then checkpoint(scanned) end
        end
        return nil,nil,scanned
    end

    local self={}

    -- 返回 base, layout, 或 nil, 原因。
    -- 缓存是必需的：全量扫一遍要几秒，不能每次切换都扫。
    function self.locate(unit_id,checkpoint)
        local cached=cache[unit_id]
        if cached then
            -- 缓存必须复验：重载后基址会变，用旧地址写等于写到别处。
            if layout.unit_id_at(read,cached.base)==unit_id then
                return cached.base,cached.layout
            end
            cache[unit_id]=nil
        end
        local pattern=M.pattern(unit_id)
        local base,parsed=scan(unit_id,pattern,checkpoint)
        if not base then return nil,'not_found' end
        cache[unit_id]={base=base,layout=parsed}
        return base,parsed
    end

    function self.invalidate(unit_id)
        if unit_id then cache[unit_id]=nil else cache={} end
    end

    function self.cached(unit_id)
        local entry=cache[unit_id]
        return entry and entry.base or nil
    end

    return self
end

return M
