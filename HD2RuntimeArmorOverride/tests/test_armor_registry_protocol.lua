local protocol=assert(loadfile(arg[1]))()
local parsed=protocol.parse([[
# manager-owned desired state
schema=1
revision=42
enabled=true
map=488691438,820333399
map=0xc72283a4,0x30e58857
]])
assert(parsed.schema==1 and parsed.revision==42 and parsed.enabled)
assert(#parsed.mappings==2 and parsed.mappings[1].target==488691438)
local roundtrip=protocol.parse(protocol.serialize(parsed))
assert(roundtrip.revision==42 and #roundtrip.mappings==2)
for _,bad in ipairs({
    'schema=2\nrevision=1\nenabled=true\n',
    'schema=1\nrevision=1\nenabled=yes\n',
    'schema=1\nrevision=1\nenabled=true\nmap=1,1\n',
    'schema=1\nrevision=1\nenabled=true\nmap=1,2\nmap=1,3\n',
    'schema=1\nrevision=1\nenabled=true\nunknown=1\n'
}) do
    assert(not pcall(protocol.parse,bad),'invalid protocol accepted')
end
print('PASS: strict external armor registry protocol parsing and serialization')
