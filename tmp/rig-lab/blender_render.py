"""Render the generated .blend so we can SEE that it is sane before shipping it.

Cycles/CPU, tiny resolution, a few samples -- this is a smoke test, not art.
Renders: OURS front, OURS side, OURS+REFERENCE front.

usage: blender.exe --background <blend> --python blender_render.py -- <outdir>
"""
import math
import os
import sys

import bpy

a = sys.argv
OUT = os.path.abspath(a[a.index('--') + 1] if '--' in a else 'out/blender')
os.makedirs(OUT, exist_ok=True)

sc = bpy.context.scene
sc.render.engine = 'CYCLES'
sc.cycles.device = 'CPU'
sc.cycles.samples = 8
sc.cycles.use_denoising = False
sc.render.resolution_x = 420
sc.render.resolution_y = 640
sc.render.film_transparent = False

world = bpy.data.worlds.new('RenderWorld')
sc.world = world
world.use_nodes = False                      # Cycles then just uses world.color
world.color = (0.05, 0.06, 0.08)

cam_data = bpy.data.cameras.new('cam')
cam_data.lens = 50
cam = bpy.data.objects.new('cam', cam_data)
sc.collection.objects.link(cam)
sc.camera = cam

sun_data = bpy.data.lights.new('sun', 'SUN')
sun_data.energy = 3.0
sun = bpy.data.objects.new('sun', sun_data)
sun.rotation_euler = (math.radians(55), 0, math.radians(-35))
sc.collection.objects.link(sun)

arm = bpy.data.objects.get('HD2_native')
if arm:
    arm.hide_render = True

for c in bpy.data.collections:
    print('[render] collection %-12s objects %d' % (c.name, len(c.objects)))


def set_vis(ours=True, ref=False, src=False):
    for o in bpy.data.objects:
        if o.type != 'MESH':
            continue
        if o.name.startswith('REF_'):
            o.hide_render = not ref
        elif o.name == 'SRC_tpose':
            o.hide_render = not src
        else:
            o.hide_render = not ours


def shot(name, loc, rot):
    cam.location = loc
    cam.rotation_euler = rot
    sc.render.filepath = os.path.join(OUT, name)
    bpy.ops.render.render(write_still=True)
    print('[render] %s -> %s.png' % (name, os.path.join(OUT, name)))


# +Y is the character's FORWARD, so the front view has to sit at +Y looking -Y
FRONT = ((0.0, 3.6, 1.05), (math.radians(90), 0, math.radians(180)))
SIDE = ((3.6, 0.0, 1.05), (math.radians(90), 0, math.radians(90)))
BACK = ((0.0, -3.6, 1.05), (math.radians(90), 0, 0))

set_vis(ours=True)
shot('blend-ours-front', *FRONT)
shot('blend-ours-side', *SIDE)
shot('blend-ours-back', *BACK)

set_vis(ours=True, ref=True)
shot('blend-compare-front', *FRONT)
shot('blend-compare-side', *SIDE)
