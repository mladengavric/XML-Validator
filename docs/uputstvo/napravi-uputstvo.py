"""Pravi PDF uputstvo (A4) iz uputstvo.html i snimaka ekrana iz UI testova.

Koraci:
  1. dotnet test tests/XmlValidator.App.Tests      (osvježava artifacts/screenshots/)
  2. dotnet publish src/XmlValidator.Cli -p:PublishProfile=win-x64
  3. python -I docs/uputstvo/napravi-uputstvo.py   (iz korijena repozitorijuma)

Slike se kopiraju u docs/uputstvo/slike/, slika terminala se crta iz stvarnog izlaza konzolnog alata nad
sintetičkim fajlom, a PDF se pravi štampanjem HTML-a u Microsoft Edge-u (headless). Potreban je Pillow.
"""
import os
import shutil
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
HERE = ROOT / "docs" / "uputstvo"
SHOTS = ROOT / "artifacts" / "screenshots"
OUT = HERE / "slike"
PDF = HERE / "Uputstvo-XML-Validator.pdf"
EDGE = Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")

# slika u uputstvu -> (snimak iz UI testova u tamnoj temi, crveni okviri (x1, y1, x2, y2) u pikselima snimka)
IMAGES = {
    "01-pocetni-ekran.png": ("main-empty-dark.png", [(12, 6, 1192, 44), (14, 52, 284, 94), (6, 748, 1194, 779)]),
    "02-prevlacenje.png": ("main-drop-overlay-dark.png", [(410, 300, 790, 480)]),
    "03-nedavni-fajlovi.png": ("main-recent-menu-dark.png", [(374, 6, 542, 42), (372, 44, 720, 190)]),
    "04-validacija-ispravan.png": ("validation-valid-dark.png", [(14, 102, 131, 141), (146, 104, 332, 141), (720, 104, 1042, 141)]),
    "05-validacija-greske.png": ("validation-errors-dark.png", [(153, 110, 342, 134), (17, 585, 1183, 729)]),
    "06-validacija-izabrana-greska.png": ("validation-issue-selected-dark.png", [(17, 682, 1183, 729), (17, 471, 1183, 489), (910, 546, 974, 576)]),
    "07-validacija-izmijenjeno.png": ("validation-modified-dark.png", [(153, 104, 482, 139), (233, 175, 316, 194), (116, 8, 255, 41)]),
    "08-ciscenje-analiza.png": ("cleaning-file-dark.png", [(14, 104, 136, 143), (160, 102, 672, 146), (160, 225, 522, 261)]),
    "09-ciscenje-podesavanja.png": ("cleaning-settings-dark.png", [(978, 104, 1188, 144), (8, 160, 1192, 290)]),
    "10-ciscenje-prikazi.png": ("cleaning-settings-show-960-dark.png", [(196, 182, 666, 220), (788, 443, 916, 479), (838, 634, 946, 660)]),
    "11-ciscenje-vise-faktura.png": ("cleaning-multiselect-dark.png", [(12, 299, 1188, 407), (942, 229, 1178, 259)]),
    "12-ciscenje-sumnjiv-blok.png": ("cleaning-suspicious-dark.png", [(12, 333, 1188, 369), (724, 520, 1186, 584)]),
    "13-ciscenje-uredi.png": ("cleaning-edit-dark.png", [(790, 283, 916, 317), (14, 372, 710, 634), (576, 378, 698, 417)]),
    "14-ciscenje-popravka.png": ("cleaning-repair-note-dark.png", [(12, 299, 1188, 335), (724, 526, 1186, 574)]),
    "15-pregled-izmjena.png": ("changes-preview-dark.png", [(70, 94, 1018, 132), (70, 161, 574, 194), (828, 648, 934, 686)]),
    "24-izvjestaj.png": ("cleaning-file-dark.png", [(606, 700, 784, 738), (272, 704, 472, 736)]),
    "16-podesavanja-opste.png": ("settings-general-dark.png", [(208, 358, 916, 444), (208, 448, 916, 534)]),
    "17-podesavanja-opste-akcije.png": ("settings-general-bottom-dark.png", [(208, 56, 916, 264), (208, 266, 916, 338), (206, 347, 417, 386)]),
    "18-seme.png": ("settings-schemas-builtin-dark.png", [(208, 176, 916, 247)]),
    "19-seme-uvoz.png": ("settings-schemas-import-dark.png", [(766, 22, 918, 62), (206, 181, 395, 220), (399, 181, 607, 220), (208, 394, 916, 572)]),
    "20-seme-link.png": ("settings-schemas-download-dark.png", [(598, 22, 762, 62), (208, 112, 916, 180)]),
    "21-zatvori.png": ("main-validation-file-dark.png", [(260, 6, 352, 42)]),
    "22-o-programu.png": ("about-shortcuts-dark.png", [(138, 110, 222, 150), (24, 174, 736, 592)]),
}

