# -*- coding: utf-8 -*-
"""Deploy a candidate package into the game's `data/` via the two symlinks.

The game resolves `9ba626afa44a3aa3.patch_0[.gpu_resources]` from its own
`data/` directory.  We never overwrite those files; we point the symlinks at a
mod directory under `E:/mod/`, exactly as round 59 did, and write a revert note
so the previous target can always be restored.

Safety
  * refuses to run while Helldivers2.exe or the manager is running
  * refuses to run if the existing target is not a symlink (would clobber a
    real file)
  * always writes the previous target to the revert note BEFORE switching

Usage:
    python deploy_candidate.py --cand E:/tmp/rig-lab/cand-m1 \
        --mod-dir E:/mod/Lunalice-RE2310-M1 --name lunalice-re2310-m1
"""
import argparse
import json
import os
import shutil
import subprocess
import sys

PATCH_NAME = '9ba626afa44a3aa3.patch_0'
GAME_DATA = r'D:\SteamLibrary\steamapps\common\Helldivers 2\data'
REVERT_NOTE = r'E:\tmp\deploy-revert-note.txt'
# Blocking: the game may hold the package open while we swap the link.
GAME_BUSY = ('Helldivers2.exe',)
# Advisory only.  `HD2ModCore/Infrastructure/ApplyExecutor.cs` is the *only*
# place the manager creates symlinks, and it runs on an explicit Apply/Deploy --
# never on start-up or shutdown.  So a running manager does not fight this
# script; but the user must not press Apply while a candidate is being tested,
# because that would relink the package back to the manager's own library node.
MANAGER_BUSY = ('HD2ModManager.exe', 'hd2mod.exe')


def running(names):
    try:
        out = subprocess.run(['tasklist', '/FO', 'CSV', '/NH'],
                             capture_output=True, text=True).stdout
    except OSError:
        return []
    low = out.lower()
    return [p for p in names if p.lower() in low]


def log(m=''):
    print(m, flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--cand', required=True)
    ap.add_argument('--mod-dir', required=True)
    ap.add_argument('--name', required=True)
    ap.add_argument('--game-data', default=GAME_DATA)
    ap.add_argument('--protocol-from', default=None,
                    help='copy this .protocol as a record (default: the '
                         'deployed one, if present)')
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    game = running(GAME_BUSY)
    if game:
        log('refusing to deploy: still running -> %s' % ', '.join(game))
        return 2
    mgr = running(MANAGER_BUSY)
    if mgr:
        log('note: %s is running. That is fine (it only relinks on an explicit '
            'Apply), but do NOT press Apply while this candidate is deployed -- '
            'that would relink the package back to the manager\'s own node.'
            % ', '.join(mgr))

    src = os.path.join(args.cand, PATCH_NAME)
    src_gpu = src + '.gpu_resources'
    for p in (src, src_gpu):
        if not os.path.isfile(p):
            log('missing %s' % p)
            return 2

    links = [os.path.join(args.game_data, PATCH_NAME),
             os.path.join(args.game_data, PATCH_NAME + '.gpu_resources')]
    old = []
    for l in links:
        if os.path.islink(l):
            old.append(os.readlink(l))
        elif os.path.exists(l):
            log('refusing: %s is a real file, not a symlink' % l)
            return 2
        else:
            old.append(None)

    log('=' * 78)
    log('deploy %s' % args.name)
    log('  patch   %d B' % os.path.getsize(src))
    log('  gpu     %d B' % os.path.getsize(src_gpu))
    log('  previous targets:')
    for l, o in zip(links, old):
        log('    %s -> %s' % (os.path.basename(l), o))
    if args.dry_run:
        log('  dry run: nothing written')
        return 0

    os.makedirs(args.mod_dir, exist_ok=True)
    for f in os.listdir(args.mod_dir):
        if f.startswith(PATCH_NAME):
            os.remove(os.path.join(args.mod_dir, f))
    shutil.copy2(src, os.path.join(args.mod_dir, PATCH_NAME))
    shutil.copy2(src_gpu, os.path.join(args.mod_dir, PATCH_NAME + '.gpu_resources'))

    proto_src = args.protocol_from
    if proto_src is None:
        guess = os.path.join(os.path.dirname(old[0] or ''), 'lunalice-re2310-test.protocol')
        proto_src = guess if os.path.isfile(guess) else None
    if proto_src and os.path.isfile(proto_src):
        shutil.copy2(proto_src, os.path.join(args.mod_dir, args.name + '.protocol'))

    # merge the material-fix manifest with the deployment record
    fixman = None
    for f in sorted(os.listdir(args.cand)):
        if f.endswith('.manifest.json'):
            fixman = json.load(open(os.path.join(args.cand, f), encoding='utf-8'))
    deploy = dict(schema='hd2mod.rig-lab-deploy', name=args.name,
                  candidate=args.cand, modDir=args.mod_dir,
                  gameData=args.game_data,
                  patchBytes=os.path.getsize(src),
                  gpuBytes=os.path.getsize(src_gpu),
                  previousTargets=old, materialFix=fixman)
    with open(os.path.join(args.mod_dir, args.name + '.deploy.json'), 'w',
              encoding='utf-8') as f:
        json.dump(deploy, f, indent=2, ensure_ascii=False)

    with open(REVERT_NOTE, 'w', encoding='utf-8', newline='\n') as f:
        f.write('REVERT (previous targets before %s):\n' % args.name)
        for l, o in zip(links, old):
            f.write('  %s\n      -> %s\n' % (l, o))
        f.write('\nTo restore, point both symlinks back at those targets.\n')

    for l, name in zip(links, (PATCH_NAME, PATCH_NAME + '.gpu_resources')):
        target = os.path.join(args.mod_dir, name)
        if os.path.islink(l) or os.path.exists(l):
            os.remove(l)
        os.symlink(target, l)
        real = os.stat(l)
        log('  %-28s -> %s  (%d B)' % (name, target, real.st_size))
    log('  revert note: %s' % REVERT_NOTE)
    return 0


if __name__ == '__main__':
    sys.exit(main())
