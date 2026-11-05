-- 「世界朝向锚定」的判据回归
--
-- 语义：开了之后，链的静止朝向钉在**绑定姿态**上，父级动画只带平移、不带转动。
-- 这是给**袖子**做的（挂在会大幅摆动的手臂上，让布滞后）。
--
-- 2026-11-05 实机：Soppo（包 L2D-3702）胸口往上飘 —— `Chest_L`/`Chest_R` 的 flags
-- 也是 0x3D，可它们挂在 **Spine4（躯干）**上，于是布不跟躯干转、只跟着平移。
-- 所以判据从"只看 flags"改成"flags + 挂在哪根骨上"，但**只在能证明是躯干/头/胯时
-- 才关掉**：解析不出名字的辅助骨（source_extra_91）一律保持旧行为，不拿猜出来的名字
-- 去改已经验收过的观感。
--
-- 用法：luajit HD2PhysBoneCompat/test/world-anchor-test.lua [模块路径]

local path = arg[1] or "HD2PhysBoneCompat/src/physbone_compat.lua"
local M = assert(assert(loadfile(path)))()

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-58s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local SLEEVE = 0x3D   -- 0x01|0x04|0x08|0x10|0x20：yaw+pitch+angle 三约束（Lilium 袖子）
local HAIR = 0x31     -- 0x01|0x10|0x20：没有 yaw/pitch（Lilium 头发）
local HAIR_YP = 0x2D  -- 0x01|0x04|0x08|0x20：有 yaw/pitch 但没有 angle（另一批头发）

print("判据")
local on, why = M.world_anchor_of(SLEEVE, "l_shoulder")
check("袖子挂手骨 ⇒ 锚定", on == true, tostring(why))
on, why = M.world_anchor_of(SLEEVE, "L_UpperArm")
check("源侧名字 L_UpperArm ⇒ 锚定", on == true, tostring(why))
on, why = M.world_anchor_of(SLEEVE, "ValveBiped.Bip01_L_UpperArm")
check("带前缀的源侧名字 ⇒ 锚定", on == true, tostring(why))

print()
print("这次要修的那一处")
on, why = M.world_anchor_of(SLEEVE, "chest")
check("Soppo 的 Chest_L：锚点 chest ⇒ 不锚定", on == false, tostring(why))
on, why = M.world_anchor_of(SLEEVE, "Spine4")
check("源侧名字 Spine4 ⇒ 不锚定", on == false, tostring(why))
for _, name in ipairs({ "hips", "spine1", "spine2", "neck", "head" }) do
	on, why = M.world_anchor_of(SLEEVE, name)
	check("锚点 " .. name .. " ⇒ 不锚定", on == false, tostring(why))
end

print()
print("不该被误伤的")
on, why = M.world_anchor_of(HAIR, "chest")
check("头发 flags 没三约束 ⇒ 无论如何都不锚定", on == false, tostring(why))
on, why = M.world_anchor_of(HAIR_YP, "l_shoulder")
check("0x2D（yaw/pitch 但无 angle）⇒ 不锚定", on == false, tostring(why))
on, why = M.world_anchor_of(SLEEVE, "source_extra_91")
check("锚点解析不出（辅助骨名）⇒ 保持旧行为（锚定）", on == true, tostring(why))
on, why = M.world_anchor_of(SLEEVE, nil)
check("锚点完全未知 ⇒ 保持旧行为（锚定）", on == true, tostring(why))
on, why = M.world_anchor_of(SLEEVE, "l_foot")
check("锚点在腿（非躯干）⇒ 不改变旧行为", on == true, tostring(why))

print()
if failures == 0 then
	print("world-anchor: 全部通过")
	os.exit(0)
else
	print(string.format("world-anchor: %d 条失败", failures))
	os.exit(1)
end
