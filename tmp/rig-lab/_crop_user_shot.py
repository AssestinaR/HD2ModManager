"""Read-only: crop the user's screenshot so the arms can be inspected closely.
Touches nothing else -- no pipeline, no model."""
import os
import sys

from PIL import Image

SRC = (r'C:\Users\Administrator\.workbuddy-ai\clipboard-images'
       r'\clipboard-2026-10-12T08-20-07-475Z-9a744f3c.jpg')
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'out')

im = Image.open(SRC).convert('RGB')
W, H = im.size
print('source %dx%d' % (W, H))

# (name, x0f, y0f, x1f, y1f) in fractions of W/H, then upscale
BOXES = [
    ('ua-left-arm', 0.00, 0.05, 0.45, 0.95),
    ('ua-right-arm', 0.55, 0.05, 1.00, 0.95),
    ('ua-left-forearm', 0.02, 0.25, 0.30, 0.95),
    ('ua-right-forearm', 0.70, 0.25, 0.98, 0.95),
    ('ua-left-hand', 0.00, 0.45, 0.20, 0.95),
    ('ua-right-hand', 0.80, 0.45, 1.00, 0.95),
]
for name, x0f, y0f, x1f, y1f in BOXES:
    box = (int(W * x0f), int(H * y0f), int(W * x1f), int(H * y1f))
    c = im.crop(box)
    s = min(4.0, 1400.0 / max(c.width, 1))
    c = c.resize((int(c.width * s), int(c.height * s)), Image.LANCZOS)
    p = os.path.join(OUT, name + '.png')
    c.save(p)
    print('  -> %s %s (from %s)' % (p, c.size, box))
