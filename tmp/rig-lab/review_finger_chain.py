"""Read-only pipeline review: actual chains, terminal axes, parent-space solve."""
import os, sys, json, hashlib
from pathlib import Path
import numpy as np
ROOT=Path(__file__).resolve().parent
os.chdir(ROOT)
sys.path.insert(0,str(ROOT))
import hd2_style as H
import dump_blender as B
import export_obj as E

def unit(x):
    return x / np.linalg.norm(x)
def angle(a,b):
    return float(np.degrees(np.arccos(np.clip(unit(a)@unit(b),-1,1))))
def point(M,p):
    return (M@np.r_[p,1])[:3]
def post(p):
    p=np.asarray(p)+np.array(B.MESH_SHIFT)
    piv=np.array([0.,0.,1.15]); piv[:2]=r['rest_hd2'][idx['hips']][:2,3]
    t=np.clip((p[...,2]-(1.15-.45))/.9,0,1)
    sc=1+.12*t*t*(3-2*t)
    return piv+(p-piv)*np.asarray(sc)[...,None]

r=H.retarget(B.VPK,B.MODEL,B.TOC,B.BH,B.PP,align='yaw',spine_map='mesh',fallback='region',
    leg_tilt=5.7,leg_splay=4,skeleton='native',mesh_shift=B.MESH_SHIFT,
    upper_scale=1.12,upper_pivot_z=1.15,upper_blend=.45,head_scale=B.HEAD_SCALE,
    arm_scratch_upper_gain=B.ARM_UPPER_GAIN,arm_elbow_open=B.ARM_ELBOW_OPEN,
    arm_hand_tilt=B.ARM_HAND_TILT,verbose=False)
idx={h['name']:h['index'] for h in r['hd2']}
src={b['name'].removeprefix(E.SRC_PREFIX):b for b in r['src']}
# Recover global alignment rotation from hand-root positions; source T-pose mirror yaw
# is fixed by actual matched shoulder/elbow vectors on both sides plus vertical.
sa=np.asarray(src['L_Forearm']['world_m']).reshape(4,4)[:3,3]-np.asarray(src['L_UpperArm']['world_m']).reshape(4,4)[:3,3]
wa=r['rest'][idx['l_elbow']][:3,3]-r['rest'][idx['l_shoulder']][:3,3]
yaw=np.arctan2(wa[1],wa[0])-np.arctan2(sa[1],sa[0])
A=H.rot_axis_angle(np.array([0.,0.,1.]),yaw)
rows=[]
for side in ('l','r'):
 for fi,fn in E.FINGER_NAMES.items():
    names=[f'{side}_{fn}_finger{k}' for k in (1,2,3)]
    ids=[idx[n] for n in names]
    ss=[f'{side.upper()}_Finger{fi}{s}' for s in ('','1','2')]
    P=np.array([r['rest'][i][:3,3] for i in ids])
    T=np.array([r['rest_hd2'][i][:3,3] for i in ids])
    source_ax=[A@np.asarray(src[s]['world_m']).reshape(4,4)[:3,0] for s in ss]
    target_ax=[r['rest_hd2'][i][:3,0] for i in ids]
    sd=[unit(P[1]-P[0]),unit(P[2]-P[1]),unit(source_ax[2])]
    td=[unit(T[1]-T[0]),unit(T[2]-T[1]),unit(target_ax[2])]
    n=unit(np.cross(td[0],td[1]))
    # Third direction out of first-two-segment plane is not tautologically zero.
    outplane=float(np.degrees(np.arcsin(np.clip(abs(n@td[2]),0,1))))
    Dp=r['D'][idx[f'{side}_hand']].copy()
    solved=[]; direction_errors=[]; bad_errors=[]; pivot_errors=[]
    for k,i in enumerate(ids):
      Q=Dp[:3,:3]
      local_world=H.min_arc_rotation(sd[k],Q.T@td[k])
      rs=r['rest'][i]; Rs=rs[:3,:3]
      basis=np.eye(4); basis[:3,:3]=np.linalg.inv(Rs)@local_world@Rs
      Di=Dp@rs@basis@np.linalg.inv(rs)
      direction_errors.append(angle(Di[:3,:3]@sd[k],td[k]))
      wrong=H.min_arc_rotation(Q@sd[k],td[k])
      bad_errors.append(angle(Q@wrong@sd[k],td[k]))
      pivot_errors.append(float(np.linalg.norm(point(Di,P[k])-point(Dp,P[k]))))
      solved.append(Di); Dp=Di
    joints=np.array([point(solved[k],P[k]) for k in range(3)])
    # Shape-only: translate the ideal chain root to target root for measurement only.
    joints_aligned=joints+T[0]-joints[0]
    rows.append(dict(side=side,finger=fn,
      source_bends_deg=[angle(sd[0],sd[1]),angle(sd[1],sd[2])],
      target_bends_deg=[angle(td[0],td[1]),angle(td[1],td[2])],
      terminal_out_of_plane_deg=outplane,
      source_axis_child_errors_deg=[angle(source_ax[k],sd[k]) for k in (0,1)],
      target_axis_child_errors_deg=[angle(target_ax[k],td[k]) for k in (0,1)],
      source_lengths_mm=(np.linalg.norm(np.diff(P,axis=0),axis=1)*1000).tolist(),
      target_lengths_mm=(np.linalg.norm(np.diff(T,axis=0),axis=1)*1000).tolist(),
      solved_direction_errors_deg=direction_errors,
      wrong_world_rotation_errors_deg=bad_errors,
      pivot_preservation_errors_m=pivot_errors,
      f3_residual_if_root_aligned_mm=float(np.linalg.norm(joints_aligned[2]-T[2])*1000),
      snapshot_final_root_proxy_gap_mm=float(np.linalg.norm(post(point(r['D'][ids[0]],P[0]))-T[0])*1000),
      target_points=T.tolist(),source_directions=np.array(sd).tolist(),target_directions=np.array(td).tolist()))
