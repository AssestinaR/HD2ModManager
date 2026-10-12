"""Close-up renders of the LEFT hand, so the finger residual pose can be judged.

Four cameras, each shot twice (ours alone, ours + the reference patch):
    front   -- from the character's front
    top     -- straight down
    outside -- from the -X side (the outside of the left arm)
    palm    -- oblique, from below/front, looking at the finger pads

usage: blender.exe --background out/blender/zoey.blend \
           --python blender_render_hand.py -- <outdir>
"""
import math
import os
import sys

import bpy
import mathutils

a = sys.argv
OUT = os.path.abspath(a[a.index('--') + 1] if '--' in a else 'out/blender')
os.makedirs(OUT, exist_ok=True)

sc = bpy.context.scene
sc.render.engine = 'CYCLES'
sc.cycles.device = 'CPU'
sc.cycles.samples = 24
sc.cycles.use_denoising = False
sc.render.resolution_x = 460
sc.render.resolution_y = 460
sc.render.film_transparent = False

world = bpy.data.worlds.new('HandWorld')
sc.world = world
world.use_nodes = False
world.color = (0.05, 0.06, 0.08)

cam_data = bpy.data.cameras.new('hcam')
cam_data.lens = 50
cam_data.clip_start = 0.01
cam = bpy.data.objects.new('hcam', cam_data)
sc.collection.objects.link(cam)
sc.camera = cam

for i, (rot, en) in enumerate((((math.radians(52), 0, math.radians(-38)), 3.4),
                               ((math.radians(118), 0, math.radians(24)), 2.2),
                               ((math.radians(70), 0, math.radians(150)), 1.4))):
    ld = bpy.data.lights.new('hsun%d' % i, 'SUN')
    ld.energy = en
    lo = bpy.data.objects.new('hsun%d' % i, ld)
    lo.rotation_euler = rot
    sc.collection.objects.link(lo)

arm = bpy.data.objects.get('HD2_native')
if arm:
    arm.hide_render = True


def set_vis(ours=True, ref=False):
    for o in bpy.data.objects:
        if o.type != 'MESH':
            continue
        if o.name.startswith('REF_'):
            o.hide_render = not ref
        elif o.name == 'SRC_tpose':
            o.hide_render = True
        else:
            o.hide_render = not ours


# the left hand, in the shipped mesh (the rig never moves, so this is also the
# bone chain's own location)
T = mathutils.Vector((-0.700, 0.215, 1.165))
D = 0.52


def shot(name, offset):
    cam.location = T + mathutils.Vector(offset)
    d = T - cam.location
    cam.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()
    sc.render.filepath = os.path.join(OUT, name)
    bpy.ops.render.render(write_still=True)
    print('[render] %s' % os.path.join(OUT, name + '.png'))


VIEWS = [('front', (0.0, D, 0.02)),
         ('top', (0.0, 0.02, D)),
         ('outside', (-D, 0.0, 0.02)),
         ('palm', (-D * 0.45, D * 0.60, -D * 0.62))]

for tag, ours, ref in (('ours', True, False), ('cmp', True, True)):
    set_vis(ours=ours, ref=ref)
    for vname, off in VIEWS:
        shot('hand-%s-%s' % (tag, vname), off)
