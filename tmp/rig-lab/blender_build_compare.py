"""把多个 model.npz 变体并排放进**一个 .blend**，每个变体带**真正的 HD2 标准骨架**。

第 78 轮重写（用户反馈：「没有 hd2 标准骨架连线的图我拿什么参考？」）。
旧版只画了肩-肘-腕三段红线，而且**没有 Armature 物体**，所以在 Blender 里既看不到
完整骨架，也没法摆姿势验证。这一版给每个变体生成：

  <标签>_mesh   整具模型（ours_bind），带 HD2 骨名的顶点组，
                **已用 Armature 修改器绑定到 <标签>_rig**（改姿势即可看形变）
  <标签>_rig    **真正的 HD2 标准骨架**（Armature 物体，逐骨 head/tail/roll 由
                model.npz 的 b_head/b_axis/b_roll/b_len/b_parent 还原），
                默认 Octahedral 显示 + 前置显示 ⇒ 直接压在网格上看"骨头在不在网格里"
  <标签>_chain  手臂骨链（shoulder→elbow→hand→hand_twist）红线，方便一眼看出朝向

用法（用 Blender 本体运行）：

  blender.exe --background --factory-startup --python blender_build_compare.py -- \
      <out.blend>  <npz_dir>:<标签>  <npz_dir>:<标签>  ...

坐标：1 单位 = 1 米，Z 上，+Y 角色前方，+X 角色右手侧。
本文件只做几何呈现，不含任何"好/坏"判断。
"""
import json
import os
import sys

import bpy
import numpy as np
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

SPACING = 2.2   # metres between copies along X

COLORS = [
    (0.62, 0.72, 0.95, 1.0),
    (0.95, 0.66, 0.30, 1.0),
    (0.45, 0.85, 0.55, 1.0),
    (0.90, 0.45, 0.80, 1.0),
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


def make_mesh(name, verts, tris, coll, rgba):
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts.tolist(), [], tris.tolist())
    me.validate(verbose=False)
    me.update()
    ob = bpy.data.objects.new(name, me)
    me.materials.append(mat(name + '_mat', rgba))
    link(ob, coll)
    for p in me.polygons:
        p.use_smooth = True
    return ob


def make_edges(name, pts, edges, coll, rgba, width=2.0):
    me = bpy.data.meshes.new(name)
    me.from_pydata(pts.tolist(), edges, [])
    me.validate(verbose=False)
    ob = bpy.data.objects.new(name, me)
    m = mat(name + '_mat', rgba)
    m.diffuse_color = rgba
    me.materials.append(m)
    link(ob, coll)
    return ob


def make_armature(name, heads, axes, rolls, lengths, parents, coll, off,
                  bnames=None):
    """Rebuild HD2's native rest as a REAL Blender armature.

    Blender bones point along their local +Y; HD2 stores the bone along local
    +X, so head/tail = head + axis*length, and the roll is set by aligning the
    bone's local +Z with HD2's own +Z.  `align_roll` does that exactly.

    `bnames` MUST be the SAME deduped list that `assign_groups` uses, otherwise
    the vertex groups (HD2 names) cannot find the bones and the Armature
    modifier silently deforms nothing.
    """
    arm = bpy.data.armatures.new(name)
    ob = bpy.data.objects.new(name, arm)
    coll.objects.link(ob)
    ob.location = off
    arm.display_type = 'OCTAHEDRAL'
    ob.show_in_front = True

    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.mode_set(mode='EDIT')
    ebs = []
    for i in range(len(heads)):
        bn = bnames[i] if bnames else '%s' % i
        eb = arm.edit_bones.new(bn)
        ebs.append(eb)
    for i, eb in enumerate(ebs):
        h = Vector(tuple(float(x) for x in heads[i]))
        a = Vector(tuple(float(x) for x in axes[i]))
        L = float(lengths[i])
        if L <= 1e-6:
            L = 0.02
        eb.head = h
        eb.tail = h + a * L
    for i, eb in enumerate(ebs):
        p = int(parents[i])
        if 0 <= p < len(ebs) and p != i:
            eb.parent = ebs[p]
            eb.use_connect = False
        z = Vector(tuple(float(x) for x in rolls[i]))
        if z.length > 1e-6:
            eb.align_roll(z)
    bpy.ops.object.mode_set(mode='OBJECT')
    return ob, ebs


