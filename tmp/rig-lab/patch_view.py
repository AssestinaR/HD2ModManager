"""Side-by-side viewer for the offline lab: several meshes + several skeletons.

Built for one question: **where do the hips, the neck and the feet sit relative to
each other in the side view?**  So the panels share a camera, the side view draws
vertical guide lines through the hip joint and through the foot, and skeletons are
drawn as lines on top of the meshes.

Panels
------
col 1  the L4D2 source bind pose, aligned into HD2 space (T-pose)
col 2  our BatchTool-style output (A-pose)
col 3  the reference patch mod extracted straight out of a real HD2 legacy patch
col 4  skeletons only (source / ours / HD2 native / patch), same camera

Row 1 = front (X-Z), row 2 = side (Y-Z).  In HD2 **+Y is the character's forward**
(verified from the patch foot mesh: ankle y = -0.1175 sits 2.7 cm from the heel at
y = -0.145, so the toes must run toward +Y), **+X is left** (the left foot mesh sits
at x in [-0.212, -0.102]), +Z is up.
"""
import argparse
import json
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
import hd2_style as H


# ------------------------------------------------------------------ obj io
def read_obj(path):
    pts, tris = [], []
    for line in open(path, encoding='utf-8', errors='replace'):
        if line.startswith('v '):
            _, x, y, z = line.split()[:4]
            pts.append((float(x), float(y), float(z)))
        elif line.startswith('f '):
            idx = [int(t.split('/')[0]) - 1 for t in line.split()[1:4]]
            if len(idx) == 3:
                tris.append((idx[0], idx[1], idx[2], 0))
    return pts, tris


def read_bone_names(path):
    names = {}
    for line in open(path, encoding='utf-8', errors='replace'):
        p = line.split()
        if len(p) >= 2:
            try:
                names[int(p[0])] = p[1]
            except ValueError:
                pass
    return names


def similarity(A, B):
    """Best uniform-scale + rotation + translation taking A onto B.

    `retarget` builds its aligned source as exactly such a transform of the raw
    source mesh, so fitting it back out of (src_pts, src1) recovers the pipeline's
    own scale/rotation/translation -- whatever `--align` mode produced them -- and
    lets us put the *skeleton* in the same space without re-deriving it by hand.
    """
    ca, cb = A.mean(0), B.mean(0)
    Hm = (A - ca).T @ (B - cb)
    U, S, Vt = np.linalg.svd(Hm)
    d = 1.0 if np.linalg.det(Vt.T @ U.T) > 0 else -1.0
    R = Vt.T @ np.diag([1.0, 1.0, d]) @ U.T
    var = float(((A - ca) ** 2).sum())
    sc = float(S.sum()) / var if var > 1e-12 else 1.0
    t = cb - sc * (R @ ca)
    return sc, R, t


