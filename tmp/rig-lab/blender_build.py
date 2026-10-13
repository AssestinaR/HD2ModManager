"""Build a .blend (and a .glb) from out/blender/model.npz, for looking at the
current pipeline result inside Blender.

Run it with Blender itself:

  blender.exe --background --factory-startup --python blender_build.py -- \
      <npz> <meta.json> <out.blend> [out.glb]

What you get
  HD2_native   armature: HD2's OWN rest pose (heads, bone axes, rolls).  The rig
               never moves in our pipeline -- the mesh is what adapts to it.
  OURS_bind    the current deliverable, skinned to HD2_native (vertex groups =
               HD2 bone names, so you can weight-paint / inspect directly).
  OURS_game    the frame the game renders (idle = HD2 rest).  With the shipped
               rig being HD2-native this is bit-identical to OURS_bind.
  SRC_tpose    the source model after the initial rigid fit (where we started).
  REF_*        the reference patch's units, for side-by-side comparison.
"""
import json
import os
import sys

import bpy
import numpy as np
from mathutils import Vector

COLORS = {
    'ours_bind': (0.62, 0.72, 0.95, 1.0),
    'ours_game': (0.35, 0.85, 0.45, 1.0),
    'src_tpose': (0.95, 0.62, 0.25, 1.0),
    'ref': (0.90, 0.30, 0.85, 1.0),
}

# the reference patch ships several body-type variants of the same garment
# (Slim AND Stocky).  Only these five are the ones our renders compare against;
# the rest go into a hidden collection so the silhouettes do not pile up.
REF_USED = {'af431352d9b95e83', 'cae35ee15168145f', '17da843a4908511e',
            '91eb7be84016ceab', '7336c6b662522f0e'}


def argv():
    a = sys.argv
    return a[a.index('--') + 1:] if '--' in a else []


def mat(name, rgba):
    m = bpy.data.materials.new(name)
    m.diffuse_color = rgba
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get('Principled BSDF')
    if bsdf:
        bsdf.inputs['Base Color'].default_value = rgba
        if 'Roughness' in bsdf.inputs:
            bsdf.inputs['Roughness'].default_value = 0.75
    return m


def new_collection(name):
    c = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(c)
    return c


def link(ob, coll):
    coll.objects.link(ob)
    for c in list(ob.users_collection):
        if c is not coll:
            c.objects.unlink(ob)
    return ob


def make_mesh(name, verts, tris, coll, rgba, smooth=True):
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts.tolist(), [], tris.tolist())
    me.validate(verbose=False)
    me.update()
    ob = bpy.data.objects.new(name, me)
    me.materials.append(mat(name + '_mat', rgba))
    link(ob, coll)
    if smooth:
        for p in me.polygons:
            p.use_smooth = True
    return ob


def build_armature(names, heads, axes, rolls, lengths, parents, coll):
    ad = bpy.data.armatures.new('HD2_native')
    ad.display_type = 'STICK'
    ao = bpy.data.objects.new('HD2_native', ad)
    link(ao, coll)
    ao.show_in_front = True

    bpy.context.view_layer.objects.active = ao
    bpy.ops.object.mode_set(mode='EDIT')
    ebs = []
    for i, nm in enumerate(names):
        eb = ad.edit_bones.new(nm)
        h = Vector((float(heads[i][0]), float(heads[i][1]), float(heads[i][2])))
        a = Vector((float(axes[i][0]), float(axes[i][1]), float(axes[i][2])))
        r = Vector((float(rolls[i][0]), float(rolls[i][1]), float(rolls[i][2])))
        eb.head = h
        eb.tail = h + a * max(float(lengths[i]), 0.01)
        try:
            eb.align_roll(r)
        except Exception:
            pass
        ebs.append(eb)
    for i, p in enumerate(parents):
        if p >= 0:
            ebs[i].parent = ebs[p]
            ebs[i].use_connect = False
    bpy.ops.object.mode_set(mode='OBJECT')
    return ao


def dedupe(names):
    seen, out = {}, []
    for n in names:
        if n in seen:
            seen[n] += 1
            out.append('%s_%d' % (n, seen[n]))
        else:
            seen[n] = 0
            out.append(n)
    return out


