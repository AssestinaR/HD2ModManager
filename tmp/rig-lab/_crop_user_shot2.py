"""Read-only: very tight crops of the two forearms in the user's screenshot."""
import os

from PIL import Image

SRC = (r'C:\Users\Administrator\.workbuddy-ai\clipboard-images'
       r'\clipboard-2026-10-12T08-20-07-475Z-9a744f3c.jpg')
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'out')
im = Image.open(SRC).convert('RGB')
print('source', im.size)

BOXES = [
    ('uu-L-forearm', 120, 230, 400, 430),      # character's right forearm
    ('uu-R-forearm', 1330, 320, 1640, 560),    # character's left forearm
    ('uu-L-wrist', 40, 300, 260, 470),
    ('uu-R-wrist', 1480, 430, 1744, 660),
    ('uu-L-elbow', 330, 190, 520, 330),
    ('uu-R-elbow', 1180, 230, 1400, 400),
]
for name, x0, y0, x1, y1 in BOXES:
    x1 = min(x1, im.width)
    y1 = min(y1, im.height)
    c = im.crop((x0, y0, x1, y1))
    s = min(6.0, 1500.0 / max(c.width, 1))
    c = c.resize((int(c.width * s), int(c.height * s)), Image.LANCZOS)
    p = os.path.join(OUT, name + '.png')
    c.save(p)
    print('  -> %s %s' % (p, c.size))
