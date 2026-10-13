# -*- coding: utf-8 -*-
"""Verify the fixed Material texture table against the SDK parser.

Authority = SDK `stingray/material.py` (loaded by file, no Blender).
Negative control = `_legacy_matwriter.py` (frozen copy of the pre-fix code).

Checks
  [1] every SDK template round-trips byte-identically through A.Material
  [2] slot keys land exactly where the SDK puts them (136 + 4*i)
  [3] texture ids land exactly where the SDK puts them (136 + 4*N + 8*i)
  [4] replacing all N ids with distinct sentinels survives an SDK read
  [5] the frozen legacy writer is REJECTED by the SDK (negative control)
  [6] every material in the deployed patch resolves inside the patch

Usage:
    python _probe_matfix.py [patch_0]
"""
import importlib.util
import json
import os
import struct
import sys
import types
from pathlib import Path

import hd2_patch as P
import hd2_asset as A
import _legacy_matwriter as L

ROOT = Path(__file__).resolve().parent
SDK = ROOT.parents[1] / 'docs/HD2SDK-CommunityEdition-3.8.0'
DEFAULT_PATCH = 'E:/mod/Lunalice-RE2310-Test/9ba626afa44a3aa3.patch_0'
OUT = ROOT / 'out/matfix-verification.json'

TEMPLATES = ['basic.material', 'basic+.material', 'advanced.material',
             'original.material', 'armorlut.material']

# Distinct sentinels so a shifted read cannot accidentally look right.
SENTINELS = [0x1111111111111111, 0x2222222222222222, 0x3333333333333333,
             0x4444444444444444, 0x5555555555555555, 0x6666666666666666,
             0x7777777777777777, 0x8888888888888888, 0x9999999999999999,
             0xAAAAAAAAAAAAAAAA, 0xBBBBBBBBBBBBBBBB, 0xCCCCCCCCCCCCCCCC,
             0xDDDDDDDDDDDDDDDD]


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    m = importlib.util.module_from_spec(spec)
    sys.modules[name] = m
    spec.loader.exec_module(m)
    return m


for pkgname in ('mfsdk', 'mfsdk.utils', 'mfsdk.stingray'):
    pkg = types.ModuleType(pkgname)
    pkg.__path__ = []
    sys.modules[pkgname] = pkg
MS = module('mfsdk.utils.memoryStream', SDK / 'utils/memoryStream.py')
SM = module('mfsdk.stingray.material', SDK / 'stingray/material.py')


def sdkread(raw):
    m = SM.StingrayMaterial()
    m.Serialize(MS.MemoryStream(bytes(raw)))
    return m


def raw_keys(raw, n):
    return [struct.unpack_from('<I', raw, 136 + 4 * i)[0] for i in range(n)]


def raw_ids(raw, n):
    return [struct.unpack_from('<Q', raw, 136 + 4 * n + 8 * i)[0] for i in range(n)]


fails = []


def check(label, ok, detail=''):
    print('  [%s] %-62s %s' % ('PASS' if ok else 'FAIL', label, detail))
    if not ok:
        fails.append(label)


