# LAB ONLY (V2): round-trip HD2 raw files through HD2SDK-CE serializers without Blender.
# usage: python -I v2_roundtrip.py <hd2sdk_dir> <raw_dir>
import sys, types, importlib.util, os, glob, traceback, copy

sdk, raw = sys.argv[1], sys.argv[2]

# stub Blender modules: serializers only touch them in Blender-facing helpers
for name in ("bpy", "mathutils", "bmesh"):
    m = types.ModuleType(name)
    m.__getattr__ = lambda attr, _n=name: types.SimpleNamespace()
    sys.modules[name] = m

def load_pkg(pkg, path):
    m = types.ModuleType(pkg); m.__path__ = [path]; sys.modules[pkg] = m
def load_mod(full, path):
    spec = importlib.util.spec_from_file_location(full, path)
    mod = importlib.util.module_from_spec(spec); sys.modules[full] = mod
    spec.loader.exec_module(mod); return mod

load_pkg("sdk", sdk)
load_pkg("sdk.utils", os.path.join(sdk, "utils"))
load_pkg("sdk.stingray", os.path.join(sdk, "stingray"))
load_mod("sdk.utils.logger", os.path.join(sdk, "utils", "logger.py"))
ms = load_mod("sdk.utils.memoryStream", os.path.join(sdk, "utils", "memoryStream.py"))
sys.modules["sdk.utils.logger"].PrettyPrint = lambda *a, **k: None
anim = load_mod("sdk.stingray.animation", os.path.join(sdk, "stingray", "animation.py"))
part = load_mod("sdk.stingray.particle", os.path.join(sdk, "stingray", "particle.py"))
sm = load_mod("sdk.stingray.state_machine", os.path.join(sdk, "stingray", "state_machine.py"))
MemoryStream = ms.MemoryStream

def first_diff(a, b):
    for i, (x, y) in enumerate(zip(a, b)):
        if x != y: return i
    return None if len(a) == len(b) else min(len(a), len(b))

def rt(kind, path):
    data = open(path, "rb").read()
    try:
        if kind == "animation":
            obj = anim.StingrayAnimation(); obj.Serialize(MemoryStream(data))
            out = MemoryStream(IOMode="write"); obj.Serialize(out); res = bytes(out.Data)
        elif kind == "state_machine":
            obj = sm.StingrayStateMachine(); obj.Serialize(MemoryStream(data))
            out = MemoryStream(IOMode="write"); obj.Serialize(out); res = bytes(out.Data)
        elif kind == "particles":
            obj = part.StingrayParticles(); obj.Serialize(MemoryStream(data))
            # SDK writes over the original bytes (in place), as SaveStingrayParticle does
            out = MemoryStream(data, IOMode="write"); obj.Serialize(out); res = bytes(out.Data)
            # independent check: write onto a zero buffer of the same size -> how many bytes the model actually covers
            z = MemoryStream(bytes(len(data)), IOMode="write"); obj.Serialize(z); zres = bytes(z.Data)
            covered = sum(1 for a, b in zip(zres, data) if a == b and b != 0)
            nonzero = sum(1 for b in data if b != 0)
            extra = f" | standalone-from-zero: {covered}/{nonzero} non-zero bytes reproduced ({covered*100//max(1,nonzero)}%), systems={obj.numParticleSystems}"
        else:
            return
        d = first_diff(data, res)
        status = "IDENTICAL" if d is None else f"DIFF at {d} (in={len(data)} out={len(res)})"
        print(f"{kind:13s} {os.path.basename(path):45s} {status}{extra if kind=='particles' else ''}")
    except Exception as e:
        print(f"{kind:13s} {os.path.basename(path):45s} EXCEPTION {type(e).__name__}: {str(e)[:120]}")

for p in sorted(glob.glob(os.path.join(raw, "**", "*.animation.main"), recursive=True)): rt("animation", p)
for p in sorted(glob.glob(os.path.join(raw, "**", "*.state_machine.main"), recursive=True)): rt("state_machine", p)
for p in sorted(glob.glob(os.path.join(raw, "**", "*.particles.main"), recursive=True)): rt("particles", p)
