import bpy

print('=== OBJECTS ===')
for o in sorted(bpy.data.objects, key=lambda x: x.name):
    vg = len(o.vertex_groups) if o.type == 'MESH' else 0
    mods = [m.type for m in o.modifiers]
    par = o.parent.name if o.parent else '-'
    print('OBJ %-26s %-9s parent=%-22s mods=%s vg=%d'
          % (o.name, o.type, par, mods, vg))
print('=== ARMATURES ===')
for o in bpy.data.objects:
    if o.type == 'ARMATURE':
        print('ARM %-22s bones=%3d  display=%s  in_front=%s'
              % (o.name, len(o.data.bones), o.data.display_type, o.show_in_front))
        for b in o.data.bones:
            if b.name in ('l_shoulder', 'l_elbow', 'l_hand', 'l_hand_twist'):
                print('    bone %-14s head=(%.4f, %.4f, %.4f)  len=%.4f  parent=%s'
                      % (b.name, b.head_local.x, b.head_local.y, b.head_local.z,
                         b.length, b.parent.name if b.parent else '-'))
print('=== TEXTS ===')
for t in bpy.data.texts:
    print('TXT %s (%d chars)' % (t.name, len(t.as_string())))