def main():
    patch = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_PATCH
    report = dict(patch=patch, templates=[], closure=[], negatives=[])

    print('== [1/2/3] SDK template round-trip + slot/id placement ==')
    for name in TEMPLATES:
        p = SDK / 'materials' / name
        if not p.exists():
            check('template %s exists' % name, False)
            continue
        base = p.read_bytes()
        m = A.Material.read(base)
        out = m.write()
        n = len(m.tex_ids)
        sdk = sdkread(out)
        check('%-20s read/write byte-identical' % name, out == base,
              '%d bytes, ntex=%d nvar=%d' % (len(base), n, len(m.variables)))
        check('%-20s slot keys == raw u32 array' % name, list(m.tex_unks) == raw_keys(base, n))
        check('%-20s ids == raw u64 array' % name, list(m.tex_ids) == raw_ids(base, n))
        check('%-20s SDK agrees on keys' % name, list(sdk.TexUnks) == list(m.tex_unks))
        check('%-20s SDK agrees on ids' % name, list(sdk.TexIDs) == list(m.tex_ids))
        report['templates'].append(dict(name=name, size=len(base), ntex=n,
                                        nvar=len(m.variables),
                                        keys=['%08x' % k for k in m.tex_unks],
                                        ids=['%016x' % i for i in m.tex_ids]))

    print('== [4] replace all ids with distinct sentinels ==')
    for name in TEMPLATES:
        p = SDK / 'materials' / name
        if not p.exists():
            continue
        base = p.read_bytes()
        m = A.Material.read(base)
        n = len(m.tex_ids)
        want = SENTINELS[:n]
        for i, tid in enumerate(want):
            m.set_texture(i, tid)
        out = m.write()
        sdk = sdkread(out)
        check('%-20s SDK reads back every sentinel' % name, list(sdk.TexIDs) == want)
        check('%-20s slot keys unchanged' % name, list(sdk.TexUnks) == list(m.tex_unks))
        check('%-20s ids at 136+4N+8i' % name, raw_ids(out, n) == want)
        check('%-20s keys at 136+4i' % name, raw_keys(out, n) == list(m.tex_unks))
        # header + variable table untouched
        check('%-20s prefix/suffix untouched' % name,
              out[:136] == base[:136] and out[136 + 12 * n:] == base[136 + 12 * n:])

    print('== [5] frozen legacy writer must be REJECTED ==')
    base = (SDK / 'materials' / 'basic.material').read_bytes()
    n = 3
    want = SENTINELS[:n]
    lm = L.LegacyMaterial.read(base)
    for i, tid in enumerate(want):
        lm.set_texture(i, tid)
    legacy_out = lm.write()
    bad = sdkread(legacy_out)
    check('legacy writer: SDK sees wrong slot keys', list(bad.TexUnks) != raw_keys(base, n),
          '%s vs %s' % (['%08x' % k for k in bad.TexUnks],
                        ['%08x' % k for k in raw_keys(base, n)]))
    check('legacy writer: SDK sees wrong ids', list(bad.TexIDs) != want,
          '%s' % ['%016x' % i for i in bad.TexIDs])
    check('legacy writer: last id accidentally correct (documented)', bad.TexIDs[-1] == want[-1])
    check('legacy writer: size unchanged (why the bug hid)', len(legacy_out) == len(base),
          '%d == %d' % (len(legacy_out), len(base)))
    # the fixed writer, same input, must pass
    fm = A.Material.read(base)
    for i, tid in enumerate(want):
        fm.set_texture(i, tid)
    good = sdkread(fm.write())
    check('fixed writer: SDK sees every sentinel', list(good.TexIDs) == want)
    report['negatives'].append(dict(legacy_keys=['%08x' % k for k in bad.TexUnks],
                                    legacy_ids=['%016x' % i for i in bad.TexIDs],
                                    legacy_rejected=True, fixed_accepted=True))

    print('== [6] deployed package: material -> texture closure ==')
    if not os.path.exists(patch):
        check('deployed patch exists', False, patch)
    else:
        entries, _raw = P.scan_entries(patch)
        textures = {e['file_id'] for e in entries if e['type_id'] == A.TEXTURE_TYPE_ID}
        bad_refs = 0
        mats = 0
        for e in entries:
            if e['type_id'] != A.MATERIAL_TYPE_ID:
                continue
            mats += 1
            toc = P.entry_payload(e, patch)['toc']
            sdk = sdkread(toc)
            ours = A.Material.read(toc)
            miss = [i for i in sdk.TexIDs if i not in textures]
            bad_refs += len(miss)
            report['closure'].append(dict(
                id='%016x' % e['file_id'], parent=ours.parent_name,
                ntex=len(ours.tex_ids), nvar=len(ours.variables),
                keys_match=(list(sdk.TexUnks) == list(ours.tex_unks)),
                ids_match=(list(sdk.TexIDs) == list(ours.tex_ids)),
                unresolved=['%016x' % i for i in miss]))
        check('every material read agrees with SDK', all(
            c['keys_match'] and c['ids_match'] for c in report['closure']),
            '%d materials' % mats)
        check('every texture reference resolves in the package', bad_refs == 0,
              '%d materials, %d textures, %d unresolved' % (mats, len(textures), bad_refs))

    print()
    print('RESULT: %d checks failed' % len(fails))
    for f in fails:
        print('  FAILED:', f)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print('REPORT', OUT)
    return 1 if fails else 0


if __name__ == '__main__':
    sys.exit(main())
