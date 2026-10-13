# -*- coding: utf-8 -*-
"""Read the Lunalice source model + its material/texture chain out of the
L4D2 addon VPKs.

Everything here is read-only.  The three things the packager needs:

  * `load_geometry()`  -> per-vertex {pos,nrm,uv} + triangles carrying their
                          material index (the same order `dump_blender.py`
                          baked into `out/blender/model.npz`)
  * `load_materials()` -> the 92 texture slots resolved to a VMT (by file name)
  * `load_textures()`  -> every VTF the packs ship, keyed by its material path

The VPK reader itself is `export_obj.parse_vpk_tree` / `read_range`.
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E

ADDONS = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'

# the model lives here
MODEL_VPK = 'Lunalice_Zoey.vpk'
# every pack that can supply a VMT or a VTF
PACK_VPKS = [
    'Lunalice_Zoey.vpk',
    'Lunalice基础材质A1主体.vpk',
    'Lunalice基础材质B1服装色白.vpk',
    'Lunalice基础材质C2金属装饰品色银.vpk',
    'Lunalice基础材质D1发色白.vpk',
    'Lunalice基础材质E3瞳色紫2.vpk',
    'Lunalice基础材质F1身体描边.vpk',
]


def _norm(p):
    return p.replace('/', '\\').lower()


class Packs:
    """Lazy index over every VPK we care about."""

    def __init__(self, addons=ADDONS, packs=None, model_vpk=MODEL_VPK):
        self.addons = addons
        self.vmt = {}       # 'materials\\x\\y.vmt' -> bytes
        self.vtf = {}       # 'materials\\x\\y.vtf' -> bytes
        self._vmt_short = {}   # 'y' (basename, no ext) -> path
        self._vtf_short = {}   # 'y' -> path
        self.model_entries = {}
        self._model_do = 0
        for name in (packs or PACK_VPKS):
            p = os.path.join(addons, name)
            if not os.path.exists(p):
                print('  [packs] missing %s' % name)
                continue
            _ver, do, ents = E.parse_vpk_tree(p)
            for f, a, o, l in ents:
                fl = _norm(f)
                if fl.endswith('.vmt'):
                    blob = E.read_range(p, do, a, o, l)
                    if blob is not None:
                        self.vmt[fl] = blob
                        self._vmt_short[os.path.basename(fl)[:-4]] = fl
                elif fl.endswith('.vtf'):
                    blob = E.read_range(p, do, a, o, l)
                    if blob is not None:
                        self.vtf[fl] = blob
                        self._vtf_short[os.path.basename(fl)[:-4]] = fl
                elif name == model_vpk and fl.rsplit('.', 1)[-1] in ('mdl', 'vvd', 'vtx'):
                    self.model_entries[fl] = (p, do, a, o, l)
        self.model_vpk_path = os.path.join(addons, model_vpk)

    # ------------------------------------------------------------------ model
    def model_files(self, model='survivor_teenangst'):
        """-> (mdl, vvd, vtx) raw blobs for the given model stem."""
        stem = model.lower()
        out = {}
        for ext in ('mdl', 'vvd', 'vtx'):
            cands = [k for k in self.model_entries
                     if k.endswith('.' + ext) and os.path.basename(k)[:-4] == stem]
            if not cands:
                cands = sorted(k for k in self.model_entries if k.endswith('.' + ext))
            cands.sort(key=lambda k: (len(os.path.basename(k)), os.path.basename(k)))
            p, do, a, o, l = self.model_entries[cands[0]]
            out[ext] = E.read_range(p, do, a, o, l)
        return out['mdl'], out['vvd'], out['vtx']

    # ------------------------------------------------------------- materials
    def vmt_text(self, name_or_path):
        k = _norm(name_or_path)
        blob = self.vmt.get(k)
        if blob is None:
            short = os.path.basename(k)
            if short.endswith('.vmt'):
                short = short[:-4]
            k2 = self._vmt_short.get(short)
            blob = self.vmt.get(k2) if k2 else None
        if blob is None:
            return None
        return blob.decode('latin-1', 'replace')

    def vtf_bytes(self, name_or_path):
        k = _norm(name_or_path)
        if not k.endswith('.vtf'):
            k += '.vtf'
        blob = self.vtf.get(k)
        if blob is None:
            blob = self.vtf.get(self._vtf_short.get(os.path.basename(k)[:-4], ''))
        return blob


def parse_vmt(text):
    """-> {'basetexture': ..., 'bumpmap': ..., '$flags': {...}} (values lowercased paths)."""
    out = {}
    if not text:
        return out
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith('//'):
            continue
        parts = line.split(None, 1)
        if len(parts) != 2:
            continue
        key, val = parts[0].lower(), parts[1].strip().strip('"').strip()
        if key.startswith('$'):
            if key in ('$basetexture', '$bumpmap', '$detail', '$basetexture2',
                       '$phongexponenttexture'):
                out[key] = _norm(val)
            out.setdefault('_raw', {})[key] = val
    return out


# ------------------------------------------------------------------ geometry
def load_geometry(mdl, vvd, vtx):
    """-> (verts, tris4, diag) with tris4 = (v0,v1,v2,material_index)."""
    diag = []
    parts = E.read_mdl_geometry(mdl)
    verts_vvd, _lod_count, _n = E.read_vvd(vvd)
    vtx_parts, ver, extra8 = E.read_vtx(vtx)
    diag.append('vtx version %d extra8=%s' % (ver, extra8))
    verts, tris = E.build_geometry(parts, verts_vvd, vtx_parts, diag)
    return verts, tris, diag


def load_material_slots(mdl):
    """-> (names, cdtextures, skin_row) for skin family 0.

    Header fields: 204 numtextures / 208 textureindex / 212 numcdtextures /
    216 cdtextureindex / 220 numskinref / 224 numskinfamilies / 228 skinindex.
    (`numskinref` is the ROW LENGTH -- 92 here, with exactly one family.)
    """
    nt, to = struct.unpack_from('<ii', mdl, 204)
    nc, co = struct.unpack_from('<ii', mdl, 212)
    nsr, nsf, so = struct.unpack_from('<iii', mdl, 220)
    cds = [E.cstr(mdl, struct.unpack_from('<i', mdl, co + 4 * i)[0]) for i in range(nc)]
    names = []
    for i in range(nt):
        e = to + i * 64
        ni, = struct.unpack_from('<i', mdl, e)
        names.append(E.cstr(mdl, e + ni))
    # skinindex entries are int16 in v53 (verified: int32 yields only 8/92 valid)
    row = [struct.unpack_from('<h', mdl, so + k * 2)[0] for k in range(nsr)]
    return names, cds, row
