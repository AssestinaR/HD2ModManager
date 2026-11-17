# LAB ONLY (V5): independent Source MDL v44-v49 animation decoder, written from Source SDK studio.h semantics
# (mstudioanimdesc_t / mstudioanim_t / mstudioanimvalue_t RLE / Quaternion48 / Quaternion64 / Vector48 / AngleQuaternion).
# Deliberately does NOT import or copy SourceIO. Compares its output with SourceIO's JSON dump.
# usage: python -I v5_ref_decoder.py <model.mdl> <sourceio.json>
import sys, struct, math, json

mdl_path, sio_path = sys.argv[1], sys.argv[2]
b = open(mdl_path, "rb").read()
u8 = lambda o: b[o]
i16 = lambda o: struct.unpack_from("<h", b, o)[0]
u16 = lambda o: struct.unpack_from("<H", b, o)[0]
i32 = lambda o: struct.unpack_from("<i", b, o)[0]
f32 = lambda o: struct.unpack_from("<f", b, o)[0]
def cstr(o): return b[o:b.index(b"\0", o)].decode("latin-1")

version = i32(4)
numbones, boneindex = i32(156), i32(160)
numanim, animindex = i32(180), i32(184)
BONE_SIZE = 216
bones = []
for i in range(numbones):
    o = boneindex + i * BONE_SIZE
    bones.append(dict(
        name=cstr(o + i32(o)), parent=i32(o + 4),
        pos=struct.unpack_from("<3f", b, o + 32), quat=struct.unpack_from("<4f", b, o + 44),
        rot=struct.unpack_from("<3f", b, o + 60), posscale=struct.unpack_from("<3f", b, o + 72),
        rotscale=struct.unpack_from("<3f", b, o + 84)))

def angle_quat(x, y, z):  # Source AngleQuaternion(RadianEuler) -> (x, y, z, w)
    sr, cr = math.sin(x / 2), math.cos(x / 2)
    sp, cp = math.sin(y / 2), math.cos(y / 2)
    sy, cy = math.sin(z / 2), math.cos(z / 2)
    return (sr * cp * cy - cr * sp * sy, cr * sp * cy + sr * cp * sy, cr * cp * sy - sr * sp * cy, cr * cp * cy + sr * sp * sy)

def quat48(o):
    x, y, zw = u16(o), u16(o + 2), u16(o + 4)
    qx = (x - 32768) / 32768.0; qy = (y - 32768) / 32768.0; qz = ((zw & 0x7FFF) - 16384) / 16384.0
    w = math.sqrt(max(0.0, 1 - qx * qx - qy * qy - qz * qz))
    return (qx, qy, qz, -w if zw >> 15 else w)

def quat64(o):
    v = struct.unpack_from("<Q", b, o)[0]
    x = v & 0x1FFFFF; y = (v >> 21) & 0x1FFFFF; z = (v >> 42) & 0x1FFFFF; wneg = v >> 63
    qx = (x - 1048576) / 1048576.5; qy = (y - 1048576) / 1048576.5; qz = (z - 1048576) / 1048576.5
    w = math.sqrt(max(0.0, 1 - qx * qx - qy * qy - qz * qz))
    return (qx, qy, qz, -w if wneg else w)

def half(h): return struct.unpack("<e", struct.pack("<H", h))[0]
def vec48(o): return (half(u16(o)), half(u16(o + 2)), half(u16(o + 4)))

def anim_value(ptr, frame):  # mstudioanimvalue_t RLE: header byte valid, byte total, then 'valid' shorts
    k = frame
    while True:
        valid, total = u8(ptr), u8(ptr + 1)
        if total == 0: return 0  # malformed guard
        if total > k: break
        k -= total
        ptr += (valid + 1) * 2
    return i16(ptr + (k + 1) * 2) if valid > k else i16(ptr + valid * 2)

