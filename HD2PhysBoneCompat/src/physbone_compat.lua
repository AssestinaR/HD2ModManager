-- ============================================================================
-- HD2 PhysBone 兼容层运行时
--
-- 作用：从 patch 的 `.gpu_resources` 末尾读出 `HD2PBX01` 容器，
-- 取出 `hd2_spring_rig.bin`（源 mod 作者手调的 jigglebone 参数），
-- 然后每帧驱动护甲 Unit 上那些「新插入的、avatar 骨架里没有同名骨」的节点，
-- 把 L4D2 的手感复现出来。
--
-- 三条铁律（都是实测踩出来的，见交接文档 §13.3 / §15）：
--   1. 必须缓存基准姿态，每帧从基准算。这类节点不会被引擎每帧重置，
--      写「当前值 × delta」会逐帧累加 —— 表现为疯狂旋转。
--   2. 只替换旋转，必须保留原来的平移。简单做 pose×delta 会把平移一起转走。
--   3. 写完调 World.update_unit，否则这一帧渲染看不到。
--
-- 引擎侧已证实（§15）：父子链会自动合成，所以我们只需写每根骨的 local pose。
--
-- Lua 5.1 没有 string.unpack，所以下面自己解定长整数与 IEEE754 f32。
-- ============================================================================

local M = {}

M.version = "1.0.0"

-- ---------------------------------------------------------------------------
-- 二进制读取（无 string.unpack 的替代）
-- ---------------------------------------------------------------------------

local function u8(s, at) return s:byte(at) or 0 end

local function u16(s, at)
	return u8(s, at) + u8(s, at + 1) * 256
end

local function u32(s, at)
	return u8(s, at) + u8(s, at + 1) * 256 + u8(s, at + 2) * 65536 + u8(s, at + 3) * 16777216
end

local function i32(s, at)
	local v = u32(s, at)
	if v >= 2147483648 then return v - 4294967296 end
	return v
end

local function u64(s, at)
	return u32(s, at) + u32(s, at + 4) * 4294967296
end

-- IEEE754 binary32。Lua 5.1 没有 unpack，手算比引 FFI 安全（FFI 可能被沙箱挡）。
local function f32(s, at)
	local b0, b1, b2, b3 = s:byte(at, at + 3)
	if b0 == nil then return 0 end
	local sign = 1
	if b3 >= 128 then
		sign = -1
		b3 = b3 - 128
	end
	local exponent = b3 * 2 + math.floor(b2 / 128)
	local mantissa = (b2 % 128) * 65536 + b1 * 256 + b0
	if exponent == 0 then
		if mantissa == 0 then return 0 end
		return sign * mantissa * 2 ^ -149
	end
	if exponent == 255 then
		if mantissa == 0 then return sign * math.huge end
		return 0 / 0
	end
	return sign * (1 + mantissa / 8388608) * 2 ^ (exponent - 127)
end

local function u32le(value)
	local b = {}
	for i = 1, 4 do
		b[i] = value % 256
		value = math.floor(value / 256)
	end
	return string.char(b[1], b[2], b[3], b[4])
end

M._binary = { u8 = u8, u16 = u16, u32 = u32, i32 = i32, u64 = u64, f32 = f32 }

-- ---------------------------------------------------------------------------
-- HD2PBX01 容器
-- ---------------------------------------------------------------------------

local CONTAINER_MAGIC = "HD2PBX01"
local FOOTER_MAGIC = "HD2PBFT1"
local FOOTER_SIZE = 56
local HEADER_SIZE = 48

--- 从一份"载体文件内容"里取出所有命名记录。
--- 先读 EOF 的 footer 得到容器长度，再回推起点——绝不用写入时的绝对偏移，
--- 因为 patch 可能被复制、重排、重新编号。
function M.parse_container(bytes)
	if type(bytes) ~= "string" or #bytes < HEADER_SIZE + FOOTER_SIZE then
		return nil, "文件太短，不可能是带容器的载体"
	end
	local footerAt = #bytes - FOOTER_SIZE + 1
	if bytes:sub(footerAt, footerAt + 7) ~= FOOTER_MAGIC then
		return nil, "末尾没有 HD2PBFT1 footer"
	end
	local containerLength = u64(bytes, footerAt + 16)          -- footer +0x10（0 基 16 → 1 基 +16）
	local start = #bytes - containerLength + 1
	if start < 1 then return nil, "footer 里的容器长度比文件还大" end
	if bytes:sub(start, start + 7) ~= CONTAINER_MAGIC then
		return nil, "算出来的起点不是 HD2PBX01"
	end

	local version = u32(bytes, start + 8)
	local count = u32(bytes, start + 40)                        -- header +0x28
	local indexOffset = u64(bytes, start + 24)                  -- header +0x18
	local indexLength = u64(bytes, start + 32)                  -- header +0x20

	local records = {}
	local at = start + indexOffset                               -- 0 基 → +1 由调用处处理
	for _ = 1, count do
		local recordAt = at                                       -- 游标每轮自己前移
		-- recordAt 已经是 1 基下标，下面的偏移直接是协议里的 0 基字段偏移。
		local nameLength = u16(bytes, recordAt)
		local dataOffset = u64(bytes, recordAt + 4)
		local dataLength = u64(bytes, recordAt + 12)
		local name = bytes:sub(recordAt + 56, recordAt + 55 + nameLength)
		records[#records + 1] = {
			name = name,
			data = bytes:sub(start + dataOffset, start + dataOffset + dataLength - 1),
			length = dataLength,
		}
		at = recordAt + 56 + nameLength
	end

	return {
		version = version,
		start = start,
		length = containerLength,
		indexLength = indexLength,
		records = records,
	}
end

--- 在容器记录里找某个名字。
function M.find_record(container, name)
	if container == nil then return nil end
	for _, record in ipairs(container.records) do
		if record.name == name then return record end
	end
	return nil
end

-- ---------------------------------------------------------------------------
-- hd2_spring_rig.bin
-- ---------------------------------------------------------------------------

local RIG_MAGIC = "HD2SRG01"
local RIG_HEADER_SIZE = 32
local PARAM_COUNT = 29

--- 参数名。顺序必须与 C# 侧 SourceJiggleBone 的声明一致。
M.parameterNames = {
	"length", "tipMass",
	"yawStiffness", "yawDamping",
	"pitchStiffness", "pitchDamping",
	"alongStiffness", "alongDamping",
	"angleLimit",
	"minYaw", "maxYaw", "yawFriction", "yawBounce",
	"minPitch", "maxPitch", "pitchFriction", "pitchBounce",
	"baseMass", "baseStiffness", "baseDamping",
	"baseMinLeft", "baseMaxLeft", "baseLeftFriction",
	"baseMinUp", "baseMaxUp", "baseUpFriction",
	"baseMinForward", "baseMaxForward", "baseForwardFriction",
}

--- 解析弹簧 rig。返回骨头数组，每项含 index / parent / name / flags / pos / params。
function M.parse_rig(bytes)
	if type(bytes) ~= "string" or #bytes < RIG_HEADER_SIZE then
		return nil, "rig 太短"
	end
	if bytes:sub(1, 8) ~= RIG_MAGIC then
		return nil, "rig magic 不对"
	end
	local version = u32(bytes, 9)
	if version ~= 2 then
		return nil, "不支持的 rig 版本 " .. tostring(version) .. "（本实现只认 v2）"
	end
	local count = u32(bytes, 13)
	local nameOffset = u32(bytes, 17)
	local nameSize = u32(bytes, 21)
	local recordSize = u32(bytes, 25)
	if recordSize < 148 then return nil, "recordSize 不合理" end

	local bones = {}
	for i = 0, count - 1 do
		local at = RIG_HEADER_SIZE + i * recordSize + 1
		local nameLocal = u32(bytes, at + 8)
		local nameStart = nameOffset + nameLocal + 1
		local nameEnd = bytes:find("\0", nameStart, true) or (nameStart + 1)
		local parentStart = nameOffset + u32(bytes, at + 12) + 1
		local parentEnd = bytes:find("\0", parentStart, true) or parentStart
		local params = {}
		for p = 1, PARAM_COUNT do
			params[M.parameterNames[p]] = f32(bytes, at + 32 + (p - 1) * 4)
		end
		bones[#bones + 1] = {
			index = u32(bytes, at),
			parent = i32(bytes, at + 4),
			name = bytes:sub(nameStart, nameEnd - 1),
			-- 父骨名字（v2 新增）：运行时靠它把链根锚到 HD2 的骨名表上。
			parentName = bytes:sub(parentStart, parentEnd - 1),
			flags = u32(bytes, at + 16),
			pos = { f32(bytes, at + 20), f32(bytes, at + 24), f32(bytes, at + 28) },
			params = params,
		}
	end
	return { version = version, bones = bones, nameTableSize = nameSize }
end

-- ---------------------------------------------------------------------------
-- 从磁盘找容器
-- ---------------------------------------------------------------------------

