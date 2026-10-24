# アプリのアイコンを描く（素材ゼロ・Pillow のみ）。python tools/make_icon.py
from PIL import Image, ImageDraw
import os
S = 1024
root = os.path.join(os.path.dirname(__file__), "..")
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
# 背景: 紫→青の縦グラデを角丸で切り抜く
grad = Image.new("RGBA", (S, S))
gp = grad.load()
for y in range(S):
    t = y / (S - 1)
    c = (int(124 + (49 - 124) * t), int(77 + (130 - 77) * t), int(255 + (246 - 255) * t), 255)
    for x in range(S):
        gp[x, y] = c
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle((0, 0, S - 1, S - 1), radius=220, fill=255)
img.paste(grad, (0, 0), mask)
d = ImageDraw.Draw(img)
white = (255, 255, 255, 255)
# アバターのシルエット（頭＋肩）
d.ellipse((332, 190, 692, 550), fill=white)
d.pieslice((172, 600, 852, 1280), 180, 360, fill=white)
# 目（グラデ色でくり抜き風）
eye = (84, 100, 250, 255)
d.ellipse((418, 340, 468, 420), fill=eye)
d.ellipse((556, 340, 606, 420), fill=eye)
# LIVE の赤丸
d.ellipse((712, 90, 932, 310), fill=(255, 255, 255, 255))
d.ellipse((742, 120, 902, 280), fill=(244, 63, 94, 255))
os.makedirs(os.path.join(root, "src/VCast.App/Assets"), exist_ok=True)
img.resize((512, 512), Image.LANCZOS).save(os.path.join(root, "docs/icon.png"))
img.save(os.path.join(root, "src/VCast.App/Assets/app.ico"),
         sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("ok")
