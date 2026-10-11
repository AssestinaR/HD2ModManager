"""A3: skin the real L4D2 mesh with the A2 rig map, then LOOK at it.

A2 proved the *skeleton* maps cleanly.  A3 answers the question the skeleton
cannot: does the MESH survive?  We take the actual VVD vertices + VVD weights +
VTX triangles, push every vertex through the per-bone map

        v' = sum_i  w_i * T_i * v          (T_i = H_sigma(i) * S_i^-1)

with bones that have no counterpart falling back to their nearest mapped
ancestor, and write two OBJ files (original + mapped) plus a flat-shaded
render so a human can eyeball where it stretches.

Nothing here touches production code; it is the offline "axe" for A3.
"""
import argparse, json, math, os, struct, sys

from PIL import Image, ImageDraw

try:
    import numpy as np
except ImportError:  # Kabsch residual degrades to zeros; everything else still works
    np = None

# ------------------------------------------------------------------ constants
BONE_STRIDE = 216
POSE_TO_BONE_OFF = 96
IN2M = 0.0254
BODY_PART_SIZE = 16
MODEL_SIZE_V53 = 148
MESH_SIZE_V53 = 116

SRC_PREFIX = 'ValveBiped.Bip01_'

BODY = [('Pelvis', 'hips'), ('Spine', 'spine1'), ('Spine2', 'spine2'), ('Spine4', 'chest'),
        ('Neck1', 'neck'), ('Head1', 'head'),
        ('L_Clavicle', 'l_clavicle'), ('L_UpperArm', 'l_shoulder'),
        ('L_Forearm', 'l_elbow'), ('L_Hand', 'l_hand'),
        ('R_Clavicle', 'r_clavicle'), ('R_UpperArm', 'r_shoulder'),
        ('R_Forearm', 'r_elbow'), ('R_Hand', 'r_hand'),
        ('L_Thigh', 'l_thigh'), ('L_Calf', 'l_knee'), ('L_Foot', 'l_foot'),
        ('R_Thigh', 'r_thigh'), ('R_Calf', 'r_knee'), ('R_Foot', 'r_foot')]

# `probe_chain.py` + `probe_weights.py` on Nick showed the spine chain is mis-wired:
#   source Spine  joint 1.099 -> HD2 spine1 joint 0.974 (-0.125) while
#   source Pelvis joint 0.819 -> HD2 hips   joint 0.974 (+0.155),
# so the pelvis mesh (centroid 1.082, joint 0.819 -- the source paints the lower
# torso onto Pelvis) jumps ABOVE the spine mesh (centroid 1.166 -> 1.041) and the
# two halves of the torso cross over.  Matching the *mesh centroids* instead gives
#   Spine 1.166 ~ HD2 spine2 1.163   and   Spine4 1.341 ~ HD2 chest 1.344,
# i.e. source Spine belongs on HD2 spine2, and HD2 spine1 (which is coincident
# with hips) simply has no counterpart.  `spine1` is therefore the bug, and this
# is the "explicit torso joint semantics" fix from the A4 list.
SPINE_MAPS = {
    'legacy': [('Spine', 'spine1'), ('Spine2', 'spine2'), ('Spine4', 'chest')],
    'mesh': [('Spine', 'spine2'), ('Spine2', 'chest'), ('Spine4', 'chest')],
}


def body_pairs(spine_map='legacy'):
    """The 20 body pairs, with the spine triple resolved by ``spine_map``."""
    out = []
    for name, target in BODY:
        if name in ('Spine', 'Spine2', 'Spine4'):
            continue
        out.append((name, target))
    rows = SPINE_MAPS[spine_map]
    # keep the anatomical order: Pelvis, Spine, Spine2, Spine4, Neck1, Head1, ...
    out.insert(1, rows[0])
    out.insert(2, rows[1])
    out.insert(3, rows[2])
    return out


FINGER_NAMES = {0: 'thumb', 1: 'index', 2: 'middle', 3: 'ring', 4: 'pinky'}
FINGERS = []
for _side, _pfx in (('L', 'l'), ('R', 'r')):
    for _idx, _fn in FINGER_NAMES.items():
        for _j, _sfx in enumerate(('', '1', '2'), start=1):
            FINGERS.append(('%s_Finger%d%s' % (_side, _idx, _sfx),
                            '%s_%s_finger%d' % (_pfx, _fn, _j)))

CORRESPONDENCE = BODY + FINGERS

# The HD2 toe/ball bones.  The MMD dictionary shipped by HD2-BatchTool maps a
# third-party toe bone onto `l_ball`, i.e. `l_ball` *is* the ball-of-foot bone --
# so ValveBiped's `Bip01_L_Toe0` belongs here too.  Kept out of CORRESPONDENCE so
# the "50 pairs" contract (20 body + 30 fingers) stays intact.
TOES = [('L_Toe0', 'l_ball'), ('R_Toe0', 'r_ball')]

# The bones HD2 holds in a different stance than the source.  `probe_chain.py`
# measured the source feet 0.153 m apart against HD2's 0.382 m, and
# `probe_weights.py` showed the source paints the shoes onto the unnameable
# `source_extra_62/63` (centroid x = +-0.076), which fall back onto L_Foot/R_Foot:
# snapping those onto HD2's splayed l_foot/r_foot (x = -+0.1908) pushes the shoes
# ~0.11 m outboard.  HD2-BatchTool's own workflow says the legs need not be
# aligned and only a straight-leg authoring stance is supported, so the sub-thigh
# chain keeps its own offsets (`transfer_translation`).  The thighs themselves
# already line up (source z 0.9428 / x 0.077 vs HD2 z 0.9417 / x 0.087) so they
# stay snapped.
LEG_BONES = ('L_Calf', 'L_Foot', 'R_Calf', 'R_Foot')


# ------------------------------------------------------------------ VPK
def parse_vpk_tree(path):
    with open(path, 'rb') as f:
        h = f.read(28)
        magic, ver = struct.unpack_from('<II', h, 0)
        if magic != 0x55AA1234:
            raise ValueError('not a VPK')
        ts, = struct.unpack_from('<I', h, 8)
        hdr = 12 if ver == 1 else 28
        f.seek(hdr)
        t = f.read(ts)
    te = len(t); pos = 0; out = []

    def rs():
        nonlocal pos
        s = pos
        while pos < te and t[pos] != 0:
            pos += 1
        v = t[s:pos].decode('utf-8', 'replace'); pos += 1
        return v

    while True:
        e = rs()
        if not e:
            break
        while True:
            d = rs()
            if not d:
                break
            while True:
                n = rs()
                if not n:
                    break
                _crc, pre, a, o, l, _t = struct.unpack_from('<IHHIIH', t, pos); pos += 18 + pre
                fn = n if e == ' ' else n + '.' + e
                out.append(((fn if d in ('', ' ') else d + '/' + fn).lstrip('/'), a, o, l))
    return ver, hdr + ts, out


def read_range(vp, do, a, o, l):
    if l <= 0 or l > 128 * 1024 * 1024:
        return None
    if a == 0x7FFF:
        with open(vp, 'rb') as f:
            f.seek(do + o)
            return f.read(l)
    s = vp[:-4] + '_%03d.vpk' % a
    if not os.path.exists(s):
        return None
    with open(s, 'rb') as f:
        f.seek(o)
        return f.read(l)


