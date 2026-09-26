local P=assert(loadfile('HD2RuntimeArmorOverride/src/armor_refresh_probe.lua'))()
local now,request=0,nil
local statuses,logs={},{}
local accessed=0
local player=setmetatable({armor_id=42,account_name='DO_NOT_CAPTURE',
    appearance={dirty=false},refresh=function()accessed=accessed+1 end},
    {__index=function()error('getter invoked')end,__pairs=function()error('pairs invoked')end})
local globals={player=player,stingray={Unit={},Application={worlds=function()
    accessed=accessed+1;return {}
end}},package={loaded={}}}
local p=P.new({globals=globals,pid=123,clock=function()return now end,
    revision=function()return 7 end,getinfo=debug.getinfo,
    read=function()return request end,
    write=function(_,s)statuses[#statuses+1]=s;return true end,
    open=function(name)
        logs[name]=''
        return {write=function(_,s)logs[name]=logs[name]..s;return true end,
            flush=function()end,close=function()end}
    end})
local session=string.rep('a',32)
local function send(seq,command,extra,pid)
    request='schema=1\npid='..(pid or 123)..'\nsession='..session
        ..'\nrequest='..seq..'\ncommand='..command..'\n'..(extra or '')
    now=now+0.6;p.tick()
end
local function frames(n)
    for _=1,n do now=now+0.05;p.tick() end
end
frames(30);assert(#statuses==0 and next(logs)==nil)
send(1,'ping',nil,999);assert(#statuses==0)
send(1,'ping');assert(statuses[#statuses]:find('state=ready',1,true))
send(2,'capture','phase=equip_b\nduration=5\n')
assert(statuses[#statuses]:find('state=capturing',1,true))
frames(20);player.armor_id=43;player.appearance.dirty=true;frames(100)
assert(statuses[#statuses]:find('state=complete',1,true))
local text=logs['refresh-'..session..'-2.jsonl']
assert(text:find('number:42',1,true) and text:find('number:43',1,true))
assert(text:find('"change":"changed"',1,true))
assert(not text:find('DO_NOT_CAPTURE',1,true) and accessed==0)
assert(text:find('Application',1,true) and text:find('worlds',1,true),
    'explicit Application surface was not inspected')
local count=#statuses
frames(10);assert(#statuses==count,'same request replayed')
send(1,'ping');assert(#statuses==count,'stale sequence accepted')
send(3,'capture','phase=idle\nduration=5\n');send(4,'stop')
assert(statuses[#statuses]:find('state=stopped',1,true))
assert(not pcall(send,5,'capture','phase=../bad\nduration=5\n'))
p.fail()
assert(statuses[#statuses]:find('state=error',1,true))
print('PASS: guided probe handshake, PID/sequence checks, deltas, privacy, stop, no getters/calls')

-- Native reads must require explicit mode and successful World preflight.
local native_ticks=0
local native=P.new({globals=globals,pid=123,clock=function()return now end,
    revision=function()return 7 end,
    read=function()return request end,
    write=function(_,s)statuses[#statuses+1]=s;return true end,
    open=function()
        return {write=function()return true end,flush=function()end,close=function()end}
    end,
    world_probe={new=function()
        local state={worlds_ok=false,errors=0,units_seen=0,hashes_seen=0,max_call_ms=0}
        function state.tick(mode)
            native_ticks=native_ticks+1;state.worlds_ok=true
            if mode=='units' then state.units_seen=1;state.hashes_seen=1 end
        end
        return state
    end}})
local function native_send(seq,command,extra)
    request='schema=1\npid=123\nsession='..session..'\nrequest='..seq
        ..'\ncommand='..command..'\n'..(extra or '')
    now=now+0.6;native.tick()
end
native_send(1,'ping')
assert(not pcall(native_send,2,'capture','phase=early\nmode=units\nduration=5\n'))
native.fail()
native_send(3,'capture','phase=metadata\nduration=5\n')
for _=1,110 do now=now+0.05;native.tick() end
assert(native_ticks==0)
native_send(4,'capture','phase=worlds\nmode=worlds\nduration=5\n')
for _=1,110 do now=now+0.05;native.tick() end
assert(statuses[#statuses]:find('worlds_validated=true',1,true))
native_send(5,'capture','phase=units\nmode=units\nduration=5\n')
for _=1,110 do now=now+0.05;native.tick() end
assert(statuses[#statuses]:find('native_units_seen=1',1,true))
native_send(6,'capture','phase=focus\nmode=focus\nduration=5\n')
for _=1,110 do now=now+0.05;native.tick() end
assert(statuses[#statuses]:find('state=complete',1,true))
session=string.rep('b',32)
native_send(1,'ping')
assert(not pcall(native_send,2,'capture','phase=early_focus\nmode=focus\nduration=5\n'))
assert(not pcall(native_send,3,'capture','phase=early_owner\nmode=owner_data\nduration=5\n'))
assert(not pcall(native_send,4,'capture','phase=early_native\nmode=owner_native\nduration=5\n'))
assert(not pcall(native_send,5,'capture','phase=early_trace\nmode=owner_trace\nduration=5\n'))
native.fail()
native_send(6,'capture','phase=worlds\nmode=worlds\nduration=5\n')
for _=1,110 do now=now+0.05;native.tick() end
native_send(7,'capture','phase=owner\nmode=owner_native\nduration=5\n')
for _=1,110 do now=now+0.05;native.tick() end
assert(statuses[#statuses]:find('state=complete',1,true))
native_send(8,'capture','phase=trace\nmode=owner_trace\nduration=5\n')
for _=1,110 do now=now+0.05;native.tick() end
assert(statuses[#statuses]:find('state=complete',1,true))
assert(statuses[#statuses]:find('samples=0',1,true),'owner trace ran unrelated metadata traversal')
assert(statuses[#statuses]:find('truncated=false',1,true))
local pulse_ok,pulse_error=pcall(native_send,9,'capture','phase=bad_pulse\nmode=route_pulse\nduration=10\n')
assert(not pulse_ok and tostring(pulse_error):find('explicit one-shot diagnostic confirmation required',1,true))
print('PASS: native mode defaults off and Unit enumeration requires same-session World preflight')