# ------------------------------------------------------------------ draw
class Panel:
    def __init__(self, img, draw, rect, box, ah, av, ad, flip, scale=None):
        self.img, self.d, self.rect, self.box = img, draw, rect, box
        self.ah, self.av, self.ad, self.flip = ah, av, ad, flip
        x0, y0, x1, y1 = rect
        self.w, self.h = x1 - x0, y1 - y0
        mn_h, mn_v = box[ah], box[av]
        mx_h, mx_v = box[3 + ah], box[3 + av]
        span = max(mx_h - mn_h, mx_v - mn_v) or 1.0
        self.sc = scale if scale is not None else min(self.w, self.h) * 0.88 / span
        self.ch = (mn_h + mx_h) / 2.0
        self.cv = (mn_v + mx_v) / 2.0
        self.zbuf = [[None] * self.w for _ in range(self.h)]

    def to_px(self, p):
        u = (p[self.ah] - self.ch) * self.sc
        v = (p[self.av] - self.cv) * self.sc
        if self.flip:
            u = -u
        return (self.rect[0] + self.w / 2.0 + u, self.rect[1] + self.h / 2.0 - v)

    def mesh(self, pts, tris, tint=(1.0, 1.0, 1.0), step=1):
        x0, y0, x1, y1 = self.rect
        faces = []
        for k in range(0, len(tris), step):
            a, b, c, _m = tris[k]
            pa, pb, pc = pts[a], pts[b], pts[c]
            faces.append(((pa[self.ad] + pb[self.ad] + pc[self.ad]) / 3.0, pa, pb, pc))
        faces.sort(key=lambda f: -f[0])
        light = (-0.35, -0.55, 0.75)
        for _dep, pa, pb, pc in faces:
            ta, tb, tc = self.to_px(pa), self.to_px(pb), self.to_px(pc)
            e1 = (pb[0] - pa[0], pb[1] - pa[1], pb[2] - pa[2])
            e2 = (pc[0] - pa[0], pc[1] - pa[1], pc[2] - pa[2])
            nx = e1[1] * e2[2] - e1[2] * e2[1]
            ny = e1[2] * e2[0] - e1[0] * e2[2]
            nz = e1[0] * e2[1] - e1[1] * e2[0]
            nl = math.sqrt(nx * nx + ny * ny + nz * nz) or 1.0
            shade = 0.22 + 0.78 * abs((nx * light[0] + ny * light[1] + nz * light[2]) / nl)
            col = (int(min(255, (118 + 118 * shade) * tint[0])),
                   int(min(255, (104 + 112 * shade) * tint[1])),
                   int(min(255, (126 + 108 * shade) * tint[2])))
            minx = max(x0, int(min(ta[0], tb[0], tc[0])))
            maxx = min(x1 - 1, int(max(ta[0], tb[0], tc[0])) + 1)
            miny = max(y0, int(min(ta[1], tb[1], tc[1])))
            maxy = min(y1 - 1, int(max(ta[1], tb[1], tc[1])) + 1)
            if minx > maxx or miny > maxy:
                continue
            den = (tb[1] - tc[1]) * (ta[0] - tc[0]) + (tc[0] - tb[0]) * (ta[1] - tc[1])
            if abs(den) < 1e-9:
                continue
            inv_den = 1.0 / den
            for py in range(miny, maxy + 1):
                row = self.zbuf[py - y0]
                for px in range(minx, maxx + 1):
                    cxp, cyp = px + 0.5, py + 0.5
                    w1 = ((tb[1] - tc[1]) * (cxp - tc[0])
                          + (tc[0] - tb[0]) * (cyp - tc[1])) * inv_den
                    w2 = ((tc[1] - ta[1]) * (cxp - tc[0])
                          + (ta[0] - tc[0]) * (cyp - tc[1])) * inv_den
                    w3 = 1.0 - w1 - w2
                    if w1 < 0 or w2 < 0 or w3 < 0:
                        continue
                    depth = -(w1 * pa[self.ad] + w2 * pb[self.ad] + w3 * pc[self.ad])
                    cur = row[px - x0]
                    if cur is None or depth < cur:
                        row[px - x0] = depth
                        self.img.putpixel((px, py), col)

    def line(self, a, b, col, width=2, dashed=False):
        pa, pb = self.to_px(a), self.to_px(b)
        if not dashed:
            self.d.line([pa, pb], fill=col, width=width)
            return
        n = max(2, int(math.dist(pa, pb) / 8))
        for i in range(n):
            if i % 2:
                continue
            t0, t1 = i / n, (i + 0.6) / n
            self.d.line([(pa[0] + (pb[0] - pa[0]) * t0, pa[1] + (pb[1] - pa[1]) * t0),
                         (pa[0] + (pb[0] - pa[0]) * t1, pa[1] + (pb[1] - pa[1]) * t1)],
                        fill=col, width=width)

    def dot(self, p, col, r=3):
        x, y = self.to_px(p)
        self.d.ellipse([x - r, y - r, x + r, y + r], fill=col)

    def label(self, p, text, col, dy=-10):
        x, y = self.to_px(p)
        self.d.text((x + 5, y + dy), text, fill=col)

    def guide(self, value, col, width=1):
        """Vertical line at a constant horizontal coordinate (side/front)."""
        x0, y0, x1, y1 = self.rect
        u = (value - self.ch) * self.sc
        if self.flip:
            u = -u
        px = int(round(x0 + self.w / 2.0 + u))
        if x0 <= px < x1:
            self.d.line([(px, y0), (px, y1 - 1)], fill=col, width=width)
        return px


