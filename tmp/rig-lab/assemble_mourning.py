# -*- coding: utf-8 -*-
"""Assemble the shipped `MourningVeil-RE2310` package.

Same idea as assemble_re2310.py, but parameterised so a second source pack can
be packaged without copying Lilium's constants.  Everything is derived from
artefacts that already exist on disk:

  * `E:/tmp/mourning-an-out/`      -- the `hd2mod equipment armor-normalize apply`
                                      output (2 carried units + 20 hidden ones)
  * `E:/tmp/r72-an-plan.json`      -- the frozen plan that produced it
  * `E:/mod/MourningVeil-Native-RE2310/preview/`  -- the render self-check

Nothing is regenerated.  The manifest is written from the real files so the
recorded sizes and hashes cannot drift from the bytes on disk.

NOTE ON THE HELMET: RE-2310's head is a *separate* archive (b4d15b145b3f7677,
category `Helmet`, single unit 0xaf431352d9b95e83) from the torso armour
(d923906f8b815987, category `Armor`).  `armor-normalize` only ever sees the
archive it normalises, so it cannot hide the helmet.  A separate mod
(`RE-2310“荣誉卫队”隐藏头盔`, guid 0532b11b-…) ships that hidden placeholder;
it must be deployed *after* this one.  This package deliberately does not touch
the head.

usage: assemble_mourning.py [--out E:/mod/MourningVeil-RE2310]
"""
import argparse
import hashlib
import json
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as HP
import hd2_unit as U

AN_OUT = r'E:/tmp/mourning-an-out'
AN_PLAN = r'E:/tmp/r72-an-plan.json'
NATIVE_DIR = r'E:/mod/MourningVeil-Native-RE2310'
FINAL_DIR = r'E:/tmp/mourning-final'
META = r'E:/tmp/mourning/out/meta.json'
DEFAULT_OUT = r'E:/mod/MourningVeil-RE2310'
PATCH = '9ba626afa44a3aa3.patch_0'
NAME = 'mourningveil-re2310'
SCHEMA = 'hd2mod.mourningveil-re2310'
PROTOCOL_MODEL = 'custom/mourningveil-re2310'


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def unit_ids(path):
    entries, _ = HP.scan_entries(path)
    out = []
    for e in entries:
        if e['type_id'] == U.UNIT_ID:
            out.append('%016x' % e['file_id'])
    return sorted(out), len(entries)


