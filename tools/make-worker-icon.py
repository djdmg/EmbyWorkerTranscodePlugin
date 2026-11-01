# Draws the worker's application icon (taskbar, shortcut, installed apps, setup): a green disc with a white "play"
# triangle split into slices, the distributed encoding. Output: src/RemoteTranscode.Worker/Assets/worker.ico.
#   python tools/make-worker-icon.py
from PIL import Image, ImageDraw
import os

S = 1024
img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.ellipse((24, 24, S - 24, S - 24), fill=(82, 181, 75, 255))           # Emby green
d.ellipse((24, 24, S - 24, S - 24), outline=(255, 255, 255, 255), width=44)
# Play triangle, cut into three vertical slices with gaps.
left, right, top, bottom = 360, 780, 250, 774
mid = (top + bottom) / 2
gap = 34
edges = [left, left + (right - left) / 3, left + 2 * (right - left) / 3, right]
def y_at(x):  # half-height of the triangle at x
    return (bottom - top) / 2 * (right - x) / (right - left)
for i in range(3):
    x0 = edges[i] + (gap / 2 if i else 0)
    x1 = edges[i + 1] - (gap / 2 if i < 2 else 0)
    d.polygon([(x0, mid - y_at(x0)), (x1, mid - y_at(x1)), (x1, mid + y_at(x1)), (x0, mid + y_at(x0))], fill=(255, 255, 255, 255))
out = os.path.join(os.path.dirname(__file__), '..', 'src', 'RemoteTranscode.Worker', 'Assets', 'worker.ico')
img.save(out, sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
img.resize((256, 256), Image.LANCZOS).save(out.replace('.ico', '-preview.png'))
print(os.path.abspath(out))
