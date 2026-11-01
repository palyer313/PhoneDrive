"""PhoneDrive three-colour vector artwork and platform exports (Pillow required)."""
from pathlib import Path
from PIL import Image, ImageDraw
import shutil

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'assets/brand'
OUT.mkdir(parents=True, exist_ok=True)
BLUE, WHITE, BLACK = '#087CFF', '#FFFFFF', '#0B0D12'
# Vector reconstruction of the approved abstract P concept. Two linked shapes,
# one open negative-space channel; shared master for every platform resource.
paths = [
    (BLUE, 'M207 92 Q174 92 174 125 V166 Q174 173 183 175 L302 204 Q328 210 328 231 V287 Q328 299 342 297 Q420 288 420 235 V183 Q420 132 380 123 L210 93 Q208 92 207 92 Z'),
    (BLUE, 'M174 184 Q174 179 166 179 Q90 183 90 252 V399 Q90 420 110 420 Q119 420 127 413 L160 385 Q175 374 194 374 H275 Q328 374 328 322 V304 Q328 295 319 293 L198 265 Q174 259 174 241 V184 Z'),
]
svg_glyph='\n'.join(f'<path fill="{colour}" d="{path}"/>' for colour,path in paths)
(OUT/'phonedrive.svg').write_text(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512"><rect x="16" y="16" width="480" height="480" rx="104" fill="{WHITE}"/>{svg_glyph}</svg>',encoding='utf-8')

# Rasterize those exact quadratic contours, rather than approximating them with unrelated shapes.
def contour(path):
    import re
    tokens=re.findall(r'[MLHVQZ]|-?\d+(?:\.\d+)?',path)
    i=0; points=[]; current=(0,0)
    while i<len(tokens):
        command=tokens[i];i+=1
        if command in ('M','L'):
            current=(float(tokens[i]),float(tokens[i+1]));i+=2;points.append(current)
        elif command=='H':current=(float(tokens[i]),current[1]);i+=1;points.append(current)
        elif command=='V':current=(current[0],float(tokens[i]));i+=1;points.append(current)
        elif command=='Q':
            control=(float(tokens[i]),float(tokens[i+1]));end=(float(tokens[i+2]),float(tokens[i+3]));i+=4
            start=current
            for j in range(1,33):
                t=j/32;points.append(((1-t)**2*start[0]+2*(1-t)*t*control[0]+t*t*end[0],(1-t)**2*start[1]+2*(1-t)*t*control[1]+t*t*end[1]))
            current=end
        elif command=='Z':break
        else:raise ValueError(command)
    return points

S=4
image=Image.new('RGBA',(512*S,512*S))
d=ImageDraw.Draw(image)
d.rounded_rectangle((16*S,16*S,496*S,496*S),104*S,fill=WHITE)
for colour,path in paths:d.polygon([(round(x*S),round(y*S)) for x,y in contour(path)],fill=colour)
image=image.resize((512,512),Image.Resampling.LANCZOS)
image.save(OUT/'phonedrive.png')
image.resize((256,256),Image.Resampling.LANCZOS).save(OUT/'phonedrive-brand-preview.png')
image.save(OUT/'phonedrive.ico',sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(128,128),(256,256)])
for ext in ['png','ico']:shutil.copy2(OUT/f'phonedrive.{ext}',ROOT/f'windows/PhoneDrive-Windows/src/PhoneDrive.Wpf/Assets/PhoneDrive.{ext}')

res=ROOT/'android/PhoneDrive-Android/app/src/main/res'
def vector(items):
    glyph=''.join(f'<path android:fillColor="{colour}" android:pathData="{path}"/>' for colour,path in items)
    return '<vector xmlns:android="http://schemas.android.com/apk/res/android" android:width="108dp" android:height="108dp" android:viewportWidth="512" android:viewportHeight="512">'+glyph+'</vector>'
foreground=vector(paths).replace('><path', '><group android:scaleX="0.72" android:scaleY="0.72" android:translateX="71.68" android:translateY="71.68"><path', 1).replace('</vector>', '</group></vector>')
legacy=vector([(WHITE,'M0 0 H512 V512 H0 Z'),*paths])
(res/'drawable/ic_launcher_foreground.xml').write_text(foreground,encoding='utf-8')
(res/'drawable/ic_launcher.xml').write_text(legacy,encoding='utf-8')
(res/'drawable/ic_launcher_brand.xml').write_text(legacy,encoding='utf-8')
(res/'mipmap-anydpi/ic_launcher.xml').write_text(legacy,encoding='utf-8')
(res/'values/brand.xml').write_text(f'<resources><color name="launcher_background">{WHITE}</color></resources>',encoding='utf-8')
# Status-bar icons must be monochrome; the shape remains the same brand mark.
notification=[(WHITE,path) for colour,path in paths if colour==BLUE]
(res/'drawable/ic_notification.xml').write_text(vector(notification),encoding='utf-8')
print('Approved abstract P artwork exported to SVG/PNG/ICO and Android launcher/notification resources.')
