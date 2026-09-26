local R=assert(loadfile('HD2RuntimeArmorOverride/src/armor_refresh_resources.lua'))()
local now,reads,rows=0,0,{}
local w,u={},{}
local race=false
local h={world_index=1,clock=function()return now end,cache={},
    globals={stingray={Application={worlds=function()return {w}end},
        World={num_units=function(x)assert(x==w);return 1 end,units=function()return {u}end},
        Unit={alive=function(x)assert(x==u);return true end,world=function()end,
            name_hash=function()return '1234567890abcdef'end}}},
    inspect_unit=function(_,x,_,resolve)
        assert(x==u and resolve);reads=reads+1
        return {state='resolved_candidate',object=race and reads%2==0 and 'other' or 'child',
            object_world='world',handle_index=2,handle_generation=3}
    end,emit=function(r)rows[#rows+1]=r end}
local r=R.new(h)
assert(r.tick({child=true},'world'))
assert(#rows==1 and rows[1].resource_hash=='1234567890abcdef')
race=true;reads=0;r.tick({child=true},'world');assert(#rows==1)
race=false;r.tick({child=true},'different_world');assert(#rows==1)
h.globals.stingray.World.num_units=function()return 9000 end
assert(not r.tick({child=true},'world') and r.errors==1)
print('PASS: resource IDs from legitimate handles, generation/object recheck, world filter, bounds')