# ------------------------------------------------------------------ skeleton
BONE_PARENT = {
    'hips': None, 'spine1': 'hips', 'spine2': 'spine1', 'chest': 'spine2',
    'neck': 'chest', 'head': 'neck',
    'l_clavicle': 'chest', 'l_shoulder': 'l_clavicle', 'l_elbow': 'l_shoulder',
    'l_hand': 'l_elbow', 'l_thigh': 'hips', 'l_knee': 'l_thigh', 'l_foot': 'l_knee',
    'r_clavicle': 'chest', 'r_shoulder': 'r_clavicle', 'r_elbow': 'r_shoulder',
    'r_hand': 'r_elbow', 'r_thigh': 'hips', 'r_knee': 'r_thigh', 'r_foot': 'r_knee',
}


def bone_lines(pts_by_name):
    out = []
    for name, head in pts_by_name.items():
        p = BONE_PARENT.get(name)
        if p and p in pts_by_name:
            out.append((pts_by_name[p], head))
    return out


def hd2_rig_heads(toc, bonehash):
    return {h['name']: (h['world'][3], h['world'][7], h['world'][11])
            for h in E.read_hd2_rig(toc, bonehash) if h['name']}


def src_rig_heads(vpk, model):
    _mp, _mdl, _v, _t, src, short = H.load_source(vpk, model)
    out = {}
    for name, b in short.items():
        m = b['world_m']
        out[name] = (m[3], m[7], m[11])
    return out


SRC_TO_HD2 = {'Pelvis': 'hips', 'Spine': 'spine1', 'Spine2': 'spine2', 'Spine4': 'chest',
              'Neck1': 'neck', 'Head1': 'head',
              'L_Clavicle': 'l_clavicle', 'L_UpperArm': 'l_shoulder',
              'L_Forearm': 'l_elbow', 'L_Hand': 'l_hand',
              'R_Clavicle': 'r_clavicle', 'R_UpperArm': 'r_shoulder',
              'R_Forearm': 'r_elbow', 'R_Hand': 'r_hand',
              'L_Thigh': 'l_thigh', 'L_Calf': 'l_knee', 'L_Foot': 'l_foot',
              'R_Thigh': 'r_thigh', 'R_Calf': 'r_knee', 'R_Foot': 'r_foot'}


def patch_rig_heads(meta, names):
    """Union of the named bones of the selected patch units.

    Patch bone matrices are stored **row-vector style** (translation flat 12..14),
    the opposite of `E.read_hd2_rig`, which already transposes.  The first dump of
    this file read flat 3/7/11 and got all zeros for every bone -- silent, because
    zero is a valid position.
    """
    out = {}
    for u in meta:
        for b in u['bones']:
            n = names.get(b['hash'])
            if n:
                m = b['matrix']
                out.setdefault(n, (m[12], m[13], m[14]))
    return out


# ------------------------------------------------------------------ measures
def foot_metrics(P, side=-1, band=0.07):
    """Heel / toe / ankle-height numbers of one foot, from the mesh alone.

    `side` picks the foot by the sign of X (HD2 keeps the character's left at -X).
    Returns (y_min, y_max, y_mid, z_min); y_min is the heel because +Y is forward.
    """
    P = np.asarray(P)
    zmin = P[:, 2].min()
    m = P[(P[:, 2] < zmin + band) & (P[:, 0] * side > 0.05)]
    if len(m) < 10:
        m = P[P[:, 2] < zmin + band]
    if not len(m):
        return None
    return (float(m[:, 1].min()), float(m[:, 1].max()),
            float((m[:, 1].min() + m[:, 1].max()) / 2.0), float(zmin))


