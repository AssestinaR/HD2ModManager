local real_ffi=require('ffi')
local index=real_ffi.new('uint64_t[411]')
local target_index,donor_index=229,227
index[target_index]=0x210000
index[donor_index]=0x230000
local lines={}
local env=setmetatable({}, {__index=_G});env._G=env
env.require=function(name) assert(name=='ffi');return real_ffi end
env.CowboyBingusModLoader={api=1,open_log=function()
    return {write=function(_,s) lines[#lines+1]=s end,flush=function() end,
        close=function() end}
end}
env.update=function() return 'ok' end
env.HD2ArmorCompositionProbe={result={
    index_base=tonumber(real_ffi.cast('uintptr_t',index)),
    matched_entries=411,
    roots={
        ['3340614564']={address=0x210000,index=target_index},
        ['820333399']={address=0x230000,index=donor_index}
    }
}}
local chunk=assert(loadfile(arg[1]));setfenv(chunk,env)
local m=chunk()
assert(m.status=='waiting')
assert(env.update()=='ok')
assert(m.status=='applied' and tonumber(index[target_index])==0x230000)
assert(m.clear() and tonumber(index[target_index])==0x210000)
assert(not m.clear())
assert(table.concat(lines):find('applied target=RE%-1861'))
print('PASS: validated aligned index swap, verification, and guarded rollback')
