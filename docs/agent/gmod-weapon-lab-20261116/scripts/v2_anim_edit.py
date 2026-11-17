# LAB ONLY (V2): edit one keyframe in fire.animation via the SDK object model, re-serialize, re-parse, compare.
# usage: python -I v2_anim_edit.py <hd2sdk_dir> <path to fire.animation.main>
import sys, types, importlib.util, os

sdk, path = sys.argv[1], sys.argv[2]
for name in ("bpy", "mathutils", "bmesh"):
    m = types.ModuleType(name); m.__getattr__ = lambda attr: types.SimpleNamespace(); sys.modules[name] = m

def pkg(n, p):
    m = types.ModuleType(n); m.__path__ = [p]; sys.modules[n] = m
def mod(n, p):
    s = importlib.util.spec_from_file_location(n, p); m = importlib.util.module_from_spec(s); sys.modules[n] = m; s.loader.exec_module(m); return m

pkg("sdk", sdk); pkg("sdk.utils", os.path.join(sdk, "utils")); pkg("sdk.stingray", os.path.join(sdk, "stingray"))
mod("sdk.utils.logger", os.path.join(sdk, "utils", "logger.py")).PrettyPrint = lambda *a, **k: None
MS = mod("sdk.utils.memoryStream", os.path.join(sdk, "utils", "memoryStream.py")).MemoryStream
anim = mod("sdk.stingray.animation", os.path.join(sdk, "stingray", "animation.py"))

data = open(path, "rb").read()
a = anim.StingrayAnimation(); a.Serialize(MS(data))
kinds = {}
for e in a.entries:
    kinds[(e.type, e.subtype)] = kinds.get((e.type, e.subtype), 0) + 1
print("bones", a.bone_count, "length", a.animation_length, "entries", len(a.entries), "kinds", kinds)

tgt = next(i for i, e in enumerate(a.entries) if e.type == 2)
before = list(a.entries[tgt].data2)
a.entries[tgt].data2 = [before[0] + 0.01] + list(before[1:])
out = MS(IOMode="write"); a.Serialize(out); res = bytes(out.Data)
b = anim.StingrayAnimation(); b.Serialize(MS(res))

def key(e):
    return (e.type, e.subtype, e.bone, e.time, tuple(round(v, 3) for v in (e.data2 or [])))
changed = [i for i, (x, y) in enumerate(zip(a.entries, b.entries)) if key(x) != key(y)]
diff = sum(1 for x, y in zip(data, res) if x != y)
print(f"edited entry {tgt} (bone {a.entries[tgt].bone}): {before} -> {b.entries[tgt].data2}")
print(f"size {len(data)} -> {len(res)}, differing bytes {diff}, entries differing from edited model after reparse: {changed}")
