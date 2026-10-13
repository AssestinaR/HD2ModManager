"""Read current patch; never build or edit production code. Preserve evidence in project."""
from pathlib import Path
import hashlib, json, struct, zipfile
import numpy as np
import hd2_patch as HP
import hd2_unit as U
ROOT = Path(__file__).resolve().parent
PATCH = Path('E:/mod/Lunalice-RigTest/9ba626afa44a3aa3.patch_0')
OUT = ROOT / 'out'
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
entries, raw = HP.scan_entries(str(PATCH))
records = [e for e in entries if e['type_id'] == U.UNIT_ID]
report = {'patch':str(PATCH), 'patch_sha256':sha(PATCH), 'gpu_sha256':sha(Path(str(PATCH)+'.gpu_resources')),
          'unit_count':len(records), 'scope':'Current repaired artifact only. Original failed package is unavailable.', 'units':[]}
for e in records:
    pl = HP.entry_payload(e, str(PATCH))
    toc, gpu = pl['toc'], pl['gpu']
    u = U.Unit.read(toc, gpu)
    # Separate bounds extraction directly from table/record byte offsets.
    table = u.mesh_info_offset
    count = struct.unpack_from('<I', toc, table)[0]
    # Cross-check table structure against reader, without using writer offsets.
    assert count == len(u.mesh_infos)
    offsets = struct.unpack_from('<'+'I'*count, toc, table+4)
    meshes=[]
    for i, mi in enumerate(u.mesh_infos):
        off = table + offsets[i]
        bounds = np.array(struct.unpack_from('<7f', toc, off+8))
        assert np.array_equal(bounds, np.array(struct.unpack('<7f', mi.unk2[:28])))
        si = u.stream_infos[mi.stream_index]
        vertices=u.decode_vertices(mi.stream_index)
        p=np.array([v['position'] for v in vertices])
        outside = ((p < bounds[:3]-1e-6) | (p > bounds[3:6]+1e-6)).any(axis=1)
        meshes.append({'mesh':i,'lod':mi.lod_index,'transform':mi.transform_index,
            'stored_bounds7':bounds.tolist(),'actual_min':p.min(axis=0).tolist(),
            'actual_max':p.max(axis=0).tolist(),'actual_max_origin_norm':float(np.linalg.norm(p,axis=1).max()),
            'vertices_outside':int(outside.sum()),'vertex_count':len(p)})
    ti=u.transform_info
    matrices=np.asarray(ti.matrices).reshape(-1,4,4)
    fk_error=[]
    for k, local in enumerate(ti.locals):
        L=np.eye(4); L[:3,:3]=np.diag(local['scale']) @ np.array(local['rot']).reshape(3,3)
        L[3,:3]=local['pos']
        par=ti.entries[k][1]
        expected=L if par==k else L @ matrices[par]
        fk_error.append(float(abs(expected-matrices[k]).max()))
    binds=[]
    for bi in u.bone_infos:
        B=np.array(bi.bones).reshape(-1,4,4)
        M=matrices[np.array(bi.real_indices)]
        binds.append(float(abs(B @ M-np.eye(4)).max()))
    report['units'].append({'id':f'{e["file_id"]:016x}','nodes':ti.count,
        'unique_hashes':len(set(ti.hashes)),'max_local_parent_world_error':max(fk_error),
        'inverse_bind_max_errors_per_lod':binds, 'meshes':meshes})
protocol=PATCH.parent/'lunalice-rigtest.protocol'
lines=protocol.read_text(encoding='utf-8').splitlines()
report['protocol']={'piece_count':sum(x.startswith('model_piece=') for x in lines),
                    'mapping_count':sum(x.startswith('map_model=') for x in lines)}
# Preserve current artifacts and relevant local code BEFORE any subsequent implementation.
files=[PATCH,Path(str(PATCH)+'.gpu_resources'),protocol,PATCH.parent/'lunalice-rigtest.manifest.json']
files += [p for p in ROOT.glob('hd2_*.py')]
files += [ROOT/'build_patch.py',ROOT/'_verify_patch.py',ROOT/'UNIT_TRANSFORM_BRIEF.md',ROOT/'review_unit_contract.py']
archive=OUT/('unit-review-evidence-'+report['patch_sha256'][:12]+'.zip')
if not archive.exists():
    with zipfile.ZipFile(archive,'x',compression=zipfile.ZIP_STORED) as z:
        for p in files:
            if p.is_file(): z.write(p,('package/' if p.parent==PATCH.parent else 'source/')+p.name)
        z.writestr('file-hashes.json',json.dumps({str(p):sha(p) for p in files if p.is_file()},indent=2))
# A concurrent build would invalidate the audit; require exact stability.
assert sha(PATCH)==report['patch_sha256']
assert sha(Path(str(PATCH)+'.gpu_resources'))==report['gpu_sha256']
report['evidence_archive']=str(archive)
report['evidence_archive_sha256']=sha(archive)
(OUT/'unit-contract-independent-review.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report,indent=2))
