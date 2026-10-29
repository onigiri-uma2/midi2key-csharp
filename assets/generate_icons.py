"""Render the original curved piano mark and optical-size tray variants with Pillow.

The SVG and ICO share the same contours. No downloaded artwork or fonts are used.
Run: python assets/generate_icons.py
"""
from pathlib import Path
from PIL import Image, ImageDraw
import struct, io

ROOT = Path(__file__).resolve().parent
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
# A grand-piano shoulder, waist and curved tail, rather than a rectangular app tile.
BODY = [('M', 3, 1.2), ('C', 6, -.1, 11.9, .7, 12.4, 3.8),
        ('C', 12.8, 6.2, 9.8, 6.3, 10.6, 8.2), ('C', 11, 9.2, 14.8, 8.3, 14.8, 10.7),
        ('L', 14.8, 13.8), ('C', 14.8, 14.7, 14.2, 15.1, 13.3, 15.1),
        ('L', 2.5, 15.1), ('C', 1.6, 15.1, 1.1, 14.6, 1.1, 13.8),
        ('L', 1.1, 4), ('C', 1.1, 2.6, 1.6, 1.8, 3, 1.2), ('Z',)]
RIM = [('M', 2, 9.5), ('L', 2, 4.1), ('C', 2, 2.8, 2.9, 2.1, 4.3, 1.9),
       ('C', 7.2, 1.4, 10.7, 2.2, 11.1, 3.8), ('C', 11.5, 5.4, 8.8, 6, 9.5, 8.1)]

def points(path):
    out=[]
    for op,*v in path:
        if op in ('M','L'): out.append(tuple(v))
        elif op=='C':
            x,y=out[-1]; a,b,c,d,e,f=v
            for i in range(1,33):
                t=i/32; u=1-t
                out.append((u**3*x+3*u*u*t*a+3*u*t*t*c+t**3*e,
                            u**3*y+3*u*u*t*b+3*u*t*t*d+t**3*f))
    return out

def svg_path(path):
    return ' '.join(op+' '+ ' '.join(str(n) for n in v) for op,*v in path)

def icon(size, state=None):
    n=size*8; k=n/16
    im=Image.new('RGBA',(n,n)); d=ImageDraw.Draw(im)
    def xy(p): return [(round(x*k),round(y*k)) for x,y in p]
    def box(v): return tuple(round(x*k) for x in v)
    def rect(v,fill,r=.2): d.rounded_rectangle(box(v),radius=round(r*k),fill=fill)
    mask=Image.new('L',(n,n)); ImageDraw.Draw(mask).polygon(xy(points(BODY)),fill=255)
    # Restrained enamel shading, with a bright rim that survives dark taskbars.
    grad=Image.new('RGBA',(n,n)); gd=ImageDraw.Draw(grad)
    for y in range(n):
        t=y/(n-1); color=tuple(round(a*(1-t)+b*t) for a,b in zip((37,139,161),(21,35,69)))+(255,)
        gd.line((0,y,n,y),fill=color)
    im.paste(grad,(0,0),mask)
    d=ImageDraw.Draw(im)
    contour=xy(points(BODY)); d.line(contour+[contour[0]],fill='#91c4cf',width=max(1,round(.16*k)))
    d.line(xy(points(RIM)),fill='#79d1d9',width=max(1,round(.22*k)))
    # Keyboard bed and individually weighted ivory keys; fewer keys at tray sizes.
    rect((1.9,9.4,14,14.4),'#0b1931',.45)
    count=5 if size<32 else 7
    pitch=11.5/count
    for i in range(count):
        left=2.2+i*pitch
        rect((left,9.8,left+pitch-.16,13.95),'#f4f3e9',.16)
        if size>=32: rect((left+.12,13.35,left+pitch-.27,13.8),'#c3d4d9',.08)
    black=(0,1,3) if count==5 else (0,1,3,4,5)
    for i in black:
        x=2.2+(i+1)*pitch-.48
        rect((x,9.6,x+.8,12.1),'#101e34',.12)
        if size>=32: rect((x+.13,9.85,x+.65,11.65),'#2a4056',.07)
    if state:
        colors={'stopped':'#627587','running':'#11865c','test':'#dca734','busy':'#237bc1','warning':'#c33f50'}
        d.ellipse(box((9.4,.15,15.85,6.6)),fill='#eef7f8')
        d.ellipse(box((9.95,.7,15.3,6.05)),fill=colors[state])
        if state=='stopped': rect((11.35,2.1,13.9,4.65),'#ffffff',.25)
        elif state=='running': d.polygon(xy([(11.8,1.8),(14.2,3.35),(11.8,4.9)]),fill='white')
        elif state=='test': d.polygon(xy([(12.6,1.5),(14.4,3.35),(12.6,5.2),(10.8,3.35)]),fill='#332a19')
        elif state=='busy': d.line(xy([(12.5,1.65),(12.5,3.5),(14,4.1)]),fill='white',width=round(.7*k))
        else:
            rect((12.2,1.6,13,3.7),'white'); rect((12.2,4.35,13,5.15),'white')
    return im.resize((size,size),Image.Resampling.LANCZOS)

def save_ico(path,state):
    images=[]
    for size in SIZES:
        data=io.BytesIO(); icon(size,state).save(data,format='PNG'); images.append(data.getvalue())
    offset=6+16*len(images)
    with path.open('wb') as f:
        f.write(struct.pack('<HHH',0,1,len(images)))
        for size,data in zip(SIZES,images):
            f.write(struct.pack('<BBBBHHII',size%256,size%256,0,0,1,32,len(data),offset)); offset+=len(data)
        for data in images: f.write(data)

def save_svg():
    keys=[]
    pitch=11.5/7
    for i in range(7):
        keys.append(f'<rect x="{2.2+i*pitch:.3f}" y="9.8" width="{pitch-.16:.3f}" height="4.15" rx=".16" fill="url(#ivory)"/>')
    for i in (0,1,3,4,5):
        keys.append(f'<rect x="{2.2+(i+1)*pitch-.48:.3f}" y="9.6" width=".8" height="2.5" rx=".12" fill="#101e34"/>')
    svg=f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">
  <defs>
    <linearGradient id="enamel" x2="0" y2="1"><stop stop-color="#258ba1"/><stop offset="1" stop-color="#152345"/></linearGradient>
    <linearGradient id="ivory" x2="0" y2="1"><stop stop-color="#fffdf3"/><stop offset=".85" stop-color="#f4f3e9"/><stop offset="1" stop-color="#c3d4d9"/></linearGradient>
  </defs>
  <path d="{svg_path(BODY)}" fill="url(#enamel)" stroke="#91c4cf" stroke-width=".16"/>
  <path d="{svg_path(RIM)}" fill="none" stroke="#79d1d9" stroke-width=".22"/>
  <rect x="1.9" y="9.4" width="12.1" height="5" rx=".45" fill="#0b1931"/>
  {''.join(keys)}
</svg>
'''
    (ROOT/'midi2key.svg').write_text(svg,encoding='utf-8')

if __name__=='__main__':
    save_svg()
    save_ico(ROOT/'midi2key.ico',None)
    for state in ('stopped','running','test','busy','warning'):
        save_ico(ROOT/f'tray-{state}.ico',state)
