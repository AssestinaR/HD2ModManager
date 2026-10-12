"""Read-only: pull the arm's silhouette out of the user's screenshot.

Threshold the bright mesh against the dark viewport, then walk the columns of the
RIGHT-hand arm (the part that sits against the background) and record the top and
bottom edge of the arm.  The centreline then says, in pixels, whether the forearm
is a straight line from elbow to wrist or has a bend in it.

Nothing is modified -- this only reads the JPEG.
"""
import os
import sys

import numpy as np
from PIL import Image

SRC = (r'C:\Users\Administrator\.workbuddy-ai\clipboard-images'
       r'\clipboard-2026-10-12T08-20-07-475Z-9a744f3c.jpg')

im = Image.open(SRC).convert('RGB')
W, H = im.size
A = np.asarray(im, dtype=np.float32)
bright = A.mean(2) > 120          # mesh is ~200, background ~60
print('image %dx%d   bright fraction %.3f' % (W, H, bright.mean()))


def scan(x0, x1, name):
    print('\n=== %s : columns %d..%d ===' % (name, x0, x1))
    print('%6s %8s %8s %8s %8s %8s' %
          ('x', 'top', 'bot', 'mid', 'thick', 'slope'))
    prev = None
    rows = []
    for x in range(x0, x1):
        col = bright[:, x]
        idx = np.where(col)[0]
        if len(idx) == 0:
            continue
        # keep only the LOWEST contiguous run (the arm hangs below the body)
        runs = []
        s = idx[0]
        p = idx[0]
        for k in idx[1:]:
            if k != p + 1:
                runs.append((s, p))
                s = k
            p = k
        runs.append((s, p))
        top, bot = runs[-1]
        if bot - top < 8:
            continue
        mid = 0.5 * (top + bot)
        rows.append((x, top, bot, mid))
    for i, (x, top, bot, mid) in enumerate(rows):
        if i % 12:
            continue
        slope = ''
        if i + 12 < len(rows):
            dx = rows[i + 12][0] - x
            dy = rows[i + 12][3] - mid
            slope = '%+.3f' % (dy / dx)
        print('%6d %8d %8d %8.1f %8d %8s'
              % (x, top, bot, mid, bot - top, slope))
    return rows


scan(1150, 1744, 'image-RIGHT arm (character left)')
scan(0, 470, 'image-LEFT arm (character right)')
