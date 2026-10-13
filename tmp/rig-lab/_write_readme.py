# -*- coding: utf-8 -*-
import glob, json, os, hashlib
MOD = r'E:/mod'
for folder in sorted(glob.glob(os.path.join(MOD, 'Lunalice-RE2310-MAT-*'))):
    f = glob.glob(os.path.join(folder, '*.manifest.json'))
    if not f:
        continue
    m = json.load(open(f[0], encoding='utf-8'))
    rows = [g for g in m['groups'] if g.get('changed')]
    L = []
    L.append('Lunalice -> HD2 材质包：%s' % m['label'])
    L.append('=' * 62)
    L.append('来源：八人包 %s  (%d 字节)' % (m['sourceVpk'], m['sourceVpkBytes']))
    L.append('主体：%s' % m['mainPatch'])
    L.append('')
    L.append('用法')
    L.append('  · 和主体包 Lunalice-RE2310-M8 一起启用即可。')
    L.append('  · HD2 的覆盖规则：patch 序号大的后加载，覆盖同 asset key 的资源。')
    L.append('    让本包序号 > 主体包序号，本包的贴图就会盖掉主体包里的同名资源。')
    L.append('  · 同一族（服装色 / 金属装饰品 / 发色 / 瞳色）一次只启用一个。')
    L.append('  · 关掉本包就回到主体包的默认外观。')
    L.append('')
    L.append('本包只改了这些贴图（材质 id / 贴图 id 与主体包完全相同）：')
    for g in rows:
        L.append('  · %-30s [%s] %s' % (g['group'], g['template'], ' '.join(g['textures'])))
    L.append('')
    L.append('没有改动的部分：Unit、网格、骨骼、权重、其余材质与贴图 —— 全部沿用主体包。')
    L.append('')
    L.append('数值')
    L.append('  entries        %d' % m['entryCount'])
    L.append('  toc            %d 字节  sha256 %s' % (m['tocBytes'], m['tocSha256']))
    L.append('  gpu_resources  %d 字节  sha256 %s' % (m['gpuBytes'], m['gpuSha256']))
    L.append('')
    L.append('preview/compare.png 里左半是主体包的现状，右半是本包的贴图。')
    open(os.path.join(folder, 'README.txt'), 'w', encoding='utf-8').write('\n'.join(L) + '\n')
    print('%-46s %s' % (os.path.basename(folder), 'README.txt'))