def mesh_facts(meta_path):
    """Vertex/triangle totals of the source mesh.

    meta.json records params/metrics only, so the numbers come from the npz
    written by dump_blender.py (pos/tris), falling back to npz keys.
    """
    import numpy as np
    npz = os.path.join(os.path.dirname(meta_path), 'model.npz')
    if os.path.isfile(npz):
        z = np.load(npz, allow_pickle=False)
        v = t = None
        for key in ('ours_bind', 'ours_game', 'pos', 'positions', 'verts', 'v'):
            if key in z.files:
                v = int(z[key].shape[0])
                break
        for key in ('tris', 'triangles', 'faces', 'f'):
            if key in z.files:
                t = int(z[key].shape[0])
                break
        if v is not None or t is not None:
            return v, t
    return None, None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default=DEFAULT_OUT)
    ap.add_argument('--an-out', default=AN_OUT)
    ap.add_argument('--an-plan', default=AN_PLAN)
    ap.add_argument('--native-dir', default=NATIVE_DIR)
    ap.add_argument('--final-dir', default=FINAL_DIR)
    ap.add_argument('--source-entries', type=int, default=0,
                    help='entry count of the *source* (pre-normalize) patch; '
                         'read from the plan when 0')
    args = ap.parse_args()

    plan = json.load(open(args.an_plan, encoding='utf-8'))
    armor = plan['armors'][0]
    carried = ['%x' % int(t['assetKey'].split('/')[1], 16) for t in armor['targets']]
    hidden = sorted('%x' % int(k.split('/')[1], 16)
                    for k in armor['hiddenUnitAssetKeys'])

    os.makedirs(args.out, exist_ok=True)
    for f in (PATCH, PATCH + '.gpu_resources'):
        shutil.copy2(os.path.join(args.an_out, f), os.path.join(args.out, f))
    pv = os.path.join(args.out, 'preview')
    if os.path.isdir(pv):
        shutil.rmtree(pv)
    shutil.copytree(os.path.join(args.final_dir, 'preview'), pv)

    toc = os.path.join(args.out, PATCH)
    gpu = toc + '.gpu_resources'
    ids, entry_count = unit_ids(toc)

    # the source patch (before normalize) tells us how many entries it had
    src_entries = args.source_entries
    src_units = None
    src_patch = os.path.join(args.native_dir, PATCH)
    if os.path.isfile(src_patch):
        sids, src_entries = unit_ids(src_patch)
        src_units = len(sids)

    app_path = os.path.join(args.final_dir, 'mourning-veil-zoey.manifest.json')
    app = json.load(open(app_path, encoding='utf-8'))
    mat_tris = sum(g['triangles'] for g in app['groups'])
    src_entries = src_entries or app.get('entryCount')
    verts, tris = None, mat_tris
    if os.path.isfile(META):
        mv, mt = mesh_facts(META)
        if isinstance(mv, int):
            verts = mv
        if isinstance(mt, int) and mt >= mat_tris:
            tris = mt

    man = dict(
        schema=SCHEMA,
        generatedBy='hd2mod equipment armor-normalize apply',
        armor=dict(archiveId=armor['archiveId'], displayName=armor['displayName']),
        helmet=dict(
            archiveId='b4d15b145b3f7677',
            unit='0xaf431352d9b95e83',
            note='RE-2310 head lives in a separate Helmet archive and is NOT '
                 'touched here.  Enable mod “RE-2310“荣誉卫队”隐藏头盔” '
                 '(0532b11b-e0a7-4587-a948-8194691285dd) after this one to hide it.'),
        sourcePatch=src_patch.replace('/', '\\'),
        sourceEntryCount=src_entries,
        sourceUnitCount=src_units,
        outputPatch=os.path.join(args.an_out, PATCH).replace('/', '\\'),
        outputEntryCount=entry_count,
        outputUnitCount=len(ids),
        carriedUnits=[dict(unit='0x' + '%016x' % int(t['assetKey'].split('/')[1], 16),
                           part=t['role'], body=t['bodyCoverage'])
                      for t in armor['targets']],
        hiddenUnitCount=len(hidden),
        hiddenUnits=['0x' + h for h in hidden],
        patchSize=os.path.getsize(toc),
        patchSha256=sha256(toc),
        gpuResourceSize=os.path.getsize(gpu),
        gpuResourceSha256=sha256(gpu),
        vertices=verts,
        triangles=tris,
        note='The whole MourningVeil (Zoey) model is written under RE-2310\'s two '
             'real Torso unit ids (Slim + Stocky, mutually exclusive at runtime, so '
             'exactly one copy is ever drawn).  The other %d units of the archive '
             'are sub-millimetre placeholders.  No Lua registry and no manager '
             'mapping are involved: the game resolves every id by itself.'
             % len(hidden),
    )
    with open(os.path.join(args.out, NAME + '.manifest.json'), 'w',
              encoding='utf-8') as fh:
        json.dump(man, fh, ensure_ascii=False, indent=1)

    proto = os.path.join(args.out, NAME + '.protocol')
    with open(proto, 'w', encoding='utf-8', newline='\n') as fh:
        fh.write('schema=2\nrevision=1\nenabled=false\n'
                 'model=%s,\n' % PROTOCOL_MODEL)
        for t in armor['targets']:
            uid = '%016x' % int(t['assetKey'].split('/')[1], 16)
            body = {'Slim': 0, 'Stocky': 1, 'Any': 3}[t['bodyCoverage']]
            fh.write('model_piece=%s,%d,%s,2,1,0\n' % (PROTOCOL_MODEL, body, uid))
        fh.write('# Produced by: hd2mod equipment armor-normalize apply\n'
                 '#   source  = %s (one full model, two native ids)\n'
                 '#   armour  = %s (archive %s)\n'
                 '#   helmet  = archive b4d15b145b3f7677 is NOT touched; enable\n'
                 '#             mod 0532b11b-… (“RE-2310“荣誉卫队”隐藏头盔”) after\n'
                 '#             this one to hide RE-2310\'s own head.\n'
                 '# The two native torso ids carry the whole model (Slim + Stocky,\n'
                 '# mutually exclusive at runtime) and the other %d units of the\n'
                 '# archive are hidden placeholders.  The game resolves every id\n'
                 '# itself, so no runtime mapping is required or wanted; this file\n'
                 '# is a record only and stays disabled on purpose.\n'
                 % (args.native_dir, armor['displayName'], armor['archiveId'],
                    len(hidden)))

    print('package : %s' % args.out)
    print('  entries        : %d' % entry_count)
    print('  units          : %d  (%d carried + %d hidden)'
          % (len(ids), len(carried), len(hidden)))
    print('  carried        : %s' % ', '.join('0x' + c for c in carried))
    print('  patch          : %d B  %s' % (man['patchSize'], man['patchSha256']))
    print('  gpu            : %d B  %s' % (man['gpuResourceSize'],
                                           man['gpuResourceSha256']))
    return 0


if __name__ == '__main__':
    sys.exit(main())
