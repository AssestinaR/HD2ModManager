-- 裙摆外推的**结构性**防回归。
--
-- 这不是行为测试，是"别再把开关藏回去"的护栏。为什么需要它：
-- 同一个坑我踩了两次 —— 把"有没有亏损"的判据写在 target 那一行之外，
-- 结果函数在更上面就 `if deficit <= 0 then return false end` 提前返回，
-- 下面那句"无条件加"成了死代码，实机上表现为"大多被拉回、少数保持角度"，
-- 而日志里的角度看起来又是对的（那条链确实拿到了角度）。
--
-- 用法：luajit HD2PhysBoneCompat/test/skirt-guard-test.lua [runtime]

local path = arg[1] or "HD2PhysBoneCompat/src/physbone_runtime.lua"
local handle = assert(io.open(path, "r"))
local source = handle:read("*a")
handle:close()

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-52s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

-- 只取 skirt_chain 的函数体（到下一个顶层 local function 为止）
local function body_of(name)
	local from = source:find("local function " .. name .. "(", 1, true)
	if from == nil then return nil end
	local to = source:find("\nlocal function ", from + 1, true)
	return source:sub(from, to or #source)
end

-- 去掉 `--` 行注释再判：这些函数里刻意留了"以前这里是开关"的注释，
-- 不剥掉就会自己把自己判成失败（踩过一次）。
local function strip_comments(text)
	return (text:gsub("%-%-[^\r\n]*", ""))
end

local chain = body_of("skirt_chain")
check("找得到 skirt_chain", chain ~= nil)
chain = chain ~= nil and strip_comments(chain) or nil

if chain ~= nil then
	-- ① 亏损不能当开关：不许出现 "deficit ... <= 0" 后跟着 return
	local has_deficit_gate = chain:match("deficit%s*<%s*=%s*0") ~= nil
		or chain:match("deficit%s*<=%s*0") ~= nil
	check("亏损不是开关（没有 deficit<=0 的提前返回）", not has_deficit_gate,
		has_deficit_gate and "发现 deficit 比较 —— 这多半又是个隐藏的门" or "")

	-- ② 权重必须是 cos（偏前为 0），不是线性 (1 − θ/90)
	local has_cos = chain:find("dot > 0 and dot", 1, true) ~= nil
	local has_linear = chain:find("1 - deviation / 90", 1, true) ~= nil
		or chain:find("1 - deviation/90", 1, true) ~= nil
	check("权重用 cos(夹角)（dot 本身）", has_cos, has_cos and "" or "没找到 `dot > 0 and dot`")
	check("不再用线性 1 − θ/90", not has_linear, has_linear and "还残留线性权重" or "")

	-- ③ 角度为 0 时必须清掉 pushOut（否则引擎里会留上一帧的旧值）
	check("零角度会清掉 pushOut", chain:find("item.pushOut = nil", 1, true) ~= nil)
end

-- ④ 选中判据不能又退回"只认 hips"
local apply = body_of("skirt_apply")
check("找得到 skirt_apply", apply ~= nil)
apply = apply ~= nil and strip_comments(apply) or nil
if apply ~= nil then
	local only_hips = apply:find('find("hips", 1, true)', 1, true) ~= nil
	check("选中判据不再写死 hips", not only_hips,
		only_hips and "skirt_apply 里还写着 find(\"hips\")" or "")
	check("走 skirt_eligible", apply:find("skirt_eligible(", 1, true) ~= nil)
end

print(string.format("\n失败 %d 项", failures))
os.exit(failures == 0 and 0 or 1)