# Isjecanje (lijevo, gore, desno, dolje): donji dio podešavanja prikazuje privremenu putanju test okruženja.
CROPS = {"17-podesavanja-opste-akcije.png": (0, 0, None, 400)}

# Prekrivanje teksta koji otkriva putanju na računaru (lista nedavnih fajlova): (x1, y1, x2, y2, zamjenski tekst).
MASKS = {"03-nedavni-fajlovi.png": [(422, 104, 712, 122, r"C:\Fakture")]}

BOX_COLOR = (255, 59, 48)
BOX_WIDTH = 4
BOX_PADDING = 3


def copy_images():
    if OUT.exists():
        shutil.rmtree(OUT)
    OUT.mkdir(parents=True)
    small = ImageFont.truetype(r"C:\Windows\Fonts\segoeui.ttf", 12)
    for target, (source, boxes) in IMAGES.items():
        image = Image.open(SHOTS / source).convert("RGB")
        draw = ImageDraw.Draw(image)
        for x1, y1, x2, y2, text in MASKS.get(target, []):
            background = image.getpixel((x2 + 2, y1 + 2))
            draw.rectangle((x1, y1, x2, y2), fill=background)
            draw.text((x1 + 2, y1 + 1), text, font=small, fill=(170, 176, 186))
        if target in CROPS:
            left, top, right, bottom = CROPS[target]
            image = image.crop((left, top, right or image.width, bottom or image.height))
            draw = ImageDraw.Draw(image)
        for x1, y1, x2, y2 in boxes:
            draw.rounded_rectangle(
                (x1 - BOX_PADDING, y1 - BOX_PADDING, x2 + BOX_PADDING, y2 + BOX_PADDING),
                radius=6, outline=BOX_COLOR, width=BOX_WIDTH)
        image.save(OUT / target, optimize=True)
    shutil.copy(ROOT / "src" / "XmlValidator.App" / "Assets" / "app-256.png", OUT / "ikona.png")


def terminal_image():
    demo = ROOT / "artifacts" / "uputstvo-cli"
    if demo.exists():
        shutil.rmtree(demo)
    demo.mkdir(parents=True)
    shutil.copy(ROOT / "tests" / "TestData" / "cleaning" / "bez-grupe-bez-podudaranja.xml", demo / "fakture-januar.xml")
    cli = ROOT / "artifacts" / "publish" / "win-x64" / "xmlvalidator-cli.exe"
    args = ["clean", "fakture-januar.xml", "--report", "izvjestaj.txt"]
    run = subprocess.run([str(cli), *args], cwd=demo, capture_output=True, encoding="utf-8")
    if run.returncode != 0:
        sys.exit(f"CLI nije uspio ({run.returncode}): {run.stderr}")
    # Putanja demo foldera se u slici prikazuje kao C:\Fakture (primjer).
    text = (run.stdout + run.stderr).replace(str(demo), r"C:\Fakture").rstrip("\n")
    lines = [r"PS C:\Fakture> xmlvalidator-cli " + " ".join(args), *text.splitlines(), "", r"PS C:\Fakture> echo $LASTEXITCODE", "0"]

    font = ImageFont.truetype(r"C:\Windows\Fonts\consola.ttf", 26)
    wrapped = []
    for line in lines:
        while len(line) > 96:
            wrapped.append(line[:96])
            line = "    " + line[96:]
        wrapped.append(line)
    pad, line_height = 28, 36
    image = Image.new("RGB", (1800, pad * 2 + line_height * len(wrapped)), (12, 12, 12))
    draw = ImageDraw.Draw(image)
    for i, line in enumerate(wrapped):
        color = (249, 241, 165) if line.startswith("PS ") else (204, 204, 204)
        draw.text((pad, pad + i * line_height), line, font=font, fill=color)
    image.save(OUT / "23-konzolni-alat.png", optimize=True)
    shutil.rmtree(demo)


def make_pdf():
    if PDF.exists():
        PDF.unlink()
    html = (HERE / "uputstvo.html").as_uri()
    subprocess.run(
        [str(EDGE), "--headless", "--disable-gpu", "--no-pdf-header-footer", "--run-all-compositor-stages-before-draw",
         f"--print-to-pdf={PDF}", html],
        check=True, capture_output=True, timeout=120)
    if not PDF.exists():
        sys.exit("PDF nije napravljen.")
    print(f"{PDF} ({PDF.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    copy_images()
    terminal_image()
    make_pdf()
