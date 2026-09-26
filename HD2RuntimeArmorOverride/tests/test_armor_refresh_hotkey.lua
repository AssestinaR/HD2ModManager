local H=assert(loadfile('HD2RuntimeArmorOverride/src/armor_refresh_hotkey.lua'))()
local now,request=100,nil
local status,logs={},{}
local revision,objects=45,'a;b'
local host={pid=123,frequency=10000000,clock=function()return now end,
    revision=function()return revision end,globals={stingray={Unit={world=function()end}}},
    read=function()return request end,
    write=function(_,s)status[#status+1]=s;return true end,
    open=function(name)
        logs[name]=''
        return {write=function(_,s)logs[name]=logs[name]..s end,flush=function()end,close=function()end}
    end,
    inspect_unit=function(_,encoded,cache)
        assert(encoded==17 and type(cache)=='table')
        return {state='resolved_candidate',association_state='complete',object='owner',
            object_world='world',handle_index=4,handle_generation=0,association_count=2,
            association_objects=objects}
    end,
    world_probe={new=function(h)
        return {errors=0,tick=function(mode)
            if mode=='owner_trace' then
                h.emit({kind='native_owner_trace',resource_hash='4d1c334d294dfa97',
                    state='resolved_candidate',association_state='complete',encoded_handle=17,
                    object='owner',object_world='world',handle_index=4,handle_generation=0})
            end
        end}
    end}}
local session=string.rep('a',32)
local p=H.new(host)
local function frames(n,step)
    for _=1,n do now=now+(step or 1/120);p.tick() end
end
local function send(n,cmd,extra)
    request='schema=1\npid=123\nsession='..session..'\nrequest='..n..'\ncommand='..cmd..'\n'..(extra or '')
    frames(3)
end
frames(100);assert(#status==0 and next(logs)==nil)
send(1,'arm');frames(190)
assert(status[#status]:find('state=ready',1,true))
local t=now
send(2,'mark','qpc_seconds='..t..'\nfrequency=10000000\nmessage_delay_ms=1\n')
assert(status[#status]:find('state=capturing',1,true))
objects='c;d';frames(170)
local log=assert(logs['hotkey-'..session..'-2.jsonl'])
assert(log:find('"valid":true',1,true) and log:find('"reason":"ok"',1,true))
assert(log:find('a;b',1,true) and log:find('c;d',1,true))
assert(log:find('"marker_is_equip_event":false',1,true))
send(3,'mark','qpc_seconds='..(now-0.4)..'\nfrequency=10000000\nmessage_delay_ms=1\n')
frames(170)
assert(logs['hotkey-'..session..'-3.jsonl']:find('clock_or_transport_delay',1,true))
send(4,'mark','qpc_seconds='..now..'\nfrequency=10000000\nmessage_delay_ms=1\n')
frames(2,0.2);frames(150)
assert(logs['hotkey-'..session..'-4.jsonl']:find('insufficient_history_or_frame_gap',1,true))
send(5,'stop');assert(status[#status]:find('state=stopped',1,true))
send(6,'arm');frames(230);revision=46;frames(2)
assert(status[#status]:find('object_or_revision_changed',1,true))
print('PASS: hotkey ring, pre/post samples, clock delay, gaps, stop, revision invalidation')
