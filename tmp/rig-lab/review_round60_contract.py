"""Independent round60 review. Reads deployed assets; writes only diagnostic JSON.
No production asset, weight or deployment is modified. SDK material parser is
loaded without Blender; candidate material bytes exist in memory only.
"""
from pathlib import Path
import hashlib
import importlib.util
import json
import struct
import sys
import types
import numpy as np
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import connected_components
import hd2_patch as P
import hd2_unit as U
import hd2_asset as A
import hd2_hash as H

ROOT = Path(__file__).resolve().parent
SDK = ROOT.parents[1] / 'docs/HD2SDK-CommunityEdition-3.8.0'
PATCH = Path('E:/mod/Lunalice-RE2310-Test/9ba626afa44a3aa3.patch_0')
OUT = ROOT / 'out/round60-independent-review.json'

def sha(p):
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()

def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    m = importlib.util.module_from_spec(spec)
    sys.modules[name] = m
    spec.loader.exec_module(m)
    return m

for name in ('r60sdk', 'r60sdk.utils', 'r60sdk.stingray'):
    pkg = types.ModuleType(name)
    pkg.__path__ = []
    sys.modules[name] = pkg
MS = module('r60sdk.utils.memoryStream', SDK / 'utils/memoryStream.py')
SM = module('r60sdk.stingray.material', SDK / 'stingray/material.py')

def sdkread(raw):
    m = SM.StingrayMaterial()
    m.Serialize(MS.MemoryStream(raw))
    return m

def hx(xs):
    return ['%016x' % x for x in xs]

entries, _ = P.scan_entries(str(PATCH))
textures = {e['file_id'] for e in entries if e['type_id'] == A.TEXTURE_TYPE_ID}
base = (SDK / 'materials/basic.material').read_bytes()
base_sdk = sdkread(base)
materials = []
for e in entries:
    if e['type_id'] != A.MATERIAL_TYPE_ID:
        continue
    raw = P.entry_payload(e, str(PATCH))['toc']
    real = sdkread(raw)
    legacy = A.Material.read(raw)
    assert real.NumTextures == 3
    intended = legacy.tex_ids  # recovered from known erroneous writer, not proof of validity
    fixed = bytearray(base)
    for i, tid in enumerate(intended):
        struct.pack_into('<Q', fixed, 136 + 4 * 3 + 8 * i, tid)
    checked = sdkread(fixed)
    assert checked.TexUnks == base_sdk.TexUnks
    assert checked.TexIDs == intended
    assert all(x in textures for x in checked.TexIDs)
    assert fixed[:136] == base[:136] and fixed[172:] == base[172:]
    # Negative control: the current production writer still passes its own
    # roundtrip but MUST fail SDK reference resolution after changing textures.
    current = A.Material.read(base)
    for i, tid in enumerate(intended):
        current.set_texture(i, tid)
    bad = sdkread(current.write())
    assert bad.TexUnks != base_sdk.TexUnks
    assert not all(x in textures for x in bad.TexIDs)
    materials.append(dict(id='%016x' % e['file_id'], sha256=hashlib.sha256(raw).hexdigest(),
        expected_keys=hx(base_sdk.TexUnks), actual_keys=hx(real.TexUnks),
        intended_ids=hx(intended), actual_ids=hx(real.TexIDs),
        actual_package_resolved=[x in textures for x in real.TexIDs],
        in_memory_corrected_resolved=[x in textures for x in checked.TexIDs],
        negative_control_rejected=True))

ue = next(e for e in entries if e['type_id'] == U.UNIT_ID and e['file_id'] == 0xcae35ee15168145f)
pl = P.entry_payload(ue, str(PATCH))
u = U.Unit.read(pl['toc'], pl['gpu'])
v = u.decode_vertices(0)
pts = np.asarray([x['position'] for x in v], float)
bi = np.asarray([x['bone_index0'] for x in v], int)
wv = np.asarray([x['bone_weight'] for x in v], float)
BONES = H.load_bonehash(str(SDK / 'hashlists/bonehash.txt'))
ti = u.transform_info
slots = u.bone_infos[0].real_indices
names = [BONES.get(ti.hashes[i], '%08x' % ti.hashes[i]) for i in slots]
slot = {n: i for i, n in enumerate(names)}
heads = np.asarray([ti.matrices[i][12:15] for i in slots], float)
npz = np.load(ROOT / 'out/blender/model.npz')
meta = json.loads((ROOT / 'out/blender/meta.json').read_text(encoding='utf-8'))
assert pts.shape == npz['ours_bind'].shape
assert np.array_equal(pts, npz['ours_bind'])
# Compare semantic bone weights, not palette slot numbers.
dense = np.zeros((len(pts), len(names)))
for j in range(4):
    np.add.at(dense, (np.arange(len(pts)), bi[:, j]), wv[:, j])
source_dense = np.zeros_like(dense)
for j in range(npz['w_bone'].shape[1]):
    srcidx = npz['w_bone'][:, j]
    valid = npz['w_val'][:, j] > 0
    dest = np.array([slot[meta['bones'][i]] for i in srcidx[valid]])
    np.add.at(source_dense, (np.nonzero(valid)[0], dest), npz['w_val'][valid, j])
assert np.array_equal(source_dense, dense)
tris = npz['tris']
edges = np.concatenate([tris[:, [0, 1]], tris[:, [1, 2]], tris[:, [2, 0]]])
graph = coo_matrix((np.ones(len(edges)), (edges[:, 0], edges[:, 1])), shape=(len(pts), len(pts)))
ncomp, labels = connected_components(graph, directed=False)