# ------------------------------------------------------------------ linear algebra
def mat34_to_4x4(m):
    return [m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
            m[8], m[9], m[10], m[11], 0.0, 0.0, 0.0, 1.0]


def mat_inverse(m):
    a = [list(m[0:4]), list(m[4:8]), list(m[8:12]), list(m[12:16])]
    inv = [[1.0 if i == j else 0.0 for j in range(4)] for i in range(4)]
    for col in range(4):
        piv = max(range(col, 4), key=lambda r: abs(a[r][col]))
        if abs(a[piv][col]) < 1e-12:
            raise ValueError('singular matrix')
        if piv != col:
            a[col], a[piv] = a[piv], a[col]
            inv[col], inv[piv] = inv[piv], inv[col]
        d = a[col][col]
        a[col] = [v / d for v in a[col]]
        inv[col] = [v / d for v in inv[col]]
        for r in range(4):
            if r == col:
                continue
            f = a[r][col]
            if f:
                a[r] = [a[r][j] - f * a[col][j] for j in range(4)]
                inv[r] = [inv[r][j] - f * inv[col][j] for j in range(4)]
    return [v for row in inv for v in row]


def mat_mul(x, y):
    return [sum(x[r * 4 + k] * y[k * 4 + c] for k in range(4)) for r in range(4) for c in range(4)]


def scale_translation(m, k):
    out = list(m)
    for i in (3, 7, 11):
        out[i] *= k
    return out


def transpose4(m):
    """HD2 stores transforms row-vector style (translation flat 12..14); Source
    uses column-vector style (translation flat 3,7,11).  Transpose to unify."""
    return [m[0], m[4], m[8], m[12], m[1], m[5], m[9], m[13],
            m[2], m[6], m[10], m[14], m[3], m[7], m[11], m[15]]


def origin(m):
    return (m[3], m[7], m[11])


def rot3(m):
    """Rotation part of a column-vector 4x4 as a flat 3x3."""
    return (m[0], m[1], m[2], m[4], m[5], m[6], m[8], m[9], m[10])


def apply3(r, v):
    return (r[0] * v[0] + r[1] * v[1] + r[2] * v[2],
            r[3] * v[0] + r[4] * v[1] + r[5] * v[2],
            r[6] * v[0] + r[7] * v[1] + r[8] * v[2])


def compose(r3, t):
    return [r3[0], r3[1], r3[2], t[0], r3[3], r3[4], r3[5], t[1],
            r3[6], r3[7], r3[8], t[2], 0.0, 0.0, 0.0, 1.0]


def xform(m, p):
    x, y, z = p
    return (m[0] * x + m[1] * y + m[2] * z + m[3],
            m[4] * x + m[5] * y + m[6] * z + m[7],
            m[8] * x + m[9] * y + m[10] * z + m[11])


# ------------------------------------------------------------------ readers
def cstr(buf, at, limit=200):
    if at <= 0 or at >= len(buf):
        return ''
    end = buf.find(b'\0', at, min(at + limit, len(buf)))
    if end < 0:
        return ''
    return buf[at:end].decode('ascii', 'replace')


def read_source_bones(mdl):
    bc, = struct.unpack_from('<i', mdl, 156)
    bo, = struct.unpack_from('<i', mdl, 160)
    bones = []
    for i in range(bc):
        e = bo + i * BONE_STRIDE
        ni, = struct.unpack_from('<i', mdl, e)
        par, = struct.unpack_from('<i', mdl, e + 4)
        p2b = struct.unpack_from('<12f', mdl, e + POSE_TO_BONE_OFF)
        name = cstr(mdl, e + ni)
        if not name or not all(c.isalnum() or c in '_.-' for c in name):
            name = 'source_extra_%d' % i
        world_in = mat_inverse(mat34_to_4x4(p2b))
        world_m = scale_translation(world_in, IN2M)
        bones.append(dict(index=i, name=name, parent=par, world_m=world_m))
    return bones


def read_hd2_rig(toc, bonehash):
    d = open(toc, 'rb').read()
    tio, = struct.unpack_from('<I', d, 0x34)
    n, = struct.unpack_from('<I', d, tio)
    mo = tio + 16 + n * 64
    eo = mo + n * 64
    ho = eo + n * 4
    names = {}
    for line in open(bonehash, encoding='utf-8', errors='replace'):
        p = line.split()
        if len(p) >= 2:
            try:
                names[int(p[0])] = p[1]
            except ValueError:
                pass
    out = []
    for i in range(n):
        par, = struct.unpack_from('<H', d, eo + i * 4 + 2)
        raw = list(struct.unpack_from('<16f', d, mo + i * 64))
        out.append(dict(index=i, name=names.get(struct.unpack_from('<I', d, ho + i * 4)[0]),
                        parent=par, world=transpose4(raw)))
    return out


