# -*- coding: utf-8 -*-
"""Assemble the shipped `Lilium-Sancta-RE2310` package.

Everything here is derived from artefacts that already exist on disk:

  * `E:/tmp/lilium-an-out/`        -- the `hd2mod equipment armor-normalize apply`
                                      output (2 carried units + 20 hidden ones)
  * `E:/tmp/lilium-an-plan.json`   -- the frozen plan that produced it
  * `E:/mod/Lilium-Sancta-Native-RE2310/preview/`  -- the round-63 self-check
                                      renders (valid: the carried unit's GPU
                                      payload is byte-identical to that build)

Nothing is regenerated.  The manifest is written from the real files so the
recorded sizes and hashes cannot drift from the bytes on disk.

usage: assemble_re2310.py [--out E:/mod/Lilium-Sancta-RE2310]
"""
import argparse
import hashlib
import json
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as HP
import hd2_unit as U

AN_OUT = r'E:/tmp/lilium-an-out'
AN_PLAN = r'E:/tmp/lilium-an-plan.json'
NATIVE_DIR = r'E:/mod/Lilium-Sancta-Native-RE2310'
DEFAULT_OUT = r'E:/mod/Lilium-Sancta-RE2310'
PATCH = '9ba626afa44a3aa3.patch_0'
NAME = 'lilium-sancta-re2310'


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


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default=DEFAULT_OUT)
    args = ap.parse_args()

    plan = json.load(open(AN_PLAN, encoding='utf-8'))
    armor = plan['armors'][0]
    carried = ['%x' % int(t['assetKey'].split('/')[1], 16) for t in armor['targets']]
    hidden = sorted('%x' % int(k.split('/')[1], 16)
                    for k in armor['hiddenUnitAssetKeys'])

    os.makedirs(args.out, exist_ok=True)
    for f in (PATCH, PATCH + '.gpu_resources'):
        shutil.copy2(os.path.join(AN_OUT, f), os.path.join(args.out, f))
    pv = os.path.join(args.out, 'preview')
    if os.path.isdir(pv):
        shutil.rmtree(pv)
    shutil.copytree(os.path.join(NATIVE_DIR, 'preview'), pv)

    toc = os.path.join(args.out, PATCH)
    gpu = toc + '.gpu_resources'
    ids, entry_count = unit_ids(toc)

    man = dict(
        schema='hd2mod.lilium-sancta-re2310',
        generatedBy='hd2mod equipment armor-normalize apply',
        armor=dict(archiveId=armor['archiveId'], displayName=armor['displayName']),
        sourcePatch=os.path.join(NATIVE_DIR, PATCH).replace('/', '\\'),
        sourceEntryCount=44,
        sourceUnitCount=2,
        outputPatch=os.path.join(AN_OUT, PATCH).replace('/', '\\'),
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
        vertices=118014,
        triangles=154066,
        note='The whole Lilium model is written under RE-2310\'s two real Torso '
             'unit ids (Slim + Stocky, mutually exclusive at runtime, so exactly '
             'one copy is ever drawn).  The other 20 units of the archive are '
             'sub-millimetre placeholders.  No Lua registry and no manager '
             'mapping are involved: the game resolves every id by itself.',
        verifiedBy='tmp/rig-lab/_verify_normalized.py --vanilla-dir '
                   'E:/tmp/hd2-mgroot/re2310-full/unit  ->  26/27 pass, '
                   '1 known (zero-index placeholder sections)',
    )
    with open(os.path.join(args.out, NAME + '.manifest.json'), 'w',
              encoding='utf-8') as fh:
        json.dump(man, fh, ensure_ascii=False, indent=1)

    proto = os.path.join(args.out, NAME + '.protocol')
    with open(proto, 'w', encoding='utf-8', newline='\n') as fh:
        fh.write('schema=2\nrevision=1\nenabled=false\n'
                 'model=custom/lilium-sancta-re2310,\n')
        for t in armor['targets']:
            uid = '%016x' % int(t['assetKey'].split('/')[1], 16)
            body = {'Slim': 0, 'Stocky': 1, 'Any': 3}[t['bodyCoverage']]
            fh.write('model_piece=custom/lilium-sancta-re2310,%d,%s,2,1,0\n'
                     % (body, uid))
        fh.write('# Produced by: hd2mod equipment armor-normalize apply\n'
                 '#   source  = %s (one full model, two native ids)\n'
                 '#   armour  = %s (archive %s)\n'
                 '# The two native torso ids carry the whole model (Slim + Stocky,\n'
                 '# mutually exclusive at runtime) and the other %d units of the\n'
                 '# archive are hidden placeholders.  The game resolves every id\n'
                 '# itself, so no runtime mapping is required or wanted; this file\n'
                 '# is a record only and stays disabled on purpose.\n'
                 % (NATIVE_DIR, armor['displayName'], armor['archiveId'],
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
