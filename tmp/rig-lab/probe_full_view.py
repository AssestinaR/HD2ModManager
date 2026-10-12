"""Render Nick and Ellis with NO filtering at all, so the eye can decide.

usage: probe_full_view.py <tag>
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
import hd2_style as H

ADDONS = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons'
WHICH = {
    'nick': ('Lilium_Sancta_Nick.vpk', 'survivor_gambler'),
    'ellis': ('Serenade_Ellis.vpk', 'survivor_mechanic'),
    'zoey': ('Lunalice_Zoey.vpk', 'survivor_teenangst'),
}


def main():
    tag = sys.argv[1] if len(sys.argv) > 1 else 'ellis'
    vpk, model = WHICH[tag]
    _mp, _mdl, verts, tris, _src, _sh = H.load_source(
        os.path.join(ADDONS, vpk), model)
    src = [(v['pos'][0] * E.IN2M, v['pos'][1] * E.IN2M, v['pos'][2] * E.IN2M)
           for v in verts]
    out = 'out/full-%s.png' % tag
    E.render(out, src, src, tris,
             '%s SOURCE bind pose, NO triangle filtering at all (%d tris)'
             % (tag, len(tris)))
    print('[written] %s' % out)


if __name__ == '__main__':
    main()
