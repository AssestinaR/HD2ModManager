# -*- coding: utf-8 -*-
"""End-to-end material chain check on a FINISHED package.

Walks the same path the game walks, on the bytes that are actually on disk:

    Unit -> mesh_info (visible LOD) -> section.material_index
         -> mesh.material_ids[matidx]            (short id)
         -> unit.section_ids.index(short id) -> unit.material_ids[k]
         -> the Material asset -> slot keys + TextureIDs
         -> the Texture assets

Every step is asserted against the package, not against the in-memory objects
the builder used.  Texture ids are read with the SDK parser (`stingray/
material.py`), never with our own reader, so a shared bug cannot hide.

The carried (visible) units are the acceptance target.  Hidden placeholder
units legitimately reference the *vanilla* armour's own materials, which the
game already ships -- those ids are cross-checked against a vanilla extract
directory and reported as `vanilla-backed` rather than as a failure.

Usage:
    python _probe_matchain.py <patch_0> [--carried id,id]
        [--vanilla-material-dir E:/tmp/hd2-mgroot/re2310-full/material]
"""
import argparse
import importlib.util
import os
import sys
import types
from pathlib import Path

import hd2_asset as A
import hd2_patch as P
import hd2_unit as U

ROOT = Path(__file__).resolve().parent
SDK = ROOT.parents[1] / 'docs/HD2SDK-CommunityEdition-3.8.0'


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    m = importlib.util.module_from_spec(spec)
    sys.modules[name] = m
    spec.loader.exec_module(m)
    return m


for pkgname in ('mcsdk', 'mcsdk.utils', 'mcsdk.stingray'):
    pkg = types.ModuleType(pkgname)
    pkg.__path__ = []
    sys.modules[pkgname] = pkg
MS = module('mcsdk.utils.memoryStream', SDK / 'utils/memoryStream.py')
SM = module('mcsdk.stingray.material', SDK / 'stingray' / 'material.py')


def sdkread(raw):
    m = SM.StingrayMaterial()
    m.Serialize(MS.MemoryStream(bytes(raw)))
    return m


def vanilla_materials(path):
    """Ids present in a vanilla extract directory (material_0x<id>.toc)."""
    if not path or not os.path.isdir(path):
        return None
    out = set()
    for f in os.listdir(path):
        if f.startswith('material_0x') and f.endswith('.toc'):
            out.add(int(f[len('material_0x'):-len('.toc')], 16))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch')
    ap.add_argument('--carried', default='cae35ee15168145f,9841b16544c8fbae')
    ap.add_argument('--vanilla-material-dir',
                    default='E:/tmp/hd2-mgroot/re2310-full/material')
    args = ap.parse_args()
    carried = {int(x, 16) for x in args.carried.split(',') if x}
    vanilla = vanilla_materials(args.vanilla_material_dir)

    patch = args.patch
    entries, _ = P.scan_entries(patch)
    materials = {e['file_id']: e for e in entries if e['type_id'] == A.MATERIAL_TYPE_ID}
    textures = {e['file_id'] for e in entries if e['type_id'] == A.TEXTURE_TYPE_ID}

    units = chains = 0
    broken, vanilla_backed, unknown = [], [], set()
    for e in entries:
        if e['type_id'] != U.UNIT_ID:
            continue
        units += 1
        pl = P.entry_payload(e, patch)
        u = U.Unit.read(pl['toc'], pl['gpu'])
        for mi in u.mesh_infos:
            if mi.lod_index < 0:
                continue
            for sec in mi.sections:
                if sec.num_indices == 0:
                    continue
                chains += 1
                if sec.material_index >= len(mi.material_ids):
                    broken.append((e['file_id'], 'matidx out of range'))
                    continue
                slot_id = mi.material_ids[sec.material_index]
                if slot_id not in u.section_ids:
                    broken.append((e['file_id'], 'short id 0x%08x not in unit table' % slot_id))
                    continue
                asset = u.material_ids[u.section_ids.index(slot_id)]
                if asset not in materials:
                    if vanilla is not None and asset in vanilla:
                        vanilla_backed.append((e['file_id'], asset))
                    else:
                        unknown.add(asset)
                        broken.append((e['file_id'], 'material 0x%016x missing' % asset))
                    continue
                raw = P.entry_payload(materials[asset], patch)['toc']
                sdk = sdkread(raw)
                ours = A.Material.read(raw)
                if list(sdk.TexUnks) != list(ours.tex_unks) or \
                        list(sdk.TexIDs) != list(ours.tex_ids):
                    broken.append((e['file_id'], 'SDK/our reader disagree on 0x%016x' % asset))
                miss = [i for i in sdk.TexIDs if i not in textures]
                if miss:
                    broken.append((e['file_id'], 'material 0x%016x -> %d unresolved texture(s)'
                                   % (asset, len(miss))))

    carried_chains = 0
    carried_broken = [b for b in broken if b[0] in carried]
    print('package      : %s' % patch)
    print('units        : %d   material assets: %d   texture assets: %d'
          % (units, len(materials), len(textures)))
    print('visible section chains walked: %d' % chains)
    print('dangling refs on non-carried units, vanilla-backed : %d (distinct ids %d)'
          % (len(vanilla_backed), len({a for _u, a in vanilla_backed})))
    print('dangling refs that are neither in-package nor vanilla : %d  %s'
          % (len(unknown), ['0x%016x' % a for a in sorted(unknown)]))
    print('broken links on carried units: %d' % len(carried_broken))
    for b in carried_broken[:20]:
        print('   0x%016x  %s' % b)
    print('RESULT: %s' % ('PASS' if not carried_broken else 'FAIL'))
    return 0 if not carried_broken else 1


if __name__ == '__main__':
    sys.exit(main())