def read_mdl_geometry(mdl):
    bpc, = struct.unpack_from('<i', mdl, 232)
    bpo, = struct.unpack_from('<i', mdl, 236)
    parts = []
    for i in range(bpc):
        e = bpo + i * BODY_PART_SIZE
        ni, = struct.unpack_from('<i', mdl, e)
        name = cstr(mdl, e + ni) or 'bodypart_%d' % i
        mc, = struct.unpack_from('<i', mdl, e + 4)
        mo, = struct.unpack_from('<i', mdl, e + 12)
        models = []
        if mc:
            ms = e + mo
            for mi in range(mc):
                me = ms + mi * MODEL_SIZE_V53
                mname = mdl[me:me + 64].split(b'\0')[0].decode('ascii', 'replace')
                meshc, = struct.unpack_from('<i', mdl, me + 72)
                mesho, = struct.unpack_from('<i', mdl, me + 76)
                vc, = struct.unpack_from('<i', mdl, me + 80)
                vbo, = struct.unpack_from('<i', mdl, me + 84)
                if vbo < 0 or vbo % 48 != 0:
                    raise ValueError('model %d vertex offset %d not a multiple of 48' % (mi, vbo))
                meshes = []
                if meshc:
                    msx = me + mesho
                    for xi in range(meshc):
                        xe = msx + xi * MESH_SIZE_V53
                        mat, = struct.unpack_from('<i', mdl, xe)
                        mvc, = struct.unpack_from('<i', mdl, xe + 8)
                        mvs, = struct.unpack_from('<i', mdl, xe + 12)
                        meshes.append(dict(material=mat, vertexCount=mvc, vertexStart=mvs))
                models.append(dict(name=mname or 'model_%d' % mi, vertexCount=vc,
                                   vertexOffset=vbo // 48, meshes=meshes))
        parts.append(dict(name=name, models=models))
    return parts


def read_vvd(data):
    if data[0:4] != b'IDSV':
        raise ValueError('bad VVD magic')
    lod_count, = struct.unpack_from('<i', data, 12)
    lod_verts = struct.unpack_from('<8i', data, 16)
    fix_count, = struct.unpack_from('<i', data, 48)
    fix_off, = struct.unpack_from('<i', data, 52)
    v_off, = struct.unpack_from('<i', data, 56)
    raw = []
    for i in range(lod_verts[0]):
        o = v_off + i * 48
        raw.append(dict(w=struct.unpack_from('<3f', data, o),
                        bones=(data[o + 12], data[o + 13], data[o + 14]),
                        boneCount=data[o + 15],
                        pos=struct.unpack_from('<3f', data, o + 16),
                        nrm=struct.unpack_from('<3f', data, o + 28)))
    if fix_count == 0:
        return raw, lod_count, lod_verts[0]
    out = [None] * lod_verts[0]
    p = 0
    for i in range(fix_count):
        o = fix_off + i * 12
        lod, vi, cnt = struct.unpack_from('<3i', data, o)
        if lod >= 0:
            out[p:p + cnt] = raw[vi:vi + cnt]
            p += cnt
    if p != lod_verts[0]:
        raise ValueError('VVD fixups emit %d vertices, expected %d' % (p, lod_verts[0]))
    return out, lod_count, lod_verts[0]


def _read_vtx_parts(data, count, offset, lod_count, extra8):
    out = []
    for i in range(count):
        e = offset + i * 8
        mc, = struct.unpack_from('<i', data, e)
        mo, = struct.unpack_from('<i', data, e + 4)
        if not (0 <= mc <= 1000):
            raise ValueError('bad vtx model count')
        models = []
        mstart = e + mo
        for mi in range(mc):
            me = mstart + mi * 8
            mlc, = struct.unpack_from('<i', data, me)
            mlo, = struct.unpack_from('<i', data, me + 4)
            if not (0 <= mlc <= lod_count):
                raise ValueError('bad vtx lod count')
            lods = []
            lstart = (me + mlo) if mlc else 0
            for li in range(mlc):
                le = lstart + li * 12
                xc, = struct.unpack_from('<i', data, le)
                xo, = struct.unpack_from('<i', data, le + 4)
                if not (0 <= xc <= 10000):
                    raise ValueError('bad vtx mesh count')
                meshes = []
                xstart = (le + xo) if xc else 0
                for xi in range(xc):
                    xe = xstart + xi * 9
                    gc, = struct.unpack_from('<i', data, xe)
                    go, = struct.unpack_from('<i', data, xe + 4)
                    if not (0 <= gc <= 100000):
                        raise ValueError('bad vtx stripgroup count')
                    groups = []
                    gsize = 33 if extra8 else 25
                    gstart = (xe + go) if gc else 0
                    for gi in range(gc):
                        ge = gstart + gi * gsize
                        vc, = struct.unpack_from('<i', data, ge)
                        vo, = struct.unpack_from('<i', data, ge + 4)
                        ic, = struct.unpack_from('<i', data, ge + 8)
                        io, = struct.unpack_from('<i', data, ge + 12)
                        if not (0 <= vc <= 2000000) or ic % 3 or not (0 <= ic <= 6000000):
                            raise ValueError('bad stripgroup counts')
                        vstart = ge + vo
                        istart = ge + io
                        orig = [struct.unpack_from('<H', data, vstart + v * 9 + 4)[0] for v in range(vc)]
                        idx = [struct.unpack_from('<H', data, istart + k * 2)[0] for k in range(ic)]
                        if any(v >= vc for v in idx):
                            raise ValueError('stripgroup index out of range')
                        groups.append(dict(orig=orig, indices=idx))
                    meshes.append(dict(groups=groups))
                lods.append(dict(meshes=meshes))
            models.append(dict(lods=lods))
        out.append(dict(models=models))
    return out


def read_vtx(data):
    ver, = struct.unpack_from('<i', data, 0)
    lod_count, = struct.unpack_from('<i', data, 20)
    bpc, = struct.unpack_from('<i', data, 28)
    bpo, = struct.unpack_from('<i', data, 32)
    for extra8 in (False, True):
        try:
            return _read_vtx_parts(data, bpc, bpo, lod_count, extra8), ver, extra8
        except Exception:
            continue
    raise ValueError('VTX tree not decodable with v6/v7 layouts')


def build_geometry(mdl_parts, vvd_verts, vtx_parts, diag):
    if len(mdl_parts) != len(vtx_parts):
        raise ValueError('MDL has %d body parts, VTX has %d' % (len(mdl_parts), len(vtx_parts)))
    verts = []
    tris = []
    for bp_i, mp in enumerate(mdl_parts):
        vp = vtx_parts[bp_i]
        if len(mp['models']) != len(vp['models']):
            raise ValueError('bodypart %s: model count mismatch' % mp['name'])
        for mi, mm in enumerate(mp['models']):
            vm = vp['models'][mi]
            if mm['vertexCount'] == 0:
                continue
            if not vm['lods']:
                raise ValueError('model %s has no VTX LODs' % mm['name'])
            lod0 = vm['lods'][0]
            if len(lod0['meshes']) != len(mm['meshes']):
                raise ValueError('model %s: mesh count mismatch' % mm['name'])
            n_before = len(verts)
            n_tri_before = len(tris)
            for xi, msh in enumerate(mm['meshes']):
                for g in lod0['meshes'][xi]['groups']:
                    base = len(verts)
                    for orig in g['orig']:
                        if orig >= msh['vertexCount']:
                            raise ValueError('VTX original vertex %d >= MDL mesh count %d'
                                             % (orig, msh['vertexCount']))
                        gi = mm['vertexOffset'] + msh['vertexStart'] + orig
                        if not (0 <= gi < len(vvd_verts)):
                            raise ValueError('global vertex %d outside VVD LOD0 (%d)'
                                             % (gi, len(vvd_verts)))
                        verts.append(vvd_verts[gi])
                    idx = g['indices']
                    for k in range(0, len(idx), 3):
                        tris.append((base + idx[k], base + idx[k + 1], base + idx[k + 2],
                                     msh['material']))
            diag.append('%-30s %-34s verts %6d  tris %6d'
                        % (mp['name'], mm['name'], len(verts) - n_before,
                           len(tris) - n_tri_before))
    return verts, tris


# ------------------------------------------------------------------ skinning
def bone_axis(m):
    """World direction of the bone's local +X.  BOTH engines put the bone along
    local +X (verified: source L_Thigh/L_Calf/Spine, HD2 l_thigh/l_knee/spine2)."""
    x, y, z = m[0], m[4], m[8]
    n = math.sqrt(x * x + y * y + z * z) or 1.0
    return (x / n, y / n, z / n)


def minimal_rotation(u, v):
    """Shortest-arc (swing only, no twist) rotation matrix taking u to v."""
    cx = u[1] * v[2] - u[2] * v[1]
    cy = u[2] * v[0] - u[0] * v[2]
    cz = u[0] * v[1] - u[1] * v[0]
    s = math.sqrt(cx * cx + cy * cy + cz * cz)
    c = u[0] * v[0] + u[1] * v[1] + u[2] * v[2]
    if s < 1e-9:
        if c > 0:
            return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
        # 180 degrees: pick any perpendicular axis
        a = (1.0, 0.0, 0.0) if abs(u[0]) < 0.9 else (0.0, 1.0, 0.0)
        ax = (u[1] * a[2] - u[2] * a[1], u[2] * a[0] - u[0] * a[2], u[0] * a[1] - u[1] * a[0])
        n = math.sqrt(sum(t * t for t in ax)) or 1.0
        ax = tuple(t / n for t in ax)
        kx, ky, kz = ax
        return [2 * kx * kx - 1, 2 * kx * ky, 2 * kx * kz, 0,
                2 * ky * kx, 2 * ky * ky - 1, 2 * ky * kz, 0,
                2 * kz * kx, 2 * kz * ky, 2 * kz * kz - 1, 0, 0, 0, 0, 1]
    kx, ky, kz = cx / s, cy / s, cz / s
    ang = math.atan2(s, c)
    K = 1.0 - math.cos(ang)
    ca, sa = math.cos(ang), math.sin(ang)
    return [ca + kx * kx * K, kx * ky * K - kz * sa, kx * kz * K + ky * sa, 0,
            ky * kx * K + kz * sa, ca + ky * ky * K, ky * kz * K - kx * sa, 0,
            kz * kx * K - ky * sa, kz * ky * K + kx * sa, ca + kz * kz * K, 0,
            0, 0, 0, 1]


def swing_matrix(sb, hb):
    """Rigid map for one bone pair using the axis swing only: the source bone's
    world +X is rotated onto the HD2 bone's world +X by the shortest arc, and the
    source origin lands on the HD2 origin.  This throws away the roll, which is
    exactly the part the two rigs disagree about."""
    R = minimal_rotation(bone_axis(sb['world_m']), bone_axis(hb['world']))
    s = origin(sb['world_m'])
    h = origin(hb['world'])
    t = tuple(h[i] - (R[i * 4] * s[0] + R[i * 4 + 1] * s[1] + R[i * 4 + 2] * s[2]) for i in range(3))
    return [R[0], R[1], R[2], t[0], R[4], R[5], R[6], t[1],
            R[8], R[9], R[10], t[2], 0.0, 0.0, 0.0, 1.0]


LANDMARK_SEGMENTS = [
    ('Pelvis', 'Spine'), ('Spine', 'Spine2'), ('Spine2', 'Spine4'),
    ('Spine4', 'Neck1'), ('Neck1', 'Head1'),
    ('L_Clavicle', 'L_UpperArm'), ('L_UpperArm', 'L_Forearm'), ('L_Forearm', 'L_Hand'),
    ('R_Clavicle', 'R_UpperArm'), ('R_UpperArm', 'R_Forearm'), ('R_Forearm', 'R_Hand'),
    ('L_Thigh', 'L_Calf'), ('L_Calf', 'L_Foot'),
    ('R_Thigh', 'R_Calf'), ('R_Calf', 'R_Foot'),
]


def segment_report(short, by_name):
    """Bone-length ratio (HD2 / source) per landmark segment.

    This is the literal mesh stretch the retarget imposes: a value of 1.0 means the
    limb keeps its source length, 2.0 means it is drawn twice as long.  It is the one
    number that survives every roll/twist convention difference, so it is the fairest
    cross-model A4 metric.
    """
    cmap = dict(CORRESPONDENCE)
    out = []
    for a_s, b_s in LANDMARK_SEGMENTS:
        sa, sb2 = short.get(a_s), short.get(b_s)
        ha = by_name.get(cmap.get(a_s)) if cmap.get(a_s) else None
        hb = by_name.get(cmap.get(b_s)) if cmap.get(b_s) else None
        if not (sa and sb2 and ha and hb):
            continue
        ls = math.dist(origin(sa['world_m']), origin(sb2['world_m']))
        lh = math.dist(origin(ha['world']), origin(hb['world']))
        if ls > 1e-6:
            out.append((a_s, lh / ls))
    return out


def source_centroids(verts):
    """Per-source-bone dominant-weight vertex centroid, in source world metres.

    ``probe_weights.py`` showed the source paints big regions onto bones whose
    JOINT sits far from the region's centre of mass:

        Pelvis  joint 0.819  -> mesh 1.082   (the lower torso is painted on Pelvis)
        Spine   joint 1.099  -> mesh 1.166
        Spine4  joint 1.219  -> mesh 1.341
        Head1   joint 1.507  -> mesh 1.634
        Neck1   joint 1.453  -> mesh 1.466

    Snapping the source JOINT onto the HD2 joint therefore drags the mesh by the
    *joint's* error rather than the *mesh's*: Head1's joint lands 0.184 m too high
    (HD2 head 1.691), which is the "long neck" that inflates height by ~9.6 %.
    The mesh's own error is only +0.057 m.  Anchoring the mesh CENTROID on the HD2
    joint is the fix, and it is only knowable from the vertices.
    """
    acc = {}
    for v in verts:
        if v['boneCount'] == 0:
            continue
        b = v['bones'][0]
        e = acc.get(b)
        p = (v['pos'][0] * IN2M, v['pos'][1] * IN2M, v['pos'][2] * IN2M)
        if e is None:
            acc[b] = [p[0], p[1], p[2], 1]
        else:
            e[0] += p[0]; e[1] += p[1]; e[2] += p[2]; e[3] += 1
    return {i: (e[0] / e[3], e[1] / e[3], e[2] / e[3]) for i, e in acc.items() if e[3]}


def build_bone_map(mdl, toc, bonehash, roll_mode='frame', root_flat=True,
                   transfer_bones=(), spine_map='legacy', anchor_centroids=None):
    """Per-source-bone rigid map onto the HD2 rig.

    ``roll_mode`` decides the *rotation*:
      frame -- the full HD2 bone frame (bakes the authored roll, which the two
               rigs disagree about: torso/neck/head ~70.8 deg off);
      swing -- shortest arc taking the source bone's +X onto the HD2 bone's +X.

    ``spine_map`` decides where the source spine triple lands (see SPINE_MAPS).

    ``transfer_bones`` decides the *translation* for the named source bones:
      absent -- snap: the source bone origin is moved onto the HD2 bone origin.
                This is what silently splays the feet from 0.153 m to 0.382 m.
      present-- transfer: keep the source bone's own offset from its parent,
                rotated into the *mapped* parent's frame.  This is the flag the
                HD2 runtime format calls ``transfer_translation`` and it is how
                HD2-BatchTool preserves a third-party rig's proportions.

    ``anchor_centroids`` maps a source bone short name (e.g. ``'Head1'``) to that
    bone's mesh centroid in source world metres.  Listed bones land their *mesh
    centroid* on the HD2 joint instead of their *joint* -- see ``source_centroids``.
    """
    src = read_source_bones(mdl)
    hd2 = read_hd2_rig(toc, bonehash)
    by_name = {h['name']: h for h in hd2 if h['name']}
    short = {b['name'][len(SRC_PREFIX):]: b for b in src if b['name'].startswith(SRC_PREFIX)}
    transfer = set(transfer_bones)
    anchor = anchor_centroids or {}
    pairs_all = body_pairs(spine_map) + FINGERS + TOES

    pairs = []
    missing = []
    for sname, hname in pairs_all:
        sb, hb = short.get(sname), by_name.get(hname)
        if sb is None or hb is None:
            missing.append((sname, hname))
            continue
        pairs.append((sname, hname, sb, hb))

    # rotation of every mapped bone, in the source's own world space
    rot = {}
    for sname, hname, sb, hb in pairs:
        if roll_mode == 'swing':
            rot[sb['index']] = rot3(minimal_rotation(bone_axis(sb['world_m']),
                                                      bone_axis(hb['world'])))
        else:
            rot[sb['index']] = rot3(hb['world'])

    # MDL stores bones parent-first, so index order is a valid topological order.
    T = {}
    for sname, hname, sb, hb in sorted(pairs, key=lambda p: p[2]['index']):
        r3 = rot[sb['index']]
        s = origin(sb['world_m'])
        pm = T.get(sb['parent']) if sb['parent'] >= 0 else None
        if sname in anchor:
            # land the mesh centroid (not the joint) on the HD2 joint
            o = origin(hb['world'])
            r = apply3(r3, anchor[sname])
        elif sname in transfer and pm is not None:
            ps = src[sb['parent']]
            po = origin(ps['world_m'])
            # where the mapped parent actually LANDS -- not the matrix's own
            # translation column, which is a different quantity entirely
            base = xform(pm, po)
            off = apply3(rot3(pm), (s[0] - po[0], s[1] - po[1], s[2] - po[2]))
            o = (base[0] + off[0], base[1] + off[1], base[2] + off[2])
            r = apply3(r3, s)
        else:
            o = origin(hb['world'])
            r = apply3(r3, s)
        T[sb['index']] = compose(r3, (o[0] - r[0], o[1] - r[1], o[2] - r[2]))

    if root_flat and short.get('Pelvis') is not None:
        # The root bone's own frame is arbitrary (it is a position anchor in both
        # engines), so leave it unrotated and only translate it onto the HD2 hips;
        # every skirt/helper fallback hangs off it and would otherwise swing with it.
        root = short['Pelvis']
        if root['index'] in T:
            h = origin(by_name['hips']['world'])
            if 'Pelvis' in anchor:
                c = anchor['Pelvis']
                T[root['index']] = [1, 0, 0, h[0] - c[0], 0, 1, 0, h[1] - c[1],
                                    0, 0, 1, h[2] - c[2], 0, 0, 0, 1]
            else:
                s = origin(root['world_m'])
                T[root['index']] = [1, 0, 0, h[0] - s[0], 0, 1, 0, h[1] - s[1],
                                    0, 0, 1, h[2] - s[2], 0, 0, 0, 1]

    # fallback: walk up the parent chain to the nearest mapped ancestor
    resolved = {}
    depth_used = {}
    for b in src:
        i = b['index']
        if i in T:
            resolved[i] = T[i]
            depth_used[i] = 0
            continue
        cur = b['parent']
        hops = 1
        while cur >= 0 and cur not in T:
            cur = src[cur]['parent']
            hops += 1
            if hops > 64:
                cur = -1
        if cur >= 0:
            resolved[i] = T[cur]
            depth_used[i] = hops
        else:
            resolved[i] = None
    return src, hd2, T, resolved, depth_used, missing, short



def skin(verts, src_bones, resolved):
    out = []
    for v in verts:
        p = (v['pos'][0] * IN2M, v['pos'][1] * IN2M, v['pos'][2] * IN2M)
        acc = [0.0, 0.0, 0.0]
        tot = 0.0
        for k in range(3):
            w = v['w'][k]
            if w <= 0:
                continue
            b = v['bones'][k]
            if b >= len(resolved):
                continue
            M = resolved[b]
            if M is None:
                M = resolved[0] or [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
            q = xform(M, p)
            acc[0] += w * q[0]; acc[1] += w * q[1]; acc[2] += w * q[2]
            tot += w
        if tot > 0:
            acc = [c / tot for c in acc]
        else:
            acc = list(p)
        out.append((acc[0], acc[1], acc[2]))
    return out


# ------------------------------------------------------------------ output
def write_obj(path, verts, tris, note=''):
    with open(path, 'w', encoding='utf-8') as f:
        if note:
            f.write('# %s\n' % note)
        for p in verts:
            f.write('v %.6f %.6f %.6f\n' % p)
        for a, b, c, _m in tris:
            f.write('f %d %d %d\n' % (a + 1, b + 1, c + 1))


def kabsch_residual(src_pts, dst_pts, max_pts=20000):
    """Per-vertex residual after the single best-fit global rigid motion.

    A rigid per-bone map is exactly rigid inside each bone, so whatever is left here
    is either (a) the intended pose change (T-pose -> A-pose) or (b) a bone mapped to
    a frame the source never intended -- which is how a helper bone falling back to
    `hips` shows up as a skirt flying off.  Edge-length ratios are blind to (b),
    because a rigid rotation preserves edge lengths; this metric is not.
    """
    if np is None:
        return [0.0] * len(src_pts), 0.0, 1.0
    step = max(1, len(src_pts) // max_pts)
    A = np.asarray(src_pts[::step], dtype=np.float64)
    B = np.asarray(dst_pts[::step], dtype=np.float64)
    ca, cb = A.mean(0), B.mean(0)
    H = (A - ca).T @ (B - cb)
    U, _S, Vt = np.linalg.svd(H)
    d = 1.0 if np.linalg.det(Vt.T @ U.T) > 0 else -1.0
    R = Vt.T @ np.diag([1.0, 1.0, d]) @ U.T
    t = cb - R @ ca
    Aall = np.asarray(src_pts, dtype=np.float64)
    Ball = np.asarray(dst_pts, dtype=np.float64)
    resid = np.linalg.norm(Ball - (Aall @ R.T + t), axis=1)
    ang = math.degrees(math.acos(max(-1.0, min(1.0, (float(np.trace(R)) - 1.0) / 2.0))))
    return resid.tolist(), ang, float(np.linalg.det(R))


def bbox(pts):
    xs = [p[0] for p in pts]; ys = [p[1] for p in pts]; zs = [p[2] for p in pts]
    return (min(xs), min(ys), min(zs), max(xs), max(ys), max(zs))


def edge_distortion(src_pts, dst_pts, tris, step=7):
    """Ratio of every (sampled) triangle edge in the mapped mesh vs the source mesh.

    This is the one quality number that does not care about coordinate conventions:
    a rigid per-bone map leaves edges inside a bone untouched (ratio 1.0), and only
    edges that straddle a bone boundary stretch.  So the tail of this distribution is
    exactly where the mesh tears.
    """
    ratios = []
    for k in range(0, len(tris), step):
        a, b, c, _m = tris[k]
        for u, v in ((a, b), (b, c), (c, a)):
            ls = math.dist(src_pts[u], src_pts[v])
            if ls < 1e-7:
                continue
            ratios.append(math.dist(dst_pts[u], dst_pts[v]) / ls)
    if not ratios:
        return 0.0, 0.0, 0.0, 0.0, 0.0, 0.0
    ratios.sort()
    n = len(ratios)
    bad = sum(1 for r in ratios if r > 1.25 or r < 0.80) / float(n)
    return (sum(ratios) / n, ratios[n // 2], ratios[int(n * 0.95)],
            ratios[int(n * 0.99)], ratios[-1], bad)


def render_panel(img, draw, pts, tris, box, ax_h, ax_v, ax_d, flip_h, rect, bg, scale=None):
    x0, y0, x1, y1 = rect
    w = x1 - x0; h = y1 - y0
    draw.rectangle([x0, y0, x1 - 1, y1 - 1], fill=bg, outline=(90, 90, 100))
    mn_h, mn_v = box[ax_h], box[ax_v]
    mx_h, mx_v = box[3 + ax_h], box[3 + ax_v]
    span = max(mx_h - mn_h, mx_v - mn_v) or 1.0
    pad = 0.06
    sc = scale if scale is not None else min(w, h) * (1 - 2 * pad) / span
    cx = (mn_h + mx_h) / 2.0
    cv = (mn_v + mx_v) / 2.0

    def T(p):
        u = (p[ax_h] - cx) * sc
        v = (p[ax_v] - cv) * sc
        if flip_h:
            u = -u
        return (x0 + w / 2.0 + u, y0 + h / 2.0 - v)

    zbuf = [[None] * w for _ in range(h)]
    light = (-0.35, -0.55, 0.75)
    faces = []
    for a, b, c, _m in tris:
        pa, pb, pc = pts[a], pts[b], pts[c]
        depth = (pa[ax_d] + pb[ax_d] + pc[ax_d]) / 3.0
        faces.append((depth, pa, pb, pc))
    faces.sort(key=lambda f: -f[0])

    for _d, pa, pb, pc in faces:
        ta, tb, tc = T(pa), T(pb), T(pc)
        ux, uy = pb[0] - pa[0], pb[1] - pa[1]
        vx, vy = pc[0] - pa[0], pc[1] - pa[1]
        e1 = (pb[0] - pa[0], pb[1] - pa[1], pb[2] - pa[2])
        e2 = (pc[0] - pa[0], pc[1] - pa[1], pc[2] - pa[2])
        nx = e1[1] * e2[2] - e1[2] * e2[1]
        ny = e1[2] * e2[0] - e1[0] * e2[2]
        nz = e1[0] * e2[1] - e1[1] * e2[0]
        nl = math.sqrt(nx * nx + ny * ny + nz * nz) or 1.0
        nd = (nx * light[0] + ny * light[1] + nz * light[2]) / nl
        shade = 0.22 + 0.78 * abs(nd)
        col = (int(120 + 115 * shade), int(105 + 110 * shade), int(125 + 105 * shade))

        minx = max(x0, int(min(ta[0], tb[0], tc[0])))
        maxx = min(x1 - 1, int(max(ta[0], tb[0], tc[0])) + 1)
        miny = max(y0, int(min(ta[1], tb[1], tc[1])))
        maxy = min(y1 - 1, int(max(ta[1], tb[1], tc[1])) + 1)
        if minx > maxx or miny > maxy:
            continue
        d = (tb[1] - tc[1]) * (ta[0] - tc[0]) + (tc[0] - tb[0]) * (ta[1] - tc[1])
        if abs(d) < 1e-9:
            continue
        inv_d = 1.0 / d
        for py in range(miny, maxy + 1):
            for px in range(minx, maxx + 1):
                cxp, cyp = px + 0.5, py + 0.5
                w1 = ((tb[1] - tc[1]) * (cxp - tc[0]) + (tc[0] - tb[0]) * (cyp - tc[1])) * inv_d
                w2 = ((tc[1] - ta[1]) * (cxp - tc[0]) + (ta[0] - tc[0]) * (cyp - tc[1])) * inv_d
                w3 = 1.0 - w1 - w2
                if w1 < 0 or w2 < 0 or w3 < 0:
                    continue
                # NEGATIVE = closer, because ax_d grows away from the camera
                depth = -(w1 * pa[ax_d] + w2 * pb[ax_d] + w3 * pc[ax_d])
                row = zbuf[py - y0]
                cur = row[px - x0]
                if cur is None or depth < cur:
                    row[px - x0] = depth
                    img.putpixel((px, py), col)


def render(path, src_pts, dst_pts, tris, title, extra=None, crop_z=None):
    W, H = 1000, 1030
    img = Image.new('RGB', (W, H), (28, 30, 36))
    draw = ImageDraw.Draw(img)
    draw.text((12, 8), title, fill=(230, 230, 240))
    # One shared scale across every panel so the two rigs can be compared honestly.
    allp = src_pts + dst_pts + (extra[1] if extra else [])
    gb = list(bbox(allp))
    if crop_z is not None:
        # Zoom into a vertical band (e.g. the torso) so joint-level tearing is
        # actually visible instead of being a couple of pixels of silhouette.
        gb[2], gb[5] = crop_z
        gb[0] = min(min(p[0] for p in allp), -0.01)
        gb[3] = max(max(p[0] for p in allp), 0.01)
    gb = tuple(gb)
    pw, ph = 480, 300
    gsc = min(pw, ph) * (1 - 2 * 0.06) / max(gb[3] - gb[0], gb[5] - gb[2], 0.001)
    panels = [
        ('src front (X-Z)', src_pts, 0, 2, 1, False),
        ('src side (Y-Z)', src_pts, 1, 2, 0, True),
    ]
    if extra:
        panels.append((extra[0], extra[1], 0, 2, 1, False))
        panels.append((extra[0] + ' side', extra[1], 1, 2, 0, True))
    panels.append(('mapped front', dst_pts, 0, 2, 1, False))
    panels.append(('mapped side', dst_pts, 1, 2, 0, True))
    for i, (label, pts, ah, av, ad, flip) in enumerate(panels):
        cx = 10 + (i % 2) * (pw + 20)
        cy = 34 + (i // 2) * (ph + 30)
        draw.text((cx + 4, cy - 14), label, fill=(200, 210, 225))
        render_panel(img, draw, pts, tris, gb, ah, av, ad, flip,
                     (cx, cy, cx + pw, cy + ph), (24, 26, 32), gsc)
    img.save(path)
    return path


# ------------------------------------------------------------------ main
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--vpk', required=True)
    ap.add_argument('--model', default='survivor_teenangst')
    ap.add_argument('--toc', required=True)
    ap.add_argument('--bonehash', required=True)
    ap.add_argument('--outdir', default='.')
    ap.add_argument('--limit-tris', type=int, default=0)
    ap.add_argument('--skip-render', action='store_true')
    ap.add_argument('--roll-mode', choices=('frame', 'swing'), default='frame',
                    help='frame = full per-bone frame map H*S^-1 (keeps the authored '
                         'roll, which the two rigs disagree about); '
                         'swing = shortest-arc axis alignment only (drops the roll)')
    ap.add_argument('--tag', default='')
    ap.add_argument('--spine-map', choices=('legacy', 'mesh'), default='legacy',
                    help='legacy = source Spine -> HD2 spine1 (spine1 is coincident '
                         'with hips, so the pelvis mesh jumps above the spine mesh); '
                         'mesh = source Spine -> HD2 spine2, matching mesh centroids')
    ap.add_argument('--transfer-bones', choices=('none', 'legs', 'all'), default='none',
                    help='none = snap every source joint onto its HD2 joint (splays the '
                         'feet to HD2\'s at-ease stance); legs = keep the sub-thigh '
                         'offsets (straight legs, as HD2-BatchTool requires); all = keep '
                         'every source offset (proportions exact, bones drift off HD2)')
    ap.add_argument('--json-out', default='',
                    help='dump the A3 metrics as JSON for batch (A4) comparison')
    ap.add_argument('--anchor', choices=('joint', 'neck', 'neck+spine', 'all'), default='joint',
                    help='joint = land each source JOINT on its HD2 joint (default); '
                         'neck = land the mesh CENTROID of Neck1/Head1 on the HD2 joints '
                         '(removes the +0.184 m "long neck" that inflates height ~9.6%%); '
                         'neck+spine = also Pelvis/Spine/Spine4; all = every mapped bone')
    ap.add_argument('--prefix', default='',
                    help='output filename prefix (default: the model basename)')
    a = ap.parse_args()

    transfer_set = ()
    if a.transfer_bones == 'legs':
        transfer_set = LEG_BONES
    elif a.transfer_bones == 'all':
        transfer_set = tuple(n for n, _h in body_pairs(a.spine_map) + FINGERS + TOES)

    _v, do, ents = parse_vpk_tree(a.vpk)
    by_path = {f.lower(): (f, arch, o, l) for f, arch, o, l in ents}

    cands = [f for f in by_path if f.lower().endswith('.mdl')
             and a.model.lower() in os.path.basename(f).lower()]
    if not cands:
        raise SystemExit('no .mdl matching %r' % a.model)
    cands.sort(key=lambda f: (len(os.path.basename(f)), os.path.basename(f)))
    mdl_path = cands[0]
    base = mdl_path[:-4]

    def blob(path):
        if path.lower() not in by_path:
            raise SystemExit('VPK entry not found: %s' % path)
        f, arch, o, l = by_path[path.lower()]
        d = read_range(a.vpk, do, arch, o, l)
        if d is None:
            raise SystemExit('could not read %s (external archive?)' % path)
        return d

    vvd_path = next((p for p in by_path if p.lower() == (base + '.vvd').lower()), None)
    vtx_path = next((p for p in by_path if p.lower() == (base + '.dx90.vtx').lower()), None)
    if vvd_path is None:
        vvd_path = next((p for p in by_path if p.lower().endswith('.vvd')
                         and os.path.basename(p)[:-4].lower() == os.path.basename(base).lower()), None)
    if vtx_path is None:
        vtx_path = next((p for p in by_path if p.lower().endswith('.vtx')
                         and os.path.basename(p).lower().startswith(os.path.basename(base).lower())), None)
    if not vvd_path or not vtx_path:
        raise SystemExit('missing companion files for %s (vvd=%s vtx=%s)'
                         % (mdl_path, vvd_path, vtx_path))

    print('=== A3  mesh check ===')
    print('mdl : %s' % mdl_path)
    print('vvd : %s' % vvd_path)
    print('vtx : %s' % vtx_path)

    mdl = blob(mdl_path)
    vvd_raw = blob(vvd_path)
    vtx_raw = blob(vtx_path)

    mdl_parts = read_mdl_geometry(mdl)
    vvd_verts, lod_count, lod0_n = read_vvd(vvd_raw)
    vtx_parts, vtx_ver, extra8 = read_vtx(vtx_raw)
    print('vvd : LODs=%d  LOD0 vertices=%d  (raw %d)'
          % (lod_count, lod0_n, sum(1 for v in vvd_verts if v)))
    print('vtx : version=%d  group stride=%s' % (vtx_ver, 33 if extra8 else 25))

    diag = []
    verts, tris = build_geometry(mdl_parts, vvd_verts, vtx_parts, diag)
    print('mesh: %d vertices, %d triangles' % (len(verts), len(tris)))
    for d in diag:
        print('   ' + d)
    if not tris:
        raise SystemExit('no triangles built')
    if a.limit_tris:
        tris = tris[:a.limit_tris]

    # ---- mesh-centroid anchoring (A4 fix (2): the "long neck") --------------
    if a.anchor == 'neck':
        anchor_names = {'Neck1', 'Head1'}
    elif a.anchor == 'neck+spine':
        anchor_names = {'Pelvis', 'Spine', 'Spine2', 'Spine4', 'Neck1', 'Head1'}
    elif a.anchor == 'all':
        anchor_names = {n for n, _h in body_pairs(a.spine_map) + FINGERS + TOES}
    else:
        anchor_names = set()
    anchor_set = {}
    if anchor_names:
        cen = source_centroids(verts)
        for _b in read_source_bones(mdl):
            _nm = _b['name'][len(SRC_PREFIX):] if _b['name'].startswith(SRC_PREFIX) else None
            if _nm in anchor_names and _b['index'] in cen:
                anchor_set[_nm] = cen[_b['index']]
        print('anchor centroid: %s' % ', '.join(sorted(anchor_set)))

    src_bones, hd2, T, resolved, depth_used, missing, short_by_name = \
        build_bone_map(mdl, a.toc, a.bonehash, a.roll_mode,
                       transfer_bones=transfer_set, spine_map=a.spine_map,
                       anchor_centroids=anchor_set)
    print('roll mode: %s   transfer: %s   spine: %s   anchor: %s'
          % (a.roll_mode, a.transfer_bones, a.spine_map, a.anchor))
    mapped = sum(1 for b in src_bones if b['index'] in T)
    print('bones: %d source, %d direct maps, %d missing correspondence'
          % (len(src_bones), mapped, len(missing)))
    for s, h in missing:
        print('   MISSING %-14s -> %s' % (s, h))
    hops = {}
    for b in src_bones:
        hops[depth_used.get(b['index'], 0)] = hops.get(depth_used.get(b['index'], 0), 0) + 1
    print('fallback hops histogram: %s' % dict(sorted(hops.items())))

    # Diagnostic A3.1 -- decompose T into the physically meaningful axis swing and
    # the leftover roll.  Both engines put the bone along local +X, so the swing is
    # the real retarget; the roll is an authored bone-local convention difference
    # that the naive H*S^-1 silently bakes into the mesh as a twist.
    hd2_by_name = {h['name']: h for h in hd2 if h['name']}
    rows = []
    for sname, hname in CORRESPONDENCE:
        sb = short_by_name.get(sname)
        hb = hd2_by_name.get(hname)
        if sb is None or hb is None:
            continue
        T_full = mat_mul(hb['world'], mat_inverse(sb['world_m']))
        tr = math.degrees(math.acos(max(-1.0, min(1.0, (T_full[0] + T_full[5] + T_full[10] - 1) / 2))))
        aS, aH = bone_axis(sb['world_m']), bone_axis(hb['world'])
        sw = math.degrees(math.acos(max(-1.0, min(1.0, sum(aS[i] * aH[i] for i in range(3))))))
        rows.append((abs(tr - sw), tr, sw, sname, hname))
    rows.sort(reverse=True)
    print('\n-- A3.1  swing (real retarget) vs full T (swing + authored roll) --')
    print('   %-14s %-22s %8s %8s %8s' % ('source', 'hd2', 'swing', 'T-rot', 'roll'))
    for roll, tr, sw, s, h in rows[:12]:
        print('   %-14s %-22s %8.2f %8.2f %8.2f' % (s, h, sw, tr, roll))
    body = [r for r in rows if r[3] in dict(BODY)]
    torso = [r for r in body if r[3] in ('Spine', 'Spine2', 'Spine4', 'Neck1', 'Head1', 'Pelvis')]
    legs = [r for r in body if r[3] in ('L_Thigh', 'L_Calf', 'R_Thigh', 'R_Calf')]
    if torso:
        print('   torso/neck/head mean roll = %.1f deg   (legs %.1f deg)'
              % (sum(r[0] for r in torso) / len(torso),
                 sum(r[0] for r in legs) / len(legs) if legs else 0.0))

    src_pts = [(v['pos'][0] * IN2M, v['pos'][1] * IN2M, v['pos'][2] * IN2M) for v in verts]
    dst_pts = skin(verts, src_bones, resolved)

    # Always compute the other roll mode too so the render can show both.
    other = 'frame' if a.roll_mode == 'swing' else 'swing'
    _s2, _h2, _T2, resolved2, _d2, _m2, _sn2 = \
        build_bone_map(mdl, a.toc, a.bonehash, other,
                       transfer_bones=transfer_set, spine_map=a.spine_map)
    other_pts = skin(verts, src_bones, resolved2)

    sb, db = bbox(src_pts), bbox(dst_pts)
    print('\nbbox source (m):  x[%.3f,%.3f] y[%.3f,%.3f] z[%.3f,%.3f]'
          % (sb[0], sb[3], sb[1], sb[4], sb[2], sb[5]))
    print('bbox mapped (m):  x[%.3f,%.3f] y[%.3f,%.3f] z[%.3f,%.3f]'
          % (db[0], db[3], db[1], db[4], db[2], db[5]))
    print('height source %.3f m -> mapped %.3f m' % (sb[5] - sb[2], db[5] - db[2]))

    # The two engines store model space in different orientations (Source keeps the
    # model's left at +X, HD2 keeps it at -X), so |dst - src| is dominated by that
    # ~180 deg space rotation and says nothing about mesh quality.  Fit the single best
    # global rigid motion and look at what is left.
    dev, space_ang, space_det = kabsch_residual(src_pts, dst_pts)
    dev_sorted = sorted(dev)
    print('residual after the best global rigid fit (the real local deformation):')
    print('   mean %.4f m  p50 %.4f  p95 %.4f  p99 %.4f  max %.4f'
          % (sum(dev) / len(dev), dev_sorted[len(dev) // 2],
             dev_sorted[int(len(dev) * 0.95)], dev_sorted[int(len(dev) * 0.99)],
             dev_sorted[-1]))
    print('   best-fit global rotation %.1f deg, det %+.3f  (the source->HD2 space change)'
          % (space_ang, space_det))

    ed_mean, ed_p50, ed_p95, ed_p99, ed_max, ed_bad = edge_distortion(src_pts, dst_pts, tris)
    print('triangle edge-length ratio dst/src (1.0 = shape preserved):')
    print('   mean %.4f  p50 %.4f  p95 %.4f  p99 %.4f  max %.4f   (%.2f%% of edges off by >25%%)'
          % (ed_mean, ed_p50, ed_p95, ed_p99, ed_max, 100.0 * ed_bad))

    # which regions move most (dominant bone per vertex)
    worst = {}
    for i, v in enumerate(verts):
        if v['boneCount'] == 0:
            continue
        b = v['bones'][0]
        nm = src_bones[b]['name'] if b < len(src_bones) else '?'
        if nm.startswith(SRC_PREFIX):
            nm = nm[len(SRC_PREFIX):]
        cur = worst.get(nm)
        if cur is None or dev[i] > cur:
            worst[nm] = dev[i]
    top = sorted(worst.items(), key=lambda kv: -kv[1])[:14]
    print('\ntop regions by max deviation (dominant bone):')
    for nm, d in top:
        print('   %-26s %.4f m' % (nm, d))

    prefix = a.prefix or os.path.basename(base)
    seg = segment_report(short_by_name, hd2_by_name)
    print('\n-- landmark segment length ratio (HD2 / source, 1.0 = unchanged) --')
    for nm, r in sorted(seg, key=lambda kv: -abs(kv[1] - 1.0))[:10]:
        print('   %-14s %.4f' % (nm, r))
    metrics = dict(
        model=os.path.basename(mdl_path), vpk=os.path.basename(a.vpk),
        roll_mode=a.roll_mode,
        body_parts=len(mdl_parts), vertices=len(verts), triangles=len(tris),
        source_bones=len(src_bones), direct_maps=mapped, missing=len(missing),
        missing_list=['%s->%s' % (s, h) for s, h in missing],
        hops={str(k): v for k, v in sorted(hops.items())},
        torso_roll=(sum(r[0] for r in torso) / len(torso)) if torso else None,
        legs_roll=(sum(r[0] for r in legs) / len(legs)) if legs else None,
        bbox_src=sb, bbox_dst=db,
        height_src=sb[5] - sb[2], height_dst=db[5] - db[2],
        dev_mean=sum(dev) / len(dev), dev_p50=dev_sorted[len(dev) // 2],
        dev_p95=dev_sorted[int(len(dev) * 0.95)], dev_p99=dev_sorted[int(len(dev) * 0.99)],
        dev_max=dev_sorted[-1], space_rot_deg=space_ang, space_det=space_det,
        edge_mean=ed_mean, edge_p50=ed_p50, edge_p95=ed_p95, edge_p99=ed_p99,
        edge_max=ed_max, edge_bad_frac=ed_bad,
        top_regions=[[nm, d] for nm, d in top[:8]],
        segments=[[nm, r] for nm, r in seg],
        seg_ratio_min=min((r for _, r in seg), default=None),
        seg_ratio_max=max((r for _, r in seg), default=None),
    )
    if a.json_out:
        with open(a.json_out, 'w', encoding='utf-8') as jf:
            json.dump(metrics, jf, indent=2, ensure_ascii=False)
        print('[written] %s' % a.json_out)

    os.makedirs(a.outdir, exist_ok=True)
    sfx = ('-' + a.tag) if a.tag else ''
    o1 = os.path.join(a.outdir, '%s-src%s.obj' % (prefix, sfx))
    o2 = os.path.join(a.outdir, '%s-mapped%s.obj' % (prefix, sfx))
    write_obj(o1, src_pts, tris, 'L4D2 source bind pose, metres, %s' % mdl_path)
    write_obj(o2, dst_pts, tris, 'mapped onto HD2 rig via per-bone T (%s roll), metres' % a.roll_mode)
    print('\n[written] %s\n[written] %s' % (o1, o2))

    if a.skip_render:
        return
    png = os.path.join(a.outdir, '%s-mesh-check%s.png' % (prefix, sfx))
    render(png, src_pts, dst_pts, tris,
           'A3 mesh check -- row1 original L4D2 bind | row2 %s roll | row3 %s roll (selected)  '
           '[spine=%s transfer=%s]  (%s)'
           % (other, a.roll_mode, a.spine_map, a.transfer_bones, os.path.basename(mdl_path)),
           extra=('%s roll' % other, other_pts))
    print('[written] %s' % png)


if __name__ == '__main__':
    main()
