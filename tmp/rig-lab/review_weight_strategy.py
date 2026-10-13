"""Read-only independent weight audit; synthetic poses are not game animations."""
from pathlib import Path
import json, math, os, runpy, hashlib
import numpy as np
ROOT = Path(__file__).resolve().parent
os.chdir(ROOT)
d = np.load(ROOT / 'out/blender/model.npz')
m = json.loads((ROOT / 'out/blender/meta.json').read_text(encoding='utf-8'))
P = d['ours_bind'].astype(float)
b = d['w_bone'].astype(int)
w = np.where(b >= 0, d['w_val'], 0).astype(float)
names = m['bones']
W = np.zeros((len(P), len(names)))
for j in range(b.shape[1]):
    ok = b[:, j] >= 0
    np.add.at(W, (np.flatnonzero(ok), b[ok, j]), w[ok, j])
# Four-slot, uniform 1/2048 largest-remainder quantizer; distinct from SDK optimum.
a = np.pad(w, ((0, 0), (0, 4-w.shape[1])))
a /= a.sum(axis=1)[:, None]
q = np.floor(a*2048).astype(int)
r = 2048-q.sum(axis=1)
keys = np.array([[names[x] if x >= 0 else '~' for x in row] + ['~']*(4-len(row)) for row in b])
for i in range(len(q)):
    order = sorted(np.flatnonzero(a[i] > 0), key=lambda j: (-(a[i,j]*2048-q[i,j]), keys[i,j], j))
    for j in order[:r[i]]: q[i,j] += 1
v = q/2048.
assert np.all(v.astype(np.float16).astype(float) == v)
assert np.all(v.sum(axis=1) == 1)
assert not np.any(v[a == 0])
assert np.max(np.abs(v-a)) < 1/2048
bad_naive = np.count_nonzero(a.astype(np.float16).astype(float).sum(axis=1) != 1)
# Same bone set can carry strongly different weights: shared-count audit misses it.
t = d['tris'].astype(int)
e = np.unique(np.sort(np.vstack((t[:,[0,1]], t[:,[1,2]], t[:,[0,2]])), axis=1), axis=0)
l1 = np.abs(W[e[:,0]]-W[e[:,1]]).sum(axis=1)
# Head-only turn: use perpendicular distance to vertical axis, not joint distance.
h = names.index('head')
low = (W.argmax(axis=1) == h) & (P[:,2] < 1.4)
J = d['b_head'][h].astype(float)
radius = np.linalg.norm((P-J)[:,:2], axis=1)
# Counterfactual 20deg difference between hips/spine1, common bind pivot.
hip, spine = names.index('hips'), names.index('spine1')
Jh = d['b_head'][hip].astype(float)
rad_hip = np.linalg.norm((P-Jh)[:,:2], axis=1)
hip_shift = W[:,hip] * 2*rad_hip*np.sin(np.deg2rad(20)/2)
R = {
 'model_sha256': hashlib.sha256((ROOT/'out/blender/model.npz').read_bytes()).hexdigest(),
 'vertex_count': len(P),
 'quantization': {'rows_tested':len(P), 'naive_nonclosure':int(bad_naive), 'grid_exact_sum_all':True,
   'grid_max_change':float(abs(v-a).max()), 'negative_control_naive_rejected':bool(bad_naive>0),
   'note':'Uniform grid is correct but not bitwise equivalent to SDK residual optimizer.'},
 'edge_weight_L1':{'p50_p90_p99_max':np.percentile(l1,[50,90,99,100]).tolist(),
   'over_1':int((l1>1).sum()), 'note':'Diagnostic only; not a crack verdict.'},
 'shared_bones_counterexample':{'a':[.999,.001], 'b':[.001,.999], 'shared_fraction':1, 'weight_L1':1.996},
 'head_dominant_below_1_4':{'count':int(low.sum()),
   'rigid_yaw30_displacement_mm_p50_max':np.percentile(2*radius[low]*np.sin(np.deg2rad(30)/2)*1000,[50,100]).tolist(),
   'radius_mm_p50_max':np.percentile(radius[low]*1000,[50,100]).tolist(),
   'note':'Rigid upper bound for head-only yaw; not an actual game pose or collision measure.'},
 'synthetic_hips_to_spine1_yaw20':{'max_mm':float(hip_shift.max()*1000),
   'note':'Counterfactual all hips weight reassignment, proves sensitivity only; NOT recommended remapping.'}}
# Reuse read-only comparison to cross-tabulate its published head->neck label.
c = runpy.run_path(str(ROOT/'_probe_weightcompare.py'))
mask = (c['our_dom']=='head') & (c['rn']=='neck')
R['head_to_neck_crosscheck'] = {'total':int(mask.sum()), 'below_z1_4':int((mask & (P[:,2]<1.4)).sum()),
 'at_or_above_z1_4':int((mask & (P[:,2]>=1.4)).sum()),
 'note':'Height is not a semantic classifier; proves pair count alone does not identify long hair.'}
# Independent randomized properties of the same simple grid algorithm.
def grid4(vals, identities):
    vals = np.asarray(vals, dtype=float)
    if vals.shape != (4,) or len(identities) != 4 or np.any(~np.isfinite(vals)) or np.any(vals < 0):
        raise ValueError('invalid weights')
    total = math.fsum(vals)
    if not math.isfinite(total) or total <= 0:
        raise ValueError('zero/invalid total')
    norm = vals/total
    integers = np.floor(norm*2048).astype(int)
    active = np.flatnonzero(norm > 0)
    order = sorted(active, key=lambda j: (-(norm[j]*2048-integers[j]), identities[j]))
    left = 2048-int(integers.sum())
    if not 0 <= left <= len(order): raise ValueError('bad remainder')
    for j in order[:left]: integers[j] += 1
    return integers/2048.
rng = np.random.default_rng(241013)
for _ in range(10000):
    vals = rng.random(4)
    vals[rng.random(4) < .35] = 0
    if vals.sum() == 0: vals[0] = 1
    out4 = grid4(vals, ['a','b','c','d'])
    assert out4.sum() == 1 and np.all(out4.astype(np.float16).astype(float) == out4)
    assert not np.any(out4[vals == 0])
    assert max(abs(out4-vals/vals.sum())) < 1/2048
    perm = rng.permutation(4)
    permuted = grid4(vals[perm], np.array(['a','b','c','d'])[perm])
    assert np.array_equal(permuted, out4[perm])
    assert np.array_equal(grid4(out4, ['a','b','c','d']), out4)
for vals in ([0,0,0,0], [-1,1,1,1], [float('nan'),1,0,0], [float('inf'),1,0,0]):
    try: grid4(vals, ['a','b','c','d'])
    except ValueError: pass
    else: raise AssertionError('invalid accepted')
R['grid_property_tests'] = {'random_rows':10000, 'exact_sum_half_representability_no_new_influence_error_bound':True,
 'permutation_equivariance':True, 'idempotence':True, 'invalid_cases_rejected':4,
 'language':'Python model of proposed C# algorithm; C# snippet not compiled in this review'}
out = ROOT/'out/weight-strategy-independent-review.json'
out.write_text(json.dumps(R,indent=2,ensure_ascii=False),encoding='utf-8')
print(json.dumps(R,indent=2,ensure_ascii=False))
