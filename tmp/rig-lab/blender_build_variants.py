"""把多个 model.npz 变体并排放进**一个 .blend**，方便在 Blender 里自由旋转、肉眼比较。

用法（用 Blender 本体运行）：

  blender.exe --background --factory-startup --python blender_build_variants.py -- \
      <out.blend>  <npz_dir>:<标签>  <npz_dir>:<标签>  ...

每个变体得到：
  <标签>_mesh   整具模型（ours_bind），**带 HD2 骨名的顶点组**（可在权重绘制模式直接看），
                不加 Armature 修改器 ⇒ 可以自由摆放在场景里做并排对比。
  <标签>_bones  该变体的 HD2 手臂骨链（shoulder→elbow→wrist→hand）用红线画出，
                与网格同一个 X 偏移 ⇒ 直接看出"骨头在不在网格里"。

所有变体的骨架逐位相同，所以骨头相对网格的差别只来自网格本身。
"""
import json
import os
import sys

import bpy
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

SPACING = 2.0   # metres between copies along X

COLORS = [
    (0.62, 0.72, 0.95, 1.0),
    (0.95, 0.66, 0.30, 1.0),
    (0.45, 0.85, 0.55, 1.0),
    (0.90, 0.45, 0.80, 1.0),
    (0.95, 0.90, 0.35, 1.0),
    (0.55, 0.85, 0.90, 1.0),
]


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


def assign_groups(ob, names, w_bone, w_val, nb):
    """把权重原样写进顶点组（与 blender_build.skin 同口径，但**不加修改器**）。"""
    vgs = [ob.vertex_groups.new(name=nm) for nm in names]
    for slot in range(w_bone.shape[1]):
        b = w_bone[:, slot]
        m = b >= 0
        if not m.any():
            continue
        bidx = b[m].astype(np.int64)
        widx = np.nonzero(m)[0]
        wgt = w_val[:, slot][m].astype(np.float64)
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


def make_edges(name, pts, edges, coll, rgba):
    me = bpy.data.meshes.new(name)
    me.from_pydata(pts.tolist(), edges, [])
    me.validate(verbose=False)
    ob = bpy.data.objects.new(name, me)
    me.materials.append(mat(name + '_mat', rgba))
    link(ob, coll)
    return ob


def add_text(name, body, loc, coll, size=0.12):
    try:
        cu = bpy.data.curves.new(name, type='FONT')
        cu.body = body
        cu.size = size
        cu.align_x = 'CENTER'
        ob = bpy.data.objects.new(name, cu)
        ob.location = loc
        link(ob, coll)
        return ob
    except Exception as e:
        print('[text] %r' % (e,))
        return None


def main():
    a = argv()
    blend_out = a[0]
    specs = a[1:]
    variants = []
    for s in specs:
        d, _, lab = s.rpartition(':')
        if not os.path.isdir(d):
            d, lab = s, ''
        variants.append((d, lab or os.path.basename(d)))

    bpy.ops.wm.read_factory_settings(use_empty=True)
    coll = new_collection('VARIANTS')
    cbone = new_collection('BONES')

    legend = []
    for i, (d, lab) in enumerate(variants):
        z = np.load(os.path.join(d, 'model.npz'))
        meta = json.load(open(os.path.join(d, 'meta.json'), encoding='utf-8'))
        names = dedupe(list(meta['bones']))
        nb = len(names)
        pos = np.asarray(z['ours_bind'], np.float64)
        tris = np.asarray(z['tris'], np.int64)
        bh = np.asarray(z['b_head'], np.float64)
        slot = {n: k for k, n in enumerate(names)}

        off = np.array([i * SPACING, 0.0, 0.0])
        col = COLORS[i % len(COLORS)]
        ob = make_mesh('%s_mesh' % lab, pos + off, tris, coll, col)
        assign_groups(ob, names, np.asarray(z['w_bone'], np.int64),
                      np.asarray(z['w_val'], np.float64), nb)

        # arm bone chain overlay
        pts, edges = [], []
        for pre in ('l', 'r'):
            chain = ['%s_shoulder' % pre, '%s_elbow' % pre, '%s_hand' % pre]
            base = len(pts)
            for nm in chain:
                pts.append(bh[slot[nm]] + off)
            edges += [(base, base + 1), (base + 1, base + 2)]
        make_edges('%s_bones' % lab, np.array(pts), edges, cbone, (0.95, 0.15, 0.15, 1.0))

        add_text('%s_label' % lab, lab, (off[0], -0.75, 1.95), coll, size=0.16)
        legend.append('  %-28s -> %s_mesh / %s_bones   (x = %+.2f m)'
                      % (lab, lab, lab, off[0]))
        print('[variant] %-28s %6d v / %6d t' % (lab, len(pos), len(tris)))

    # scene: sun + camera so a fresh open is immediately usable
    try:
        cd = bpy.data.cameras.new('Camera')
        cam = bpy.data.objects.new('Camera', cd)
        bpy.context.scene.collection.objects.link(cam)
        cam.location = (0.0, -4.2, 1.5)
        cam.rotation_euler = (1.35, 0.0, 0.0)
        bpy.context.scene.camera = cam
        ld = bpy.data.lights.new('Sun', type='SUN')
        ld.energy = 3.0
        lo = bpy.data.objects.new('Sun', ld)
        bpy.context.scene.collection.objects.link(lo)
        lo.location = (2.0, -3.0, 5.0)
        lo.rotation_euler = (0.6, 0.2, 0.5)
    except Exception as e:
        print('[scene] %r' % (e,))

    txt = bpy.data.texts.new('README')
    txt.write(
        'HD2ModManager rig-lab -- 第 77b 轮：选项 1（4e 关节残差平移接进 arm_scratch）\n'
        '====================================================================\n\n'
        '每个变体是一整具模型，沿 +X 方向按 2 m 间隔并排摆放：\n\n'
        + '\n'.join(legend) + '\n\n'
        '<标签>_mesh   整具模型（ours_bind），带 HD2 骨名的顶点组（可进权重绘制模式看）。\n'
        '              没有加 Armature 修改器 ⇒ 可以随意移动/旋转/单独隐藏。\n'
        '<标签>_bones  该变体的 HD2 手臂骨链（l/r_shoulder→elbow→hand）红线，\n'
        '              与网格同一偏移 ⇒ 直接看出"骨头在不在网格里"。\n\n'
        '所有变体的骨架逐位相同（b_head 逐位一致），所以骨头与网格的差别全部来自网格本身。\n'
        '本文件只做几何呈现，不含任何"好/坏"判断 —— 请自己旋转观察。\n\n'
        '坐标：1 单位 = 1 米，Z 上，+Y 角色前方，+X 角色右手侧。\n')

    os.makedirs(os.path.dirname(os.path.abspath(blend_out)), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(blend_out))
    print('[saved] %s  (%.1f MB)' % (blend_out, os.path.getsize(blend_out) / 1e6))


if __name__ == '__main__':
    main()
