"""A2: solve the L4D2 -> HD2 rig map and measure it.

For every Source bone with an HD2 counterpart we compute the exact map

        T_i = H_sigma(i) * S_i^-1

where S_i is the Source bone's world bind (converted to metres) and H_j the HD2
bone's world bind.  This is the "per-bone similarity" answer:
  * bone origins land exactly on HD2 joints;
  * the mapped skeleton carries HD2's segment lengths exactly;
  * T itself is a pure rotation (singular values 1,1,1) -- no local shear.

What it does NOT tell you is whether the MESH survives: forcing HD2's segment
lengths onto a Source mesh stretches it wherever the two rigs disagree.  That
stretch is reported here as `HD2 length / Source length` per segment, and is the
honest distortion number.  A3 renders it.

Outputs rig-map.json (per-bone 4x4 + correspondence + metrics).
"""
import argparse, json, math, os, struct

BONE_STRIDE = 216
POSE_TO_BONE_OFF = 96
IN2M = 0.0254

SRC_PREFIX = 'ValveBiped.Bip01_'

# Source bone -> HD2 node name.  No toe bones exist in this L4D2 rig.
BODY = [('Pelvis', 'hips'), ('Spine', 'spine1'), ('Spine2', 'spine2'), ('Spine4', 'chest'),
        ('Neck1', 'neck'), ('Head1', 'head'),
        ('L_Clavicle', 'l_clavicle'), ('L_UpperArm', 'l_shoulder'),
        ('L_Forearm', 'l_elbow'), ('L_Hand', 'l_hand'),
        ('R_Clavicle', 'r_clavicle'), ('R_UpperArm', 'r_shoulder'),
        ('R_Forearm', 'r_elbow'), ('R_Hand', 'r_hand'),
        ('L_Thigh', 'l_thigh'), ('L_Calf', 'l_knee'), ('L_Foot', 'l_foot'),
        ('R_Thigh', 'r_thigh'), ('R_Calf', 'r_knee'), ('R_Foot', 'r_foot')]

# ValveBiped fingers: Finger0=thumb .. Finger4=pinky, each 3 joints.
FINGER_NAMES = {0: 'thumb', 1: 'index', 2: 'middle', 3: 'ring', 4: 'pinky'}
FINGERS = []
for side, pfx in (('L', 'l'), ('R', 'r')):
    for idx, fname in FINGER_NAMES.items():
        for j, suffix in enumerate(('', '1', '2'), start=1):
            FINGERS.append(('%s_Finger%d%s' % (side, idx, suffix),
                            '%s_%s_finger%d' % (pfx, fname, j)))

CORRESPONDENCE = BODY + FINGERS

# Segments whose stretch we report (Source short names).
SEGMENTS = [('Pelvis', 'Spine4'), ('Spine4', 'Neck1'), ('Neck1', 'Head1'),
            ('L_UpperArm', 'L_Hand'), ('R_UpperArm', 'R_Hand'),
            ('L_Thigh', 'L_Foot'), ('R_Thigh', 'R_Foot')]


# ------------------------------------------------------------------ VPK
def parse_vpk_tree(path):
    with open(path, 'rb') as f:
        h = f.read(28)
        magic, ver = struct.unpack_from('<II', h, 0)
        if magic != 0x55AA1234: raise ValueError('not a VPK')
        ts, = struct.unpack_from('<I', h, 8)
        hdr = 12 if ver == 1 else 28
        f.seek(hdr); t = f.read(ts)
    te = len(t); pos = 0; out = []
    def rs():
        nonlocal pos
        s = pos
        while pos < te and t[pos] != 0: pos += 1
        v = t[s:pos].decode('utf-8', 'replace'); pos += 1
        return v
    while True:
        e = rs()
        if not e: break
        while True:
            d = rs()
            if not d: break
            while True:
                n = rs()
                if not n: break
                _crc, pre, a, o, l, _t = struct.unpack_from('<IHHIIH', t, pos); pos += 18 + pre
                fn = n if e == ' ' else n + '.' + e
                out.append(((fn if d in ('', ' ') else d + '/' + fn).lstrip('/'), a, o, l))
    return ver, hdr + ts, out


