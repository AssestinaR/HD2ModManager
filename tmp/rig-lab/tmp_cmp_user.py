from PIL import Image, ImageDraw
OUT = r'E:/Data/source/repos/WpfApp1/tmp/rig-lab/out/uv-crops'
usr = Image.open(r'C:/Users/Administrator/.workbuddy-ai/clipboard-images/clipboard-2026-10-13T09-50-55-214Z-9ab8683a.png').convert('RGB')
raw = Image.open(OUT + r'\hair-crop-ours-raw.png').convert('RGB')
flp = Image.open(OUT + r'\hair-crop-ours-flip.png').convert('RGB')
CELL = 430
tiles = [('USER SCREENSHOT', usr), ('hair_ear island RAW (v .007-.182)', raw),
         ('hair_ear island V-FLIPPED (v .818-.993)', flp)]
sheet = Image.new('RGB', (CELL*3, CELL+26), (28,32,38))
dr = ImageDraw.Draw(sheet)
for k,(lab,im) in enumerate(tiles):
    th = im.copy(); th.thumbnail((CELL-6,CELL-6))
    sheet.paste(th, (k*CELL+3, 3))
    dr.text((k*CELL+4, CELL+6), lab, fill=(235,240,246))
p = OUT + r'\user-vs-islands.png'
sheet.save(p); print('->', p)
