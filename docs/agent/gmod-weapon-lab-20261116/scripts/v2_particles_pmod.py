# LAB ONLY (V2): round-trip .particles through hd2-particle-modder's data classes (GUI part not loaded).
# usage: python -I v2_particles_pmod.py <particle_modder.py> <file.particles.main>...
import sys, types, struct, math, os
import numpy as np

src_path, files = sys.argv[1], sys.argv[2:]
lines = open(src_path, encoding="utf-8").read().splitlines()
# keep only the data model: everything before the first GUI class
end = next(i for i, l in enumerate(lines) if l.startswith("class EmitterView"))
start = next(i for i, l in enumerate(lines) if l.startswith("VERSION = "))  # first line after the import block
body = lines[start:end]

class _Rot:  # stand-in for scipy Rotation that keeps the 3x3 matrix verbatim (scipy would re-orthonormalize it)
    def __init__(self, m=None): self.m = m
    @staticmethod
    def from_matrix(m): return _Rot([list(r) for r in m])
    def as_matrix(self): return self.m
    def as_euler(self, *a, **k): return [0.0, 0.0, 0.0]
ns = {"struct": struct, "math": math, "np": np, "os": os, "Rotation": _Rot, "__name__": "pmod_model"}
exec(compile("\n".join(body), src_path, "exec"), ns)
PE, MS = ns["ParticleEffect"], ns["MemoryStream"]

for f in files:
    data = open(f, "rb").read()
    try:
        pe = PE(); s = MS(data); pe.from_memory_stream(s)
        consumed = s.tell() if hasattr(s, "tell") else getattr(s, "location", "?")
        out = MS(data) if "data" in MS.__init__.__code__.co_varnames else MS()
        try:
            out = MS(bytearray(data)); out.set_write_mode() if hasattr(out, "set_write_mode") else None
        except Exception:
            pass
        pe.write_to_memory_stream(out)
        res = bytes(getattr(out, "data", getattr(out, "Data", b"")))
        same = res == data
        nsys = len(getattr(pe, "particle_systems", []) or [])
        diff = next((i for i, (a, b) in enumerate(zip(data, res)) if a != b), None)
        print(f"{os.path.basename(f)}: parsed systems={nsys} consumed={consumed}/{len(data)} roundtrip_identical={same} first_diff={diff} out_len={len(res)}")
        for i, ps in enumerate(getattr(pe, "particle_systems", [])[:8]):
            em = getattr(ps, "emitter", None); vis = getattr(ps, "visualizer", None)
            print(f"   system {i}: emitter={type(em).__name__ if em else None} {getattr(em,'emitter_type',getattr(em,'type',''))} visualizer={getattr(vis,'visualizer_type',getattr(vis,'type',''))} material={hex(getattr(vis,'material_id',0) or 0) if vis else ''}")
    except Exception as e:
        import traceback
        print(f"{os.path.basename(f)}: EXCEPTION {type(e).__name__}: {str(e)[:200]}")
        traceback.print_exc(limit=2)

# --- coverage: write the parsed model onto an all-zero buffer of the same size ---
for f in files:
    data = open(f, "rb").read()
    pe = PE(); pe.from_memory_stream(MS(data))
    z = MS(bytearray(len(data))); z.set_write_mode()
    try:
        pe.write_to_memory_stream(z); res = bytes(z.data)
        nz = sum(1 for b in data if b); cov = sum(1 for a, b in zip(res, data) if b and a == b)
        print(f"{os.path.basename(f)}: model reproduces {cov}/{nz} non-zero bytes from scratch ({cov*100//max(1,nz)}%)")
    except Exception as e:
        print(f"{os.path.basename(f)}: from-scratch write failed: {type(e).__name__}: {e}")
    for i, ps in enumerate(pe.particle_systems):
        attrs = {k: v for k, v in vars(ps).items() if not k.startswith("_")}
        em = attrs.get("emitter") or attrs.get("emitters")
        vis = attrs.get("visualizer")
        vt = getattr(vis, "visualizer_type", getattr(vis, "type", None)) if vis else None
        print(f"   sys{i}: keys={sorted(attrs)[:14]} vis_type={vt} vis_mat={hex(getattr(vis,'material_id',0) or 0) if vis else None}")
        if i == 0: print("   sys0 visualizer attrs:", sorted(vars(vis).keys()) if vis else None)