def skin(ob, arm, names, w_bone, w_val, nb):
    """Write the vertex groups EXACTLY as they arrive.

    Round 57: this used to do `np.round(w, 3)`.  Rounding each influence on its
    own does not preserve the sum (0.333333 x3 -> 0.999), and the SDK addon's
    export path then has to renormalise in float, after which the cast to
    vec4_half no longer lands on an exact decoded sum (measured: 60074 of 121704
    vertices, worst 3.662e-04).  dump_blender now snaps every influence onto the
    1/2048 grid and merges duplicate bones first, so the values that arrive here
    are already exact: their float32 sum is 1.0 and so is their float16 sum.
    Re-rounding them would throw that away.
    """
    vgs = [ob.vertex_groups.new(name=nm) for nm in names]
    for slot in range(w_bone.shape[1]):
        b = w_bone[:, slot]
        m = b >= 0
        if not m.any():
            continue
        bidx = b[m].astype(np.int64)
        widx = np.nonzero(m)[0]
        wgt = w_val[:, slot][m].astype(np.float64)
        # every weight is k/2048, so this key is exact, not a rounding of one
        units = np.round(wgt * 2048.0).astype(np.int64)
        key = bidx * 2049 + units
        order = np.argsort(key, kind='stable')
        key, widx = key[order], widx[order]
        cut = np.nonzero(np.diff(key))[0] + 1
        starts = np.concatenate(([0], cut))
        ends = np.concatenate((cut, [len(key)]))
        for s, e in zip(starts, ends):
            u = int(key[s])
            b_i, k = int(u // 2049), int(u % 2049)
            w = k / 2048.0
            if 0 <= b_i < nb and w > 0.0:
                vgs[b_i].add(widx[s:e].tolist(), float(w), 'REPLACE')
    ob.parent = arm
    ob.matrix_parent_inverse.identity()
    ob.matrix_world.identity()
    md = ob.modifiers.new('Armature', 'ARMATURE')
    md.object = arm
    md.use_vertex_groups = True


def main():
    a = argv()
    npz = a[0] if a else 'out/blender/model.npz'
    metaj = a[1] if len(a) > 1 else 'out/blender/meta.json'
    blend_out = a[2] if len(a) > 2 else 'out/blender/zoey.blend'
    glb_out = a[3] if len(a) > 3 else ''

    d = np.load(npz, allow_pickle=False)
    meta = json.load(open(metaj, encoding='utf-8'))
    names = dedupe(list(meta['bones']))
    nb = len(names)
    print('[build] npz=%s  %d bones' % (npz, nb))

    bpy.ops.wm.read_factory_settings(use_empty=True)

    c_ours = new_collection('OURS')
    c_src = new_collection('SOURCE')
    c_ref = new_collection('REFERENCE')
    c_alt = new_collection('REFERENCE_alt')
    c_alt.hide_viewport = True
    c_alt.hide_render = True

    arm = build_armature(names, d['b_head'], d['b_axis'], d['b_roll'],
                         d['b_len'], d['b_parent'], c_ours)

    ob_bind = make_mesh('OURS_bind', d['ours_bind'], d['tris'], c_ours,
                        COLORS['ours_bind'])
    skin(ob_bind, arm, names, d['w_bone'], d['w_val'], nb)

    ob_game = make_mesh('OURS_game', d['ours_game'], d['tris'], c_ours,
                        COLORS['ours_game'])
    skin(ob_game, arm, names, d['w_bone'], d['w_val'], nb)
    ob_game.hide_viewport = True          # same as bind -- unhide to verify

    make_mesh('SRC_tpose', d['src_tpose'], d['tris'], c_src,
              COLORS['src_tpose'], smooth=False)

    rp, rt = d['ref_pts'], d['ref_tris']
    for u in meta['refs']:
        s, n = int(u['start']), int(u['count'])
        sub = rt[(rt[:, 0] >= s) & (rt[:, 0] < s + n)] - s
        used = u['fid'] in REF_USED
        nm = 'REF_%s_%s' % (u['fid'][:8], (u['tag'] or 'unit')[:24])
        make_mesh(nm, rp[s:s + n], sub, c_ref if used else c_alt,
                  COLORS['ref'])
        print('  [ref] %-46s %6d v / %6d t %s'
              % (nm, n, len(sub), '' if used else '(alt, hidden)'))

    # a note inside the file, so the .blend explains itself
    txt = bpy.data.texts.new('README')
    txt.write(
        'HD2ModManager -- L4D2 -> HD2 migration, step 2 (torso scale)\n'
        '===========================================================\n\n'
        'params: %s\n\n'
        'OURS_bind   current deliverable, skinned to the HD2_native armature.\n'
        'OURS_game   the frame the game renders (idle).  Hidden by default: it\n'
        '            is bit-identical to OURS_bind because the shipped rig IS\n'
        '            HD2 native, so there is nothing to correct at runtime.\n'
        'SRC_tpose   the source model after the initial rigid fit.\n'
        'REF_*       the reference patch units (their own skeleton is HD2 native\n'
        '            too, and they did not move a single bone).  The patch ships\n'
        '            Slim AND Stocky variants of the same garment; only these five\n'
        '            are the ones our renders compare against --\n'
        '              af431352 Torso_Undergarment_Slim  (head + cat ears + hair)\n'
        '              cae35ee1 Torso_Undergarment_Slim  (the torso top)\n'
        '              17da843a Hip_Undergarment_Stocky\n'
        '              91eb7be8 LeftArm_Undergarment_Slim\n'
        '              7336c6b6 RightArm_Undergarment_Slim\n'
        '            the remaining units live in REFERENCE_alt, hidden by default\n'
        '            (showing them all at once piles the body types together).\n\n'
        'The armature is HD2 rest, never moved by our pipeline.  Bones 0-8 are\n'
        'scene-graph / helper nodes (StingrayEntityRoot, FbxAxisSystem_Conv,\n'
        'skeleton, root, climb_ref, attach_cam_1, boss, boss_aim); "boss" is the\n'
        'hip root-motion bone.  Bone lengths are NOT in the game data -- they are\n'
        'derived from the child heads, so treat tails as indicative only; heads\n'
        'and bone axes are exact.\n\n'
        'Scale: 1 unit = 1 metre, Z up, +Y = character forward, +X = their RIGHT.\n'
        % json.dumps(meta.get('params', {}), ensure_ascii=False))

    bpy.context.view_layer.update()
    for ob in bpy.data.objects:
        if ob.type == 'MESH':
            print('  [obj] %-46s %6d v' % (ob.name, len(ob.data.vertices)))

    os.makedirs(os.path.dirname(os.path.abspath(blend_out)), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(blend_out))
    print('[saved] %s  (%.1f MB)' % (blend_out,
                                     os.path.getsize(blend_out) / 1e6))

    if glb_out:
        try:
            bpy.ops.export_scene.gltf(filepath=os.path.abspath(glb_out),
                                      export_format='GLB',
                                      export_apply=False)
            print('[saved] %s  (%.1f MB)' % (glb_out,
                                             os.path.getsize(glb_out) / 1e6))
        except Exception as e:
            print('[glb] export failed: %r' % (e,))


if __name__ == '__main__':
    main()