def read_range(vp, do, a, o, l):
    if l <= 0 or l > 128 * 1024 * 1024: return None
    if a == 0x7FFF:
        with open(vp, 'rb') as f:
            f.seek(do + o); return f.read(l)
    s = vp[:-4] + '_%03d.vpk' % a
    if not os.path.exists(s): return None
    with open(s, 'rb') as f:
        f.seek(o); return f.read(l)


# ------------------------------------------------------------------ linear algebra
def mat34_to_4x4(m):
    return [m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
            m[8], m[9], m[10], m[11], 0.0, 0.0, 0.0, 1.0]


def mat_inverse(m):
    a = [list(m[0:4]), list(m[4:8]), list(m[8:12]), list(m[12:16])]
    inv = [[1.0 if i == j else 0.0 for j in range(4)] for i in range(4)]
    for col in range(4):
        piv = max(range(col, 4), key=lambda r: abs(a[r][col]))
        if abs(a[piv][col]) < 1e-12: raise ValueError('singular matrix')
        if piv != col:
            a[col], a[piv] = a[piv], a[col]; inv[col], inv[piv] = inv[piv], inv[col]
        d = a[col][col]
        a[col] = [v / d for v in a[col]]; inv[col] = [v / d for v in inv[col]]
        for r in range(4):
            if r == col: continue
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
    """HD2 stores its transforms row-vector style (translation in the last row,
    flat 12..14).  Source uses column-vector style (translation at 3, 7, 11).
    Transposing converts HD2's storage into Source's convention."""
    return [m[0], m[4], m[8], m[12],
            m[1], m[5], m[9], m[13],
            m[2], m[6], m[10], m[14],
            m[3], m[7], m[11], m[15]]


def origin(m):
    return (m[3], m[7], m[11])


def xform(m, p):
    x, y, z = p
    return (m[0] * x + m[1] * y + m[2] * z + m[3],
            m[4] * x + m[5] * y + m[6] * z + m[7],
            m[8] * x + m[9] * y + m[10] * z + m[11])


def singular_values_3x3(m):
    a = [[m[0], m[1], m[2]], [m[4], m[5], m[6]], [m[8], m[9], m[10]]]
    v = [[sum(a[k][i] * a[k][j] for k in range(3)) for j in range(3)] for i in range(3)]
    for _ in range(60):
        if v[0][1] ** 2 + v[0][2] ** 2 + v[1][2] ** 2 < 1e-24: break
        for p, q in ((0, 1), (0, 2), (1, 2)):
            if abs(v[p][q]) < 1e-18: continue
            theta = (v[q][q] - v[p][p]) / (2 * v[p][q])
            t = (1 if theta >= 0 else -1) / (abs(theta) + math.sqrt(theta * theta + 1))
            c = 1 / math.sqrt(t * t + 1); s = t * c
            for k in range(3):
                vk_p, vk_q = v[k][p], v[k][q]
                v[k][p] = c * vk_p - s * vk_q; v[k][q] = s * vk_p + c * vk_q
            for k in range(3):
                vp_k, vq_k = v[p][k], v[q][k]
                v[p][k] = c * vp_k - s * vq_k; v[q][k] = s * vp_k + c * vq_k
    return sorted((math.sqrt(max(0.0, v[i][i])) for i in range(3)), reverse=True)


