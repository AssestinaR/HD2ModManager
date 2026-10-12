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


def shot(name, loc, rot, res=None):
    cam.location = loc
    cam.rotation_euler = rot
    if res:
        sc.render.resolution_x, sc.render.resolution_y = res
    sc.render.filepath = os.path.join(OUT, name)
    bpy.ops.render.render(write_still=True)
    print('[render] %s -> %s.png' % (name, os.path.join(OUT, name)))


def shot_at(name, loc, target, res=None, lens=None):
    """Place the camera at `loc` and aim it at `target` (world space)."""
    import mathutils
    if lens:
        cam_data.lens = lens
    cam.location = loc
    d = mathutils.Vector(target) - cam.location
    cam.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()
    if res:
        sc.render.resolution_x, sc.render.resolution_y = res
    sc.render.filepath = os.path.join(OUT, name)
    bpy.ops.render.render(write_still=True)
    print('[render] %s -> %s.png' % (name, os.path.join(OUT, name)))


# +Y is the character's FORWARD, so the front view has to sit at +Y looking -Y
FRONT = ((0.0, 3.6, 1.05), (math.radians(90), 0, math.radians(180)))
SIDE = ((3.6, 0.0, 1.05), (math.radians(90), 0, math.radians(90)))
BACK = ((0.0, -3.6, 1.05), (math.radians(90), 0, 0))
# close-ups: the round-2.5 damage is the twintails / head ribbon seen from behind
HEAD_BACK = ((0.0, -1.30, 1.60), (math.radians(90), 0, 0))
HEAD_FRONT = ((0.0, 1.30, 1.60), (math.radians(90), 0, math.radians(180)))
# round 45: the arm chain -- a straight-down top view shows the Y (forward)
# offset of the whole arm, which is what "the arm looks crooked" really was.
ARM_TOP = ((0.0, 0.15, 5.00), (0.0, 0.0, 0.0))
# close-ups of the character's LEFT arm (-X side); 50 mm lens at ~0.9 m
ARM_FRONT_L = ((-0.50, 1.05, 1.35), (math.radians(90), 0, math.radians(180)))
ARM_TOP_L = ((-0.50, 0.15, 2.60), (0.0, 0.0, 0.0))

set_vis(ours=True)
shot('blend-ours-front', *FRONT)
shot('blend-ours-side', *SIDE)
shot('blend-ours-back', *BACK)
shot('blend-ours-head-back', *HEAD_BACK)
shot('blend-ours-head-front', *HEAD_FRONT)
shot('blend-ours-arm-top', *ARM_TOP)
shot('blend-ours-arm-front-l', *ARM_FRONT_L)
shot('blend-ours-arm-top-l', *ARM_TOP_L)

set_vis(ours=True, ref=True)
shot('blend-compare-front', *FRONT)
shot('blend-compare-side', *SIDE)
shot('blend-compare-arm-top', *ARM_TOP)
shot('blend-compare-arm-top-l', *ARM_TOP_L)

# round 45b: the seam the user reported is at the ARMPIT, so give it a proper
# close-up from the front-outside, ours and reference.
ARMPIT = (-0.20, 0.02, 1.34)
set_vis(ours=True)
shot_at('blend-ours-armpit-l', (-0.30, 0.90, 1.66), ARMPIT, res=(700, 700))
set_vis(ours=True, ref=True)
shot_at('blend-compare-armpit-l', (-0.30, 0.90, 1.66), ARMPIT, res=(700, 700))
set_vis(ours=True, ref=True)
shot_at('blend-compare-armpit-l-top', (-0.22, 0.10, 2.40), ARMPIT, res=(700, 700))
