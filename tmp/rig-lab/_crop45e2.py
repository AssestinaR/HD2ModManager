import os
from PIL import Image, ImageDraw
SRC = r"C:/Users/Administrator/.workbuddy-ai/clipboard-images/clipboard-2026-10-12T08-20-07-475Z-9a744f3c.jpg"
im = Image.open(SRC); W,H = im.size
out="out"
def crop_grid(name, box, zoom=3, step=25):
    x0,y0,x1,y1 = box
    c = im.crop(box).resize(((x1-x0)*zoom,(y1-y0)*zoom), Image.LANCZOS)
    d = ImageDraw.Draw(c)
    gx=(x0//step)*step
    while gx<=x1:
        X=(gx-x0)*zoom
        if X>=0: d.line([(X,0),(X,c.height)],fill=(255,80,80),width=1); d.text((X+2,2),str(gx),fill=(255,170,170))
        gx+=step
    gy=(y0//step)*step
    while gy<=y1:
        Y=(gy-y0)*zoom
        if Y>=0: d.line([(0,Y),(c.width,Y)],fill=(80,200,255),width=1); d.text((2,Y+2),str(gy),fill=(170,220,255))
        gy+=step
    p=os.path.join(out,name+".png"); c.save(p); print("wrote",p,c.size)
crop_grid("f-elbow-L",(200,250,560,520))
crop_grid("f-elbow-R",(1250,250,1610,520))
crop_grid("f-hand-L",(20,400,320,660))
crop_grid("f-hand-R",(1500,400,1744,680))
