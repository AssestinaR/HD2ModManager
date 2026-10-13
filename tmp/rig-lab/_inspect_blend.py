# -*- coding: utf-8 -*-
"""What is actually inside zoey.blend, from the export path's point of view?

The HD2SDK addon's export (`stingray/unit.py::GetMeshData`) demands specific
things of a scene before it will write a Unit:

  * `object.material_slots[idx].name` must parse as an INTEGER
    (`mat_id = int(object.material_slots[idx].name)`, else it raises
    "Material name must be a number"),
  * the object must carry the SDK's own authoring custom properties
    (`Z_ObjectID`, `MeshInfoIndex`, `BoneInfoIndex`, `matslot<N>`),
  * UVs and vertex colours are read from mesh layers.

This script dumps what is actually present, so we know how far the diagnostic
scene is from an exportable one.

Run with Blender:
  blender.exe --background --factory-startup --python _inspect_blend.py -- <blend>
"""
import json
import os
import sys

import bpy


def argv():
    a = sys.argv
    return a[a.index('--') + 1:] if '--' in a else []


def main():
    a = argv()
    blend = a[0] if a else 'out/blender/zoey.blend'
    bpy.ops.wm.open_mainfile(filepath=os.path.abspath(blend))

    rep = dict(blend=os.path.abspath(blend), objects=[], materials=[])
    for ob in bpy.data.objects:
        row = dict(name=ob.name, type=ob.type,
                   parent=ob.parent.name if ob.parent else None,
                   collections=[c.name for c in ob.users_collection])
        if ob.type == 'MESH':
            me = ob.data
            row.update(vertices=len(me.vertices), polygons=len(me.polygons),
                       materials=[ms.material.name if ms.material else None
                                  for ms in ob.material_slots],
                       uv_layers=[l.name for l in me.uv_layers],
                       color_attributes=[c.name for c in me.color_attributes],
                       vertex_groups=len(ob.vertex_groups),
                       modifiers=[(m.name, m.type) for m in ob.modifiers],
                       custom_props={k: str(ob[k])[:60]
                                     for k in ob.keys() if k != '_RNA_UI'})
        if ob.type == 'ARMATURE':
            row.update(bones=len(ob.data.bones),
                       custom_props={k: str(ob[k])[:60]
                                     for k in ob.keys() if k != '_RNA_UI'})
        rep['objects'].append(row)

    for m in bpy.data.materials:
        numeric = None
        try:
            numeric = int(m.name)
        except ValueError:
            numeric = None
        rep['materials'].append(dict(
            name=m.name, parses_as_integer=numeric is not None,
            node_count=len(m.node_tree.nodes) if m.use_nodes else 0,
            has_image=[n.image.name for n in (m.node_tree.nodes if m.use_nodes else [])
                       if n.type == 'TEX_IMAGE' and n.image]))

    print(json.dumps(rep, indent=1, ensure_ascii=False))
    out = os.path.join(os.path.dirname(os.path.abspath(blend)), 'blend-inspect.json')
    json.dump(rep, open(out, 'w', encoding='utf-8'), indent=1, ensure_ascii=False)
    print('[written] %s' % out)


main()
