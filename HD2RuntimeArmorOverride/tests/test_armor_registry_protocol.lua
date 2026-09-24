local protocol=assert(loadfile(arg[1]))()
local parsed=protocol.parse([[
# manager-owned desired state
schema=2
revision=42
enabled=true
map=488691438,820333399
map=0xc72283a4,0x30e58857
model=base/re2310,820333399
map_model=651522724,base/re2310
]])
assert(parsed.schema==2 and parsed.revision==42 and parsed.enabled)
assert(#parsed.mappings==3 and parsed.mappings[1].target==488691438)
assert(#parsed.models==1 and parsed.models[1].key=='base/re2310')
local roundtrip=protocol.parse(protocol.serialize(parsed))
local model_mapping
for _,mapping in ipairs(roundtrip.mappings) do
    if mapping.model then model_mapping=mapping end
end
assert(roundtrip.revision==42 and #roundtrip.mappings==3
    and model_mapping and model_mapping.model=='base/re2310')
local custom=protocol.parse([[
schema=2
revision=1
enabled=true
model=custom/coat,
model_piece=custom/coat,3,0011223344556677,4,0,0
model_piece=custom/coat,3,8899aabbccddeeff,5,0,0
map_model=488691438,custom/coat
]])
assert(custom.models[1].source==nil and #custom.models[1].pieces==2)
assert(not pcall(protocol.parse,[[
schema=2
revision=1
enabled=true
model=custom/bad,
model_piece=custom/bad,3,0011223344556677,0,0,0
map_model=488691438,custom/bad
]]),'slot=0 protocol accepted')
assert(not pcall(protocol.parse,[[
schema=2
revision=1
enabled=true
model=custom/bad,
model_piece=custom/bad,3,0011223344556677,1,0,0
model_piece=custom/bad,3,8899aabbccddeeff,1,0,0
map_model=488691438,custom/bad
]]),'duplicate model Piece accepted')
for _,bad in ipairs({
    'schema=1\nrevision=1\nenabled=yes\n',
    'schema=1\nrevision=1\nenabled=true\nmap=1,1\n',
    'schema=1\nrevision=1\nenabled=true\nmap=1,2\nmap=1,3\n',
    'schema=1\nrevision=1\nenabled=true\nunknown=1\n'
}) do
    assert(not pcall(protocol.parse,bad),'invalid protocol accepted')
end
print('PASS: strict external armor registry protocol parsing and serialization')
