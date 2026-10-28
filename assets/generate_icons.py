"""Generate midi2key's code-drawn, multi-resolution Windows icons (requires Pillow)."""
from pathlib import Path
from PIL import Image, ImageDraw
import struct, io

ROOT = Path(__file__).resolve().parent
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)

def icon(size, state=None):
    scale = 8
    image = Image.new('RGBA', (size * scale, size * scale))
    draw = ImageDraw.Draw(image)
    k = size * scale / 16
    def box(v): return tuple(round(x*k) for x in v)
    def rect(v, fill, radius=0):
        draw.rounded_rectangle(box(v), radius=round(radius*k), fill=fill)
    def poly(v, fill): draw.polygon([(round(x*k),round(y*k)) for x,y in v], fill=fill)
    # Strong silhouette, four white keys, two black keys: legible at native tray size.
    rect((.5,.5,15.5,15.5), '#3348a8', 3)
    rect((1,1,15,3.8), '#5275e3', 2)
    for left in (2.5,5.5,8.5,11.5):
        rect((left,5,left+2,13.5), '#ffffff', .35)
    for left in (4.1,7.1): rect((left,4.8,left+1.65,9.5), '#16234b', .2)
    if state:
        colors = {'stopped':'#64748b','running':'#13854b','test':'#f5b734','busy':'#2375cf','warning':'#d33f42'}
        draw.ellipse(box((8.2,.1,15.9,7.8)), fill='#ffffff')
        draw.ellipse(box((9,.9,15.1,7)), fill=colors[state])
        if state == 'stopped': rect((10.7,2.6,13.4,5.3), '#ffffff', .2)
        elif state == 'running': poly([(11,2.3),(13.8,4),(11,5.7)],'#ffffff')
        elif state == 'test': poly([(12.1,2),(14,4),(12.1,6),(10.2,4)],'#382b0b')
        elif state == 'busy':
            draw.line([(round(12*k),round(2.1*k)),(round(12*k),round(4.1*k)),(round(13.5*k),round(4.7*k))], fill='#ffffff', width=max(1,round(.8*k)))
        elif state == 'warning':
            rect((11.7,2,12.6,4.3),'#ffffff',.2)
            rect((11.7,5,12.6,5.9),'#ffffff',.2)
    return image.resize((size,size), Image.Resampling.LANCZOS)

def save_ico(path, state):
    images=[]
    for size in SIZES:
        data=io.BytesIO(); icon(size,state).save(data,format='PNG'); images.append(data.getvalue())
    offset=6+16*len(images)
    with path.open('wb') as f:
        f.write(struct.pack('<HHH',0,1,len(images)))
        for size,data in zip(SIZES,images):
            f.write(struct.pack('<BBBBHHII',size%256,size%256,0,0,1,32,len(data),offset)); offset+=len(data)
        for data in images: f.write(data)

if __name__ == '__main__':
    save_ico(ROOT/'midi2key.ico',None)
    for state in ('stopped','running','test','busy','warning'):
        save_ico(ROOT/f'tray-{state}.ico',state)