# ------------------------------------------------------------------ main
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--vpk', required=True)
    ap.add_argument('--model', required=True)
    ap.add_argument('--toc', required=True)
    ap.add_argument('--bonehash', required=True)
    ap.add_argument('--pose-profile', required=True)
    ap.add_argument('--patch-dump', required=True, help='dir written by tmp/patchdump')
    ap.add_argument('--patch-units', default='',
                    help='comma separated unit file ids (hex) to include; default = all with geometry')
    ap.add_argument('--align', default='kabsch')
    ap.add_argument('--spine-map', default='legacy')
    ap.add_argument('--scale', type=float, default=0.0,
                    help='explicit uniform scale; 0 = auto (head JOINT to head joint)')
    ap.add_argument('--head-offset', type=float, default=-0.015,
                    help='how far the source head JOINT is placed from HD2 head joint')
    ap.add_argument('--fallback', default='parent')
    ap.add_argument('--crop', default='', help='minz,maxz -- zoom the vertical band')
    ap.add_argument('--rows', choices=('both', 'front', 'side'), default='both')
    ap.add_argument('--cols', default='src,ours,ref,skel',
                    help='any of src,ours,idle,ref,skel -- idle = our mesh re-posed by '
                         "HD2's own rest, i.e. what the game actually displays")
    ap.add_argument('--panel', default='370x440', help='panel size WxH')
    ap.add_argument('--tri-step', type=int, default=1)
    ap.add_argument('--out', required=True)
    ap.add_argument('--title', default='')
    a = ap.parse_args()

    # --- our pipeline -------------------------------------------------------
    r = H.retarget(a.vpk, a.model, a.toc, a.bonehash, a.pose_profile,
                   align=a.align, spine_map=a.spine_map, fallback=a.fallback,
                   scale=a.scale, head_offset=a.head_offset, verbose=False)
    src_pts = [tuple(p) for p in np.asarray(r['src1'])]
    our_pts = [tuple(p) for p in np.asarray(r['out'])]
    idle_pts = [tuple(p) for p in np.asarray(r['out_idle'])]
    tris = r['tris']

    # --- the reference patch mod -------------------------------------------
    meta = json.load(open(os.path.join(a.patch_dump, 'units.json'), encoding='utf-8'))
    names = read_bone_names(a.bonehash)
    want = set(x.lower() for x in a.patch_units.split(',')) if a.patch_units else None
    ref_pts, ref_tris, vbase = [], [], 0
    used = []
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        if want is not None and fid not in want:
            continue
        path = os.path.join(a.patch_dump, 'unit-%s.obj' % fid)
        if not os.path.exists(path):
            continue
        p, t = read_obj(path)
        if not t:
            continue
        used.append((u['tag'], u['unitFileId'], len(p), len(t)))
        ref_pts.extend(p)
        ref_tris.extend([(x[0] + vbase, x[1] + vbase, x[2] + vbase, 0) for x in t])
        vbase += len(p)
    print('patch units used:')
    for tag, fid, nv, nt in used:
        print('   %-40s %s  v=%6d t=%6d' % (tag, fid, nv, nt))

    # --- skeletons ----------------------------------------------------------
    src_h0 = src_rig_heads(a.vpk, a.model)
    sc, R, t = similarity(np.asarray(r['src_pts']), np.asarray(r['src1']))
    print('source->HD2 similarity recovered from the pipeline: scale %.6f, rot %.2f deg'
          % (sc, math.degrees(math.acos(max(-1.0, min(1.0, (np.trace(R) - 1) / 2))))))
    src_aligned = {SRC_TO_HD2[k]: tuple(sc * (R @ np.asarray(v)) + t)
                   for k, v in src_h0.items() if k in SRC_TO_HD2}
    ours_h = {k: tuple(v) for k, v in r['new_head'].items() if k in BONE_PARENT}
    hd2_h = {k: v for k, v in hd2_rig_heads(a.toc, a.bonehash).items() if k in BONE_PARENT}
    patch_h = patch_rig_heads(meta, names)
    patch_h = {k: v for k, v in patch_h.items() if k in BONE_PARENT}

    # --- the numbers the user actually asked about --------------------------
    def axis_report(tag, heads, mesh=None):
        hip = heads.get('l_thigh') or heads.get('hips')
        neck = heads.get('neck')
        head = heads.get('head')
        foot = heads.get('l_foot')
        s = '%-8s' % tag
        if hip:
            s += ' hip y %+.4f z %.4f |' % (hip[1], hip[2])
        if neck and hip:
            s += ' neck d %+.4f |' % (neck[1] - hip[1])
        if head and hip:
            s += ' head d %+.4f |' % (head[1] - hip[1])
        if foot and hip:
            s += ' ankle d %+.4f' % (foot[1] - hip[1])
        print(s)
        if mesh is not None:
            fm = foot_metrics(mesh)
            if fm and hip:
                print('%-8s foot mesh y [%+.4f, %+.4f]  mid %+.4f  ->  heel d %+.4f, '
                      'mid-foot d %+.4f, toe d %+.4f (all vs the hip)'
                      % ('', fm[0], fm[1], fm[2], fm[0] - hip[1], fm[2] - hip[1],
                         fm[1] - hip[1]))
                print('%-8s hip sits at %.1f%% of the foot (0%% = heel, 100%% = toe)'
                      % ('', 100.0 * (hip[1] - fm[0]) / max(fm[1] - fm[0], 1e-9)))

    print('\n-- side-view alignment (HD2: +Y = forward, so a NEGATIVE d means "behind the hip") --')
    axis_report('SOURCE', src_aligned, src_pts)
    axis_report('OURS', ours_h, our_pts)
    axis_report('HD2', hd2_h, None)
    axis_report('PATCH', patch_h, ref_pts)

    # --- render -------------------------------------------------------------
    allp = src_pts + our_pts + idle_pts + ref_pts
    gb = list(E.bbox(allp))
    if a.crop:
        lo, hi = (float(x) for x in a.crop.split(','))
        gb[2], gb[5] = lo, hi
        gb[0] = min(min(p[0] for p in allp), -0.01)
        gb[3] = max(max(p[0] for p in allp), 0.01)
    gb = tuple(gb)

    pw, ph = (int(x) for x in a.panel.lower().split('x'))
    gsc = min(pw, ph) * 0.88 / max(gb[3] - gb[0], gb[5] - gb[2], 0.001)

    views = [('front  (X-Z)', 0, 2, 1, False), ('side  (Y-Z, +Y = forward)', 1, 2, 0, True)]
    if a.rows == 'front':
        views = views[:1]
    elif a.rows == 'side':
        views = views[1:]

    cols = [('1  L4D2 source (T-pose, aligned)', 'src'), ('2  OURS (BatchTool-style)', 'ours'),
            ('3  reference patch mod', 'ref'), ('4  skeletons', 'skel')]
    COLS = {'src': ('L4D2 source (T-pose, aligned)', 'src'),
            'ours': ('OURS bind pose (A-pose)', 'ours'),
            'idle': ("OURS posed by HD2's own rest (what the game shows)", 'idle'),
            'ref': ('reference patch mod', 'ref'),
            'skel': ('skeletons', 'skel')}
    cols = [('%d  %s' % (i + 1, COLS[k][0]), COLS[k][1])
            for i, k in enumerate(x.strip() for x in a.cols.split(',')) if k in COLS]

    W, Hh = 12 + len(cols) * (pw + 14), 60 + len(views) * (ph + 26) + 20
    img = Image.new('RGB', (W, Hh), (28, 30, 36))
    d = ImageDraw.Draw(img)
    d.text((12, 8), a.title or 'source | ours | reference patch | skeletons', fill=(230, 230, 240))
    d.text((12, 24), 'guides:  hip (yellow)   ankle joint (cyan)   mid-foot (magenta)'
           '   crop=%s' % (a.crop or 'full'), fill=(150, 160, 180))

    for ci, (label, kind) in enumerate(cols):
        cx = 12 + ci * (pw + 14)
        d.text((cx + 4, 42), label, fill=(200, 210, 225))
        for ri, (tag, ah, av, ad, flip) in enumerate(views):
            cy = 60 + ri * (ph + 26)
            d.text((cx + 4, cy - 13), tag, fill=(160, 170, 190))
            p = Panel(img, d, (cx, cy, cx + pw, cy + ph), gb, ah, av, ad, flip, gsc)
            if kind == 'src':
                p.mesh(src_pts, tris, (1.0, 0.96, 0.92), a.tri_step)
            elif kind == 'ours':
                p.mesh(our_pts, tris, (0.94, 1.0, 0.96), a.tri_step)
            elif kind == 'idle':
                p.mesh(idle_pts, tris, (1.0, 0.98, 0.86), a.tri_step)
            elif kind == 'ref':
                p.mesh(ref_pts, ref_tris, (0.96, 0.96, 1.0), a.tri_step)
            else:
                for heads, col in ((src_aligned, (255, 150, 90)), (ours_h, (110, 235, 160)),
                                   (hd2_h, (110, 170, 255)), (patch_h, (255, 120, 200))):
                    for h0, h1 in bone_lines(heads):
                        p.line(h0, h1, col, 2)
                    for v in heads.values():
                        p.dot(v, col, 2)
                if tag.startswith('side'):
                    for heads, col in ((src_aligned, (255, 150, 90)), (ours_h, (110, 235, 160)),
                                       (hd2_h, (110, 170, 255)), (patch_h, (255, 120, 200))):
                        hip = heads.get('l_thigh') or heads.get('hips')
                        if hip:
                            p.guide(hip[1], col, 1)
            # guides: hip / ankle / mid-foot verticals (side view only)
            if tag.startswith('side'):
                mesh = {'src': src_pts, 'ours': our_pts, 'idle': idle_pts, 'ref': ref_pts,
                        'skel': our_pts}[kind]
                heads = {'src': src_aligned, 'ours': ours_h, 'idle': ours_h, 'ref': patch_h,
                         'skel': ours_h}[kind]
                hip = heads.get('l_thigh') or heads.get('hips')
                foot = heads.get('l_foot')
                fm = foot_metrics(mesh) if mesh else None
                if hip:
                    px = p.guide(hip[1], (255, 210, 90), 1)
                    d.text((px + 2, cy + 2), 'hip', fill=(255, 210, 90))
                if foot:
                    px = p.guide(foot[1], (120, 220, 255), 1)
                    d.text((px + 2, cy + 16), 'ankle', fill=(120, 220, 255))
                if fm:
                    px = p.guide(fm[2], (255, 120, 220), 1)
                    d.text((px + 2, cy + 30), 'mid-foot', fill=(255, 120, 220))
                if hip:
                    txt = 'hip y %+.4f' % hip[1]
                    if foot:
                        txt += '   ankle d %+.4f' % (foot[1] - hip[1])
                    if fm:
                        txt += '   mid-foot d %+.4f' % (fm[2] - hip[1])
                    d.text((cx + 4, cy + ph - 14), txt, fill=(210, 220, 235))
    img.save(a.out)
    print('\n[written] %s' % a.out)


if __name__ == '__main__':
    main()