arms = []
for side in ('l', 'r'):
    e, h, sh = [slot[side + '_' + n] for n in ('elbow', 'hand', 'shoulder')]
    E, W = heads[e], heads[h]
    axis = W - E
    length = float(np.linalg.norm(axis))
    axis /= length
    s = (pts - E) @ axis
    radius = np.linalg.norm(pts - E - s[:, None] * axis, axis=1)
    # Fixed geometry-only broad selection: intentionally labelled screening,
    # not certified skin membership. No dominant-bone classification.
    near = (radius < .10) & (s > 0) & (s < length)
    # Rotate hand AND descendants 30 degrees around a transverse wrist axis.
    # This is synthetic bend stress, NOT a captured game frame.
    rotaxis = np.cross(axis, [0., 0., 1.])
    rotaxis /= np.linalg.norm(rotaxis)
    a = np.deg2rad(30)
    K = np.array([[0., -rotaxis[2], rotaxis[1]], [rotaxis[2], 0., -rotaxis[0]], [-rotaxis[1], rotaxis[0], 0.]])
    R = np.eye(3) + np.sin(a) * K + (1 - np.cos(a)) * K @ K
    hand_ti = slots[h]
    def below(idx):
        seen = set()
        while idx not in seen and 0 <= idx < ti.count:
            if idx == hand_ti:
                return True
            seen.add(idx)
            idx = ti.entries[idx][1]
        return False
    desc = [j for j, t in enumerate(slots) if below(t)]
    wh = dense[:, desc].sum(axis=1)
    delta = wh[:, None] * ((pts - W) @ R.T - (pts - W))
    # Limiting control: reassign wrist-subtree mass to elbow only on a fixed
    # middle ROI. This is deliberately NOT a finished reweighting recipe.
    middle = near & (s >= .25 * length) & (s < .7 * length)
    control = dense.copy()
    control[np.ix_(middle, desc)] = 0
    control[middle, e] += wh[middle]
    assert np.allclose(control.sum(axis=1), dense.sum(axis=1), rtol=0, atol=1e-12)
    assert np.array_equal(control[~middle], dense[~middle])
    control_delta = control[:, desc].sum(axis=1)[:, None] * ((pts - W) @ R.T - (pts - W))
    assert np.max(np.abs(control_delta[middle])) < 1e-12
    # Negative control: keeping the original weights violates the stationary
    # middle-forearm test by centimetres.
    assert np.max(np.linalg.norm(delta[middle], axis=1)) > .04
    rows = []
    for comp in np.unique(labels[near]):
        sel = near & (labels == comp)
        if sel.sum() < 10:
            continue
        bins = []
        for lo, hi in ((0, .25), (.25, .7), (.7, .9), (.9, 1.)):
            mask = sel & (s >= lo * length) & (s < hi * length)
            if not mask.any():
                continue
            disp = np.linalg.norm(delta[mask], axis=1) * 1000
            bins.append(dict(t=[lo, hi], n=int(mask.sum()),
                hand=float(dense[mask, h].mean()), elbow=float(dense[mask, e].mean()),
                shoulder=float(dense[mask, sh].mean()), radius_mm=float(np.mean(radius[mask]) * 1000),
                synthetic_wrist30_mm_median=float(np.median(disp)), synthetic_wrist30_mm_max=float(np.max(disp))))
        rows.append(dict(component=int(comp), whole_component_n=int((labels == comp).sum()),
            selected_n=int(sel.sum()), s_mm=[float(s[sel].min()*1000), float(s[sel].max()*1000)], bins=bins))
    arms.append(dict(side=side, length_m=length, components=rows,
        fixed_middle_control_displacement_max_mm=float(np.max(np.linalg.norm(control_delta[middle], axis=1))*1000),
        old_weight_negative_control_displacement_max_mm=float(np.max(np.linalg.norm(delta[middle], axis=1))*1000)))

report = dict(patch=str(PATCH), patch_sha256=sha(PATCH), gpu_sha256=sha(str(PATCH)+'.gpu_resources'),
    scripts={n: sha(ROOT / n) for n in ('hd2_asset.py', 'build_patch.py', 'dump_blender.py', 'hd2_style.py')},
    material_count=len(materials), texture_count=len(textures), materials=materials,
    geometry_equal_frozen_npz=True, semantic_weights_equal_frozen_npz=True,
    connected_component_count=ncomp, arms=arms,
    limits='Components are topology groups, not semantic skin labels. Synthetic wrist bend is not game animation. No production fix or candidate package generated.')
OUT.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print('patch sha256:', report['patch_sha256'])
print('material/texture counts:', len(materials), len(textures))
print('SDK unresolved package refs:', sum(not b for m in materials for b in m['actual_package_resolved']))
print('In-memory repair and old-writer negative control:', len(materials), 'passed')
print('Deployed geometry and semantic weights exactly equal frozen NPZ: True')
for arm in arms:
    print('ARM', arm['side'], 'components', len(arm['components']))
    print('middle control/old max mm:', arm['fixed_middle_control_displacement_max_mm'], arm['old_weight_negative_control_displacement_max_mm'])
    for row in arm['components']:
        if row['whole_component_n'] == 443:
            print('component', row['component'], 'size', row['whole_component_n'], 'selected', row['selected_n'], 'bins', row['bins'])
print('REPORT', OUT)