-- 为什么要在若干候选路径里试：Lua 拿不到"游戏安装目录"，
-- io.open 的基址是游戏进程的工作目录。不同安装方式下 data 目录的相对位置可能不同，
-- 所以宁可多试几个，失败也不抛。
function M.default_search_paths(archiveId, maxIndex)
	archiveId = archiveId or "9ba626afa44a3aa3"
	if maxIndex == nil then
		-- 不给上限就**按实际部署算**：patch 序号 = mod 在配置里的载入顺序，
		-- 装上百个 mod 时能到三位数，任何写死的窗口都会漏掉排在后面的容器。
		local prefix = M.detect_prefix(archiveId)
		local last = prefix and M.find_max_patch_index(prefix, archiveId) or -1
		maxIndex = last < 0 and 15 or (last + 5)
	end

	local paths = {}
	for index = 0, maxIndex do
		paths[#paths + 1] = string.format("data/%s.patch_%d.gpu_resources", archiveId, index)
		paths[#paths + 1] = string.format("./data/%s.patch_%d.gpu_resources", archiveId, index)
		paths[#paths + 1] = string.format("../data/%s.patch_%d.gpu_resources", archiveId, index)
	end
	return paths
end

--- 这个前缀下有没有 patch N（基文件或 `.gpu_resources` sidecar 都算"存在"）。
--- <para>
--- 只看"有没有文件"，不读内容：要用它做几十次探测，不能把几十个几十 MB 的包打开读。
--- 基文件与 sidecar 任一存在就算这个序号被占用——部署时一个 patch group 的三份文件
--- （base / .stream / .gpu_resources）共用同一个序号，所以按任一文件判断都不会漏。
--- </para>
function M.patch_exists(prefix, archiveId, index)
	local base = prefix .. archiveId .. ".patch_" .. index
	local handle = io.open(base, "rb")
	if handle ~= nil then
		handle:close()
		return true
	end
	handle = io.open(base .. ".gpu_resources", "rb")
	if handle ~= nil then
		handle:close()
		return true
	end
	return false
end

--- 找出部署里**最大的** patch 序号：倍增探上界 + 二分定位。
--- <para>
--- 为什么不能线性扫：序号上界是"这个玩家装了多少个带 patch 的 mod"，没有可信上限；
--- 按 0..N 全扫是 O(N) 次 io.open，装两百个 mod 就是两百次磁盘探测。
--- 倍增+二分是 O(log N)：N=200 时约二十次。
--- </para>
--- <para>
--- 部署序列是连续的（管理器每次先删掉 data 里所有旧 patch，再按载入顺序从 0 重新编号），
--- 所以"存在 ⇒ 之前的都存在"这个假设成立；万一哪天出现空洞，调用方还会按 +5 的余量补扫。
--- </para>
function M.find_max_patch_index(prefix, archiveId, cap)
	cap = cap or 4096
	if not M.patch_exists(prefix, archiveId, 0) then return -1 end

	local low, high = 0, 1
	while high <= cap and M.patch_exists(prefix, archiveId, high) do
		low, high = high, high * 2
	end
	if high > cap then high = cap end

	while low + 1 < high do
		local mid = math.floor((low + high) / 2)
		if M.patch_exists(prefix, archiveId, mid) then
			low = mid
		else
			high = mid
		end
	end
	return low
end

--- 找出"能打开 patch 的那个前缀"。
--- <para>
--- 运行时不知道 data 的绝对路径，只能挨个试；试准一个就够——再往下扫都用它，
--- 免得每个序号都把五个前缀试一遍（那是五倍的 io.open）。
--- 返回 <c>nil</c> 表示一个前缀都没有 patch_0，调用方按"没部署"处理。
--- </para>
function M.detect_prefix(archiveId, prefixes)
	prefixes = prefixes or { "data/", "data\\", "./data/", "../data/", "..\\data\\" }
	for _, prefix in ipairs(prefixes) do
		if M.patch_exists(prefix, archiveId, 0) then return prefix end
	end
	return nil
end

local function read_file(path)
	local handle = io.open(path, "rb")
	if handle == nil then return nil end
	local bytes = handle:read("*a")
	handle:close()
	return bytes
end

-- 只读末尾 N 字节。
-- 为什么必须这么做：`.gpu_resources` 动辄几十 MB，整读会让加载卡住好几秒
-- （实测 3~5 秒，玩家能明显感到卡顿）。而容器就在文件末尾、只有十来 KB，
-- 所以先 seek 到末尾拿大小，再按需回读——协议文档里也是这么要求的。
local function read_tail(path, count)
	local handle = io.open(path, "rb")
	if handle == nil then return nil end
	local size = handle:seek("end")
	if size == nil or size < count then
		handle:close()
		return nil, 0
	end
	handle:seek("set", size - count)
	local bytes = handle:read(count)
	handle:close()
	return bytes, size
end

--- 只取容器那一段（footer 56 字节 → 解析出容器长度 → 回读容器）。
--- 成功返回容器字节串与文件大小；失败返回 nil 与原因。
function M._read_container_bytes(path)
	-- 先拿 footer：它固定在 EOF 的 56 字节里。
	local footer, size = read_tail(path, FOOTER_SIZE)
	if footer == nil then
		return nil, "文件小于 56 字节或读不到"
	end
	if footer:sub(1, 8) ~= FOOTER_MAGIC then
		return nil, "末尾没有 HD2PBFT1 footer"
	end
	-- footer 串里 footer 从 1 开始，所以 0 基的 0x10 落在 1 基的 17。
	local containerLength = u64(footer, 17)
	if containerLength < HEADER_SIZE + FOOTER_SIZE or containerLength > size then
		return nil, "footer 里的容器长度不自洽"
	end

	-- 再只回读容器本身。
	local containerBytes = read_tail(path, containerLength)
	if containerBytes == nil then
		return nil, "回读容器失败"
	end
	if containerBytes:sub(1, 8) ~= CONTAINER_MAGIC then
		return nil, "容器起点不是 HD2PBX01"
	end
	return containerBytes, size
end

--- 扫候选路径，返回第一个能解析出弹簧 rig 的结果。
function M.load_rig(searchPaths, logger)
	searchPaths = searchPaths or M.default_search_paths()
	for _, path in ipairs(searchPaths) do
		-- 先看文件在不在（避免对每个候选路径都去 open 全文）。
		local probeHandle = io.open(path, "rb")
		if probeHandle ~= nil then
			probeHandle:close()
			-- 先认"裸 rig"（HD2SRG01）：热改参数时最常用这个形态，不必每次重新封容器。
			local magicHandle = io.open(path, "rb")
			local magic = magicHandle and magicHandle:read(8) or ""
			if magicHandle then magicHandle:close() end
			if magic == RIG_MAGIC then
				local fileHandle = io.open(path, "rb")
				local raw = fileHandle and fileHandle:read("*a") or nil
				if fileHandle then fileHandle:close() end
				local rig, reason = raw and M.parse_rig(raw) or nil, nil
				if rig ~= nil then
					if logger then
						logger(string.format("[physbone] 从 %s 读到裸 rig：%d 根骨", path, #rig.bones))
					end
					return rig, path
				end
				if logger then
					logger(string.format("[physbone] %s 是 HD2SRG01 但解析失败：%s", path, tostring(reason)))
				end
			end
			local containerBytes, sizeOrReason = M._read_container_bytes(path)
			local container = containerBytes and M.parse_container(containerBytes) or nil
			if container == nil and logger then
				logger(string.format("[physbone] %s 里没有可用容器（%s）", path, tostring(sizeOrReason)))
			end
			if container ~= nil then
				local record = M.find_record(container, "hd2_spring_rig.bin")
				if record ~= nil then
					local rig = M.parse_rig(record.data)
					if rig ~= nil then
						if logger then
							logger(string.format("[physbone] 从 %s 读到 %d 根骨（容器 %d 字节）",
								path, #rig.bones, container.length))
						end
						return rig, path
					end
					if logger then
						local _, reason = M.parse_rig(record.data)
						logger("[physbone] 容器里 hd2_spring_rig.bin 解析失败：" .. tostring(reason))
					end
				end
			end
		end
	end
	return nil, "所有候选路径都没找到可用的弹簧 rig"
end

-- ---------------------------------------------------------------------------
-- 链：把 jiggle 骨按父子关系串起来
-- ---------------------------------------------------------------------------

--- 返回链数组。链根 = 父骨不是 jiggle 骨的那些。
--- 把 jiggle 骨树切成**互不重叠的路径**（每个骨只属于一条路径）。
--- <para>
--- ⚠️ <b>不能再把整棵树 DFS 摊平成一条链。</b>旧实现（2026-11-01 及之前）从链根出发
--- 一路 `table.remove(stack)` 下去，于是<b>分叉节点的第二个子骨被当成第一个子骨的续节</b>：
--- 方向沿 `k-1` 传递时，兄弟分支的弯曲会串到另一条分支上。真实资产实测：
--- `silent_code_shinano` 骨 96→{97,98,100} 摊平后，"99→100"这一对人造折角是
--- <b>154.3°</b>，而生产日志里数千帧恒定不变的"偏离绑定峰值"恰好就是 154.3° ——
--- 就是它（用户描述为"闪电状折痕 / Z 字形扭曲"）。
--- </para>
--- <para>
--- 切法：一条路径从"链根"或"分叉节点的每个子骨"开始，沿<b>唯一子骨</b>一路走到底。
--- 分叉节点本身留在它所在的那条路径里（作为末节），子骨各自开新路径。
--- 这样每个骨恰好被一条路径驱动一次，不会有两个求解器抢写同一根骨。
--- </para>
--- <para>
--- 路径首节如果父骨也是 jiggle 骨（即它是某个分叉的孩子），它的"上一节"在别的路径里，
--- 拿不到那一条的模拟方向 ⇒ 用<b>静止方向</b>当基准（跨路径的耦合一律不做）。
--- 这是有意的降级：宁可让分叉处"不传递弯曲"，也不要造假折角。
--- </para>
---@return table chains 每条路径是一个骨记录数组；首节的 `isBranch` 为 true 表示它有 jiggle 父骨
---@return table byIndex 源骨下标 → 骨记录
function M.build_chains(bones)
	local byIndex = {}
	for _, bone in ipairs(bones) do byIndex[bone.index] = bone end

	local children = {}
	for _, bone in ipairs(bones) do
		local key = bone.parent
		children[key] = children[key] or {}
		children[key][#children[key] + 1] = bone
	end

	M.apply_world_segments(bones)

	-- 起点：① 父骨不是 jiggle 骨（真正的链根）② 父骨是 jiggle 骨但那个父骨有多个 jiggle 子骨
	local function isForkParent(bone)
		local kids = children[bone.index]
		return kids ~= nil and #kids > 1
	end

	local chains, visited = {}, {}
	local function walk(start)
		local path = {}
		local current = start
		local guard = 0
		while current ~= nil do
			-- 防成环：源数据里父子关系成环的话，这个 while 会永远转下去，
			-- 而它跑在游戏主线程上 —— 表现就是整局卡死（AppHang），不是报错。
			guard = guard + 1
			if guard > 10000 then break end
			if visited[current.index] then break end
			visited[current.index] = true
			path[#path + 1] = current
			local kids = children[current.index]
			if kids == nil or #kids == 0 then break end
			if #kids > 1 then break end       -- 分叉：当前节点收尾，子骨各自开新路径
			current = kids[1]
		end
		return path
	end

	-- 先按"源骨下标"把起点排好，保证同一份 rig 每次切出来的顺序一致
	-- （顺序影响日志与写节点顺序；不确定性会让"同样的资产两次表现不同"变成玄学问题）。
	local starts = {}
	for _, bone in ipairs(bones) do
		local parent = byIndex[bone.parent]
		if parent == nil or isForkParent(parent) then
			starts[#starts + 1] = bone
		end
	end
	table.sort(starts, function(a, b) return a.index < b.index end)

	for _, start in ipairs(starts) do
		local path = walk(start)
		if #path > 0 then
			path.isBranch = (byIndex[start.parent] ~= nil)
			path.parentRecord = path.isBranch and byIndex[start.parent] or nil
			chains[#chains + 1] = path
		end
	end

	-- 兜底：成环 / 自引用导致某些骨没有任何"起点"可达。按骨下标补成单节路径，
	-- 让它们至少还能被驱动（并且计数上不会凭空少一批）。
	for _, bone in ipairs(bones) do
		if not visited[bone.index] then
			chains[#chains + 1] = { bone, isBranch = byIndex[bone.parent] ~= nil,
				parentRecord = byIndex[bone.parent] }
		end
	end
	return chains, byIndex
end

-- ---------------------------------------------------------------------------
-- 尖端点簧核（Valve `jigglebones.cpp` 语义；纯数字，不碰引擎，可离线单测）
--
-- 为什么换内核：旧核是"每骨两个独立角度振子 + 锚点位置二阶差分 + 手调 MASS_BASE"。
-- 读 Valve 源码后确认三件事（见 docs/physbone-compat-design-consolidated.md §3）：
--   1. 弹簧作用在**尖端质点的世界位置**上：`a = k·e − c·v`，**没有质量除法**
--      ⇒ `ωn = √k`、`ζ = c/(2√k)`，**不需要任何自由常数**；
--   2. `tipMass` 不是质量，它是**重力加速度幅值**（源码 `tipAccel.z -= tipMass`）
--      ⇒ 静止下垂角 = `tipMass/(k·length)`，与单位无关；
--   3. 驱动来自 **goalTip 与 tipPos 的差**，不需要任何加速度输入
--      ⇒ 二阶差分、maxAccel 钳位、positionSmoothing 这一整类补丁全部可以删。
--
-- 本实现是 "Tier B"：额外读锚点的世界旋转，把 bind 偏移与切向轴旋到当前朝向，
-- 于是**转身/转头也会产生惯性**（旧核完全做不到，这正是"像钢板"的主因之一）。
-- 实测依据：E5（`world_rotation` 跟着动画变）、E5b（四元数是 xyzw）、
--          E1（上轴 = +Z）、E2（`to_elements` 是 packed9）、E3（轴必须自己归一化）。
-- ---------------------------------------------------------------------------

-- Source 的长度单位是 1 单位 ≈ 0.75 英寸 ≈ 1.905 cm。
-- ⚠️ 这个 local 必须在下面所有用到它的函数**之前**声明（Lua 是词法作用域，
-- 后声明的 local 对先定义的函数不可见 —— 会静默取到全局 nil）。
local SOURCE_UNIT_TO_METERS = 0.75 * 0.0254

local function qmul(ax, ay, az, aw, bx, by, bz, bw)	return aw * bx + ax * bw + ay * bz - az * by,
		aw * by - ax * bz + ay * bw + az * bx,
		aw * bz + ax * by - ay * bx + az * bw,
		aw * bw - ax * bx - ay * by - az * bz
end

local function qconj(x, y, z, w) return -x, -y, -z, w end

--- 用四元数旋转一个向量：v' = q · v · q*
local function qrot(qx, qy, qz, qw, vx, vy, vz)
	local tx = 2 * (qy * vz - qz * vy)
	local ty = 2 * (qz * vx - qx * vz)
	local tz = 2 * (qx * vy - qy * vx)
	return vx + qw * tx + (qy * tz - qz * ty),
		vy + qw * ty + (qz * tx - qx * tz),
		vz + qw * tz + (qx * ty - qy * tx)
end

--- 轴角 → 四元数。**轴必须自己归一化**（一致性电池 E3：引擎不会替你归一化）。
local function q_axis_angle(ax, ay, az, angle)
	local length = math.sqrt(ax * ax + ay * ay + az * az)
	if length < 1e-9 then return 0, 0, 0, 1 end
	local s = math.sin(angle * 0.5) / length
	return ax * s, ay * s, az * s, math.cos(angle * 0.5)
end

--- 从 f 转到 t 的最小旋转（都是单位向量）。
--- <para>
--- 返回 <c>轴 x, y, z, 角度</c>；<b>同向</b>时返回 nil（不需要转）。
--- ⚠️ <b>反平行也必须返回一个确定的轴</b>，不能返回 nil —— 调用方历史上把 nil 当成"零旋转"，
--- 于是 180° 的转弯变成 0°（离线实测：目标 179.95° / 180.0° 输出 0°，方向误差 179.95° / 180°）。
--- 180° 的旋转轴不唯一（任何垂直于 f 的轴都行），所以这里取一个**确定**的垂直轴，
--- 保证同一输入每次得到同一条轴（否则每帧随机翻，表现是抖动）。
--- </para>
local function swing_axis(fx, fy, fz, tx, ty, tz)
	local dot = fx * tx + fy * ty + fz * tz
	if dot > 0.9999995 then return nil end
	local cx, cy, cz = fy * tz - fz * ty, fz * tx - fx * tz, fx * ty - fy * tx
	local length = math.sqrt(cx * cx + cy * cy + cz * cz)
	if length < 1e-9 then
		if dot > 0 then return nil end
		-- 反平行：造一个与 f 垂直的确定轴。f 接近世界 up 时退到 +X，避免叉积退化。
		local ax, ay, az = 0, 0, 1
		if math.abs(fz) > 0.9 then ax, ay, az = 1, 0, 0 end
		cx, cy, cz = fy * az - fz * ay, fz * ax - fx * az, fx * ay - fy * ax
		length = math.sqrt(cx * cx + cy * cy + cz * cz)
		if length < 1e-9 then return 1, 0, 0, math.pi end
		return cx / length, cy / length, cz / length, math.pi
	end
	local clamped = math.max(-1, math.min(1, dot))
	return cx / length, cy / length, cz / length, math.acos(clamped)
end

M._q = { mul = qmul, conj = qconj, rot = qrot, axis_angle = q_axis_angle, swing = swing_axis }

--- 源骨的约束标志位（`mstudiojigglebone_t.flags` 的低 7 位，与 HD2SourceModel.SourceJiggleFlags 一致）。
M.FLAG_FLEXIBLE = 0x01
M.FLAG_RIGID = 0x02
M.FLAG_YAW_CONSTRAINED = 0x04
M.FLAG_PITCH_CONSTRAINED = 0x08
M.FLAG_ANGLE_CONSTRAINED = 0x10
M.FLAG_LENGTH_CONSTRAINED = 0x20
M.FLAG_BASE_SPRING = 0x40

--- 取 flags 的某一位。PUC Lua 5.1 没有位运算库，用整除取余。
local function flag_set(flags, bit)
	if type(flags) ~= "number" or flags <= 0 then return false end
	return math.floor(flags / bit) % 2 == 1
end

-- ---------------------------------------------------------------------------
-- 世界朝向锚定：这条链的"静止朝向"要不要钉在绑定姿态上
-- ---------------------------------------------------------------------------
--
-- 语义：开了之后，链的目标朝向用**绑定姿态**的锚点旋转来算，父级动画只带动平移、
-- 不带转动。观感就是"这块布不跟着肢体转，只跟着走"。
--
-- 这个模式是给**袖子**做的：大袖子挂在会大幅摆动的手臂上，让它滞后于手臂才对。
-- 判据一直是"flags 同时带 yaw/pitch/angle 三种约束"（0x04|0x08|0x10）。
--
-- **2026-11-05 实测发现这条判据太宽**：Soppo（包 L4D2-3702）的 `Chest_L`/`Chest_R`
-- flags 也是 0x3D，可它们挂在 **Spine4（躯干）**上。于是胸口的布不跟躯干转，只跟着平移
-- —— 玩家弯腰/转身时布保持原朝向，实机看到的就是"胸口反重力立起来/往上飘"。
--
-- 真正的区别不在 flags，而在**挂在哪根骨上**：
--   · Lilium 的 0x3D 链根全挂在 `L_UpperArm` / `R_UpperArm`（手臂）——袖子 ✓
--   · Soppo 的 0x3D 链根挂在 `Spine4`（躯干）——胸口 ✗
--
-- 所以判据改成"flags + 锚点是哪根骨"，但**只在能证明是躯干/头/胯时才关掉**：
-- 解析不出名字（`source_extra_91` 这类辅助骨）时一律保持旧行为，宁可不动，
-- 也不要因为一个猜出来的名字把已验收的观感改掉。
--- 躯干/头/胯：挂在它们上的布必须跟着躯干转。
--- 用模式而不是固定表：源侧是 `Spine` / `Spine1` / `Spine2` / `Spine4`（没有 Spine3），
--- HD2 侧是 `spine1` / `spine2` / `chest`——同一根骨在两套命名里叫法不同，逐条列举必然漏。
local function is_torso_anchor(key)
	if key == "chest" or key == "neck" or key == "head"
		or key == "hips" or key == "pelvis" then
		return true
	end
	return key:match("^spine%d*$") ~= nil
end

--- 这条链要不要世界朝向锚定。
--- @param flags number 源 jigglebone 的 flags
--- @param anchorName string|nil 链根挂到的那根 **HD2** 骨名（解析不出就是源父骨名）
--- @return boolean worldAnchored, string reason
function M.world_anchor_of(flags, anchorName)
	local byFlags = flag_set(flags, 0x04) and flag_set(flags, 0x08) and flag_set(flags, 0x10)
	if not byFlags then return false, "flags 没有 yaw+pitch+angle 三约束" end
	if anchorName == nil then return true, "锚点未知，保持旧行为" end
	local key = tostring(anchorName):lower()
	-- 源侧名字（`ValveBiped.Bip01_Spine4` / `Spine4`）也认，省掉一层"锚点表没命中"的漏判。
	key = key:gsub("^valvebiped%.bip01_", "")
	if is_torso_anchor(key) then
		return false, "锚点在躯干/头（" .. tostring(anchorName) .. "）⇒ 必须跟着躯干转"
	end
	return true, "flags 齐全且锚点不是躯干"
end

--- 29 个源参数 → 求解器增益。
--- <para>
--- 关键：`k` 的量纲是 1/s²、`c` 是 1/s，**与长度单位无关**，所以 ωn/ζ 可以直用。
--- 但 `length` 和 `tipMass` **是两种不同的量，必须用两个不同的比例换算**：
---   `length` 是**长度**（源单位 → 米，×0.01905）；
---   `tipMass` 是 Valve 里的**重力加速度幅值**（源单位/s² → 米/s²，同样 ×0.01905）。
--- 只有在"rig 里的长度已经是米"的场合，长度比例才该是 1.0 —— 而重力比例**仍然**是 0.01905。
--- 2026-10-31 踩过：把同一个 scale 用在两者上（探针传 1.0），于是 g=200 而不是 3.81，
--- 静垂角算成 81.9°，远超 35° 限位 ⇒ 头发被重力顶死在限位上、"总是指向正下方"。
--- 旧版的 `MASS_BASE = 300` 把 `tipMass` 当惯性质量放进分母，等于给每个资产换了刚度，已删除。
--- </para>
---@param opts table|nil { normalize = false, flags = <源骨 flags> }
---   `flags` 决定"限位从哪来"：带 `angleCon` 就用锥；只有 `yawCon/pitchCon` 就由分轴范围外包成锥；
---   两者都没有才真的不限位。不给 flags 时保持旧行为（只看 `angleLimit`），便于纯数学测试。
function M.gains_of(params, lengthScale, gravityScale, opts)
	lengthScale = lengthScale or SOURCE_UNIT_TO_METERS
	gravityScale = gravityScale or SOURCE_UNIT_TO_METERS
	params = params or {}
	local kYaw = math.max(tonumber(params.yawStiffness) or 50, 1e-3)
	local cYaw = math.max(tonumber(params.yawDamping) or 0, 0)
	-- 源模型里 pitch 的刚度/阻尼常常与 yaw 不同；旧核只读 yaw，等于丢掉一半参数。
	local kPitch = math.max(tonumber(params.pitchStiffness) or kYaw, 1e-3)
	local cPitch = math.max(tonumber(params.pitchDamping) or cYaw, 0)
	local length = math.max(tonumber(params.length) or 30, 1e-3) * lengthScale
	-- 角度限位 ≥180° 等于**不限位**（袖子的 360° 就是这种情况）。
	-- 必须归一成 0：否则 cos(2π)=1 会让 step_tip 的 `dot < cosLimit` 几乎恒真，
	-- 每骨每帧白跑一次限位分支（swing_axis + q_axis_angle + qrot）。
	-- 行为等价：limit=2π 时 `angle > 2π` 永远不成立，和 limit=0 跳过分支完全一样。
	local angleLimit = math.abs(tonumber(params.angleLimit) or 0.6)
	if angleLimit >= math.pi then angleLimit = 0 end
	-- 分轴限位：**带符号**，且与锥**叠加**（不是二选一）。
	--
	-- Valve 的三个 flag 各自独立生效：yawCon 夹 yaw 区间、pitchCon 夹 pitch 区间、
	-- angleCon 夹总偏角。以前我们是"有锥就不看分轴"，而且把区间取成
	-- `max(|min|,|max|)` 的**对称**值。
	--
	-- 2026-11-05 实测（L4D2-3831123094 的头发）：作者给的是**单边**区间 ——
	--   `Bn_r_cefa01/02` yaw [0, +10°]（只能往外）、`Bn_liuhaia01/aic01/aid01/b01` yaw [−10°, 0]。
	-- 对称化之后"只能往外"被抹成"里外都行"，实机就是头发朝脸那一侧甩。
	-- 现在按原值夹：`devYaw ∈ [sin(minYaw), sin(maxYaw)]`。
	local limitSource = "cone"
	local flags = opts and opts.flags
	local yawMin, yawMax, pitchMin, pitchMax = 0, 0, 0, 0
	local axisLimits = false
	if flags ~= nil then
		-- `limitRelax` 把所有分轴限位按倍数放宽（1.0 = 完全尊重作者）。
		-- 存在的理由：有些资产作者给的 yaw 只有 ±15°，而实测摆动需求能到 ±57°
		-- （lime 的 Side.L/R、Back_S.L/R）——按作者值夹必然是"几乎不动的硬杆"。
		-- 尊重还是放宽是**产品选择**，不是对错，所以做成显式开关，不偷偷改。
		local relax = (opts and tonumber(opts.limitRelax)) or 1
		if relax <= 0 then relax = 1 end
		-- 角 → 分量界限。比的是单位向量在该轴上的分量，所以差值就是 sin。
		-- 与旧实现同一条规则：|θ| ≥ π 视作 1（等于不限），θ = 0 就是 0。
		local function bound(angle)
			if angle == 0 then return 0 end
			if math.abs(angle) >= math.pi then return angle > 0 and 1 or -1 end
			return math.sin(angle)
		end
		if flag_set(flags, M.FLAG_YAW_CONSTRAINED) then
			local lo = (tonumber(params.minYaw) or 0) * relax
			local hi = (tonumber(params.maxYaw) or 0) * relax
			if lo ~= 0 or hi ~= 0 then
				yawMin, yawMax = bound(lo), bound(hi)
				axisLimits = true
			end
		end
		if flag_set(flags, M.FLAG_PITCH_CONSTRAINED) then
			local lo = (tonumber(params.minPitch) or 0) * relax
			local hi = (tonumber(params.maxPitch) or 0) * relax
			if lo ~= 0 or hi ~= 0 then
				pitchMin, pitchMax = bound(lo), bound(hi)
				axisLimits = true
			end
		end
		if axisLimits then
			limitSource = "yawPitchAxes"
		elseif flag_set(flags, M.FLAG_ANGLE_CONSTRAINED) and angleLimit == 0 then
			-- 带 angleCon 但限位写成了 0。严格讲这是"作者要求一点都不许转"，
			-- 但真按 0° 夹会让这根骨**完全冻住**，而"冻住一块布"比"它多晃两下"难看得多，
			-- 也更容易被误判成"物理没生效"。目前**不强制**，只在限位来源统计里标出来
			-- （`coneZero`），真遇到这样的资产再去决定要不要夹。扫过的四份 rig 里没有这种。
			limitSource = "coneZero"
		elseif angleLimit == 0 then
			-- 三种约束都没有 ⇒ 真的不限位。
			limitSource = "none"
		end
	end
	local gains = {
		kYaw = kYaw, cYaw = cYaw,
		kPitch = kPitch, cPitch = cPitch,
		L = length,                                        -- 虚拟杆长（米）
		g = (tonumber(params.tipMass) or 0) * gravityScale, -- 重力加速度幅值（米/秒²）
		limit = angleLimit,
		limitSource = limitSource,
		axisLimits = axisLimits,
		-- 带符号的区间（单位向量在该轴上的分量界限）。对称情况 min = −max，与旧行为等价。
		limitYawMin = yawMin, limitYawMax = yawMax,
		limitPitchMin = pitchMin, limitPitchMax = pitchMax,
		-- 诊断只用：旧的"对称幅值"口径，方便报告里继续显示 ±x°。
		limitYaw = math.max(math.abs(yawMin), math.abs(yawMax)),
		limitPitch = math.max(math.abs(pitchMin), math.abs(pitchMax)),
		-- 总偏角上限（Valve 的 angleCon）：分量是 sin，所以"总偏角的正弦"就是两分量平方和开根。
		sinTotal = angleLimit > 0 and math.sin(angleLimit) or 0,
		-- 预存 cos(limit)：step_tip 判断"是否超限"时只需 dot < cosLimit，
		-- 不用每骨每子步调一次 acos（省一次 C 库调用）。见 step_tip 的说明。
		cosLimit = math.cos(angleLimit),
		omegaYaw = math.sqrt(kYaw), zetaYaw = cYaw / (2 * math.sqrt(kYaw)),
		omegaPitch = math.sqrt(kPitch), zetaPitch = cPitch / (2 * math.sqrt(kPitch)),
	}
	-- ⚠️ **默认不再做频段归一化**（2026-11-03 反转了 2026-11-01 的决定）。
	--
	-- 理由：`yawStiffness` / `yawDamping` 就是作者用来区分"硬铰接 ↔ 软布料"的旋钮，
	-- 换成 ωn = √k 与 ζ = c/(2√k) 之后两族的差别是**量级**级别的（真实数据）：
	--   · 头发族   k=50, c=7  ⇒ ωn=7.07 rad/s, ζ=0.49  （弹、回位快、会回弹）
	--   · 大袖子族 k=1,  c=10 ⇒ ωn=1.00 rad/s, ζ=5.0   （沉、滞、不回弹、被重力主导）
	-- 而旧策略把 ωn 夹进 [4,12]、ζ 夹进 [0.4,1.2]，等于把"很软很滞的布"**改写成**
	-- "中等硬、还有点弹的链" —— 那正是"多截棍感"的一部分来源，也是用户明确要求
	-- "尽量复用来源参数、不要手写参数"要去掉的东西。
	--
	-- 保留 `M.normalize_gains` 与两个频段常量：① 诊断脚本（`rig-gains-report` /
	-- `rig-tools`）要报"哪些骨会被夹"；② 想临时回到旧口径可传 `{ normalize = true }`。
	if opts and opts.normalize == true then
		M.normalize_gains(gains)
	else
		gains.normalized = false
	end
	return gains
end

--- 频段归一化：**保留作者的静垂角与阻尼比**，只把自然频率夹进"人眼当它是弹簧"的频段。
---
--- <para>
--- 为什么需要：源作者的数值是按 Source 的求解器调的。大袖子（`k=1, c=10, tipMass=500`）在那边是
--- **布料**手感；搬到我们的逐节尖端弹簧里，`ωn=√k=1 rad/s`（周期 6.3 秒）、`ζ=5`，
--- 结果就是"跟不上 1 Hz 的胳膊动作，整条链被拖着甩"。它在 L4D2 里没有旋转限制（`angleLimit=360°`），
--- 我们也不该给它加——要改的是刚度，不是限位。
--- </para>
--- <para>
--- 归一化只动 **超出频段** 的骨：头发 `ω=7.07, ζ=0.49` 本来就在频段内，输出与源参数逐位相同
--- （离线实测：末节角度 9–22° → 9–22°、尖端摆幅 0.20 → 0.19 m）。
--- 大袖子则从"40–56° 乱甩、尖端 0.28 m"变成"稳定垂落 7°、尖端 0.05 m"。
--- </para>
M.OMEGA_BAND = { min = 4, max = 12 }
-- 下界取 0.4 而不是 0.5：实测样本（Lunalice/Lilium 头发）ζ = c/(2√k) 落在 0.49 附近，
-- 用 0.5 当边界会把这批本来就正常的骨也推一下（离线可见：尖端摆幅 0.20 → 0.19 m）。
-- 0.4 让它们**逐位不变**，同时仍能把袖子的 ζ=5 拉回来。
M.ZETA_BAND = { min = 0.4, max = 1.2 }

function M.normalize_gains(gains)
	local k, c, g, L = gains.kYaw, gains.cYaw, gains.g, gains.L
	if k == nil or L == nil or L <= 0 then return gains end
	local omega = math.sqrt(math.max(k, 1e-9))
	local zeta = (c or 0) / (2 * omega)
	local newOmega = math.max(M.OMEGA_BAND.min, math.min(M.OMEGA_BAND.max, omega))
	local newZeta = math.max(M.ZETA_BAND.min, math.min(M.ZETA_BAND.max, zeta))
	gains.omegaRaw, gains.zetaRaw = omega, zeta
	if math.abs(newOmega - omega) < 1e-9 and math.abs(newZeta - zeta) < 1e-9 then
		gains.normalized = false
		return gains
	end

	-- 静垂角（作者的观感属性）：θ = atan(g / (k·L))。
	-- ⚠️ **不要**为了让 θ 守恒去放大 g。曾经这么做过（g = k·L·tanθ ⇒ 袖子 g=152 m/s²），
	-- 结果是重力压倒一切：尖端被定长约束死死压在球面最低点，胳膊怎么动它都不动 ——
	-- 实测"首节转角变化 0.0°"，玩家看到的就是一块被夹住的钢片。
	-- 保持作者的 g（袖子 9.5 m/s²），静垂角会从 89° 变成约 72°，但实测**世界空间里依然接近垂直**
	-- （7 节累加），同时首节有 16° 的可见摆动。
	local droop = math.atan((g or 0) / math.max(k * L, 1e-9))
	gains.droopRaw = droop
	local newK = newOmega * newOmega
	gains.kYaw = newK
	gains.cYaw = 2 * newZeta * newOmega
	local omegaPitch = math.sqrt(math.max(gains.kPitch or k, 1e-9))
	local newOmegaPitch = math.max(M.OMEGA_BAND.min, math.min(M.OMEGA_BAND.max, omegaPitch))
	gains.kPitch = newOmegaPitch * newOmegaPitch
	gains.cPitch = 2 * newZeta * newOmegaPitch
	gains.omegaYaw, gains.zetaYaw = newOmega, newZeta
	gains.omegaPitch, gains.zetaPitch = newOmegaPitch, newZeta
	gains.normalized = true
	return gains
end

--- 四元数 → 轴角（轴单位化，角度 0..π）。
--- 取「短弧」表示（先保证 w ≥ 0），否则相邻帧会在 q/−q 之间跳变。
local function q_to_axis_angle(qx, qy, qz, qw)
	if qw < 0 then qx, qy, qz, qw = -qx, -qy, -qz, -qw end
	local s2 = qx * qx + qy * qy + qz * qz
	local len = math.sqrt(s2)
	if len < 1e-9 then return 0, 0, 1, 0 end
	return qx / len, qy / len, qz / len, 2 * math.atan2(len, qw)
end
M._q.to_axis_angle = q_to_axis_angle

--- **曲率扩散**：把"相邻节的额外弯曲"沿链抹平。
--- <para>
--- 为什么非有不可（2026-11-03 实测）：现在每一节都是**独立的弹簧铰链**，相邻两节之间
--- 没有任何东西约束"曲率"。于是弯曲必然塌到某一个关节上，表现为**折线 / 多截棍**：
--- <list type="bullet">
--- <item>旗袍 chiffon 的麻花辫 `pd.021`（4 节、无重力、ζ≈0.49）：相邻节夹角
--- <b>9.4° / 42.2° / 9.6°</b> —— 整条链的弯曲全集中在第 3 个关节，前后两节几乎不弯。</item>
--- <item>Lilium 的大袖子（7 节、`g/(k·L)≥1`）：每节都被重力钉到自己球心正下方，
--- 7 节累加 630°，作者的形状被整个拽走。</item>
--- </list>
--- 真实头发/布料是一根**弹性杆**：曲率沿长度铺开。这里做的就是那件事 ——
--- 把每节的"额外弯曲"（相对 bind 的相对旋转）取出来，沿链做一次 1-2-1 加权平滑，
--- 再重建各节方向。
--- </para>
--- <para>
--- <b>对正常模型零影响</b>：额外弯曲全为 0（= 形状等于 bind 形状）时，平滑后仍是 0，
--- 重建出的方向与输入逐位相同；`blend = 0` 时直接返回。
--- </para>
---@param item table 需要 `dirs` / `restDir` / `segDirLocal` / `bones` / `tips`
---@param blend number 0..1，0 = 关闭（逐位不变），1 = 完全取邻居平均
---@param budget number|nil 整条链"额外弯曲总量"的弧度上限；超了就整体等比缩回（nil = 不限）
function M.relax_bending(item, blend, budget)
	local dirs = item.dirs
	local rest = item.restDir
	local count = #dirs
	if blend == nil or blend <= 0 or rest == nil then return false end
	if count < 3 then return false end     -- 两节只有一个关节，没有"曲率分布"可言

	-- ① 每节的额外弯曲，表达成**轴 × 角**的世界矢量（相邻节的轴基本同向，可以线性平均）
	local extra = item.bendExtra
	if extra == nil then extra = {}; item.bendExtra = extra end
	local smoothed = item.bendSmooth
	if smoothed == nil then smoothed = {}; item.bendSmooth = smoothed end

	local total = 0
	for index = 2, count do
		local sr, sc = rest[index - 1], rest[index]
		local ba, bb, bc, bAngle = swing_axis(sr[1], sr[2], sr[3], sc[1], sc[2], sc[3])
		local da, db, dc = dirs[index - 1], dirs[index]
		local ca, cb, cc, cAngle = swing_axis(da[1], da[2], da[3], db[1], db[2], db[3])

		local ex, ey, ez = 0, 0, 0
		if cAngle ~= nil then
			-- q_extra = q_cur · conj(q_bind)
			local curX, curY, curZ, curW = q_axis_angle(ca, cb, cc, cAngle)
			local bindX, bindY, bindZ, bindW = 0, 0, 0, 1
			if bAngle ~= nil then bindX, bindY, bindZ, bindW = q_axis_angle(ba, bb, bc, bAngle) end
			local qx, qy, qz, qw = qmul(curX, curY, curZ, curW, -bindX, -bindY, -bindZ, bindW)
			local ax, ay, az, angle = q_to_axis_angle(qx, qy, qz, qw)
			ex, ey, ez = ax * angle, ay * angle, az * angle
		end
		local slot = extra[index]
		if slot == nil then slot = {}; extra[index] = slot end
		slot[1], slot[2], slot[3] = ex, ey, ez
		total = total + math.sqrt(ex * ex + ey * ey + ez * ez)
	end

	-- ② 整链预算：弯曲总量超过预算就整体等比缩回。
	--    这一条治的是"7 节 × 每节都朝同一侧弯"累加成螺旋 —— 那种链在画面上会卷回去。
	local scale = 1
	if budget ~= nil and budget > 0 and total > budget then scale = budget / total end

	-- ③ 1-2-1 平滑（端点不动），再按 blend 与原值插值
	for index = 2, count do
		local cur = extra[index]
		local prev = extra[index - 1] or cur
		local next = extra[index + 1] or cur
		local avgX = (prev[1] + 2 * cur[1] + next[1]) * 0.25
		local avgY = (prev[2] + 2 * cur[2] + next[2]) * 0.25
		local avgZ = (prev[3] + 2 * cur[3] + next[3]) * 0.25
		local slot = smoothed[index]
		if slot == nil then slot = {}; smoothed[index] = slot end
		slot[1] = (cur[1] + (avgX - cur[1]) * blend) * scale
		slot[2] = (cur[2] + (avgY - cur[2]) * blend) * scale
		slot[3] = (cur[3] + (avgZ - cur[3]) * blend) * scale
	end

	-- ④ 从链首往后重建方向：dir_k = dir_{k-1} 依次转 q_bind · q_extra
	for index = 2, count do
		local sr, sc = rest[index - 1], rest[index]
		local ba, bb, bc, bAngle = swing_axis(sr[1], sr[2], sr[3], sc[1], sc[2], sc[3])
		local bindX, bindY, bindZ, bindW = 0, 0, 0, 1
		if bAngle ~= nil then bindX, bindY, bindZ, bindW = q_axis_angle(ba, bb, bc, bAngle) end
		local smooth = smoothed[index]
		local sx, sy, sz = smooth[1], smooth[2], smooth[3]
		local sa = math.sqrt(sx * sx + sy * sy + sz * sz)
		local exX, exY, exZ, exW = 0, 0, 0, 1
		if sa > 1e-12 then exX, exY, exZ, exW = q_axis_angle(sx / sa, sy / sa, sz / sa, sa) end
		local qx, qy, qz, qw = qmul(bindX, bindY, bindZ, bindW, exX, exY, exZ, exW)
		local d = dirs[index - 1]
		local nx, ny, nz = qrot(qx, qy, qz, qw, d[1], d[2], d[3])
		local len = math.sqrt(nx * nx + ny * ny + nz * nz)
		if len > 1e-9 then
			local slot = dirs[index]
			slot[1], slot[2], slot[3] = nx / len, ny / len, nz / len
		end
	end
	return true
end

-- ---------------------------------------------------------------------------
-- ---------------------------------------------------------------------------

--- 每帧一次：算出「静止方向」（bind 方向在当前锚点朝向下的样子）与「相邻节之间的绑定相对旋转」。
--- 只算一次，子步里复用；`step_tip` 与 `local_rotations` 都吃这一份，判据才一致。
--- <para>
--- 为什么这里就是"头发不自然下垂 / 朝错误方向下垂"的修复点：
--- 旧实现里本节的"父段前向"是直接抄上一节的**模拟**方向，于是链的零力平衡态变成一条**直线**，
--- 作者在 bind 姿态里做出的下垂被当成"要纠正的偏差"。正确关系是：
--- 本节目标 = 上一节的模拟方向 **经「上一节 → 本节」的绑定相对旋转**转过去 ——
--- 这样 `dirs[k-1] == rest[k-1]` 时目标正好等于 `rest[k]`，bind 形状就是平衡形状。
--- </para>
local function ensure_rest_dirs(item, qx, qy, qz, qw)
	local gqx, gqy, gqz, gqw = qx, qy, qz, qw
	-- 世界朝向锚定（`item.goalQ`）：目标朝向用**绑定姿态**的锚点旋转来算，而不是当前动画旋转。
	-- 效果：父级（上臂）怎么转，只带动这条链**平移**，不改变它的朝向 —— 布料感的关键。
	-- 对应源数据里大袖子的 yaw/pitch 约束 flag（0x3d），头发（0x31）不启用。
	if item.goalQ ~= nil then
		gqx, gqy, gqz, gqw = item.goalQ[1], item.goalQ[2], item.goalQ[3], item.goalQ[4]
	end
	local count = #item.bones
	local rest = item.restDir
	if rest == nil then rest = {}; item.restDir = rest end
	local rel = item.relDev
	if rel == nil then rel = {}; item.relDev = rel end
	for index = 1, count do
		local s = item.segDirLocal[index]
		local slot = rest[index]
		if slot == nil then slot = {}; rest[index] = slot end
		slot[1], slot[2], slot[3] = qrot(gqx, gqy, gqz, gqw, s[1], s[2], s[3])
		rel[index] = nil
	end
	for index = 2, count do
		local a, b = rest[index - 1], rest[index]
		local ax, ay, az, angle = swing_axis(a[1], a[2], a[3], b[1], b[2], b[3])
		if ax ~= nil then rel[index] = { ax, ay, az, angle } end
	end
	item.restQx, item.restQy, item.restQz, item.restQw = qx, qy, qz, qw
end
M._ensure_rest_dirs = ensure_rest_dirs

--- 一步积分（纯数字）。anchor 位姿来自动画侧（我们从不写锚点，所以没有反馈）。
--- item 需要：bones[k].gains / .offLocal（锚点局部系里的 bind 偏移）/ .frameLocal（left/up）、
---            item.segDirLocal[k]（bind 时该节在锚点局部系里的方向）、item.tips[k]、item.dirs。
function M.step_tip(item, dt, apx, apy, apz, qx, qy, qz, qw)
	local bones, tips = item.bones, item.tips
	local prevX, prevY, prevZ
	-- 局部化标准库函数：PUC Lua 5.1 不会自动消掉重复的全局→字段查找。
	local sqrt, acos = math.sqrt, math.acos

	-- ── 每帧预算：静止方向 + 相邻节的"绑定相对旋转" ──────────────────────
	-- ⚠️ 这是**修"头发不自然下垂 / 朝错误方向下垂"的那一处**。
	--
	-- 旧实现的 `prev`（本节的"父段前向"）对 k>1 直接抄上一节的**模拟**方向：
	--     prev_k = dirs[k-1]
	-- 于是链的**零力平衡态是一条直线**，而不是作者在 bind 姿态里做出来的弯曲形状。
	-- 更糟的是首帧 `tip.ready == false` 直接把尖端放到这个目标上 ⇒ **第一帧就把整条链掰直**，
	-- 每节在 bind 里的 20~26° 静态弯曲被当成"需要被弹簧纠正掉的偏差"。
	-- 用户看到的"头发不会自然下垂、反而指向一个错误的方向"就是它。
	--
	-- 正确关系（与 Valve 的 `jigglebones.cpp` 同构）：本节的目标方向 =
	--   **上一节的模拟方向** 经「上一节 → 本节」的**绑定相对旋转**转过去。
	-- 这样 dirs[k-1] == rest[k-1] 时 prev_k 正好等于 rest[k] ⇒ bind 形状就是平衡形状，
	-- 而父级真的摆动起来时，偏差照常沿链传下去（"链式弯曲"仍然在）。
	--
	-- 成本控制：q 在一帧内不变（update_tip 用同一个锚点跑多个子步），
	-- 所以静止方向与相对旋转**每帧只算一次**，子步里只剩 1 次 axis_angle + 1 次 qrot。
	if item.restQx ~= qx or item.restQy ~= qy or item.restQz ~= qz or item.restQw ~= qw then
		ensure_rest_dirs(item, qx, qy, qz, qw)
	end
	local restDirs, relDevs = item.restDir, item.relDev

	for k = 1, #bones do
		local bone, tip, gains = bones[k], tips[k], bones[k].gains
		-- 该节的基座：**动画基座** —— 只由锚点位姿决定，不含我们写出的姿态（⇒ 不会自激）。
		--
		-- 试过两种"串行基座"（基座取上一节算出来的位置），都**不要**：
		--   ① 基座 = 上一节模拟尖端（杆长 L）⇒ 链被拉长到 7×0.57 m，
		--      最大折返幅度从 14.7~34.0° 恶化到 69~96°、首尾夹角 60°→114°；
		--   ② 基座 = 上一节基座 + 真实骨距 ⇒ 折返占比反而更高（见 zigzag-lab3 的 C 列）。
		-- 结论：折线不是基座模型造成的，别在这条路上再试。
		local bx, by, bz
		do
			local ox, oy, oz = qrot(qx, qy, qz, qw, bone.offLocal[1], bone.offLocal[2], bone.offLocal[3])
			bx, by, bz = apx + ox, apy + oy, apz + oz
		end
		-- 父段前向 = 本节**静止方向**，再叠加上一节的实际偏差（见上面每帧预算的说明）。
		-- 路径首节（链根，或分叉节点下的分支起点）没有"同一路径里的上一节"可用
		-- ⇒ 直接用静止方向，不做跨路径耦合（宁可分叉处不传递弯曲，也不造假折角）。
		--
		-- ⚠️ `relDevs[k] == nil` 有**两种完全不同的含义**，必须靠 `k` 区分：
		--   ① k>1 且 rel 为 nil ⇒ 相邻两节的静止方向**相同**，相对旋转是**单位阵**
		--      ⇒ prev 必须是 `dirs[k-1]` 原样透传，**不能**退回静止方向 ——
		--      否则父节的偏差传不下来，多节链就变成"只有第一节会动"（离线实测：
		--      各节转角 7.66° / 0.00° / 0.00°）。
		--   ② k==1 ⇒ 真的没有父节。
		local rest, rel = restDirs[k], relDevs[k]
		if k == 1 then
			prevX, prevY, prevZ = rest[1], rest[2], rest[3]
		elseif rel == nil then
			local d = item.dirs[k - 1]
			prevX, prevY, prevZ = d[1], d[2], d[3]
		else
			local rqx, rqy, rqz, rqw = q_axis_angle(rel[1], rel[2], rel[3], rel[4])
			local d = item.dirs[k - 1]
			prevX, prevY, prevZ = qrot(rqx, rqy, rqz, rqw, d[1], d[2], d[3])
		end
		-- 分解轴（把 bind 时的正交基旋到当前朝向）
		local lx, ly, lz = qrot(qx, qy, qz, qw, bone.frameLocal[1], bone.frameLocal[2], bone.frameLocal[3])
		local ux, uy, uz = qrot(qx, qy, qz, qw, bone.frameLocal[4], bone.frameLocal[5], bone.frameLocal[6])
		local gx = bx + gains.L * prevX
		local gy = by + gains.L * prevY
		local gz = bz + gains.L * prevZ
		if not tip.ready then
			tip.px, tip.py, tip.pz = gx, gy, gz
			tip.vx, tip.vy, tip.vz = 0, 0, 0
			tip.ready = true
		end
		local ex, ey, ez = gx - tip.px, gy - tip.py, gz - tip.pz
		local errYaw, errPitch = lx * ex + ly * ey + lz * ez, ux * ex + uy * ey + uz * ez
		-- ⚠️ Valve 用的是**绝对** tipVel（不是相对目标速度）——匀速走动时的持续滞后正是它给的
		local velYaw = lx * tip.vx + ly * tip.vy + lz * tip.vz
		local velPitch = ux * tip.vx + uy * tip.vy + uz * tip.vz
		local accYaw = gains.kYaw * errYaw - gains.cYaw * velYaw
		local accPitch = gains.kPitch * errPitch - gains.cPitch * velPitch
		-- 重力：上的反面（一致性电池 E1 实测世界 +Z 朝上）
		local ax = accYaw * lx + accPitch * ux
		local ay = accYaw * ly + accPitch * uy
		local az = accYaw * lz + accPitch * uz - gains.g
		tip.vx, tip.vy, tip.vz = tip.vx + ax * dt, tip.vy + ay * dt, tip.vz + az * dt
		tip.px, tip.py, tip.pz = tip.px + tip.vx * dt, tip.py + tip.vy * dt, tip.pz + tip.vz * dt
		-- 定长约束（样本 flags=0x31 含 lenC ⇒ 尖端钉在半径 L 的球面上，且没有 along 弹簧）
		local dx, dy, dz = tip.px - bx, tip.py - by, tip.pz - bz
		local length = math.sqrt(dx * dx + dy * dy + dz * dz)
		if length < 1e-6 then
			dx, dy, dz, length = prevX, prevY, prevZ, 1
		end
		local nx, ny, nz = dx / length, dy / length, dz / length
		if gains.axisLimits then
			-- 分轴限位（源骨 `yawCon`/`pitchCon`）：夹的是**这一节相对自己静止姿态**的偏转。
			--
			-- ⚠️ 为什么不能沿用下面那个锥（那是相对**父节**的）：锥会沿着链累加。
			-- 实测一份 3 节侧发（每节 30/20/20°），从下垂 18° 起能累到 70°，足够让它翘到水平以上。
			-- Valve 的 min/max yaw/pitch 是每根骨相对**自身静止姿态**的范围，不累加。
			--
			-- ⚠️⚠️ 三个轴必须取**同一个参考系**。这里曾经出过一次很严重的错：
			-- 绑定方向用 `goalQ`（绑定时锚点朝向）旋转，而 left/up 用**当前** `q` 旋转 ——
			-- 两个参考系一错开，`l·n` / `u·n` 就不再是"偏离多少"，而是一个随机数，
			-- 于是每帧都在被夹，角色一动（身体转向）头发就被钉在某个方向上：
			-- 实机表现是"跑起来头发向前翘起、特别僵硬"。
			-- 现在统一用静止参考系，而且**在 attach 时就算好**（goalQ 恒定）⇒ 每帧零成本。
			local lf = bone.limitFrame
			local blx, bly, blz, bux, buy, buz, bbx, bby, bbz
			if lf ~= nil then
				blx, bly, blz = lf[1], lf[2], lf[3]
				bux, buy, buz = lf[4], lf[5], lf[6]
				bbx, bby, bbz = lf[7], lf[8], lf[9]
			else
				-- 没有 goalQ（父级朝向会带着走）的链：静止参考系就是当前锚点朝向，逐帧算。
				local b = item.segDirLocal[k]
				local f = bone.frameLocal
				blx, bly, blz = qrot(qx, qy, qz, qw, f[1], f[2], f[3])
				bux, buy, buz = qrot(qx, qy, qz, qw, f[4], f[5], f[6])
				bbx, bby, bbz = qrot(qx, qy, qz, qw, b[1], b[2], b[3])
			end
			local devYaw = blx * nx + bly * ny + blz * nz
			local devPitch = bux * nx + buy * ny + buz * nz
			local overYaw, overPitch = 0, 0     -- 0 未超限；±1 = 从哪一侧超的
			-- 带符号区间：作者写 [0, +10°] 就是"只许往外"，不能再被对称成 ±10°。
			if devYaw > gains.limitYawMax then devYaw, overYaw = gains.limitYawMax, 1
			elseif devYaw < gains.limitYawMin then devYaw, overYaw = gains.limitYawMin, -1 end
			if devPitch > gains.limitPitchMax then devPitch, overPitch = gains.limitPitchMax, 1
			elseif devPitch < gains.limitPitchMin then devPitch, overPitch = gains.limitPitchMin, -1 end
			-- 总偏角（Valve 的 angleCon）：与分轴**叠加**，不是二选一。
			-- 两个分量正交，所以"总偏角的正弦"就是 √(y²+p²)；超过 sin(angleLimit) 就按比例收回来。
			if gains.sinTotal > 0 then
				local magnitude = math.sqrt((devYaw * devYaw) + (devPitch * devPitch))
				if magnitude > gains.sinTotal then
					local scale = gains.sinTotal / magnitude
					devYaw, devPitch = devYaw * scale, devPitch * scale
					if overYaw == 0 then overYaw = 1 end
					if overPitch == 0 then overPitch = 1 end
				end
			end
			if overYaw ~= 0 or overPitch ~= 0 then
				-- 用"静止方向 + 两个轴上的分量"重建，再**软一点**回夹。
				--
				-- ⚠️ 不要硬吸附到位（`n = normalize(clamped)`）。硬吸附有两个可看出来的后果：
				--   ① 撞墙那一下会把速度留在墙外，下一次释放时被甩出去 ⇒ 尖端"卡一下再弹开"；
				--   ② 3 节链每节各自贴在自己的边界上，形状变成一个折角点，看着"不自然弯折"。
				-- 做法：位置只往边界靠 kSoft（保留一点弹性余量），同时把**朝墙外**的速度分量去掉
				-- （非弹性约束）。这样贴着边界滑动是连续的，既不抖也不折。
				local vx = bbx + devYaw * blx + devPitch * bux
				local vy = bby + devYaw * bly + devPitch * buy
				local vz = bbz + devYaw * blz + devPitch * buz
				local vlen = math.sqrt(vx * vx + vy * vy + vz * vz)
				if vlen > 1e-9 then
					vx, vy, vz = vx / vlen, vy / vlen, vz / vlen
					local soft = bone.limitSoft or 0.4
					local mixX = nx + (vx - nx) * soft
					local mixY = ny + (vy - ny) * soft
					local mixZ = nz + (vz - nz) * soft
					local mlen = math.sqrt(mixX * mixX + mixY * mixY + mixZ * mixZ)
					if mlen > 1e-9 then
						nx, ny, nz = mixX / mlen, mixY / mlen, mixZ / mlen
					end
					-- 去掉朝墙外的那一份速度：否则它会一直堆着，等约束不再生效时一次性甩出来。
					local vyaw = tip.vx * blx + tip.vy * bly + tip.vz * blz
					if overYaw > 0 and vyaw > 0 then
						tip.vx, tip.vy, tip.vz = tip.vx - vyaw * blx, tip.vy - vyaw * bly, tip.vz - vyaw * blz
					elseif overYaw < 0 and vyaw < 0 then
						tip.vx, tip.vy, tip.vz = tip.vx - vyaw * blx, tip.vy - vyaw * bly, tip.vz - vyaw * blz
					end
					local vpitch = tip.vx * bux + tip.vy * buy + tip.vz * buz
					if overPitch > 0 and vpitch > 0 then
						tip.vx, tip.vy, tip.vz = tip.vx - vpitch * bux, tip.vy - vpitch * buy, tip.vz - vpitch * buz
					elseif overPitch < 0 and vpitch < 0 then
						tip.vx, tip.vy, tip.vz = tip.vx - vpitch * bux, tip.vy - vpitch * buy, tip.vz - vpitch * buz
					end
				end
			end
		end
		-- 角度限位：与该节动画方向的夹角不超过 limit（相对自己的 goal，不是相对世界）
		--
		-- ⚠️ 不要为了"判断是否超限"去算 acos：acos 是 C 库调用，而这里只需要比较大小。
		-- 预存 cosLimit = cos(limit)，判据变成 dot < cosLimit（dot 是单位向量点积 = cos 夹角）。
		-- 只有**真的超限**时才需要角度值（而且下面用的是 limit 本身，连 acos 都不用）。
		--
		-- 分轴约束的骨**不走这条**：锥是相对父节的，会累加（见上面 axisLimits 的说明）。
		local dot = nx * prevX + ny * prevY + nz * prevZ
		if not gains.axisLimits and gains.limit > 0 and dot < gains.cosLimit then
			local sx, sy, sz, _ = swing_axis(prevX, prevY, prevZ, nx, ny, nz)
			if sx ~= nil then
				local rqx, rqy, rqz, rqw = q_axis_angle(sx, sy, sz, gains.limit)
				local obx, oby, obz = nx, ny, nz
				nx, ny, nz = qrot(rqx, rqy, rqz, rqw, prevX, prevY, prevZ)
				-- 同上：把**朝墙外**的那份速度去掉。否则它会一直堆在墙外，
				-- 等约束不再生效时一次性甩出来 —— 实机上就是"卡一下再弹开"。
				local anx = obx - prevX * dot
				local any = oby - prevY * dot
				local anz = obz - prevZ * dot
				local alen = math.sqrt(anx * anx + any * any + anz * anz)
				if alen > 1e-9 then
					anx, any, anz = anx / alen, any / alen, anz / alen
					local outward = tip.vx * anx + tip.vy * any + tip.vz * anz
					if outward > 0 then
						tip.vx, tip.vy, tip.vz =
							tip.vx - outward * anx, tip.vy - outward * any, tip.vz - outward * anz
					end
				end
			else
				nx, ny, nz = prevX, prevY, prevZ
			end
		end
		tip.px, tip.py, tip.pz = bx + gains.L * nx, by + gains.L * ny, bz + gains.L * nz
		local radial = tip.vx * nx + tip.vy * ny + tip.vz * nz
		tip.vx, tip.vy, tip.vz = tip.vx - radial * nx, tip.vy - radial * ny, tip.vz - radial * nz
		-- 记录本帧最大尖端速度**的平方**：消费方只做阈值比较（< 1e-8），
		-- 所以这里不必开平方（每骨每子步省一次 sqrt）。
		-- ⚠️ 累加到 **item**（链容器）而不是 tip（本节）：update_tip 读的是 item.tip.maxSpeed2。
		-- 写到 tips[k] 上会让"尖端是否静止"恒为真（永远是 nil/0），运动门限就只剩锚点判据，
		-- 结果是还在摆动的链被冻结（2026-11-01 抓到）。
		local speed2 = tip.vx * tip.vx + tip.vy * tip.vy + tip.vz * tip.vz
		if speed2 > (item.maxSpeed2 or 0) then item.maxSpeed2 = speed2 end
		-- 复用缓冲，不每帧新建 table：375 fps × 每链每节一个 = 每秒几万个短命表，
		-- 光 GC 就能吃掉一截帧时间（性能优化 2026-11-01）。
		local slot = item.dirs[k]
		if slot == nil then slot = {}; item.dirs[k] = slot end
		slot[1], slot[2], slot[3] = nx, ny, nz
	end

	-- 曲率扩散：把"额外弯曲"沿链抹平，免得弯曲塌到某一个关节上（折线 / 多截棍）。
	-- 默认关闭（`bendBlend` 为 nil）⇒ 现有行为逐位不变；见 M.relax_bending 的说明。
	if item.bendBlend ~= nil and item.bendBlend > 0 then
		M.relax_bending(item, item.bendBlend, item.bendBudget)
	end
end

--- 由"每节世界方向"反算**每节的 local 旋转**（父→子，累积）。
--- <para>
--- 推导：记锚点的世界旋转为 q，第 k 节的世界旋转为 `acc_k·…`。
--- 我们要的是"净旋转把本节的 bind 方向转到期望方向"，即
---   `acc_{k-1} · S_k · segLocal_k = q⁻¹ · dir_k`
--- ⇒ `S_k = swing(segLocal_k → acc_{k-1}⁻¹ · (q⁻¹ · dir_k))`，然后在父空间里累积 `acc_k = acc_{k-1}·S_k`。
--- ⚠️ 这里的顺序很容易写错：**不能**拿 `acc_{k-1}·segLocal_k` 去和 `q⁻¹·dir_k` 求增量 ——
--- 那是把"已经转过的方向"当起点，累下来的总弯曲会偏大（写错过一次，靠
--- `p2-solver-test.lua` 的"净旋转必须复现目标方向"断言抓出来）。
--- </para>
function M.local_rotations(item, qx, qy, qz, qw)
	local cqx, cqy, cqz, cqw = qconj(qx, qy, qz, qw)
	local ax, ay, az, aw = 0, 0, 0, 1
	-- 复用输出缓冲（见 step_tip 里的说明）：返回的是**同一个**表，调用方只能当帧用。
	local out = item.rotBuf
	if out == nil then out = {}; item.rotBuf = out end
	for k = 1, #item.bones do
		local dir = item.dirs[k] or item.segDirLocal[k]
		local dlx, dly, dlz = qrot(cqx, cqy, cqz, cqw, dir[1], dir[2], dir[3])
		-- 把目标方向换到**父空间**：acc_{k-1}⁻¹ · dLocal
		local ax2, ay2, az2, aw2 = qconj(ax, ay, az, aw)
		local tlx, tly, tlz = qrot(ax2, ay2, az2, aw2, dlx, dly, dlz)
		local seg = item.segDirLocal[k]
		local sx, sy, sz, angle = swing_axis(seg[1], seg[2], seg[3], tlx, tly, tlz)
		if sx == nil then sx, sy, sz, angle = 0, 0, 1, 0 end
		local slot = out[k]
		if slot == nil then slot = {}; out[k] = slot end
		slot[1], slot[2], slot[3], slot[4] = sx, sy, sz, angle
		local qx3, qy3, qz3, qw3 = q_axis_angle(sx, sy, sz, angle)
		ax, ay, az, aw = qmul(ax, ay, az, aw, qx3, qy3, qz3, qw3)
	end
	local acc = item.accQBuf
	if acc == nil then acc = {}; item.accQBuf = acc end
	acc[1], acc[2], acc[3], acc[4] = ax, ay, az, aw
	item.accQ = acc
	return out
end

-- ---------------------------------------------------------------------------
-- 旧核（角度振子）保留**作为对照基线**：仍可用 `options.solver = "angle"` 切换，
-- 方便在游戏里 A/B 看"哪一种更像头发"。它不是默认路径。
-- ---------------------------------------------------------------------------

-- L4D2 的那 29 个参数是**游戏调校增益**，不是 SI 单位；求解器闭源。
-- 这里用标准阻尼受迫振子，把它们映射成 ωn 与 ζ——这两个量决定"软硬"。
-- 末端质量的归一化基准是唯一的自由常数，取 300（校准到"不撞限位、观感合理"）。
local MASS_BASE = 300.0

-- Source 的长度单位是 1 单位 ≈ 0.75 英寸 ≈ 1.905 cm。
-- rig 里的段长是**源单位**，而运行时拿到的加速度是 **HD2 的米/秒²**。
-- 不换算就直接相除，drive 会小约 50 倍，弹簧几乎不动（实测只摆动 0.1°）。
-- （常量在上面的 P2 段落里已声明，这里不再重复声明以免遮蔽。）

function M.derive_gains(params)
	local stiffness = math.max(params.yawStiffness or 50, 1)
	local damping = params.yawDamping or 7
	local mass = math.max((params.tipMass or 200) / MASS_BASE, 0.05)
	local omega = math.sqrt(stiffness / mass)
	local zeta = damping / (2 * math.sqrt(stiffness * mass))
	if zeta < 0.05 then zeta = 0.05 elseif zeta > 1.5 then zeta = 1.5 end
	return omega, zeta
end

--- 给每根骨算出**真实段长**。
--- rig 里存的是世界 bind 位置（用来把新骨放到同一处），不是相对父骨的偏移，
--- 所以段长要自己求：优先用「到父骨世界位置的距离」，父骨不在 rig 里就退到
--- 「到第一个子骨的距离」，再不行给一个温和的默认值。
--- 段长决定加速度换算成角加速度的比例，用错会让弹簧明显偏硬或偏软。
function M.apply_world_segments(bones)
	local byIndex = {}
	for _, bone in ipairs(bones) do byIndex[bone.index] = bone end

	local function distance(a, b)
		local dx, dy, dz = a.pos[1] - b.pos[1], a.pos[2] - b.pos[2], a.pos[3] - b.pos[3]
		return math.sqrt(dx * dx + dy * dy + dz * dz)
	end

	for _, bone in ipairs(bones) do
		local length = nil
		local parent = byIndex[bone.parent]
		if parent ~= nil then
			length = distance(bone, parent)
		else
			for _, other in ipairs(bones) do
				if other.parent == bone.index then
					length = distance(bone, other)
					break
				end
			end
		end
		if length == nil or length < 0.01 then length = 10.0 end
		bone.segmentLength = length
	end
	return bones
end

--- 一根骨的弹簧状态。
---@param segmentUnitScale number 段长（rig 里的长度单位）→ 米 的比例。默认按源单位换算。
---  为什么做成参数：rig 有两种来源。从源模型（L4D2）导出的 rig 里长度是**源单位**，
---  而从 Unit 的 TransformInfo 反推出来的链位置是**米**。硬编码换算会把后者算小 52 倍，
---  弹簧会硬得像焊死（等同于"头发像钢板"）。
function M.new_spring(bone, segmentUnitScale)
	segmentUnitScale = segmentUnitScale or SOURCE_UNIT_TO_METERS
	local omega, zeta = M.derive_gains(bone.params)
	local dx, dy, dz = bone.pos[1], bone.pos[2], bone.pos[3]
	return {
		bone = bone,
		omega = omega,
		zeta = zeta,
		limit = math.abs(bone.params.angleLimit or 0.6),
	-- 段长用**米**：后面要拿它去除米/秒² 的加速度。
	-- 优先用源模型的**虚拟杆长**（length 参数），源模型 length=30（≈0.57 m）远大于
	-- 真实骨距（≈0.14 m）——作者刻意拉长，为的就是软、甩得开。
	--
	-- ⚠️ 单位换算必须同时作用在**两条来源**上。旧写法是
	-- `(params.length) or (segmentLength) * scale`，因为 `or` 的优先级更高，
	-- 只要 params.length 存在，scale 就被整个绕过 —— 实测 segmentLength=30 而不是 0.5715，
	-- 驱动小了 52 倍（2026-10-31 离线复现）。现在整个表达式一起乘。
	segmentLength = math.max(
		((bone.params and bone.params.length) or
			(bone.segmentLength or math.sqrt(dx * dx + dy * dy + dz * dz))) * segmentUnitScale,
		1e-3),
		yaw = 0, pitch = 0,
		yawVelocity = 0, pitchVelocity = 0,
	}
end

--- 一步积分。accel 是该骨枢轴在父空间里的加速度（三个分量）。
function M.step_spring(spring, dt, accelX, accelY, accelZ)
	local axes = { accelY, accelZ }        -- 骨轴是局部 +X，垂直方向取 Y/Z
	local state = { spring.yaw, spring.pitch }
	local velocity = { spring.yawVelocity, spring.pitchVelocity }

	for axis = 1, 2 do
		local drive = axes[axis] / spring.segmentLength
		local accel = -spring.omega * spring.omega * state[axis]
			- 2 * spring.zeta * spring.omega * velocity[axis]
			+ drive
		velocity[axis] = velocity[axis] + accel * dt
		state[axis] = state[axis] + velocity[axis] * dt
		local limit = spring.limit
		if limit > 0 and math.abs(state[axis]) > limit then
			state[axis] = (state[axis] > 0) and limit or -limit
			velocity[axis] = velocity[axis] * -0.25   -- 触顶时保留一点回弹，避免"贴死在极限"
		end
	end

	-- 最后一道闸：任何非有限值都复位。
	-- 宁可这一帧"回到原位"，也不能让 NaN 写进节点矩阵——那会让网格消失且不可恢复。
	local function sane(value)
		if type(value) ~= "number" then return 0 end
		if value ~= value or value == math.huge or value == -math.huge then return 0 end
		return value
	end
	spring.yaw, spring.pitch = sane(state[1]), sane(state[2])
	spring.yawVelocity, spring.pitchVelocity = sane(velocity[1]), sane(velocity[2])
	if spring.limit > 0 then
		if spring.yaw > spring.limit then spring.yaw = spring.limit end
		if spring.yaw < -spring.limit then spring.yaw = -spring.limit end
		if spring.pitch > spring.limit then spring.pitch = spring.limit end
		if spring.pitch < -spring.limit then spring.pitch = -spring.limit end
	end
end
M.MASS_BASE = MASS_BASE

-- ---------------------------------------------------------------------------
-- 运行时：挂到 Unit 上并每帧驱动
-- ---------------------------------------------------------------------------

-- 源骨名 → HD2 骨名。只覆盖"链根挂在哪"这个用途——链内部的父子关系不需要映射，
-- 因为链上每一节都是我们自己新插的节点。
-- 这几个对应关系与仓库里的 hd2_style.py 一致（Spine→spine1、Spine4→chest、Head1→head）。
M.default_anchor_map = {
	["ValveBiped.Bip01_Head1"] = "head",
	["Bip01_Head1"] = "head",
	["Head1"] = "head",
	["ValveBiped.Bip01_Spine4"] = "chest",
	["Spine4"] = "chest",
	["ValveBiped.Bip01_Spine2"] = "spine2",
	["Spine2"] = "spine2",
	["ValveBiped.Bip01_Spine1"] = "spine1",
	["Spine1"] = "spine1",
	["ValveBiped.Bip01_Spine"] = "spine1",
	["Spine"] = "spine1",
	["ValveBiped.Bip01_Pelvis"] = "hips",
	["Pelvis"] = "hips",
	["ValveBiped.Bip01_L_Calf"] = "l_knee",
	["L_Calf"] = "l_knee",
	["ValveBiped.Bip01_R_Calf"] = "r_knee",
	["R_Calf"] = "r_knee",
	-- 手臂/腿/手：Lilium 的缎带骨锚在 UpperArm 上，缺这几条会被记「缺锚点」而整链跳过。
	-- 对应关系与 HD2SourceMigration/SourceToHd2RigMap.cs 完全一致（L_UpperArm→l_shoulder，
	-- **不是** l_clavicle —— 源侧锁骨骨太短）。
	["ValveBiped.Bip01_L_UpperArm"] = "l_shoulder",
	["L_UpperArm"] = "l_shoulder",
	["ValveBiped.Bip01_R_UpperArm"] = "r_shoulder",
	["R_UpperArm"] = "r_shoulder",
	["ValveBiped.Bip01_L_Forearm"] = "l_elbow",
	["L_Forearm"] = "l_elbow",
	["ValveBiped.Bip01_R_Forearm"] = "r_elbow",
	["R_Forearm"] = "r_elbow",
	["ValveBiped.Bip01_L_Hand"] = "l_hand",
	["L_Hand"] = "l_hand",
	["ValveBiped.Bip01_R_Hand"] = "r_hand",
	["R_Hand"] = "r_hand",
	["ValveBiped.Bip01_L_Thigh"] = "l_thigh",
	["L_Thigh"] = "l_thigh",
	["ValveBiped.Bip01_R_Thigh"] = "r_thigh",
	["R_Thigh"] = "r_thigh",
	["ValveBiped.Bip01_L_Foot"] = "l_foot",
	["L_Foot"] = "l_foot",
	["ValveBiped.Bip01_R_Foot"] = "r_foot",
	["R_Foot"] = "r_foot",
	["ValveBiped.Bip01_Neck1"] = "neck",
	["Neck1"] = "neck",
}

--- 从源骨名推出应有的 HD2 锚点骨名。
--- <para>
--- 兼容：占位骨名可能带命名空间前缀（<c>j&lt;12hex&gt;/source_extra_72</c>）。
--- 映射表里只有"有真名的源骨"，所以带前缀的先按原样查一次，查不到再拆掉前缀查。
--- </para>
function M.anchor_name(sourceParentName, anchorMap)
	anchorMap = anchorMap or M.default_anchor_map
	local mapped = anchorMap[sourceParentName]
	if mapped ~= nil then return mapped end
	local stripped = M.bone_name_aliases(sourceParentName)[2]
	if stripped ~= nil then return anchorMap[stripped] end
	return nil
end

-- ---------------------------------------------------------------------------
-- 碰撞体（纯数学球 / 胶囊）
--
-- **原版 L4D2 的 jigglebone 没有任何碰撞**（Valve `jigglebones.cpp` 全文没有一次碰撞查询，
-- 骨骼之间也不通讯）。所以这一层不是"还原 Source"，而是**补上用户明确要求的缺失能力**：
-- "我们完全没做胶囊碰撞体……效果可以略微差一点，但起码不能有缺失"。
--
-- ⚠️ HD2 的 Lua **没有任何物理查询 API**：`PhysicsWorld` / `Actor` / `Joint` / `Mover` /
-- `Cloth` / `Raycast` / `NavigationMesh` 整层被裁掉（`docs/hd2-armor-physics-research.md:453-457`，
-- 另有 `HD2RuntimeArmorOverride` 全部 67 个 Lua 文件 `raycast|spherecast|PhysicsWorld` 零命中）。
-- ⇒ 只能用**纯数字**的身体代理做位置投影，不能问引擎"这里有没有东西"。
--
-- 代理从 **HD2 原生骨名**生成（这些名字在真实 Unit 里确定存在），
-- 半径是**拟定默认值**、按身形尺度缩放，不是实测人体尺寸。
-- 缺骨就降级（端点缺一个 → 退化成以另一端为球心的球；两个都缺 → 记一笔跳过），
-- **绝不静默把整层关掉**。
-- ---------------------------------------------------------------------------

--- 骨名的**候选写法**：带命名空间的原名，以及去掉命名空间后的名字。
--- <para>
--- 为什么要这一步：转换器给占位骨名加了按来源模型派生的命名空间
--- （<c>j&lt;12hex&gt;/source_extra_72</c>，见 HD2SourceModel 的 SourceBone.DisplayName），
--- 目的是让两个模型的骨名不再重名。但**旧 patch 里的节点名没有前缀**，
--- 而容器可能来自任一侧（新容器 + 旧 patch、旧容器 + 新 patch 都会出现）。
--- 所以判定一律走"两种写法都试"，这样新旧产物可以互相兼容。
--- </para>
---@return table 1~2 个候选名（第一个永远是原名）
function M.bone_name_aliases(name)
	if type(name) ~= "string" or #name == 0 then return {} end
	-- 前缀形如 j 后面跟十六进制（长度不限），再接 `/`
	local stripped = name:match("^j%x+/(.+)$")
	if stripped ~= nil and #stripped > 0 then
		return { name, stripped }
	end
	return { name }
end

--- 把 local pose 变成**纯数字**快照。
--- <para>
--- 为什么不能缓存 `Unit.local_pose` 返回的句柄：2026-10-31 一致性电池实测（E6），
--- 缓存句柄和"当场重读"在 **0.10 秒**后就分叉，而且差值是 `inf` —— 引擎的
--- Matrix4x4 / Vector3 / Quaternion 是**临时对象**，连一帧都留不住（早先记的"约 8 秒"偏乐观）。
--- 所以：任何句柄都必须**当场**转成数字。
--- </para>
---
--- ⚠️ 布局是实测定死的（一致性电池 E2，2026-10-31）：
--- `Matrix4x4.to_elements` 返回 **packed9**：旋转在 1..9（连续 9 个），平移在 10..12。
--- 而且这 9 个数按**行主序**读出来是 R 的**转置**（引擎内部按列主序存）。
--- 旧版这里按"每行 3 旋转 + 1 平移"取 1,2,3/5,6,7/9,10,11，两处都错 —— 已删除。
--- 另外 `Matrix4x4.rotation(pose)` 这条路**不可用**（实测它返回的不是 3×3 旋转矩阵，
--- 两种解释的误差都是 1.0），所以不再从 pose 反解旋转，直接读 pose 自己的 12 个数。
local function snapshot_pose(S, pose)
	if pose == nil then return nil end
	local ok, e = pcall(function() return { S.Matrix4x4.to_elements(pose) } end)
	if not ok or type(e) ~= "table" or #e < 12 then return nil end
	for index = 1, 12 do
		local value = e[index]
		if type(value) ~= "number" or value ~= value then return nil end
	end
	return {
		px = e[10], py = e[11], pz = e[12],
		-- 存**引擎原样**的 9 个旋转数（列为主序、当作行主序读就是 Rᵀ）。
		-- 这样写回时不需要做任何转置推理：谁读谁负责解释，见 rebuild_pose。
		r = { e[1], e[2], e[3], e[4], e[5], e[6], e[7], e[8], e[9] },
	}
end

--- 把"引擎原样 9 个数"解释成轴角。返回单位轴 + 角度；单位旋转返回 nil。
--- <para>
--- 为什么绕轴角而不是直接四元数：`Quaternion.from_elements` 虽然存在（E0），
--- 但参数顺序/表还是多参从未验证；而 `axis_angle` + `from_quaternion_position`
--- 是实测可靠的唯一构造链（E2/E3 误差 1e-8）。
--- </para>
--- ⚠️ E3 的结论：引擎**不会**自己归一化轴，所以这里必须自己归一化。
local function stored9_to_axis_angle(r)
	if type(r) ~= "table" or #r < 9 then return nil end
	-- 引擎存的是列主序；取出来按行主序读是 Rᵀ。这里要的是真正的 R，
	-- 所以按 R = (行主序读)ᵀ 取元素：R[i][j] = r[j*3+i] 反过来写就是下面这样。
	local m11, m12, m13 = r[1], r[4], r[7]
	local m21, m22, m23 = r[2], r[5], r[8]
	local m31, m32, m33 = r[3], r[6], r[9]
	local trace = m11 + m22 + m33
	local cos = math.max(-1, math.min(1, (trace - 1) * 0.5))
	local angle = math.acos(cos)
	if angle < 1e-6 then return nil end          -- 单位旋转
	local ax, ay, az
	if angle < math.pi - 1e-3 then
		ax = (m32 - m23) * 0.5 / math.sin(angle)
		ay = (m13 - m31) * 0.5 / math.sin(angle)
		az = (m21 - m12) * 0.5 / math.sin(angle)
	else
		-- 接近 180°：sin→0，改用对角元分支，否则除以 0
		local xx = math.max(0, (m11 + 1) * 0.5)
		local yy = math.max(0, (m22 + 1) * 0.5)
		local zz = math.max(0, (m33 + 1) * 0.5)
		ax, ay, az = math.sqrt(xx), math.sqrt(yy), math.sqrt(zz)
		if m21 + m12 < 0 then ay = -ay end
		if m31 + m13 < 0 then az = -az end
	end
	local length = math.sqrt(ax * ax + ay * ay + az * az)
	if length < 1e-9 then return nil end
	return ax / length, ay / length, az / length, angle
end

--- 用已验证的原语重建一个 local pose：轴角 → from_quaternion_position。
--- 单位旋转时用 `axis_angle(z, 0)`（它给出的就是单位四元数）。
local function rebuild_pose(S, snapshot)
	if snapshot == nil then return nil end
	local ax, ay, az, angle = stored9_to_axis_angle(snapshot.r)
	local q
	if ax == nil then
		q = S.Quaternion.axis_angle(S.Vector3(0, 0, 1), 0)
	else
		q = S.Quaternion.axis_angle(S.Vector3(ax, ay, az), angle)
	end
	if q == nil then return nil end
	return S.Matrix4x4.from_quaternion_position(q, S.Vector3(snapshot.px, snapshot.py, snapshot.pz))
end

--- 读一个世界坐标并**当场**转成三个数字。读不出返回 nil。
local function read_vec3(S, vector)
	if vector == nil then return nil end
	local ok, x, y, z = pcall(function() return S.Vector3.to_elements(vector) end)
	if ok and type(x) == "number" and x == x then return x, y, z end
	return nil
end

--- 读一个节点的世界旋转，**当场**转成 xyzw 四个数字并归一化（E5b：顺序是 xyzw）。
local function read_quat4(S, unit, node)
	if unit == nil or node == nil then return nil end
	if type(S.Quaternion) ~= "table" or type(rawget(S.Quaternion, "to_elements")) ~= "function" then
		return nil
	end
	local ok, x, y, z, w = pcall(function()
		local q = S.Unit.world_rotation(unit, node)
		if q == nil then return nil end
		return S.Quaternion.to_elements(q)   -- 必须与上一步同一次调用内完成
	end)
	if not ok or type(x) ~= "number" or type(w) ~= "number" then return nil end
	local length = math.sqrt(x * x + y * y + z * z + w * w)
	if length < 1e-9 then return nil end
	return x / length, y / length, z / length, w / length
end

--- 写进引擎之前的硬检查。
--- `Matrix4x4.is_valid` 只查 NaN/inf，**拦不住 1e14**（实测就是被它放过去的），
--- 所以这里额外要求：12 个元素都是有限且不大，且旋转部行列式 ≈ 1。
--- 退化（det=0）会把网格塌成一点再拉成针；超大量级会把整束头发甩到天边。
local function pose_sane(S, pose)
	local ok, e = pcall(function() return { S.Matrix4x4.to_elements(pose) } end)
	if not ok or e == nil then return false end
	for index = 1, 12 do
		local value = e[index]
		if type(value) ~= "number" or value ~= value or value > 1e6 or value < -1e6 then return false end
	end
	-- ⚠️ 布局：`Matrix4x4.to_elements` 返回 **12 个数 = 列主序 3×4**：
	--    旋转连着占前 9 个（c0 c1 c2 各 3 个），平移在最后 3 个。
	-- 实测样本：`1 0 0  0 1 0  0 0 1  -0.0746 0.1910 0.0955`（单位旋转 + 一个平移）。
	-- 曾经按"行主序"取 1,2,3 / 5,6,7 / 9,10,11 当旋转，把平移混了进去 ⇒ det 算成 0，
	-- 于是**好矩阵被判成坏矩阵**、一根骨都写不进去。行列式对转置不变，所以直接取前 9 个即可。
	local a, b, c = e[1], e[2], e[3]
	local d, f1, g = e[4], e[5], e[6]
	local h, i, j = e[7], e[8], e[9]
	local det = a * (f1 * j - g * i) - b * (d * j - g * h) + c * (d * i - f1 * h)
	if det < 0.5 or det > 1.5 then return false end
	return true
end

-- ---------------------------------------------------------------------------
-- 生命周期闸（K1/K2：军械库 / 空投仓闪退）
--
-- 事实（2026-11-01 定稿）：
--   · 军械库页面会**另起一套玩家模型**，换甲时旧 Unit 被销毁、新的被创建，
--     整个过程持续若干帧；空投仓的换甲页同理。切图/切场景更是整世界换掉。
--   · 我们的 session 里存的是 `unit` userdata + **节点下标**。Unit 一旦被销毁，
--     这两个东西都变成野指针；再调 `Unit.world_position` / `set_local_pose`
--     就是原生 AV —— **pcall 拦不住**（进程直接没了，连 Lua 错误都没有）。
--   · 所以判据必须**事前**："这个 Unit 现在还活着吗"，而不是"出错了再捕获"。
--
-- 关键前提（让"每帧查一次"就足够严密）：
--   Lua 跑在主线程上，引擎不可能在我们一次 update 的**中途**销毁 Unit。
--   因此"帧首活着"⇒"这一帧内都活着"。所有引擎调用都必须在通过这道闸之后。
--
-- 可用原语：`stingray.Unit.alive(unit) -> boolean`（HD2 在线；两条独立证据：
--   本仓库 HD2RuntimeArmorOverride 的 world probe 实机调过 577 次全部返回布尔，
--   MDL3 的 engine_counts 也用 `stingray.Unit.alive` 判活。对已销毁 Unit 返回 false，不抛错。）
-- ---------------------------------------------------------------------------

--- 这个 Unit 还活着吗。**任何**引擎调用之前都要先过这里。
--- 没有 `Unit.alive` 的引擎版本退回"乐观为真"（至少不比原来更差）。
--- <para>
--- 第二个返回值只在"判死原因是调用本身抛错"时给出文本。它不改变判死口径（仍然只有
--- `Unit.alive` 一条路），只是让日志能区分**真的销毁**和**签名不对/引擎换了**——
--- 这两种在观测上都表现为 `alive == false`，但修法完全相反。
--- </para>
---@return boolean alive
---@return string|nil 抛错文本
function M.unit_alive(S, unit)
	if unit == nil then return false, "unit 为 nil" end
	if type(S) ~= "table" or type(S.Unit) ~= "table" then return true end
	local alive = rawget(S.Unit, "alive")
	if type(alive) ~= "function" then return true end
	local ok, value = pcall(alive, unit)
	if not ok then return false, "Unit.alive 抛错：" .. tostring(value) end
	-- 只信明确的 false；nil / 非布尔（老引擎或签名不同）按"活着"处理，
	-- 免得一次误判就把正常驱动永久关掉。
	if value == false then return false end
	return true
end

--- 会话身份诊断：**只观察，不据此停驱动**。
--- 返回 { nodes = 当前场景图节点数, has = 抽样骨名是否还能解析 }。
--- <para>
--- 2026-11-01 实机教训：把"节点数变了 / 抽样骨名解析不到"直接当成"Unit 被重建"是**错的** ——
--- 7 个会话在驱动 7 帧后全部被判 rebuilt，于是 attach → 驱动几帧 → 丢弃 → 再 attach 无限循环，
--- 表现就是"头发竖着但冻结，并在垂直和原样之间来回切"。
--- 所以：判死只信 `Unit.alive`；身份漂移先当**观测**记日志，确认了真正的判据再开 strictIdentity。
--- </para>
function M.session_identity(session)
	local S = session.S
	local unit = session.unit
	local now = { nodes = nil, has = nil }
	local okCount, count = pcall(function() return S.Unit.num_scene_graph_items(unit) end)
	if okCount and type(count) == "number" then now.nodes = count end
	if session.identityNode ~= nil then
		local okHas, has = pcall(function() return S.Unit.has_node(unit, session.identityNode) end)
		if okHas then now.has = has end
	end
	return now
end

--- 当前帧号。调用方每帧调一次 `M.begin_frame(n)`；用于"同一帧只做一次"的去重。
--- 不调也不影响正确性（只是少了去重）。
M.frame = nil

--- 标记进入新的一帧。传引擎帧号或任意自增整数即可。
function M.begin_frame(stamp)
	M.frame = stamp
end

--- 会话健康度。返回 "ok" / "dead"（Unit 没了）/ "rebuilt"（**仅** strictIdentity 时可能出现）。
--- <para>
--- 判死只信 `Unit.alive`：这是唯一有实机证据（577 次调用全返回布尔、对已销毁 Unit 返回 false）的原语。
--- 身份漂移默认只记录，不参与判定 —— 理由见 M.session_identity 的注释。
--- </para>
--- 成本：每帧 1 次引擎调用（alive）；身份采样每 `identityEvery` 次 2 次。
function M.session_health(session, stamp)
	if session == nil then return "dead" end
	if session.dead ~= nil then return session.dead end
	-- 同一帧只判一次：探针的 prune_sessions 和 update_tip 都会问，不去重就是每帧 2 次引擎调用/会话。
	if stamp ~= nil and session.healthStamp == stamp then return "ok" end
	if stamp ~= nil then session.healthStamp = stamp end
	local S = session.S
	local aliveNow, aliveError = M.unit_alive(S, session.unit)
	if not aliveNow then
		session.dead = "dead"
		session.deadReason = aliveError
		return "dead"
	end
	session.healthFrames = (session.healthFrames or 0) + 1
	local every = session.identityEvery or 30
	if (session.healthFrames % every) == 0 then
		local now = M.session_identity(session)
		session.identityNow = now
		-- ★ 节点数变化 = 场景图被重建（换装 / 切换 LOD）⇒ 我们缓存的**节点下标**可能越界。
		-- 对越界下标调 set_local_rotation 是写野内存：轻则崩溃、重则引擎卡死（2026-11-01 实测挂起）。
		-- ⚠️ 判据**只用 num_scene_graph_items**，绝不用 has_node：
		-- 实测 has_node 会对同一根骨从 true 翻成 false（而 nodes 171→171 不变），拿它判"重建"会误杀，
		-- 表现就是"每秒 attach 一次、头发在垂直和原样之间来回切"。节点数没出现过这种假信号。
		if now.nodes ~= nil and session.nodeCount ~= nil and now.nodes ~= session.nodeCount then
			if not session.rebuildLogged then
				session.rebuildLogged = true
				local logger = session.logger or function() end
				logger("[physbone][LIFECYCLE] 场景图节点数 %s→%s ⇒ 判定为重建，停止驱动该 Unit（等重挂）",
					tostring(session.nodeCount), tostring(now.nodes))
			end
			session.dead = "rebuilt"
			return "rebuilt"
		end
		-- has_node 漂移**仅记录**（理由见上，它不可靠）。
		if now.has ~= nil and now.has ~= true and not session.identityDriftLogged then
			session.identityDriftLogged = true
			local logger = session.logger or function() end
			logger("[physbone][IDENTITY] 抽样骨名 has_node=false（nodes %s→%s）——仅观察，不停止驱动",
				tostring(session.nodeCount), tostring(now.nodes))
		end
		if session.strictIdentity and (now.has ~= nil and now.has ~= true) then
			session.dead = "rebuilt"
			return "rebuilt"
		end
	end
	return "ok"
end

--- 从多份候选 rig 里选出**属于当前这套护甲**的那一份。
---
--- 为什么需要它：多个带物理的 mod 同时安装时，每个 patch 尾部都有自己的容器，
--- 运行时会读到好几份 rig。选错 = 用别的模型的骨名去驱动 → 0 条链，或者更糟：
--- 骨名恰好撞上（`source_extra_*` 是按源模型骨骼下标生成的，不同模型会重名）→ 写到错的骨上。
---
--- 判据（按优先级）：
---   1. **全命中**（该 rig 的每根骨名都能在候选 unit 上解析）优先于部分命中；
---   2. 同为全命中时，命中数多的（骨更全的那份）优先；
---   3. 再打平则取**先出现**的（调用方按 patch 序号传进来，序号小的更可能是目标）。
---
--- ⚠️ 两个必须遵守的前提：
---   · 判据只能用 `Unit.has_node(unit, id) == true`，**不能**用 `Unit.node` ——
---     后者对不存在的名字也会返回一个数字（2026-10-31 实测），会把错 rig 刷成满分。
---   · 打分对象要传**多个**候选 unit（节点数最多的前几个），不能只传 `armorList[1]`：
---     人台 / 队友的护甲件对谁都 0 命中，只打它会让所有 rig 打平、于是选中序号最小那份。
---
---@param S table stingray 命名空间
---@param units table 候选 unit 数组（会对每个 unit 取最大命中数）
---@param candidates table { { rig = <parse_rig 结果>, path = <string> }, ... }
---@return table|nil best { rig, path, score, total, full }
---@return table scored 排序后的完整打分表
function M.select_rig(S, units, candidates)
	if type(candidates) ~= "table" or #candidates == 0 then return nil, {} end
	units = units or {}
	local function id32(text)
		if type(text) ~= "string" then return nil end
		local ok, value = pcall(function() return S.Application.string_to_id32(text) end)
		if ok then return value end
		return nil
	end
	local function bone_ids(rig)
		local ids = {}
		for index = 1, #rig.bones do
			-- 一根骨可能有 1~2 种写法（带/不带命名空间）；任一写法能解析就算命中。
			ids[#ids + 1] = M.bone_name_aliases(rig.bones[index].name)
		end
		return ids
	end

	local scored = {}
	for order, candidate in ipairs(candidates) do
		local rig = candidate.rig
		if rig ~= nil and type(rig.bones) == "table" and #rig.bones > 0 then
			local aliases = bone_ids(rig)
			local total = #rig.bones
			local score = 0
			for _, unit in ipairs(units) do
				if M.unit_alive(S, unit) then
					local hits = 0
					for _, names in ipairs(aliases) do
						for _, name in ipairs(names) do
							local id = id32(name)
							if id ~= nil then
								local ok, has = pcall(S.Unit.has_node, unit, id)
								if ok and has == true then
									hits = hits + 1
									break
								end
							end
						end
						if hits >= total then break end
					end
					if hits > score then score = hits end
				end
			end
			scored[#scored + 1] = {
				rig = rig, path = candidate.path, order = order,
				score = score, total = total, full = (score == total),
			}
		end
	end
	if #scored == 0 then return nil, scored end

	-- 排序：全命中 → 命中多 → 骨更多 → 先出现的（order 保证确定性；Lua 的 sort 不稳定）。
	table.sort(scored, function(a, b)
		if a.full ~= b.full then return a.full end
		if a.score ~= b.score then return a.score > b.score end
		if a.total ~= b.total then return a.total > b.total end
		return a.order < b.order
	end)
	return scored[1], scored
end

--- 生成"同一套护甲"的缓存键：**资源哈希 + 节点数**。
--- <para>
--- 为什么要它：多人同屏时可能有好几个玩家穿**同一套** mod 护甲，它们是同一个资源的不同实例，
--- 场景图节点下标**完全一样**。而 attach 最贵的那一段（每根骨 `has_node` + `node`，
--- 231 根骨 ≈ 460 次引擎调用）对每个实例都会重跑一遍。
--- 有了这个键，第 2 个玩家起直接复用下标表，attach 成本从 N× 降到 1× + (N-1)×(读姿态)。
--- </para>
--- 拿不到资源哈希时返回 nil（退回不缓存，行为与优化前一致）。
function M.attach_cache_key(S, unit, nodeCount)
	local ok, value = pcall(function() return S.Unit.name_hash(unit) end)
	if not ok or type(value) ~= "string" or #value < 8 then return nil, nil end
	local cache = M._attachCache
	if cache == nil then cache = {}; M._attachCache = cache end
	return value .. "|" .. tostring(nodeCount), cache
end

--- 清空 attach 缓存。换关卡 / 换模型后可以调一次（不清也不会错，只是多占一点内存）。
function M.clear_attach_cache()
	M._attachCache = {}
end

--- 把 rig 挂到一个 Unit 上。
--- 只做"解析节点 + 缓存基准姿态"，不做任何写入——失败要能在进入逐帧循环前暴露。
---@param S table stingray 命名空间
---@param unit userdata 护甲 Unit
---@param rig table parse_rig 的结果
---@param options table { logger, anchorMap, boneNamePrefix, maxChains }
function M.attach(S, unit, rig, options)
	-- 第一行就落日志：只要这行没出现，就说明死在进入 attach 的路上。
	-- 2026-10-31 就是靠"这行没出现"把范围锁到了唯一那句引擎调用上。
	options = options or {}
	local logger = options.logger or function() end
	logger("[physbone] attach 进入：rig %d 根骨", #rig.bones)
	-- 生命周期闸：attach 本身要发上百次引擎调用（每根骨 has_node / node / local_pose /
	-- world_position）。Unit 在 attach 途中被销毁同样是 AV，所以进门先验一次。
	if not M.unit_alive(S, unit) then
		logger("[physbone] attach 放弃：目标 Unit 已销毁（alive=false）")
		return { S = S, world = options.world, unit = unit, rig = rig, sessions = {}, dead = "dead" }
	end
	local id32 = function(text)
		local ok, value = pcall(function() return S.Application.string_to_id32(text) end)
		if ok then return value end
		return nil
	end

	-- ⚠️ **绝不能试探性调用引擎的构造器**。2026-10-31 实测：`S.Quaternion(0,0,0,1)`
	-- 直接把游戏进程打死（日志停在上一行，连 Lua 错误都没有 —— 进程已经没了）。
	-- 所以能力判断只做**类型检查**，一次引擎调用都不发。
	M._quatFromElements = type(S.Quaternion) == "table"
		and type(rawget(S.Quaternion, "from_elements")) == "function"
	logger("[physbone] 四元数 from_elements=%s（仅类型检查）", tostring(M._quatFromElements))
	-- 快路径自检：确认 from_elements 的参数顺序 + set_local_rotation 可用（每个进程只跑一次）。
	if options.fastWrite ~= false then M.probe_fast_write(S, logger) end

	logger("[physbone] attach 开始：rig %d 根骨", #rig.bones)
	local chains = M.build_chains(rig.bones)
	logger("[physbone] 链数 %d", #chains)
	local sessions, skippedNoNode, skippedNoAnchor = {}, 0, 0

	-- 节点总数：下面用它把"看起来是数字但越界"的返回值挡掉。
	-- 教训（2026-10-31）：`Unit.node` 对**不存在的名字**也会返回一个数字，
	-- 拿它去调 local_pose/set_local_pose 会直接 AV 崩溃（不是抛 Lua 错误，pcall 拦不住）。
	-- 所以这里必须自己验边界，不能只信返回值的类型。
	local okCount, nodeCount = pcall(function() return S.Unit.num_scene_graph_items(unit) end)
	if not okCount or type(nodeCount) ~= "number" then
		logger("[physbone] 拿不到节点总数，放弃 attach")
		return { S = S, world = options.world, unit = unit, rig = rig, sessions = {} }
	end

	--- 解析一个节点下标；不可用时返回 nil（绝不返回可疑数字）。
	local function resolveNode(text)
		if type(text) ~= "string" or #text == 0 then return nil end
		-- 骨名可能带命名空间前缀（新容器）也可能不带（旧 patch），两种写法都试一次。
		for _, candidate in ipairs(M.bone_name_aliases(text)) do
			local id = id32(candidate)
			if id ~= nil then
				local okHas, has = pcall(function() return S.Unit.has_node(unit, id) end)
				if okHas and has == true then
					local okNode, node = pcall(function() return S.Unit.node(unit, id) end)
					-- Unit.node 是 1 基下标（§12.1 实测）：合法范围是 1..nodeCount。
					-- ⚠️ 它对**不存在的名字**也会返回一个数字，所以必须先过 has_node（上面已经过了）。
					if okNode and type(node) == "number" and node >= 1 and node <= nodeCount then
						return node
					end
				end
			end
		end
		return nil
	end

	-- 多玩家穿同一套护甲时，节点下标完全一样（同一个资源的不同实例）。
	-- 缓存"链 → 节点下标"，第 2 个玩家起就不用再跑几百次 has_node/node。
	local cacheKey, attachCache = M.attach_cache_key(S, unit, nodeCount)
	local cachedChains = cacheKey and attachCache[cacheKey]
	if cachedChains ~= nil then
		logger("[physbone] 复用同款护甲的节点解析缓存（%d 条链，省掉约 %d 次引擎调用）",
			#chains, #chains * 2 * 3)
	end

	-- 用**下标循环**而不是 ipairs：ipairs 遇到 nil 洞就停，
	-- 而 `#chains` 报表长度时可能仍然算上洞后面的元素 ——
	-- 两者不一致的表现就是"日志说 2 条链，实际只处理 1 条"（2026-10-31 实测踩到）。
	for chainIndex = 1, #chains do
		local chain = chains[chainIndex]
		-- attach 整段跑在同一帧里，正常情况引擎不可能中途销毁 Unit（单线程）。
		-- 这条是保险：万一某个引擎调用内部把它弄失效了（懒加载失败、资源回收），
		-- 我们宁愿少挂几条链，也不要在失效 Unit 上继续读 local_pose / world_position。
		if not M.unit_alive(S, unit) then
			logger("[physbone] attach 中断：第 %d/%d 条链时目标 Unit 已销毁", chainIndex, #chains)
			return { S = S, world = options.world, unit = unit, rig = rig,
				sessions = {}, dead = "dead", partialSessions = sessions }
		end
		logger("[physbone] 链 #%d/%d：首节=%s 节数=%s", chainIndex, #chains,
			tostring(chain and chain[1] and chain[1].name),
			tostring(chain and #chain))
		if chain == nil then
			skippedNoNode = skippedNoNode + 1
		elseif options.maxChains and #sessions >= options.maxChains then
			break
		else

		-- 1) 链上每一节都必须是真实存在的节点
		--    同款护甲复用缓存时，抽第一根骨做一次 has_node 抽查：万一实例的节点布局不同
		--    （理论上不会），抽查失败就退回逐根解析。
		local cached = cachedChains and cachedChains[chainIndex]
		local nodes, allFound = nil, true
		if cached ~= nil and cached.nodes ~= nil then
			local firstBone = chain[1]
			local firstId = firstBone ~= nil and id32(firstBone.name) or nil
			if firstId ~= nil then
				local okHas, has = pcall(function() return S.Unit.has_node(unit, firstId) end)
				if okHas and has == true then nodes = cached.nodes end
			end
		end
		if nodes == nil then
			nodes = {}
			for _, bone in ipairs(chain) do
				local node = resolveNode(bone.name)
				if node == nil then allFound = false break end
				nodes[#nodes + 1] = node
			end
		end
		if not allFound then
			skippedNoNode = skippedNoNode + 1
		else
			-- 2) 锚点：链根挂在哪根 HD2 骨上（用它的世界位置算加速度）
			-- 锚点**名字**要在锚点解析之前就算好：世界朝向锚定的判据要用它判断
			-- "这条链挂在手臂上还是躯干上"（见 M.world_anchor_of）。
			local anchorName = M.anchor_name(chain[1].parentName, options.anchorMap)
				or chain[1].parentName
			local anchorNode = cached and cached.anchorNode or nil
			if anchorNode == nil then
				-- rig v2 直接带父骨名字，所以这里能跨到 HD2 的骨名表上。
				-- 映射表没命中时**把名字本身当成 HD2 骨名**再试一次：链是我们自己插进 Unit 的，
				-- 那时父骨名本来就写成 HD2 的名字（如 "head"），没有"源→HD2"这层要转。
				-- 这样做是安全的：名字不存在时 resolveNode 返回 nil，只会计进"缺锚点"。
				if anchorName ~= nil then
					anchorNode = resolveNode(anchorName)
				end
			end

			if anchorNode == nil then
				skippedNoAnchor = skippedNoAnchor + 1
			else
				local springs, basePoses = {}, {}
				-- 链内角度预算：链上每节的偏角会**逐级叠加**。
				-- 三节各吃满 35° ⇒ 末梢合计 105°，吊坠会被折到完全相反的方向
				-- （玩家看到的就是"反重力指着上方，而且不动"）。
				-- 所以把源参数给的单节限位按节数摊开，保证整条链的总偏离不超预算。
				local share = 1 / math.max(#chain, 1)
				local snapshotFailed = false
				for index, bone in ipairs(chain) do
					springs[index] = M.new_spring(bone, options.segmentUnitScale)
					-- 源模型三节都是原始 35°（angleLimit），不按链长摊开。摊开正是"根部带动"的原因之一。
					local ok, pose = pcall(function() return S.Unit.local_pose(unit, nodes[index]) end)
					-- 存**数字**，不存句柄：实测缓存句柄 0.1 秒就变 inf（一致性电池 E6）。
					-- 以前这里还留了 snap.pose / snap.rot 两条"句柄退路"，现已全删 ——
					-- 它们不是退路，它们是把坏矩阵引入节点的唯一入口。
					local snap = (ok and pose ~= nil) and snapshot_pose(S, pose) or nil
					basePoses[index] = snap
					if basePoses[index] == nil then snapshotFailed = true end
				end

				-- P2：采一遍每节的 bind 世界位置 + 锚点 bind 旋转，算出尖端点簧要的几何量。
				-- 全部**当场**转成数字（E6：句柄 0.1 秒就变 inf）。
				--
				-- ⚠️ 长度与重力是**两种量，必须分开给比例**（详见 M.gains_of 的说明）：
				--   从源模型导出的 rig，`length` 是源单位（1 单位 ≈ 0.01905 m）；
				--   运行时从 Unit 反推的合成 rig，长度已经是**米**（比例 1.0），
				--   但 `tipMass` 始终是 Valve 的单位/s²，比例**仍然是** 0.01905。
				local lengthScale = options.lengthScale
					or options.segmentUnitScale
					or ((options.rigLengthUnit == "meter") and 1.0 or SOURCE_UNIT_TO_METERS)
				local gravityScale = options.gravityScale or SOURCE_UNIT_TO_METERS
				local tipData = nil
				if not snapshotFailed then
					local bindWorld, okWorld = {}, true
					for index = 1, #nodes do
						local wx, wy, wz = read_vec3(S, S.Unit.world_position(unit, nodes[index]))
						if wx == nil then okWorld = false break end
						bindWorld[index] = { wx, wy, wz }
					end
					local qbx, qby, qbz, qbw = read_quat4(S, unit, anchorNode)
					if okWorld and qbx ~= nil then
						-- 锚点 bind 世界位置（用它把每节的 bind 位置换成锚点局部系）
						local awx, awy, awz = read_vec3(S, S.Unit.world_position(unit, anchorNode))
						local cqx, cqy, cqz, cqw = qconj(qbx, qby, qbz, qbw)
						local bonesNew, tipsNew, segDirLocal = {}, {}, {}
						for index = 1, #chain do
							local world = bindWorld[index]
							local ox, oy, oz = world[1] - awx, world[2] - awy, world[3] - awz
							local lx, ly, lz = qrot(cqx, cqy, cqz, cqw, ox, oy, oz)
							local gains = M.gains_of(chain[index].params, lengthScale, gravityScale,
								{ flags = chain[index].flags, limitRelax = options.limitRelax })
							bonesNew[index] = {
								gains = gains,
								offLocal = { lx, ly, lz },
								frameLocal = { 0, 0, 1, 0, 1, 0 },  -- 占位，下面按 segDir 重算
							}
							tipsNew[index] = { ready = false, px = 0, py = 0, pz = 0, vx = 0, vy = 0, vz = 0 }
						end
						-- 先量一下这条链的真实骨距，判断"杆长是否离谱"。
						-- ⚠️ 有些资产的 jiggle 骨在骨架里挤成一团（实测 Lilium 的头发：相邻两骨只差 1.2 源单位
						-- ≈ 2 cm，而源参数 `length` 是 30 单位 ≈ 57 cm）。这种链的"节间方向"纯粹是噪声，
						-- 却会被当作 57 cm 杆子的方向 → 一节正、一节反，侧面看就是**锯齿/闪电形折弯**
						-- （2026-10-31 用户实测：大袖子 7 节不折、头发 2 节折，因为袖子骨距 8~16 单位、杆长 10）。
						local spacingSum, spacingCount, spacingMin = 0, 0, math.huge
						for index = 1, #chain - 1 do
							local a, b = bindWorld[index], bindWorld[index + 1]
							local dx, dy, dz = b[1] - a[1], b[2] - a[2], b[3] - a[3]
							local distance = math.sqrt(dx * dx + dy * dy + dz * dz)
							if distance > 1e-9 then
								spacingSum = spacingSum + distance
								spacingCount = spacingCount + 1
								if distance < spacingMin then spacingMin = distance end
							end
						end
						-- 关于"杆长 ≫ 真实骨距"的链（Lilium 头发）：**暂不做任何夹取**。
						-- 2026-10-31 试过"杆长收到骨距 + 限位收到 14°"，实测反而更糟：
						--   · 限位 14° ⇒ 摆动被压死，头发像钢棍；
						--   · 杆长收到骨距 ⇒ 静垂角从 atan(g/(k·0.57))=7.6° 变成 27°，静止姿态前翘；
						--   · 而"闪电折痕"照旧。
						-- 结论：折痕不是杆长/限位造成的。保留原始参数，先取数据（见 [diag] 行）再动手。
						local spacingAvg = spacingCount > 0 and (spacingSum / spacingCount) or nil
						local straightX, straightY, straightZ

						-- 每节的 bind 方向（世界）→ 锚点局部系
						-- ⚠️ 末节没有"下一节"可用。曾经写成 `bindWorld[min(i+1,#chain)]`（取到自己）→ 差值为 0
						-- → 退回一个**任意**方向 (0,0,-1)。后果是末节在开局就带一个大角度错转：
						-- 3 节马尾表现为"末端向内扭曲"，而**单节链**（背后两大片飘带）整片都错转
						-- （2026-10-31 用户实机看到的就是这个）。
						-- 正确取法：末节用"上一节 → 本节"的入射方向；单节链用"锚点 → 本节"。
						for index = 1, #chain do
							local a = bindWorld[index]
							local sx, sy, sz
							if index < #chain then
								local b = bindWorld[index + 1]
								sx, sy, sz = b[1] - a[1], b[2] - a[2], b[3] - a[3]
							elseif index > 1 then
								local previous = bindWorld[index - 1]
								sx, sy, sz = a[1] - previous[1], a[2] - previous[2], a[3] - previous[3]
							else
								sx, sy, sz = a[1] - awx, a[2] - awy, a[3] - awz
							end
							local length = math.sqrt(sx * sx + sy * sy + sz * sz)
							if length < 1e-6 then
								logger("[physbone] 链 %s 第 %d 节方向退化（长度 %.2e），退回 +Y 占位",
									tostring(chain[1] and chain[1].name), index, length)
								sx, sy, sz, length = 0, 1, 0, 1
							end
							local wx, wy, wz = sx / length, sy / length, sz / length
							local lx, ly, lz = qrot(cqx, cqy, cqz, cqw, wx, wy, wz)
							segDirLocal[index] = { lx, ly, lz }
							if #chain == 1 or index == #chain then
								logger("[physbone] 链 %s 第 %d/%d 节 bind 方向=(%.2f %.2f %.2f) 段长=%.3f m",
									tostring(chain[1] and chain[1].name), index, #chain,
									wx, wy, wz, length)
							end
						end
						-- 每节的正交基：forward = 该节方向；left/up 由它叉出来（避免与世界 up 退化）
						for index = 1, #chain do
							local f = segDirLocal[index]
							local hintX, hintY, hintZ = 0, 0, 1
							if math.abs(f[3]) > 0.9 then hintX, hintY, hintZ = 0, 1, 0 end
							local cx, cy, cz = hintY * f[3] - hintZ * f[2], hintZ * f[1] - hintX * f[3], hintX * f[2] - hintY * f[1]
							local length = math.sqrt(cx * cx + cy * cy + cz * cz)
							if length < 1e-6 then cx, cy, cz, length = 1, 0, 0, 1 end
							cx, cy, cz = cx / length, cy / length, cz / length
							local ux = f[2] * cz - f[3] * cy
							local uy = f[3] * cx - f[1] * cz
							local uz = f[1] * cy - f[2] * cx
							bonesNew[index].frameLocal = { cx, cy, cz, ux, uy, uz }
						end
						-- 世界朝向锚定：源数据里同时带 **yaw 约束(0x04) + pitch 约束(0x08)** 的骨，
						-- 按"布料式"处理 —— 父级旋转只带动平移，不改变朝向。
						--
						-- ⚠️ 还必须**同时带 angleCon(0x10)**，这条是 2026-11-01 补的。
						-- 原判据只查 0x04+0x08，样本恰好是"大袖子 0x3d（有 0x10）"和"头发 0x31（没有 0x04/0x08）"，
						-- 于是看着能把两者分开。但真实资产里还有第三类：**纯 yaw/pitch 的头发（0x2d，
						-- 0x01|0x04|0x08|0x20，没有 0x10）**——它被误判成"布料"，于是头怎么转头发都不跟，
						-- 相对头就是扭了整整一个转头角。实机看着就是"跑起来（身体转向）头发向前翘起、特别僵硬"。
						-- 袖子是挂在手臂上的布，世界锚定合理；头发是长在头上的，必须跟着头走（只是滞后）。
						--
						-- ⚠️ 2026-11-05 再补一条：光看 flags 还是不够。Soppo 的 `Chest_L`/`Chest_R`
						-- flags 也是 0x3D，但挂在 **Spine4（躯干）** 上 ⇒ 胸口的布不跟躯干转，
						-- 实机是"胸口反重力立起来/往上飘"。判据交给 M.world_anchor_of：
						-- flags 齐全 **且** 锚点不是躯干/头/胯。
						local worldAnchored, anchorReason = M.world_anchor_of(chain[1].flags or 0, anchorName)
						if worldAnchored then
							logger("[physbone] 链 %s：flags=0x%02x，锚点 %s ⇒ 世界朝向锚定（父级只带动平移）",
								tostring(chain[1] and chain[1].name), chain[1].flags or 0,
								tostring(anchorName))
						elseif flag_set(chain[1].flags or 0, 0x04) then
							-- 只对"本来会开、这次被关掉"的链说话，免得每个 0x31 头发都刷一行。
							logger("[physbone] 链 %s：flags=0x%02x 但%s ⇒ 跟父级转（这是修胸口上飘的那一处）",
								tostring(chain[1] and chain[1].name), chain[1].flags or 0, anchorReason)
						end
						tipData = {
							bones = bonesNew, tips = tipsNew, segDirLocal = segDirLocal,
							dirs = {}, anchorBindQ = { qbx, qby, qbz, qbw },
							goalQ = worldAnchored and { qbx, qby, qbz, qbw } or nil,
						}
						tipData.bendBlend = options.bendBlend == nil and 1.0 or options.bendBlend
						-- 分轴限位的**静止参考系**：右/上/前三个轴一次性在 goalQ 里算好。
						--
						-- ⚠️ 必须和"绑定方向"用同一个参考系。曾经混用过（方向用 goalQ、轴用当前 q），
						-- 于是 `l·n` 不再是"偏离多少"而是随机数，每帧都被夹 ⇒ 一动就僵硬/向前翘。
						-- goalQ 是绑定时锚点朝向、恒定，所以这里能预计算，每帧零成本。
						if tipData.goalQ ~= nil then
							local gq = tipData.goalQ
							for index = 1, #bonesNew do
								if bonesNew[index].gains ~= nil and bonesNew[index].gains.axisLimits then
									local f = bonesNew[index].frameLocal
									local b = segDirLocal[index]
									local l1, l2, l3 = qrot(gq[1], gq[2], gq[3], gq[4], f[1], f[2], f[3])
									local u1, u2, u3 = qrot(gq[1], gq[2], gq[3], gq[4], f[4], f[5], f[6])
									local b1, b2, b3 = qrot(gq[1], gq[2], gq[3], gq[4], b[1], b[2], b[3])
									bonesNew[index].limitFrame = { l1, l2, l3, u1, u2, u3, b1, b2, b3 }
								end
							end
						end
					else
						logger("[physbone] 链 %s：拿不到 bind 世界位置/锚点旋转，退回旧核", tostring(chain[1] and chain[1].name))
					end
				end
				logger("[physbone] 链 %s：%d 节，节点 %d 个，基准姿态=%s", tostring(chain[1] and chain[1].name), #chain, #nodes, snapshotFailed and "失败" or "成功")
				if snapshotFailed then
					logger("[physbone] 0x%x 有一条链读不出基准姿态，整条链跳过（宁可不驱动，也不写坏矩阵）",
						chain[1] and chain[1].index or 0)
				else
					-- 护栏用的 bind 末节世界坐标（数字），用于"节点飞走"检测。
					-- 护栏必须以**相对锚点**的位移度量：角色走动时整个世界坐标都在变，
					-- 用绝对世界坐标会把"人走了 1.5 m"误判成"姿态崩了"（2026-10-31 实测撞过）。
					local bindAnchor = nil
					local okA, aPos = pcall(function() return S.Unit.world_position(unit, anchorNode) end)
					if okA and aPos ~= nil then
						local okAX, ax, ay, az = pcall(function() return S.Vector3.to_elements(aPos) end)
						if okAX and type(ax) == "number" then bindAnchor = { ax, ay, az } end
					end
					local bindTip = nil
					local okTip, tipPos = pcall(function() return S.Unit.world_position(unit, nodes[#nodes]) end)
					if okTip and tipPos ~= nil then
						local okX, tx, ty, tz = pcall(function() return S.Vector3.to_elements(tipPos) end)
						if okX and type(tx) == "number" then bindTip = { tx, ty, tz } end
					end
					sessions[#sessions + 1] = {
						chain = chain,
						nodes = nodes,
						springs = springs,
						basePoses = basePoses,
						bindTip = bindTip,
						bindAnchor = bindAnchor,
						anchorNode = anchorNode,
						tip = tipData,
					previousPosition = nil,
					previousVelocity = { 0, 0, 0 },
					lastAccel = { 0, 0, 0 },
				}
				-- 记进缓存：同款护甲的下一个实例直接复用这两个下标表。
				if cacheKey ~= nil then
					if cachedChains == nil then
						cachedChains = {}
						attachCache[cacheKey] = cachedChains
					end
					cachedChains[chainIndex] = { nodes = nodes, anchorNode = anchorNode }
				end
			end
		end
		end
	end   -- 收尾：if chain == nil
	-- 收尾：for chainIndex 循环。⚠️ 这个 end 以前被放到 return 之后，
	-- 于是「logger + return」整体落进了循环体 —— 第一轮迭代就 return，
	-- 第 2 条链永远处理不到（日志表现为「链数 2」但只有「链 #1/2」）。
	-- 2026-10-31 离线复现：tmp/lua-check/attach-loop-test.lua
	end

	logger(string.format("[physbone] attach：可用链 %d 条（缺节点 %d、缺锚点 %d）",
		#sessions, skippedNoNode, skippedNoAnchor))
	-- 限位来源统计：源模型里有三种约束形式（锥 / 偏航 / 俯仰），这里把"实际用了哪一种"摊开。
	-- 这条能直接看出"某个 mod 的头发为什么甩得离谱"：如果 `不限位` 的骨很多，
	-- 说明它用的是 yaw/pitch 约束而我们没读（历史 bug），而不是参数本身有问题。
	do
		local tally, bySource = 0, {}
		for _, item in ipairs(sessions) do
			local tip = item.tip
			if tip ~= nil and tip.bones ~= nil then
				for index = 1, #tip.bones do
					local gains = tip.bones[index].gains
					if gains ~= nil then
						tally = tally + 1
						local key = gains.limitSource or "cone"
						if gains.limit <= 0 then key = "不限位" end
						bySource[key] = (bySource[key] or 0) + 1
					end
				end
			end
		end
		if tally > 0 then
			local keys = {}
			for key in pairs(bySource) do keys[#keys + 1] = key end
			table.sort(keys)
			local parts = {}
			for _, key in ipairs(keys) do
				parts[#parts + 1] = string.format("%s×%d", key, bySource[key])
			end
			logger("[physbone] 限位来源：%s（共 %d 根骨）", table.concat(parts, " "), tally)
		end
	end
	-- 身份指纹：之后每帧靠它判断"这个 Unit 还是我当初挂的那个吗"。
	--   nodeCount    —— 场景图节点数，变了说明 Unit 被重建/换装（**这是唯一可靠的判据**）；
	--   identityNode —— 第一条链第一节的骨名 id，仅用于观测（has_node 会假翻，不能据此判死）。
	local identityNode = nil
	if #sessions > 0 and sessions[1].chain ~= nil and sessions[1].chain[1] ~= nil then
		identityNode = id32(sessions[1].chain[1].name)
	end
	-- 快路径的写入语义校验：拿到真实节点后做一次（非破坏性，把同一个旋转写回去比对）。
	if #sessions > 0 and sessions[1].nodes ~= nil and sessions[1].nodes[1] ~= nil then
		M.verify_fast_write(S, unit, sessions[1].nodes[1], logger)
	end
	return {
		S = S,
		world = options.world,      -- 写姿态后要 update_unit；缺它就只写不刷新
		unit = unit,
		rig = rig,
		sessions = sessions,
		logger = logger,     -- update 也要能打日志（update 里没有 options）
		nodeCount = nodeCount,          -- 生命周期闸：场景图节点数（仅用于漂移观测）
		identityNode = identityNode,    -- 生命周期闸：抽样骨名 id（仅用于漂移观测）
		identityEvery = options.identityEvery or 30,
		-- 身份漂移是否参与"停驱动"判定。默认 **false**：只有实机确认了正确判据才打开。
		-- 2026-11-01：开着它导致每秒一次 attach/丢弃循环（头发冻结并来回切）。
		strictIdentity = options.strictIdentity == true,
		-- 驱动强度与鲁棒性参数。默认值是照"真实走路头部加速度 3~8 m/s²"定的：
		-- 上限 20 留了余量，但远小于位置跳变能产生的量级。
		maxAccel = options.maxAccel or 10,   -- 走路 3~8 m/s²；留一点余量
		positionSmoothing = options.positionSmoothing or 0.2,
		jumpThreshold = options.jumpThreshold or 0.25,   -- 米；一帧移动 25cm 只可能是跳变
		jumpCooldown = options.jumpCooldown or 30,        -- 跳变后静默帧数
		chainBudget = options.chainBudget or 0.55,        -- 整条链的累计偏角上限（rad，约 31°）
		-- 保留 `solver` 字段只为了那条**已废弃的角度振子核**（`options.solver = "angle"`）还能做 A/B。
		-- 生产只有 `tip` 一个核：粒子链（`chain`）2026-11-03 实机验证后**已移除**——
		-- 摆幅只剩一半（"头发焊在头上"）、且没有性能优势。
		solver = options.solver or "tip",
	}
end   -- M.attach 的收尾（缺它会连 M.update 一起吞掉，§24 踩过）

--- 每帧调用。dt 用引擎时间；返回本次实际写入的骨数（便于测成本）。
function M.update(session, dt, maxWrite)
	if session == nil or #session.sessions == 0 then return 0 end
	-- P2：默认走尖端点簧核。`options.solver = "angle"` 可切回旧核，仅用于游戏内 A/B 对照。
	if session.solver ~= "angle" then return M.update_tip(session, dt, maxWrite) end
	-- 生命周期闸（同 update_tip）：旧核也在碰 Unit，一样必须先验活。
	local health = M.session_health(session)
	if health ~= "ok" then
		if not session.deadLogged then
			session.deadLogged = true
			local logger = session.logger or function() end
			logger("[physbone][LIFECYCLE] 会话失效（%s）：停止驱动该 Unit（旧核）", health)
		end
		return 0
	end
	local logger = session.logger or function() end   -- update 里没有 options.logger，必须自己取；否则失败路径那句日志本身会抛错，把真原因一起吞掉
	-- 进门自检（只打一次）：这里数到的链数与 attach 报的条数对不上，就是 bug 所在。
	if session ~= nil and session.logger ~= nil and not session.entryDiag then
		session.entryDiag = true
		local first = session.sessions and session.sessions[1]
		session.logger("[physbone] update 入口：sessions=%s 首链节数=%s 首节base=%s",
			tostring(session.sessions and #session.sessions or -1),
			tostring(first and first.chain and #first.chain or -1),
			tostring(first and first.basePoses and type(first.basePoses[1])))
	end
	local S = session.S
	local unit = session.unit
	-- 数值稳定性：dt 异常（首帧、卡顿）时夹住，否则弹簧会被一个大步长打飞。
	if dt == nil or dt <= 0 or dt > 0.1 then dt = 1 / 60 end

	local written = 0
	for _, item in ipairs(session.sessions) do
		-- 1) 锚点的世界位置 → 二阶差分得到加速度（这是"引擎侧做不到、必须我们算"的那一步）
		local okPos, worldPosition = pcall(function() return S.Unit.world_position(session.unit, item.anchorNode) end)
		local px, py, pz = nil, nil, nil
		if okPos and worldPosition ~= nil then
			pcall(function() px, py, pz = S.Vector3.to_elements(worldPosition) end)
		end

		local accel = { 0, 0, 0 }
		if item.skipFrames ~= nil and item.skipFrames > 0 then
			item.skipFrames = item.skipFrames - 1
			px = nil        -- 静默期内不施力，弹簧自己回到 0
		end

		if px ~= nil then
			-- ① 低通：位置先做指数平滑再差分。
			-- 世界坐标本身只有 float32 精度，直接二阶差分会把 1e-3 的抖动放大成几十 m/s²。
			if item.smoothedPosition == nil then
				item.smoothedPosition = { px, py, pz }
			else
				local alpha = session.positionSmoothing
				item.smoothedPosition = {
					item.smoothedPosition[1] + alpha * (px - item.smoothedPosition[1]),
					item.smoothedPosition[2] + alpha * (py - item.smoothedPosition[2]),
					item.smoothedPosition[3] + alpha * (pz - item.smoothedPosition[3]),
				}
			end
			px, py, pz = item.smoothedPosition[1], item.smoothedPosition[2], item.smoothedPosition[3]

			-- ② 跳变检测：一帧内位移超过阈值，说明是"单位被重新定位 / 切 LOD / 换场景"，
			-- 不是角色在做加速运动。这种情况必须当作**不连续**：清空速度、跳过这一帧，
			-- 否则弹簧会被这个假加速度持续打满（实测导致吊坠被折到指向上方并卡死）。
			local jumped = false
			if item.previousPosition ~= nil then
				local dx = px - item.previousPosition[1]
				local dy = py - item.previousPosition[2]
				local dz = pz - item.previousPosition[3]
				if math.sqrt(dx * dx + dy * dy + dz * dz) > session.jumpThreshold then
					jumped = true
				end
			end

			if jumped then
				item.previousPosition = { px, py, pz }
				item.previousVelocity = { 0, 0, 0 }
				item.skipFrames = session.jumpCooldown
			elseif item.previousPosition ~= nil then
				local velocity = {
					(px - item.previousPosition[1]) / dt,
					(py - item.previousPosition[2]) / dt,
					(pz - item.previousPosition[3]) / dt,
				}
				accel = {
					(velocity[1] - item.previousVelocity[1]) / dt,
					(velocity[2] - item.previousVelocity[2]) / dt,
					(velocity[3] - item.previousVelocity[3]) / dt,
				}
				item.previousVelocity = velocity

				-- 钳位是必需的，不是保险丝：世界坐标的 float32 精度有限，
				-- 二阶差分会把 1e-3 级别的位置噪声放大成几十上百的加速度，
				-- 结果弹簧被噪声顶死在角度限位上（实测峰值一路 35.0°）。
				--
				-- 但钳位本身必须防 NaN/inf：`inf * 0` 是 NaN，而 NaN 会顺着
				-- 弹簧 → 四元数 → 节点矩阵一路污染下去，节点塌成一个点、
				-- 网格直接消失，而且**再也不会恢复**（2026-10-31 实测的"挂饰消失"）。
				local function finite(value)
					return type(value) == "number" and value == value
						and value < math.huge and value > -math.huge
				end
				if not (finite(accel[1]) and finite(accel[2]) and finite(accel[3])) then
					accel = { 0, 0, 0 }
				else
					local magnitude = math.sqrt(accel[1] ^ 2 + accel[2] ^ 2 + accel[3] ^ 2)
					local limit = session.maxAccel
					if magnitude > limit and magnitude > 0 then
						local scale = limit / magnitude
						accel = { accel[1] * scale, accel[2] * scale, accel[3] * scale }
					end
					if magnitude > (item.peakAccel or 0) then item.peakAccel = magnitude end
				end
			end
			item.previousPosition = { px, py, pz }
		end
		item.lastAccel = accel

		-- 2) 逐节积分 + 写 local pose（顺序天然是父→子，因为 link 里就是这么排的）
		for index = 1, #item.chain do
			if maxWrite and written >= maxWrite then break end
			local spring = item.springs[index]
			M.step_spring(spring, dt, accel[1], accel[2], accel[3])

			-- 二次保险：把整条链的累计偏角封在 session.chainBudget 内。
			-- 单节限位已经按节数摊开，这里再兜一次，防止参数/节数变化时又叠成反重力。
			if session.chainBudget ~= nil and session.chainBudget > 0 then
				local accumulated = 0
				for k = 1, index do
					accumulated = accumulated + math.abs(item.springs[k].yaw) + math.abs(item.springs[k].pitch)
				end
				if accumulated > session.chainBudget and accumulated > 0 then
					local scale = session.chainBudget / accumulated
					for k = 1, index do
						item.springs[k].yaw = item.springs[k].yaw * scale
						item.springs[k].pitch = item.springs[k].pitch * scale
					end
				end
			end
			-- 不再逐级衰减：源模型每节的参数完全相同，累加弯曲来自层级，而不是靠衰减。

			local base = item.basePoses[index]
			if base ~= nil and not item.disabled then
				-- 每一步失败都带一句原因返回。为什么：整条链有 6 个引擎调用，
				-- 任何一环坏掉都只表现为"什么都没写"，光看结果永远分不清是
				-- API 失效、姿态非法、还是句柄过期（2026-10-31 实测：写了 8 秒后突然开始 0 写入）。
				local okWrite, didWrite, reason = pcall(function()
					-- 纯矩阵路径：彻底避开四元数。
					-- 为什么：`Quaternion.multiply` 一旦吃进引擎给的"四元数句柄"就吐 inf/NaN
					-- （实测 combined=inf,-nan,-nan,-nan），而 axis_angle 生成的四元数转成矩阵是可靠的。
					-- 顺序不确定，所以两种都试，谁 `is_valid` 且通过 `pose_sane` 就用谁，并报告是哪一种。
					-- 方案 1：从零构造，不做任何"旋转合成"。
					-- 依据：实测我们插入的 6 个节点，基准 local 姿态的旋转部分是**单位阵**
					-- （验尸日志：1 0 0 / 0 1 0 / 0 0 1 + 平移），所以不需要 base 的旋转，
					-- 只要"角度 + 基准平移"就能拼出正确姿态。
					local qYaw = S.Quaternion.axis_angle(S.Vector3(0, 0, 1), spring.yaw)
					if qYaw == nil then return false, "qYaw=nil" end
					local combined = qYaw
					if math.abs(spring.pitch) > 1e-6 then
						local qPitch = S.Quaternion.axis_angle(S.Vector3(0, 1, 0), spring.pitch)
						if qPitch ~= nil then
							local okM, m = pcall(function() return S.Quaternion.multiply(qYaw, qPitch) end)
							if okM and m ~= nil then combined = m end
						end
					end
					local scratch = S.Matrix4x4.from_quaternion_position(combined, S.Vector3(base.px, base.py, base.pz))
					if scratch == nil then return false, "scratch=nil" end
					if not S.Matrix4x4.is_valid(scratch) then return false, "scratch_invalid" end
					if not pose_sane(S, scratch) then return false, "scratch_insane" end
					if not session.scratchDiag then
						session.scratchDiag = true
						logger("[physbone][构造] 从零构造成功：yaw=%.2f 度 pitch=%.2f 度", spring.yaw * 180 / math.pi, spring.pitch * 180 / math.pi)
					end
					S.Unit.set_local_pose(unit, item.nodes[index], scratch)
					return true
				end)
				-- 计数必须按**实际写没写**算。以前这里只看 pcall 成功与否，
				-- 于是"姿态不合法被 is_valid 挡掉"也会算成"写入成功"——日志会撒谎，
				-- 排查"画面不动"时会被带偏。
				if okWrite and didWrite == true then
					written = written + 1
				else
					session.skippedWrites = (session.skippedWrites or 0) + 1
					if session.firstFailReason == nil then
						session.firstFailReason = string.format("node#%d %s", index,
							tostring(didWrite) .. (reason ~= nil and ("/" .. tostring(reason)) or ""))
						logger("[physbone][失败] 第一次写入失败：%s", session.firstFailReason)
					end
				end
			else
				session.skippedWrites = (session.skippedWrites or 0) + 1
				if item.disabled ~= true and session.firstFailReason == nil then
					session.firstFailReason = string.format("node#%d base=nil", index)
					logger("[physbone][失败] basePose 为空：%s", session.firstFailReason)
				end
			end
		end

		-- 3) 护栏：末节世界坐标飞出去就说明姿态已经坏到不可逆 —— 立刻把这条链清零并停掉。
		-- 为什么必须有这一层：`is_valid` 与行列式检查都在"写出之前"，一旦哪一帧漏过去，
		-- 网格会被拉成一根针，而且**肉眼看见时已经发生**（2026-10-31 用户实拍）。
		-- 这里用"末节离 bind 位置多远"当最终判据，代价是每链每帧一次 world_position。
		if item.bindTip ~= nil and item.bindAnchor ~= nil and not item.disabled and written > 0 then
			local okTip, tipPos = pcall(function() return S.Unit.world_position(unit, item.nodes[#item.nodes]) end)
			local okDep, depPos = pcall(function() return S.Unit.world_position(unit, item.anchorNode) end)
			if okTip and tipPos ~= nil and okDep and depPos ~= nil then
				-- ⚠️ 以前写的是 `local tx, ty, tz = pcall(...)`：`tx` 拿到的是**成功布尔**，
				-- 于是 `type(tx)=="number"` 永远为假 —— 这道"防头发拉成针"的护栏
				-- 从写下的第一天起就没执行过一次（2026-10-31 离线复现并修正）。
				local okTip2, tx, ty, tz = pcall(function() return S.Vector3.to_elements(tipPos) end)
				local okDep2, ax, ay, az = pcall(function() return S.Vector3.to_elements(depPos) end)
				if okTip2 and okDep2 and type(tx) == "number" and type(ax) == "number" then
					-- 相对锚点的偏移，角色整体移动被抵消
					local nx, ny, nz = tx - ax, ty - ay, tz - az
					local bx = item.bindTip[1] - item.bindAnchor[1]
					local by = item.bindTip[2] - item.bindAnchor[2]
					local bz = item.bindTip[3] - item.bindAnchor[3]
					local dx, dy, dz = nx - bx, ny - by, nz - bz
					local drift = math.sqrt(dx * dx + dy * dy + dz * dz)
					if drift ~= drift or drift > 1.5 then
						for index = 1, #item.springs do
							item.springs[index].yaw, item.springs[index].pitch = 0, 0
							item.springs[index].yawVelocity, item.springs[index].pitchVelocity = 0, 0
						end
						item.disabled = true
						logger("[physbone][护栏] 相对锚点偏离 %.2f m，这条链已被停用（姿态不可信）", drift)
					end
				end
			end
		end
			end

	-- 铁律 3：写完让引擎重算世界变换。
	-- 代价说明：world 为 nil 时**必须不调**——update_unit(nil, unit) 是一次 AV 崩溃，
	-- 而不是能靠 pcall 拦住的 Lua 错误（2026-10-31 实测踩过）。
	if written > 0 and session.world ~= nil then
		pcall(function() S.World.update_unit(session.world, unit) end)
	end
	return written
end

--- 把链上每根骨**写回基准姿态**并清空弹簧。
--- <para>
--- 为什么需要它：调试时经常要"把头发交还给引擎"，比如改完参数想对比原样、
--- 或者要换一条链重新挂。直接停掉 update 会把最后一帧的偏角**冻在**头上，
--- 看起来像头发被掰弯了不会再动（2026-10-31 用户报的第一个现象就是这个形状）。
--- 停用必须显式写回基准姿态，才算真的交还。
--- </para>
function M.release(session)
	if session == nil or session.sessions == nil then return 0 end
	local S = session.S
	local unit = session.unit
	-- 生命周期闸：Unit 已经没了的话，"写回基准姿态"就是在写野指针。
	-- 这时唯一正确的动作是**忘掉它**（弹簧数值清零，不碰引擎）。
	if not M.unit_alive(S, unit) then
		for _, item in ipairs(session.sessions) do
			for index = 1, #item.springs do
				local spring = item.springs[index]
				spring.yaw, spring.pitch = 0, 0
				spring.yawVelocity, spring.pitchVelocity = 0, 0
			end
		end
		session.dead = "dead"
		return 0
	end
	local restored = 0
	for _, item in ipairs(session.sessions) do
		item.lastWritten = nil   -- 交还姿态后脏标记必须失效，否则下次驱动会以为"没变"而不写
		for index = 1, #item.chain do
			local base = item.basePoses[index]
			if base ~= nil then
				pcall(function()
					-- 只用**数字**重建。以前这里优先用 attach 时缓存的 pose 句柄，
					-- 而实测该句柄 0.1 秒后就是 inf —— 那不是"退路"，那是必然写坏。
					local pose = rebuild_pose(S, base)
					if pose ~= nil and pose_sane(S, pose) then
						S.Unit.set_local_pose(unit, item.nodes[index], pose)
						restored = restored + 1
					end
				end)
			end
		end
		for index = 1, #item.springs do
			local spring = item.springs[index]
			spring.yaw, spring.pitch = 0, 0
			spring.yawVelocity, spring.pitchVelocity = 0, 0
		end
	end
	if restored > 0 and session.world ~= nil then
		pcall(function() S.World.update_unit(session.world, unit) end)
	end
	return restored
end

--- 给一个"摆动轴角"再叠一个额外旋转（用于裙摆外推）。
--- <para>
--- `rotation` 是 `local_rotations` 产出的 (ax, ay, az, angle)，轴在**父空间**。
--- 额外旋转也按父空间给，结果转回轴角形式，交给 `write_pose` 走原来那条路。
--- 为什么要单独一个函数：热路径每根骨每帧都在跑，这里必须是纯数字运算、零分配
--- （返回的是表，但只给当帧用，和 `local_rotations` 的契约一致）。
--- </para>
function M.push_out_rotation(rotation, axisX, axisY, axisZ, angle)
	if angle == nil or angle == 0 then return rotation end
	local rx, ry, rz, rw = q_axis_angle(rotation[1], rotation[2], rotation[3], rotation[4])
	local ex, ey, ez, ew = q_axis_angle(axisX, axisY, axisZ, angle)
	local nx, ny, nz, nw = qmul(ex, ey, ez, ew, rx, ry, rz, rw)
	-- 归一化 + 取 w ≥ 0 的等价表示（q 与 −q 是同一个旋转），这样回转出的角一定在 [0, π]。
	local len = math.sqrt(nx * nx + ny * ny + nz * nz + nw * nw)
	if len < 1e-9 then return rotation end
	nx, ny, nz, nw = nx / len, ny / len, nz / len, nw / len
	if nw < 0 then nx, ny, nz, nw = -nx, -ny, -nz, -nw end
	local w = nw > 1 and 1 or nw
	local s = math.sqrt(1 - w * w)
	if s < 1e-6 then
		return { rotation[1], rotation[2], rotation[3], 0 }
	end
	return { nx / s, ny / s, nz / s, 2 * math.acos(w) }
end

-- ---------------------------------------------------------------------------
-- P2 驱动：尖端点簧核的引擎侧
--
-- 唯一碰引擎的地方。每链每帧只做三件事：读锚点世界位置、读锚点世界旋转、写每节 local pose。
-- 没有加速度差分，也没有任何引擎侧矩阵/四元数乘法 —— 合成全在 Lua 数字域完成。
-- ---------------------------------------------------------------------------
--- 写一个 local pose。**模块级函数**，这样热路径可以用 `pcall(write_pose, ...)`
--- 而不是 `pcall(function() ... end)` —— 后者每根骨每帧都新建一个闭包（315 个/帧 × 375 fps）。
--- <para>
--- 两条路径：
---   快路径（`M._fastWrite`）：四元数在 Lua 里算好，引擎只做 `from_elements` + `set_local_rotation`
---     = **每骨 2 次引擎调用**；
---   矩阵路径（默认/回退）：`Vector3` + `axis_angle` + `Vector3` + `from_quaternion_position` + `set_local_pose`
---     = 每骨 5 次。
--- 位置不需要写：我们从不改平移，`set_local_rotation` 会保留它。
--- </para>
local function write_pose(S, unit, node, rotation, base)
	if M._fastWrite == true then
		local qx, qy, qz, qw = q_axis_angle(rotation[1], rotation[2], rotation[3], rotation[4])
		local q = S.Quaternion.from_elements(qx, qy, qz, qw)
		if q == nil then return false end
		S.Unit.set_local_rotation(unit, node, q)
		return true
	end
	local q = S.Quaternion.axis_angle(S.Vector3(rotation[1], rotation[2], rotation[3]), rotation[4])
	if q == nil then return false end
	local pose = S.Matrix4x4.from_quaternion_position(q, S.Vector3(base.px, base.py, base.pz))
	if pose == nil then return false end
	S.Unit.set_local_pose(unit, node, pose)
	return true
end

--- 快路径的**写入语义**校验（只在拿到真实节点后做一次，非破坏性）。
--- <para>
--- 前面的 `probe_fast_write` 只验证了 `from_elements` 的参数顺序；它**没有**验证
--- `Unit.set_local_rotation(unit, node, quat)` 到底做了什么。这里补上：
--- 读出某根骨当前的 local_pose 和 local_rotation，把**同一个**旋转写回去，再读 local_pose 比对
--- —— 12 个元素必须逐位不变（尤其是 10/11/12 三个平移）。
--- </para>
--- 为什么要查平移：如果 `set_local_rotation` 会把平移清零（或签名不同），骨头会塌到原点，
--- 蒙皮矩阵退化 → 法线/包围体出 NaN → 引擎的剔除或空间划分可能**死循环**（实测挂起）。
--- 不通过就永久回退到矩阵路径（每骨 5 次调用，但那是被长期验证过的）。
function M.verify_fast_write(S, unit, node, logger)
	if M._fastWrite ~= true or M._fastWriteVerified then return M._fastWrite end
	M._fastWriteVerified = true
	logger = logger or function() end
	local ok, good = pcall(function()
		if node == nil then return false end
		local beforePose = S.Unit.local_pose(unit, node)
		if beforePose == nil then return false end
		local before = { S.Matrix4x4.to_elements(beforePose) }
		if #before < 12 then return false end
		local q = S.Unit.local_rotation(unit, node)
		if q == nil then return false end
		local qx, qy, qz, qw = S.Quaternion.to_elements(q)
		if type(qx) ~= "number" or type(qw) ~= "number" then return false end
		local same = S.Quaternion.from_elements(qx, qy, qz, qw)
		if same == nil then return false end
		S.Unit.set_local_rotation(unit, node, same)
		local afterPose = S.Unit.local_pose(unit, node)
		if afterPose == nil then return false end
		local after = { S.Matrix4x4.to_elements(afterPose) }
		if #after < 12 then return false end
		for index = 1, 12 do
			local a, b = before[index], after[index]
			if type(a) ~= "number" or type(b) ~= "number" then return false end
			if a ~= a or b ~= b then return false end
			if math.abs(a - b) > 1e-4 then return false end
		end
		return true
	end)
	if ok and good == true then
		logger("[physbone] 快路径写入校验通过：set_local_rotation 保留平移、旋转原样写回")
		return true
	end
	M._fastWrite = false
	logger("[physbone] ⚠️ 快路径写入校验未通过（%s）⇒ 永久回退到矩阵路径（每骨 5 次调用）",
		tostring(ok and good or good))
	return false
end

--- 快路径自检（每个引擎命名空间只跑一次，结果缓存在 `M._fastWrite`）。
--- <para>
--- 为什么要自检：`Quaternion.from_elements` 存在，但**参数顺序从未验证**（E0 只确认了它存在）。
--- 用错的顺序不会崩，只会得到错误旋转 —— 那是最难查的一类问题。
--- 做法：用已经验证过的 `axis_angle` 造一个参考四元数，读出它的 4 个分量，
--- 再用 `from_elements` 按同样的 4 个数造一个，比较两者。一致 ⇒ 顺序就是 `to_elements` 的顺序。
--- </para>
--- 任一步不满足就返回 false，热路径继续走已验证的矩阵路径（不会更差）。
function M.probe_fast_write(S, logger)
	if M._fastWrite ~= nil then return M._fastWrite end
	M._fastWrite = false
	logger = logger or function() end
	if type(rawget(S.Unit, "set_local_rotation")) ~= "function" then
		logger("[physbone] 快路径不可用：没有 Unit.set_local_rotation ⇒ 用矩阵路径")
		return false
	end
	if type(rawget(S.Quaternion, "from_elements")) ~= "function"
		or type(rawget(S.Quaternion, "axis_angle")) ~= "function"
		or type(rawget(S.Quaternion, "to_elements")) ~= "function" then
		logger("[physbone] 快路径不可用：四元数构造/读取缺函数 ⇒ 用矩阵路径")
		return false
	end
	local ok, same = pcall(function()
		-- (1,2,3) 归一化后的轴 + 0.7 rad：三个分量都非零，顺序错了必然对不上。
		local axis = S.Vector3(0.2672612, 0.5345225, 0.8017837)
		local reference = S.Quaternion.axis_angle(axis, 0.7)
		if reference == nil then return false end
		local x, y, z, w = S.Quaternion.to_elements(reference)
		if type(x) ~= "number" or type(w) ~= "number" then return false end
		local copy = S.Quaternion.from_elements(x, y, z, w)
		if copy == nil then return false end
		local cx, cy, cz, cw = S.Quaternion.to_elements(copy)
		local function close(a, b) return type(a) == "number" and math.abs(a - b) < 1e-4 end
		return close(x, cx) and close(y, cy) and close(z, cz) and close(w, cw)
	end)
	if ok and same == true then
		M._fastWrite = true
		logger("[physbone] 快路径已启用：每骨 2 次引擎调用（from_elements + set_local_rotation）")
	else
		logger("[physbone] 快路径自检未通过（%s）⇒ 继续用矩阵路径（每骨 5 次调用）",
			tostring(ok and same or same))
	end
	return M._fastWrite
end

--- 调试版：额外做 `is_valid` + `pose_sane` 回读校验。**只在 debugPoseCheck 打开时用**。
local function write_pose_checked(S, unit, node, rotation, base)
	local q = S.Quaternion.axis_angle(S.Vector3(rotation[1], rotation[2], rotation[3]), rotation[4])
	if q == nil then return false end
	local pose = S.Matrix4x4.from_quaternion_position(q, S.Vector3(base.px, base.py, base.pz))
	if pose == nil then return false end
	if not S.Matrix4x4.is_valid(pose) then return false end
	if not pose_sane(S, pose) then return false end
	S.Unit.set_local_pose(unit, node, pose)
	return true
end

--- 纯 Lua 的**输入**自检，替代热路径上的 `is_valid` + `pose_sane` 回读。
--- <para>
--- 原来每根骨写完再回读 12 个数验一遍（`Matrix4x4.is_valid` + `Matrix4x4.to_elements`），
--- 那是 2 次引擎调用/骨/帧。而坏矩阵只可能来自坏的**输入**（轴未归一化、角度 NaN、位置发疯），
--- 所以在这里把输入验掉就够了，等价但零引擎调用。
--- 真正的回读校验保留在 `write_pose_checked`，`debugPoseCheck` 打开时使用。
--- </para>
local function rotation_sane(rotation, base)
	local angle = rotation[4]
	if angle ~= angle or angle < 0 or angle > 6.2832 then return false end
	local ax, ay, az = rotation[1], rotation[2], rotation[3]
	if ax ~= ax or ay ~= ay or az ~= az then return false end
	local norm = ax * ax + ay * ay + az * az
	if norm < 0.5 or norm > 1.5 then return false end       -- 轴必须接近单位长度
	local px, py, pz = base.px, base.py, base.pz
	if px ~= px or py ~= py or pz ~= pz then return false end
	if math.abs(px) > 1e4 or math.abs(py) > 1e4 or math.abs(pz) > 1e4 then return false end
	return true
end

function M.update_tip(session, dt, maxWrite)
	if session == nil or #session.sessions == 0 then return 0 end
	-- ⚠️ 生命周期闸必须在**任何**引擎调用之前（K1/K2）。这条一旦跳过，
	-- 军械库/空投仓换甲时就是对已销毁 Unit 调 world_position/set_local_pose ⇒ 原生 AV。
	local health = M.session_health(session, M.frame)
	if health ~= "ok" then
		if not session.deadLogged then
			session.deadLogged = true
			local logger = session.logger or function() end
			logger("[physbone][LIFECYCLE] 会话失效（%s）：停止驱动该 Unit（%d 条链）",
				health, #session.sessions)
		end
		return 0
	end
	local logger = session.logger or function() end
	local S = session.S
	local unit = session.unit
	-- dt：Valve 自己夹 [0.001, 0.0333]；半隐式积分的稳定域是 k·h² + 2c·h < 4，
	-- 所以大 dt 必须拆子步，不能硬塞（旧核只把 >0.1 的换成 1/60，等于掩盖问题）。
	if dt == nil or dt <= 0 or dt > 0.25 then dt = 1 / 60 end
	if dt < 0.001 then dt = 0.001 end
	local steps = 1
	if dt > 1 / 60 then steps = math.min(4, math.ceil(dt / (1 / 60))) end
	local h = dt / steps

	local writePose = session.debugPoseCheck and write_pose_checked or write_pose
	-- "姿态没变就别写"的阈值（弧度）。**这是性能上最划算的一个旋钮**：
	-- 每次写入要 2 次引擎调用（`Quaternion.from_elements` + `set_local_rotation`），
	-- 而实机实测 ~1000 次引擎调用 ≈ 2.8 ms（341~418 条链）。把几乎没变的姿态跳过，
	-- 直接按比例省引擎调用。
	--
	-- 取值依据：0.0015 rad ≈ 0.086°。对一根 10 cm 的骨，是 0.15 mm 的末端位移 ——
	-- 肉眼与动画分辨率都吃不出差别。旧值 0.0004（0.023°）过于保守：慢速摆动时
	-- 绝大多数骨每帧都在"刚刚超一点点"，于是每帧照写。
	local epsilon = session.writeEpsilon or 0.0015
	-- ── 按"离相机多远"加权（2026-11-03）──────────────────────────────────
	-- 判据是 `|Δθ| > 阈值`，所以**放大阈值 = 要求更大的变化才值得写**：
	--   · 自己的护甲（order=1）权重 1.0 ⇒ 阈值 0.086°，一分不省、观感不变；
	--   · 队友/远处的护甲权重递减 ⇒ 阈值放大 2~3 倍 ⇒ 只有明显动了才写。
	-- 角度大的骨天然更容易越过阈值 ⇒ "角度小就少写"不需要额外判断项。
	--
	-- ⚠️ 距离按**套护甲**算，不按每根骨算：同一套护甲所有骨的跨度只有 ~1 m，
	-- 相对相机几米到几十米，骨与骨的差别全是噪声；而按骨算要额外调
	-- `Unit.world_position` —— 那正是我们要省的引擎调用。按"套"算，距离信息**免费**
	-- （运行时本来就按相机距离给玩家排过序，`ownerOrder` 就是它）。
	local order = session.ownerOrder or 1
	local weight = 1.0
	if order == 2 then
		weight = 0.55
	elseif order == 3 then
		weight = 0.40
	elseif order >= 4 then
		weight = 0.30
	end
	if session.remoteWriteWeight ~= nil and order > 1 then
		weight = session.remoteWriteWeight
	end
	epsilon = epsilon / weight
	-- 保险：每 refreshEvery 帧无条件全量写一次。
	-- 脏标记假设"我们上次写的姿态引擎会保留"——这条实测成立，但万一某条路径把节点重置了
	-- （切 LOD / 引擎重算 bind），没有这次全量刷新就会永久停在错的位置上。
	session.refreshCounter = (session.refreshCounter or 0) + 1
	if session.refreshCounter >= (session.refreshEvery or 240) then
		session.refreshCounter = 0
		for _, item in ipairs(session.sessions) do item.lastWritten = nil end
	end

	-- ★ 锚点读数**按节点缓存**：一条护甲的 15 条头发链往往都挂同一个 head 节点，
	-- 原来每条链都读一遍 world_position + world_rotation（2 次引擎调用），15 条就是 30 次。
	-- 现在每个**不同的**锚点节点每帧只读一次。
	--
	-- 缓存记录**常驻**、用"帧序号"标记有效，不再每帧 pairs 清空 + 重建 table：
	-- 那会每秒产生上万个短命 table（PUC Lua 5.1 是标记清扫，这些都会进 GC）。
	-- 记录布局：[1..3]=位置 [4..7]=四元数 [8]=最后采样的帧序号
	session.anchorEpoch = (session.anchorEpoch or 0) + 1
	local epoch = session.anchorEpoch
	local anchors = session.anchorCache
	if anchors == nil then anchors = {}; session.anchorCache = anchors end

	local items = session.sessions
	local itemCount = #items
	-- 运动门限：锚点**自上次真正求解以来**移动/转动小于这个量，且尖端已静止 ⇒ 整条链跳过。
	-- ⚠️ 和"逐位相等"的区别：人台/待机角色会有极小的动画抖动，逐位相等抓不到，
	-- 于是它们每帧都在跑完整求解（实测 20 个 unit 里一大半是这种）。用容差才能真正省下来。
	-- 因为比较的是"上次求解时"的锚点，缓慢持续移动会累积超过门限、自然触发一次求解，不会漏。
	local motionEps2 = session.motionEpsilon or 0.001
	motionEps2 = motionEps2 * motionEps2
	local motionDot = session.motionCosAngle or 0.99999887   -- cos(0.0015 rad) ≈ 0.086°
	local staticLimit = session.staticSkipLimit or 120
	local jumpThreshold = session.jumpThreshold or 0.25
	local jumpEps2 = jumpThreshold * jumpThreshold
	local writeEpsilonNeg = -epsilon
	-- 局部化热点引用：PUC Lua 5.1 不会自动消掉重复的 table 链式查找。
	local stepTip, localRotations = M.step_tip, M.local_rotations

	local written, skippedSame = 0, 0
	-- 本帧**引擎调用次数**（估算但可核对）：
	--   每个不同锚点 = world_position + Vector3.to_elements + world_rotation + Quaternion.to_elements = 4
	--   每根写成功的骨 = from_elements + set_local_rotation（快路径 2）或矩阵路径 5
	--   外加 1 次 Unit.alive + 最多 1 次 World.update_unit
	-- 为什么不用计时：`Application.time_since_launch` 每帧只更新一次，块内首尾差值恒为 0；
	-- `os.clock()` 又会把引擎其它线程的占用算进来（实测同工作量 1.58 ms vs 5.21 ms）。
	-- 调用次数是唯一不会被环境噪声污染的口径。
	local calls = 1
	local perWrite = (M._fastWrite == true) and 2 or 5

	for si = 1, itemCount do
		local item = items[si]
		local tip = item.tip
		if not item.disabled and tip ~= nil then
			local anchorNode = item.anchorNode
			local cached = anchors[anchorNode]
			if cached == nil then
				cached = { 0, 0, 0, 0, 0, 0, 1, 0 }
				anchors[anchorNode] = cached
			end
			if cached[8] ~= epoch then
				local apx, apy, apz = read_vec3(S, S.Unit.world_position(unit, anchorNode))
				local qx, qy, qz, qw = read_quat4(S, unit, anchorNode)
				if apx ~= nil and qx ~= nil then
					cached[1], cached[2], cached[3] = apx, apy, apz
					cached[4], cached[5], cached[6], cached[7] = qx, qy, qz, qw
					cached[8] = epoch
					calls = calls + 4
				else
					cached = nil
					calls = calls + 4
				end
			end
			if cached ~= nil then
				local apx, apy, apz = cached[1], cached[2], cached[3]
				local qx, qy, qz, qw = cached[4], cached[5], cached[6], cached[7]
				-- 与**上次真正求解时**的锚点比：位移平方 + 四元数点积（|dot| 接近 1 = 转得很少）
				local lastQ = item.lastAnchorQ
				local static = false
				if lastQ ~= nil and (tip.maxSpeed2 or 0) < 1e-8
					and (item.staticFrames or 0) < staticLimit then
					local dx, dy, dz = apx - lastQ[1], apy - lastQ[2], apz - lastQ[3]
					if (dx * dx + dy * dy + dz * dz) < motionEps2 then
						local d = lastQ[4] * qx + lastQ[5] * qy + lastQ[6] * qz + lastQ[7] * qw
						if d < 0 then d = -d end
						if d > motionDot then static = true end
					end
				end
				if static then
					item.staticFrames = (item.staticFrames or 0) + 1
					-- 这些骨这帧"处理过但没写"，计入 handled，否则上层会误判成"驱动失效"。
					skippedSame = skippedSame + #tip.bones
				else
				item.staticFrames = 0
				tip.maxSpeed2 = 0
				if lastQ == nil then lastQ = {}; item.lastAnchorQ = lastQ end
				lastQ[1], lastQ[2], lastQ[3] = apx, apy, apz
				lastQ[4], lastQ[5], lastQ[6], lastQ[7] = qx, qy, qz, qw
				-- 位置跳变（换装 / 切 LOD / 瞬移）：让尖端直接对齐目标，比"静默 N 帧"恢复得快
				local jumped = false
				if item.lastAnchor ~= nil then
					local dx = apx - item.lastAnchor[1]
					local dy = apy - item.lastAnchor[2]
					local dz = apz - item.lastAnchor[3]
					jumped = (dx * dx + dy * dy + dz * dz) > jumpEps2
				end
				local tips = tip.tips
				if jumped then
					for index = 1, #tips do tips[index].ready = false end
					item.lastWritten = nil   -- 跳变后必须整条重写（否则脏标记会让我们以为"没变"）
					item.jumps = (item.jumps or 0) + 1
					if item.jumps <= 3 then
						logger("[physbone][RESYNC] 锚点跳变，%d 个尖端已对齐目标（第 %d 次）",
							#tips, item.jumps)
					end
				end
				local lastAnchor = item.lastAnchor
				if lastAnchor == nil then lastAnchor = {}; item.lastAnchor = lastAnchor end
				lastAnchor[1], lastAnchor[2], lastAnchor[3] = apx, apy, apz

				for _ = 1, steps do stepTip(tip, h, apx, apy, apz, qx, qy, qz, qw) end
				local rotations = localRotations(tip, qx, qy, qz, qw)
				item.lastRotations = rotations   -- 给观测层读（探针按它打"各节转角"）

				local maxAngle = 0
				local lastWritten = item.lastWritten
				if lastWritten == nil then lastWritten = {}; item.lastWritten = lastWritten end
				local basePoses, nodeIds = item.basePoses, item.nodes
				local boneCount = #tip.bones
				for index = 1, boneCount do
					if maxWrite and written >= maxWrite then break end
					local base = basePoses[index]
					local rotation = rotations[index]
					-- 裙摆外推：只对**链根**叠一个额外旋转（世界轴 → 锚点父空间）。
					-- 为什么只动链根：整片布是靠链根朝向摆出去的，逐节动会把布扯变形。
					-- `item.pushOut` 由运行时算好（世界坐标下的轴 + 角度），默认是 nil ⇒ 零影响。
					if index == 1 and rotation ~= nil and item.pushOut ~= nil then
						local po = item.pushOut
						local cxq, cyq, czq, cwq = qconj(qx, qy, qz, qw)
						local pax, pay, paz = qrot(cxq, cyq, czq, cwq, po[1], po[2], po[3])
						rotation = M.push_out_rotation(rotation, pax, pay, paz, po[4])
					end
					if base ~= nil and rotation ~= nil then
						if rotation[4] > maxAngle then maxAngle = rotation[4] end
						if rotation_sane(rotation, base) then
							-- 和上次写进去的值比：几乎没变就跳过（引擎里那一份还在）。
							-- 用两次比较代替 math.abs（省一次标准库 C 调用）。
							local prev = lastWritten[index]
							local same = false
							if prev ~= nil then
								local d = prev[4] - rotation[4]
								if d >= writeEpsilonNeg and d <= epsilon then
									same = (prev[1] * rotation[1] + prev[2] * rotation[2]
										+ prev[3] * rotation[3]) > 0.99999
								end
							end
							if same then
								skippedSame = skippedSame + 1
							else
								local okWrite, didWrite = pcall(writePose, S, unit, nodeIds[index], rotation, base)
								if okWrite and didWrite == true then
									written = written + 1
									calls = calls + perWrite
									if prev == nil then prev = {}; lastWritten[index] = prev end
									prev[1], prev[2], prev[3], prev[4] =
										rotation[1], rotation[2], rotation[3], rotation[4]
								else
									session.skippedWrites = (session.skippedWrites or 0) + 1
									if session.firstFailReason == nil then
										session.firstFailReason = string.format("tip node#%d %s", index, tostring(didWrite))
										logger("[physbone][失败] 尖端核写入失败：%s", session.firstFailReason)
									end
								end
							end
						else
							-- 输入自检没过：绝不写，也不静默（坏输入只可能是上游算错）
							session.skippedWrites = (session.skippedWrites or 0) + 1
							if session.firstFailReason == nil then
								session.firstFailReason = string.format("tip node#%d 输入自检未通过", index)
								logger("[physbone][失败] 尖端核输入异常：%s（angle=%.3f）",
									session.firstFailReason, rotation[4])
							end
						end
					end
				end

				-- G4 活度：连"数值全合法、写入全成功、但一直不动"的钢板状态也要能报出来。
				-- 这是旧三道闸（写前 / 写后 / 零写入）全都抓不到的一类失败。
				local angleDeg = maxAngle * 180 / math.pi
				if angleDeg > (item.maxAngleDeg or 0) then item.maxAngleDeg = angleDeg end
				item.frames = (item.frames or 0) + 1
				if angleDeg > 0.5 then item.lastMoveFrame = item.frames end
				if (item.frames - (item.lastMoveFrame or 0)) > 600 and not item.frozenLogged then
					item.frozenLogged = true
					logger("[physbone][G4] 连续 600 帧最大偏角只有 %.2f° ⇒ 求解器活着但冻结（查输入为 0 / 增益映射）",
						angleDeg)
				end
				end   -- 收尾：静态链跳过分支的 else
			end
		end
	end

	if written > 0 and session.world ~= nil then
		pcall(function() S.World.update_unit(session.world, unit) end)
		calls = calls + 1
	end
	session.engineCalls = calls
	session.skippedSame = skippedSame
	session.tipApplied = (session.tipApplied or 0) + 1
	return written
end

return M