# ------------------------------------------------------------------ readers
def read_source_bones(mdl):
    bc, = struct.unpack_from('<i', mdl, 156)
    bo, = struct.unpack_from('<i', mdl, 160)
    bones = []
    for i in range(bc):
        e = bo + i * BONE_STRIDE
        ni, = struct.unpack_from('<i', mdl, e)
        par, = struct.unpack_from('<i', mdl, e + 4)
        p2b = struct.unpack_from('<12f', mdl, e + POSE_TO_BONE_OFF)
        name = ''
        at = e + ni
        if 0 <= at < len(mdl) - 1:
            end = mdl.find(b'\0', at, min(at + 160, len(mdl)))
            if end > at: name = mdl[at:end].decode('ascii', 'replace')
        if not name or not all(c.isalnum() or c in '_.-' for c in name):
            name = 'source_extra_%d' % i
        world_in = mat_inverse(mat34_to_4x4(p2b))       # inches
        world_m = scale_translation(world_in, IN2M)     # metres
        bones.append(dict(index=i, name=name, parent=par,
                          world_in=world_in, world_m=world_m))
    return bones


def read_hd2_rig(toc, bonehash):
    d = open(toc, 'rb').read()
    tio = struct.unpack_from('<I', d, 0x34)[0]
    n = struct.unpack_from('<I', d, tio)[0]
    lo = tio + 16; mo = lo + n * 64; eo = mo + n * 64; ho = eo + n * 4
    names = {}
    for line in open(bonehash, encoding='utf-8', errors='replace'):
        p = line.split()
        if len(p) >= 2:
            try: names[int(p[0])] = p[1]
            except ValueError: pass
    out = []
    for i in range(n):
        inc, par = struct.unpack_from('<HH', d, eo + i * 4)
        raw = list(struct.unpack_from('<16f', d, mo + i * 64))
        out.append(dict(index=i, name=names.get(struct.unpack_from('<I', d, ho + i * 4)[0]),
                        parent=par, world=transpose4(raw)))
    return out


