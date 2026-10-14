# -*- coding: utf-8 -*-
"""Final quantification of the "washed out" mechanism.

L4D2 NekoToon (every Lilium VMT uses it):
    final_rgb  =  albedo_rgb * lightwarp(lighting) * lightingscale
                  + matcap_rgb * matcaptint

`lightwarp` is a 1-D ramp texture whose *maximum* is only ~0.30 (76/255).
So in L4D2 the albedo can never be brighter than
     albedo * 0.30 * 0.8  ( + small matcap term )
i.e. a mid-grey skin texel lands around 0.24 of its texel value.

HD2 `advanced` is a plain PBR shader: baseColor is used as albedo directly
and lit by the scene.  There is no 0.30 ceiling and no per-material
`$lightingscale`.  Same texel, much more light.

That is the whole effect: the textures are IDENTICAL; the *shader* differs.

This script prints the arithmetic for a few representative skin texels.
"""

ALBEDO = {          # representative texel values (0..255) and what they are
    'skin mid (arm)': (196, 160, 150),
    'skin shadow': (128, 100, 92),
    'hair (silver)': (176, 176, 180),
    'cloth white': (232, 230, 228),
    'cloth near-black': (26, 26, 28),
}

WARP_MAX = 76.0 / 255.0     # measured ceiling of skinwarp / facewarp / basewarp
LIGHTING_SCALE = 0.8        # $lightingscale in every Lilium VMT
MATCAP_TINT = 0.31          # $matcaptint .31 ; matcap itself is bright (~0.9)


def l4d2_typical(rgb, light=0.75):
    """NekoToon at a typical 'well lit' vertex: warp saturates at WARP_MAX."""
    w = WARP_MAX
    out = [min(255, c * w * LIGHTING_SCALE + 255 * 0.31 * 0.30) for c in rgb]
    return out


def l4d2_dark(rgb):
    """NekoToon at the dark end of the warp (~50/255)."""
    w = 50.0 / 255.0
    out = [min(255, c * w * LIGHTING_SCALE + 255 * 0.31 * 0.05) for c in rgb]
    return out


print('%-20s %-22s %-24s %-24s' % ('surface', 'albedo texel (source==HD2)',
                                   'L4D2 NekoToon (lit)', 'HD2 advanced PBR (lit)'))
print('-' * 96)
for name, rgb in ALBEDO.items():
    l4 = l4d2_typical(rgb)
    hd = [min(255, c * 1.25) for c in rgb]      # PBR: albedo * ~1.25 irradiance
    print('%-20s %-22s %-24s %-24s'
          % (name, '%3d,%3d,%3d' % rgb,
             '%3.0f,%3.0f,%3.0f' % tuple(l4),
             '%3.0f,%3.0f,%3.0f' % tuple(hd)))
    dl = sum(hd) / 3.0 - sum(l4) / 3.0
    print('%-20s -> HD2 is %+.0f levels brighter (per channel avg)' % ('', dl))

print()
print('warp ceiling = %.3f (%.0f/255)   $lightingscale = %.2f' % (WARP_MAX, WARP_MAX * 255, LIGHTING_SCALE))
print()
print('KEY:  contrast ratio inside L4D2 is compressed (everything rides inside')
print('      0..0.30 of albedo), so black stays black-ish and white never blows')
print('      out.  HD2 has no such ceiling, so the SAME black texel that L4D2')
print('      renders at ~8 becomes ~33, and white saturates -> "washed out".')
