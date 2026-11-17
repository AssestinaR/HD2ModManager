local module = dofile(arg[1])
local carrier, donor, helmet = '293d8135eb95873b', '74fa01a11be3f987', 'bc20d0b4efff128c'
local text = carrier .. ',' .. donor .. ',2,3,1:2:3:1,' .. carrier .. ':' .. helmet
local rule = assert(module.parse(text))
assert(not module.parse(text:gsub('1:2:3:1', '1:2:3:2')))
assert(not module.parse(text:gsub('1:2:3:1', '1:2:4:1')))

local world, preview = {}, {}
local currentWorld = world
local now, spawned, linked, destroyed = 0, 0, 0, 0
local loaded, rejectBinding = false, false
local avatar = { nodes = 2, position = { 1, 2, 3 } }
local shoulder = { id = carrier, nodes = 2, position = { 1, 2, 3 }, visible = true }
local visor = { id = helmet, nodes = 2, position = { 1, 2, 3 }, visible = true }
local anotherVisor = { id = helmet, nodes = 2, position = { 6, 2, 3 }, visible = true }
local previewShoulder = { id = carrier, nodes = 2, position = { 1, 2, 3 }, visible = true }
local mainShoulders = { shoulder }
local sr = {
	Application = {
		main_world = function() return currentWorld end,
		worlds = function() return { world, preview } end,
		time_since_launch = function() return now end,
		can_get = function(kind, id) return loaded and kind == 'unit' and id == donor end,
	},
	World = {
		units_by_resource = function(w, id)
			if w == currentWorld and w ~= world then return {} end
			assert(w == world, 'a preview world was searched')
			if id == carrier then return mainShoulders end
			if id == helmet then return { visor, anotherVisor } end
			return { avatar }
		end,
		spawn_unit = function(w, id)
			assert(w == world and id == donor)
			spawned = spawned + 1
			return { nodes = 3, id = id, visible = true }
		end,
		link_unit = function(w, model, modelNode, owner, ownerNode)
			assert(w == world and model.id == donor and owner == avatar)
			assert((modelNode == 1 and ownerNode == 2) or (modelNode == 3 and ownerNode == 1))
			if rejectBinding and modelNode == 3 then error('test binding failure') end
			linked = linked + 1
		end,
		unlink_unit = function(w, model) assert(w == world and model.id == donor) end,
		destroy_unit = function(w, model)
			assert(w == world and model.id == donor)
			destroyed = destroyed + 1
			model.dead = true
		end,
	},
	Unit = {
		alive = function(unit) return unit and not unit.dead end,
		world_position = function(unit) return unit.position end,
		num_scene_graph_items = function(unit) return unit.nodes end,
		resource_name = function(unit) return '#ID[' .. unit.id .. ']' end,
		set_unit_visibility = function(unit, value)
			assert(unit ~= previewShoulder)
			unit.visible = value
		end,
	},
	IdString64 = { from_hex = function(id) return id end },
	Vector3 = { to_elements = unpack },
	Vector4 = function(...) return { ... } end,
}
local mapping = module.new(sr, print)
mapping.configure({ rule })
mapping.update()
assert(spawned == 0 and shoulder.visible and visor.visible)

loaded, now = true, 1
mapping.update()
assert(spawned == 1 and linked == 2 and not shoulder.visible and not visor.visible)
assert(previewShoulder.visible and anotherVisor.visible)

now = 2
mapping.update()
assert(spawned == 1)
mainShoulders, now = {}, 3
mapping.update()
assert(destroyed == 1 and shoulder.visible and visor.visible)

mainShoulders, now = { shoulder }, 4
mapping.update()
assert(spawned == 2 and not shoulder.visible)
mapping.configure({})
mapping.update()
assert(destroyed == 2 and shoulder.visible and visor.visible)

mapping.configure({ rule })
mapping.update()
assert(spawned == 3 and not shoulder.visible)
currentWorld, now = {}, 5
mapping.update()
assert(destroyed == 3 and shoulder.visible and visor.visible)

currentWorld, rejectBinding, now = world, true, 6
mapping.update()
assert(spawned == 4 and destroyed == 4 and shoulder.visible and visor.visible)
now = 7
mapping.update()
assert(spawned == 4, 'failed binding is not retried every poll')
print('player armour instance tests passed')
