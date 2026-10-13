from PIL import Image
OUT = r'E:/Data/source/repos/WpfApp1/tmp/rig-lab/out/uv-crops'
box = (200, 20, 500, 320)
outs = []
for tag in ('ours-verbatim', 'ours-flipped'):
    im = Image.open(OUT + '\\' + tag + '.png').crop(box)
    im = im.resize((im.width*2, im.height*2), Image.LANCZOS)
    outs.append(im)
sheet = Image.new('RGB', (outs[0].width*2+8, outs[0].height), (28,32,38))
sheet.paste(outs[0], (0,0)); sheet.paste(outs[1], (outs[0].width+8,0))
p = OUT + r'\ours-head-both.png'
sheet.save(p)
print('->', p)
