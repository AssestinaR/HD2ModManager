import sys
from PIL import Image
OUT = r'E:/Data/source/repos/WpfApp1/tmp/rig-lab/out/uv-crops'
a = Image.open(OUT + r'\texrender-verbatim.png')
b = Image.open(OUT + r'\texrender-flipped.png')
box = (210, 10, 440, 250)
ca = a.crop(box).resize((230*3, 240*3), Image.LANCZOS)
cb = b.crop(box).resize((230*3, 240*3), Image.LANCZOS)
sheet = Image.new('RGB', (ca.width*2+8, ca.height), (28,32,38))
sheet.paste(ca, (0,0)); sheet.paste(cb, (ca.width+8,0))
p = OUT + r'\texrender-head-both.png'
sheet.save(p)
print('->', p)