# ------------------------------------------------------------------ main
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--vpk', required=True)
    ap.add_argument('--model', default='survivor_teenangst')
    ap.add_argument('--toc', required=True)
    ap.add_argument('--bonehash', required=True)
    ap.add_argument('--skel', help='hd2_skel.json, for the independent position check')
    ap.add_argument('--out', default='rig-map.json')
    a = ap.parse_args()

    _v, do, ents = parse_vpk_tree(a.vpk)
    mdl = None; full = None
    for f, arch, o, l in ents:
        if f.lower().endswith('.mdl') and a.model.lower() in f.lower():
            full = f; mdl = read_range(a.vpk, do, arch, o, l); break
    if mdl is None: raise SystemExit('no .mdl matching %r' % a.model)

    src = read_source_bones(mdl)
    hd2 = read_hd2_rig(a.toc, a.bonehash)
    by_name = {h['name']: h for h in hd2 if h['name']}
    short = {b['name'][len(SRC_PREFIX):]: b for b in src if b['name'].startswith(SRC_PREFIX)}

    print('=== A2  rig map ===')
    print('source : %s   %d bones' % (full, len(src)))
    print('hd2    : %d transform nodes, %d named' % (len(hd2), len(by_name)))
    print('source scaled inches -> metres by %.4f before mapping' % IN2M)

    maps, missing = {}, []
    for sname, hname in CORRESPONDENCE:
        sb, hb = short.get(sname), by_name.get(hname)
        if sb is None or hb is None:
            missing.append((sname, hname, 'no source bone' if sb is None else 'no HD2 node')); continue
        maps[sname] = dict(src=sb, hd2=hb, T=mat_mul(hb['world'], mat_inverse(sb['world_m'])))

    print('\n-- correspondence: %d mapped, %d missing --' % (len(maps), len(missing)))
    for s, h, why in missing:
        print('   MISSING  %-14s -> %-22s (%s)' % (s, h, why))

    # metric 1 -- independent check: our HD2 node positions vs the reference constant
    print('\n-- metric 1: HD2 node positions vs hd2_skel.json (independent, external) --')
    if a.skel and os.path.exists(a.skel):
        skel = {k: v for k, v in json.load(open(a.skel, encoding='utf-8')).items()
                if isinstance(v, list) and len(v) == 3}
        errs = []
        for h in hd2:
            if h['name'] in skel:
                errs.append((math.dist(origin(h['world']), skel[h['name']]), h['name']))
        errs.sort(reverse=True)
        mx = errs[0][0] if errs else float('inf')
        print('   compared %d nodes; max %.6f m; worst %s'
              % (len(errs), mx, errs[0][1] if errs else '-'))
        print('   -> %s   (this validates the transpose above)' % ('PASS' if mx < 1e-3 else 'FAIL'))
        m1 = mx
    else:
        print('   skipped (no --skel)'); m1 = None

    # metric 2 -- mapped segment length vs HD2, and the honest stretch
    print('\n-- metric 2: segment length.  "source" is the original, "hd2" is the target --')
    print('   %-22s %9s %9s %9s %9s %9s' % ('segment', 'source', 'hd2', 'mapped', 'err', 'stretch'))
    worst_len = 0.0
    stretches = []
    for a1, b1 in SEGMENTS:
        if a1 not in maps or b1 not in maps: continue
        s_len = math.dist(origin(maps[a1]['src']['world_m']), origin(maps[b1]['src']['world_m']))
        h_len = math.dist(origin(maps[a1]['hd2']['world']), origin(maps[b1]['hd2']['world']))
        m_len = math.dist(xform(maps[a1]['T'], origin(maps[a1]['src']['world_m'])),
                          xform(maps[b1]['T'], origin(maps[b1]['src']['world_m'])))
        err = abs(m_len - h_len)
        worst_len = max(worst_len, err)
        st = h_len / s_len if s_len else 0.0
        stretches.append((a1 + '->' + b1, st))
        print('   %-22s %9.4f %9.4f %9.4f %9.2e %9.4f'
              % (a1 + '->' + b1, s_len, h_len, m_len, err, st))
    print('   max length error = %.9f m  ->  %s'
          % (worst_len, 'PASS' if worst_len < 1e-3 else 'FAIL'))
    print('   ^ this is the MESH distortion: the mapped skeleton has HD2 lengths exactly,')
    print('     so any region whose Source length differed by x%% is stretched by x%%.')

    # metric 3 -- is the map itself rigid?
    print('\n-- metric 3: singular values of T (1,1,1 = the map adds no shear/scale) --')
    worst_sv = max(abs(sv[0] - 1.0) + abs(sv[2] - 1.0)
                   for sv in (singular_values_3x3(e['T']) for e in maps.values()))
    print('   max deviation from (1,1,1) = %.3e  ->  %s'
          % (worst_sv, 'PASS (map is rigid)' if worst_sv < 1e-6 else 'FAIL'))

    # coverage
    print('\n-- coverage --')
    print('   source bones             : %d' % len(src))
    print('   mapped directly          : %d' % len(maps))
    print('   fall back to nearest mapped ancestor : %d' % (len(src) - len(maps)))

    payload = dict(
        model=full, sourceUnit='inch', sourceScaleToMetres=IN2M,
        sourceBones=[dict(index=b['index'], name=b['name'], parent=b['parent'],
                          worldMetres=[round(v, 9) for v in b['world_m']]) for b in src],
        hd2Nodes=[dict(index=h['index'], name=h['name'], parent=h['parent'],
                       world=[round(v, 9) for v in h['world']]) for h in hd2],
        correspondence={s: dict(hd2=e['hd2']['name'], T=[round(v, 9) for v in e['T']])
                        for s, e in maps.items()},
        metrics=dict(hd2VsReferenceMaxM=m1, maxSegmentLengthErrorM=worst_len,
                     maxRigidDeviation=worst_sv, stretch=dict(stretches),
                     mapped=len(maps), missing=len(missing),
                     sourceBones=len(src)),
    )
    json.dump(payload, open(a.out, 'w', encoding='utf-8'), ensure_ascii=False)
    print('\n[written] %s' % a.out)


main()
