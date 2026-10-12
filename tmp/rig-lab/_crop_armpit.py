import os
from PIL import Image
SRC="out/armpit.png"; im=Image.open(SRC); print("size",im.size)
def c(name,box,zoom=3):
    x0,y0,x1,y1=box
    q=im.crop(box).resize(((x1-x0)*zoom,(y1-y0)*zoom),Image.LANCZOS)
    p="out/"+name+".png"; q.save(p); print("wrote",p,q.size)
# 行1 = 正视(俯视/正视) ; 列3 = 我们, 列4 = 参照
c("g-ours-front",(860,40,1270,520),3)
c("g-ref-front",(1380,40,1988,520),3)
