from PIL import Image
im=Image.open("out/armpit.png")
def c(name,box,zoom=2):
    x0,y0,x1,y1=box
    q=im.crop(box).resize(((x1-x0)*zoom,(y1-y0)*zoom),Image.LANCZOS)
    p="out/"+name+".png"; q.save(p); print("wrote",p,q.size)
c("h-ours-row1",(999,62,1479,531),2)
c("h-ref-row1",(1504,62,1973,531),2)