def add_text(name, body, loc, coll, size=0.14):
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
    cbone = new_collection('BONE_LINES')

    legend = []
    for i, (d, lab) in enumerate(variants):
        z = np.load(os.path.join(d, 'model.npz'))
        meta = json.load(open(os.path.join(d, 'meta.json'), encoding='utf-8'))
        names = dedupe(list(meta['bones']))
        nb = len(names)
        pos = np.asarray(z['ours_bind'], np.float64)
        tris = np.asarray(z['tris'], np.int64)
        bh = np.asarray(z['b_head'], np.float64)
        ba = np.asarray(z['b_axis'], np.float64)
        br = np.asarray(z['b_roll'], np.float64)
        bl = np.asarray(z['b_len'], np.float64)
        bp = np.asarray(z['b_parent'], np.int64)
        slot = {n: k for k, n in enumerate(names)}

        off = np.array([i * SPACING, 0.0, 0.0])
        col = COLORS[i % len(COLORS)]

        rig, _ebs = make_armature('%s_rig' % lab, bh, ba, br, bl, bp, coll, off,
                                  bnames=names)
        ob = make_mesh('%s_mesh' % lab, pos, tris, coll, col)
        assign_groups(ob, names, np.asarray(z['w_bone'], np.int64),
                      np.asarray(z['w_val'], np.float64), nb)
        ob.parent = rig
        md = ob.modifiers.new('Armature', 'ARMATURE')
        md.object = rig
        md.use_vertex_groups = True

        # arm chain overlay (drawn in world space, so bake `off` in)
        pts, edges = [], []
        for pre in ('l', 'r'):
            chain = ['%s_shoulder' % pre, '%s_elbow' % pre,
                     '%s_hand' % pre, '%s_hand_twist' % pre]
            base = len(pts)
            for nm in chain:
                pts.append(bh[slot[nm]] + off)
            edges += [(base + k, base + k + 1) for k in range(len(chain) - 1)]
        make_edges('%s_chain' % lab, np.array(pts), edges, cbone,
                   (0.95, 0.15, 0.15, 1.0))

        add_text('%s_label' % lab, lab, (off[0], -0.75, 1.98), coll, size=0.16)
        legend.append('  %-24s -> %s_mesh / %s_rig / %s_chain   (x = %+.2f m)'
                      % (lab, lab, lab, lab, off[0]))
        print('[variant] %-24s %6d v / %6d t / %d bones'
              % (lab, len(pos), len(tris), len(bh)))

    try:
        cd = bpy.data.cameras.new('Camera')
        cam = bpy.data.objects.new('Camera', cd)
        bpy.context.scene.collection.objects.link(cam)
        cam.location = (0.0, -4.6, 1.5)
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
        'HD2ModManager rig-lab -- 第 78 轮：小臂自动对准（起源肘 -> HD2 腕）\n'
        '==============================================================\n\n'
        '每个变体沿 +X 方向按 %.1f m 间隔并排摆放：\n\n' % SPACING
        + '\n'.join(legend) + '\n\n'
        '<标签>_mesh   整具模型，带 HD2 骨名的顶点组，**已绑定到 <标签>_rig**\n'
        '              （Armature 修改器）⇒ 可以直接进 Pose Mode 摆手臂看形变。\n'
        '<标签>_rig    **真正的 HD2 标准骨架**（逐骨 head/tail/roll 由 model.npz 还原），\n'
        '              Octahedral 显示 + 前置显示 ⇒ 骨头直接压在网格上。\n'
        '<标签>_chain  手臂骨链 shoulder→elbow→hand→hand_twist 红线。\n\n'
        '怎么用（建议）：\n'
        '  1) 选中某具 <标签>_mesh，Shift+点它的 <标签>_rig，进 Pose Mode；\n'
        '  2) 只转 l_shoulder / l_elbow，看小臂是跟着骨头走还是甩出去；\n'
        '  3) 或者直接用 Numpad 1/3/7 看正面 / 侧面 / 俯视。\n\n'
        '所有变体的骨架逐位相同（b_head 逐位一致），所以骨头与网格的差别全部来自网格本身。\n'
        '本文件只做几何呈现，不含任何"好/坏"判断 —— 请自己旋转观察。\n\n'
        '坐标：1 单位 = 1 米，Z 上，+Y 角色前方，+X 角色右手侧。\n')

    os.makedirs(os.path.dirname(os.path.abspath(blend_out)), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(blend_out))
    print('[saved] %s  (%.1f MB)' % (blend_out, os.path.getsize(blend_out) / 1e6))


if __name__ == '__main__':
    main()
