"""Verify the BoneInfo matrix formula against the shipping reference unit.

`CanonicalInverseJointMatrixCompiler.Build` says

    origin   = inverse(transpose(TransformInfo.Matrices[meshTransformIndex]))
    raw[k]   = transpose( inverse( origin * transpose(TransformInfo.Matrices[realIndices[k]]) ) )

which simplifies (row-vector convention, the one the file uses) to

    raw[k]   = M_mesh @ inverse(M_bone_k)

Also dumps what avatar.matrices[0] is, so we know what to use as the mesh
transform when we copy the avatar TransformInfo verbatim.
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as HP
import hd2_unit as U

TOC = r'E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310\9ba626afa44a3aa3.patch_0'
AVATAR = 'avatar/unit_0x4d1c334d294dfa97.toc'


def main():
    entries, _raw = HP.scan_entries(TOC)
    for e in entries:
        if e['type_id'] != U.UNIT_ID or e['file_id'] != 0x9841B16544C8FBAE:
            continue
        pl = HP.entry_payload(e, TOC)
        u = U.Unit.read(pl['toc'], pl['gpu'])
        ti = np.asarray(u.transform_info.matrices, np.float64).reshape(-1, 4, 4)
        print('unit TransformInfo count = %d' % len(ti))
        for mi in u.mesh_infos:
            print('   mesh tf=%d lod=%d' % (mi.transform_index, mi.lod_index))
        print('M_mesh (tf of the LOD0 mesh) =')
        for r in ti[u.mesh_infos[3].transform_index]:
            print('     ', np.round(r, 6))
        ok = bad = 0
        for bi in u.bone_infos[:1]:
            mesh_i = u.mesh_infos[3].transform_index
            M = ti[mesh_i]
            for k, real in enumerate(bi.real_indices):
                want = M @ np.linalg.inv(ti[real])
                got = np.asarray(bi.bones[k], np.float64).reshape(4, 4)
                if np.abs(want - got).max() < 1e-4:
                    ok += 1
                else:
                    bad += 1
                    if bad <= 3:
                        print('   MISMATCH k=%d real=%d  maxdiff=%.6f'
                              % (k, real, np.abs(want - got).max()))
        print('bones: formula matches %d / mismatches %d' % (ok, bad))
        # what does the unit's own mesh transform look like vs identity?
        print('mesh transform == identity ?', bool(np.abs(ti[u.mesh_infos[3].transform_index]
                                                          - np.eye(4)).max() < 1e-6))

    # avatar
    av = U.Unit.read(open(AVATAR, 'rb').read())
    at = np.asarray(av.transform_info.matrices, np.float64).reshape(-1, 4, 4)
    print('\navatar count=%d  matrices[0]=' % len(at))
    for r in at[0]:
        print('     ', np.round(r, 6))
    print('avatar matrices[0] == identity ?', bool(np.abs(at[0] - np.eye(4)).max() < 1e-6))
    print('avatar matrices[4] (root) =')
    for r in at[4]:
        print('     ', np.round(r, 6))


if __name__ == '__main__':
    main()
