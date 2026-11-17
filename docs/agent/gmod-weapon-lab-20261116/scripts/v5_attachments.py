# LAB ONLY (V5): list attachments + raw sequence events via SourceIO structs
import sys, types
repo, mdl_path = sys.argv[1], sys.argv[2]
pkg = types.ModuleType("SourceIO"); pkg.__path__ = [repo]; sys.modules["SourceIO"] = pkg
for n in ("bpy", "mathutils", "bmesh"):
    m = types.ModuleType(n)
    def _ga(a):
        if a.startswith("__"): raise AttributeError(a)
        return types.SimpleNamespace()
    m.__getattr__ = _ga; sys.modules[n] = m
from SourceIO.library.utils import FileBuffer
from SourceIO.library.models.mdl.v49.mdl_file import MdlV49
mdl = MdlV49.from_buffer(FileBuffer(mdl_path))
for a in mdl.attachments:
    print("attachment", repr(a.name), "bone", mdl.bones[a.parent_bone].name if hasattr(a, "parent_bone") else vars(a))
for s in mdl.sequences:
    if s.events:
        print("seq", s.name, [str(e) for e in s.events])
