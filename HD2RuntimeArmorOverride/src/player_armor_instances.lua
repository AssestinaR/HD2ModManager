-- Scoped armour replacements run only in the gameplay world.
local AVATAR = 'content/fac_helldivers/cha_avatar/avatar_helldiver'
local RADIUS_SQUARED = 0.3 * 0.3
local POLL_SECONDS = 0.2

local function try(fn, ...)
	if type(fn) ~= 'function' then return nil end
	local ok, value = pcall(fn, ...)
	if ok then return value end
end

local function validId(value)
	return #value == 16 and value:match('^[0-9a-fA-F]+$') ~= nil
end

local function parse(value)
	local carrier, model, avatarCount, modelCount, links, hide =
		value:match('^(%x+),(%x+),(%d+),(%d+),([%d:]+),([%x:]+)$')
	if not carrier or not validId(carrier) or not validId(model) then return nil end
	avatarCount, modelCount = tonumber(avatarCount), tonumber(modelCount)
	if avatarCount < 1 or avatarCount > modelCount or modelCount > 4096 then return nil end
	local boneLinks, parentSeen, previousChild = {}, {}, 0
	for number in links:gmatch('[^:]+') do boneLinks[#boneLinks + 1] = tonumber(number) end
	if #boneLinks ~= avatarCount * 2 then return nil end
	for i = 1, #boneLinks, 2 do
		local child, parent = boneLinks[i], boneLinks[i + 1]
		if child <= previousChild or child > modelCount or parent < 1 or parent > avatarCount or parentSeen[parent] then
			return nil
		end
		parentSeen[parent], previousChild = true, child
	end
	local hideIds = {}
	for id in hide:gmatch('[^:]+') do
		if not validId(id) or #hideIds >= 128 then return nil end
		hideIds[#hideIds + 1] = id:lower()
	end
	return {
		key = value, carrier = carrier:lower(), model = model:lower(),
		avatarCount = avatarCount, modelCount = modelCount, links = boneLinks, hide = hideIds,
	}
end

local function new(sr, log)
	local A, W, U, Id64, V3 = sr.Application, sr.World, sr.Unit, sr.IdString64, sr.Vector3
	local rules, sessions, failed = {}, {}, {}
	local world, nextPoll

	local function alive(unit)
		return try(U.alive, unit) == true
	end

	local function vector(v)
		if v == nil then return nil end
		local x, y, z = V3.to_elements(v)
		if type(x) == 'number' and type(y) == 'number' and type(z) == 'number' then return { x, y, z } end
	end

	local function position(unit, node)
		return vector(try(U.world_position, unit, node))
	end

	local function near(a, b, maxDistanceSquared)
		if not a or not b then return false end
		local x, y, z = a[1] - b[1], a[2] - b[2], a[3] - b[3]
		return x*x + y*y + z*z <= maxDistanceSquared
	end

	local function resources(id)
		return try(W.units_by_resource, world, Id64.from_hex(id)) or {}
	end

	local function resourceId(unit)
		return tostring(try(U.resource_name, unit)):lower():match('#id%[(%x+)%]')
	end

	local function release(session)
		for unit, id in pairs(session.hidden) do
			if alive(unit) and resourceId(unit) == id then pcall(U.set_unit_visibility, unit, true) end
		end
		if alive(session.model) then
			pcall(W.unlink_unit, world, session.model)
			pcall(W.destroy_unit, world, session.model)
		end
	end

	local function begin(avatar, rule)
		if failed[rule.key] or try(A.can_get, 'unit', Id64.from_hex(rule.model)) ~= true then return nil end
		local model
		local ok, err = pcall(function()
			local rotation = sr.Vector4(0, 0, 0, 1)
			model = W.spawn_unit(world, Id64.from_hex(rule.model), U.world_position(avatar, 1), rotation)
			if not alive(model) or resourceId(model) ~= rule.model
				or U.num_scene_graph_items(model) ~= rule.modelCount then
				error('spawn did not return the requested model and rig')
			end
		end)
		if not ok then
			-- Invalid fallback units may be owned by the engine. Leave them to world teardown.
			failed[rule.key] = true
			log('player armour spawn failed: ' .. tostring(err))
			return nil
		end

		local session = { rule = rule, model = model, hidden = {} }
		local linked, linkError = pcall(function()
			for i = 1, #rule.links, 2 do
				W.link_unit(world, model, rule.links[i], avatar, rule.links[i + 1])
			end
		end)
		if not linked then
			release(session)
			failed[rule.key] = true
			log('player armour binding failed: ' .. tostring(linkError))
			return nil
		end
		log('player armour attached: ' .. rule.carrier .. ' -> ' .. rule.model)
		return session
	end

	local function update()
		if #rules == 0 and next(sessions) == nil then return end
		local main = try(A.main_world)
		if main ~= world then
			if world ~= nil and next(sessions) ~= nil then
				for _, liveWorld in ipairs(try(A.worlds) or {}) do
					if liveWorld == world then
						for _, session in pairs(sessions) do release(session) end
						break
					end
				end
			end
			-- A retired world owns the destruction of its units.
			world, sessions, failed, nextPoll = main, {}, {}, nil
		end
		if world == nil then return end
		local now = A.time_since_launch()
		if next(sessions) ~= nil and sr.Matrix4x4 then
			local pose = try(W.debug_camera_pose, world)
			local eye = pose and vector(try(sr.Matrix4x4.translation, pose))
			if eye then
				for avatar, session in pairs(sessions) do
					if alive(avatar) and alive(session.model) and try(U.has_node, avatar, 'head') == true then
						local head = try(U.node, avatar, 'head')
						local visible = not near(eye, head and position(avatar, head), 0.6 * 0.6)
						if session.visible ~= visible then
							U.set_unit_visibility(session.model, visible)
							session.visible = visible
						end
					end
				end
			end
		end
		if nextPoll and now < nextPoll then return end
		nextPoll = now + POLL_SECONDS

		local avatars, matched = {}, {}
		for _, avatar in ipairs(try(W.units_by_resource, world, AVATAR) or {}) do
			if alive(avatar) and (try(U.num_scene_graph_items, avatar) or 0) > 0 then
				avatars[avatar] = position(avatar, 1)
			end
		end
		for _, rule in ipairs(rules) do
			for _, shoulder in ipairs(resources(rule.carrier)) do
				if alive(shoulder) and (try(U.num_scene_graph_items, shoulder) or 0) > 0 then
					local p, owner = position(shoulder, 1), nil
					for avatar, origin in pairs(avatars) do
						if near(p, origin, RADIUS_SQUARED) then
							if owner then owner = nil; break end
							owner = avatar
						end
					end
					if owner and U.num_scene_graph_items(owner) == rule.avatarCount then
						matched[owner] = matched[owner] == nil and rule or false
					end
				end
			end
		end
		for avatar, session in pairs(sessions) do
			local rule = matched[avatar]
			if not rule or rule.key ~= session.rule.key or not alive(session.model) then
				release(session)
				sessions[avatar] = nil
			end
		end
		for avatar, rule in pairs(matched) do
			if rule then
				local session = sessions[avatar] or begin(avatar, rule)
				if session then
					sessions[avatar] = session
					for part in pairs(session.hidden) do
						if not alive(part) then session.hidden[part] = nil end
					end
					for _, id in ipairs(rule.hide) do
						for _, part in ipairs(resources(id)) do
							if alive(part) and (try(U.num_scene_graph_items, part) or 0) > 0
								and near(position(part, 1), avatars[avatar], RADIUS_SQUARED) then
								U.set_unit_visibility(part, false)
								session.hidden[part] = id
							end
						end
					end
				end
			end
		end
	end

	local function configure(nextRules)
		rules, nextPoll = nextRules, nil
	end
	return { update = update, configure = configure }
end

return { new = new, parse = parse }