def decode_desc(d):
    name = cstr(d + i32(d + 4)); fps = f32(d + 8); flags = i32(d + 12); nframes = i32(d + 16)
    animblock, animidx = i32(d + 52), i32(d + 56)
    sectionindex, sectionframes = i32(d + 80), i32(d + 84)
    delta = bool(flags & 0x04)
    if animblock != 0:
        return name, fps, nframes, delta, None  # external .ani block: not present in this sample
    out = {bn["name"]: {"pos": [], "rot": []} for bn in bones}
    for f in range(nframes):
        if sectionframes:
            sec = f // sectionframes; lf = f - sec * sectionframes
            base = d + i32(d + sectionindex + sec * 8 + 4)
        else:
            lf = f; base = d + animidx
        seen = {}
        p = base
        while True:
            bone, fl, nxt = u8(p), u8(p + 1), i16(p + 2)
            seen[bone] = (p, fl)
            if nxt == 0: break
            p += nxt
        for bi, bn in enumerate(bones):
            if bi in seen:
                p, fl = seen[bi]; o = p + 4
                if fl & 0x02:   rot = quat48(o); o += 6
                elif fl & 0x20: rot = quat64(o); o += 8
                elif fl & 0x08:
                    vp = o; o += 6
                    e = [anim_value(vp + i16(vp + 2 * i), lf) * bn["rotscale"][i] if i16(vp + 2 * i) else 0.0 for i in range(3)]
                    if not (fl & 0x10): e = [e[i] + bn["rot"][i] for i in range(3)]
                    rot = angle_quat(*e)
                else:
                    rot = (0.0, 0.0, 0.0, 1.0) if delta else tuple(bn["quat"])
                if fl & 0x01:   pos = vec48(o)
                elif fl & 0x04:
                    vp = o
                    pos = [anim_value(vp + i16(vp + 2 * i), lf) * bn["posscale"][i] if i16(vp + 2 * i) else 0.0 for i in range(3)]
                    if not (fl & 0x10): pos = [pos[i] + bn["pos"][i] for i in range(3)]
                else:
                    pos = (0.0, 0.0, 0.0) if delta else tuple(bn["pos"])
            else:
                rot = (0.0, 0.0, 0.0, 1.0) if delta else tuple(bn["quat"])
                pos = (0.0, 0.0, 0.0) if delta else tuple(bn["pos"])
            out[bn["name"]]["pos"].append(list(pos)); out[bn["name"]]["rot"].append(list(rot))
    return name, fps, nframes, delta, out

ref = {}
for i in range(numanim):
    d = animindex + i * 100
    name, fps, n, delta, frames = decode_desc(d)
    ref[("d." if delta else "") + name] = (fps, n, delta, frames)

sio = json.load(open(sio_path))
print(f"model v{version} bones={numbones} anim_descs={numanim}; SourceIO anims={len(sio['anims'])}")

def qdist_deg(a, c):
    dot = abs(sum(x * y for x, y in zip(a, c))) / (math.sqrt(sum(x * x for x in a)) * math.sqrt(sum(x * x for x in c)) or 1)
    return math.degrees(2 * math.acos(min(1.0, dot)))

worst_all = (0, 0)
for name, (fps, n, delta, frames) in ref.items():
    s = sio["anims"].get(name)
    if s is None:
        print(f"  {name:30s} MISSING in SourceIO output"); continue
    if frames is None:
        print(f"  {name:30s} external block (skipped)"); continue
    maxp = maxr = 0.0; where = None; cmp_bones = 0
    for bone, fr in frames.items():
        sb = s["frames"].get(bone)
        if sb is None: continue
        cmp_bones += 1
        sp, sr = sb.get("pos"), sb.get("rot")
        for f in range(min(n, len(sp or []), len(sr or []))):
            dp = max(abs(x - y) for x, y in zip(fr["pos"][f], sp[f]))
            dr = qdist_deg(fr["rot"][f], sr[f])
            if dp > maxp: maxp = dp
            if dr > maxr: maxr = dr; where = (bone, f)
    worst_all = (max(worst_all[0], maxp), max(worst_all[1], maxr))
    ok = "OK " if maxp < 1e-3 and maxr < 0.1 else "DIFF"
    print(f"  {ok} {name:30s} frames={n:4d} delta={delta!s:5s} bones_compared={cmp_bones:3d} max|dpos|={maxp:.6f} max_drot={maxr:.4f}deg worst={where}")
print(f"overall worst: dpos={worst_all[0]:.6f} (units: inches)  drot={worst_all[1]:.4f} deg")
