# LAB ONLY (V5): decode all animations of a Source MDL (v48/v49) with SourceIO's library (no Blender), dump to JSON.
# usage: python -I v5_sourceio_decode.py <sourceio_repo_dir> <model.mdl> <out.json>
import sys, types, json, os
import numpy as np

repo, mdl_path, out_path = sys.argv[1], sys.argv[2], sys.argv[3]
# expose the repo as package "SourceIO" without running its Blender-facing __init__
pkg = types.ModuleType("SourceIO"); pkg.__path__ = [repo]; sys.modules["SourceIO"] = pkg
for name in ("bpy", "mathutils", "bmesh"):
    m = types.ModuleType(name)
    def _ga(attr):
        if attr.startswith("__"): raise AttributeError(attr)
        return types.SimpleNamespace()
    m.__getattr__ = _ga; sys.modules[name] = m

from SourceIO.library.utils import FileBuffer
from SourceIO.library.models.mdl.v49.mdl_file import MdlV49
from SourceIO.library.models.mdl import load_animations as la

buf = FileBuffer(mdl_path)
mdl = MdlV49.from_buffer(buf)
print("version", mdl.header.version, "bones", len(mdl.bones), "anim_descs", len(mdl.anim_descs), "sequences", len(mdl.sequences))

class NoContent:  # c_ models in GMod keep animations inline; external .ani lookup returns nothing
    def find_file(self, *_a, **_k): return None

anims = la.load_animations_from_mdl(mdl, buf, NoContent(), None)
out = {"version": mdl.header.version, "bones": [b.name for b in mdl.bones],
       "bind": {b.name: {"pos": list(map(float, b.position)), "quat": list(map(float, b.quat))} for b in mdl.bones},
       "sequences": [{"name": s.name, "activity": getattr(s, "activity_name", ""), "events": [getattr(e, "event", None) if not hasattr(e, "__dict__") else {k: str(v) for k, v in vars(e).items()} for e in getattr(s, "events", [])]} for s in mdl.sequences],
       "anims": {}}
for a in anims:
    frames = {}
    for bone, d in a.frames.items():
        rec = {}
        if isinstance(d, dict):
            items = d.items()
        elif getattr(d, "dtype", None) is not None and d.dtype.names:
            items = ((n, d[n]) for n in d.dtype.names)
        else:
            items = (("raw", d),)
        for k, v in items:
            rec[k] = np.asarray(v, dtype=np.float64).tolist()
        frames[bone] = rec
    out["anims"][a.name] = {"fps": float(a.fps), "frame_count": int(a.frame_count), "delta": bool(a.is_delta), "frames": frames}
json.dump(out, open(out_path, "w"), indent=0)
print("decoded", len(anims), "animations ->", out_path)
for name, a in list(out["anims"].items())[:40]:
    print(f"  {name:28s} fps={a['fps']:5.1f} frames={a['frame_count']:4d} delta={a['delta']} bones_with_data={len(a['frames'])}")
