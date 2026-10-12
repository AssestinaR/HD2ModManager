"""Re-open the generated .blend and CHECK it, instead of trusting the builder.

Three checks:
  1. counts      -- armature bones / vertex groups / mesh sizes
  2. rest==bind  -- with the armature at rest, the modifier-evaluated mesh must
                    be bit-identical to the array we dumped (any mismatch means
                    the bind matrices or the bone frames were built wrong)
  3. skin works  -- rotate one bone and confirm the expected part of the mesh
                    actually moves (a rig where nothing moves is a fake rig)

usage: blender.exe --background <blend> --python blender_verify.py -- <npz>
"""
import sys

import bpy
import numpy as np

a = sys.argv
npz = a[a.index('--') + 1] if '--' in a else 'out/blender/model.npz'
d = np.load(npz, allow_pickle=False)

arm = bpy.data.objects.get('HD2_native')
ob = bpy.data.objects.get('OURS_bind')
print('[verify] armature %s: %d bones, %d collections'
      % (arm.name, len(arm.data.bones), len(arm.data.collections_all)))
print('[verify] OURS_bind: %d verts, %d polys, %d vertex groups, modifiers %s'
      % (len(ob.data.vertices), len(ob.data.polygons), len(ob.vertex_groups),
         [m.type for m in ob.modifiers]))


def evaluated(ob):
    dg = bpy.context.evaluated_depsgraph_get()
    ev = ob.evaluated_get(dg)
    me = ev.to_mesh()
    co = np.empty((len(me.vertices), 3), dtype=np.float64)
    me.vertices.foreach_get('co', co.ravel())
    ev.to_mesh_clear()
    return co


ref = d['ours_bind'].astype(np.float64)
got = evaluated(ob)
print('[verify] rest frame vs dumped mesh: max |d| = %.3e m   (want 0)'
      % np.abs(got - ref).max())

# ---- does the skin actually bind? ---------------------------------------
name = 'l_elbow'
if name in arm.pose.bones:
    pb = arm.pose.bones[name]
    pb.rotation_mode = 'XYZ'
    pb.rotation_euler = (0.0, 0.0, 0.6)
    bpy.context.view_layer.update()
    moved = evaluated(ob)
    dd = np.linalg.norm(moved - ref, axis=1)
    print('[verify] rotating %s by 0.6 rad: %d verts moved >1 mm, max %.3f m'
          % (name, int((dd > 1e-3).sum()), dd.max()))
    lo = ref[dd > 1e-3]
    if len(lo):
        print('[verify]   moved region bbox x[%.3f,%.3f] y[%.3f,%.3f] '
              'z[%.3f,%.3f]'
              % (lo[:, 0].min(), lo[:, 0].max(), lo[:, 1].min(), lo[:, 1].max(),
                 lo[:, 2].min(), lo[:, 2].max()))
    pb.rotation_euler = (0.0, 0.0, 0.0)

# ---- bone sanity ---------------------------------------------------------
for nm in ('hips', 'spine2', 'chest', 'neck', 'head', 'l_shoulder', 'l_elbow',
           'l_hand', 'l_knee', 'l_foot'):
    b = arm.data.bones.get(nm)
    if b:
        h = b.head_local
        print('[verify]   %-12s head %7.4f %7.4f %7.4f  len %.4f'
              % (nm, h.x, h.y, h.z, b.length))

for o in bpy.data.objects:
    if o.type == 'MESH':
        vs = np.empty((len(o.data.vertices), 3))
        o.data.vertices.foreach_get('co', vs.ravel())
        print('[verify] obj %-46s z[%.3f,%.3f]' % (o.name, vs[:, 2].min(),
                                                   vs[:, 2].max()))