import patch_view as PV
meta=json.loads((ROOT/'out/patchdump/units.json').read_text(encoding='utf-8'))
hashnames=PV.read_bone_names(B.BH)
patch_checks=[]
for uu in meta:
 for bb in uu.get('bones',[]):
    nm=hashnames.get(bb['hash'])
    if nm and nm in idx and 'finger' in nm:
      pm=np.asarray(bb['matrix']).reshape(4,4).T
      patch_checks.append((nm,float(np.max(np.abs(pm-r['rest_hd2'][idx[nm]])))))
print('Reference full finger matrices: unique',len(set(n for n,v in patch_checks)),'max delta',max([v for n,v in patch_checks],default=-1))
report={'reference_finger_matrices_checked':len(set(n for n,v in patch_checks)), 'reference_finger_matrix_max_delta':max([v for n,v in patch_checks],default=None),'source_sha256':hashlib.sha256((ROOT/'hd2_style.py').read_bytes()).hexdigest(),
        'note':'Bone-chain review only; root-aligned residual is hypothetical translation, not applied to mesh. Final root proxy passes working point through mesh postprocess and is not a surface measurement.',
        'rows':rows}
assert max(max(q['solved_direction_errors_deg']) for q in rows)<1e-5
assert max(max(q['pivot_preservation_errors_m']) for q in rows)<1e-12
assert max(max(q['wrong_world_rotation_errors_deg']) for q in rows)>1
(ROOT/'out/finger-chain-independent-review.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
for q in rows:
 print(q['side'],q['finger'],'bends',np.round(q['target_bends_deg'],2),'plane',round(q['terminal_out_of_plane_deg'],2),
       'lengths',np.round(q['source_lengths_mm'],2),np.round(q['target_lengths_mm'],2),
       'f3 gap',round(q['f3_residual_if_root_aligned_mm'],2),'root proxy',round(q['snapshot_final_root_proxy_gap_mm'],2),
       'bad rotation',np.round(q['wrong_world_rotation_errors_deg'],2))
print('axis/child max source',max(max(q['source_axis_child_errors_deg']) for q in rows),'target',max(max(q['target_axis_child_errors_deg']) for q in rows))
print('All chain assertions passed; wrong parent-space formula fails >1 degree.')
