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
    "01-pocetni-ekran.png": ("main-empty-dark.png", [(12, 6, 1192, 44), (12, 55, 308, 94), (6, 748, 1194, 779)]),
    "02-prevlacenje.png": ("main-drop-overlay-dark.png", [(410, 300, 790, 480)]),
    "03-nedavni-fajlovi.png": ("main-recent-menu-dark.png", [(390, 6, 546, 42), (383, 42, 730, 190)]),
    "04-validacija-ispravan.png": ("validation-valid-dark.png", [(16, 104, 129, 139), (150, 106, 320, 138), (370, 300, 830, 480)]),
    "05-validacija-greske.png": ("validation-errors-dark.png", [(150, 106, 432, 138), (17, 557, 1183, 729)]),
    "06-validacija-izabrana-greska.png": ("validation-issue-selected-dark.png", [(17, 683, 1183, 728), (17, 444, 1183, 461), (922, 518, 986, 548)]),
    "07-validacija-izmijenjeno.png": ("validation-modified-dark.png", [(150, 104, 490, 141), (233, 175, 316, 194), (1038, 104, 1184, 139)]),
    "07a-validacija-pitanje.png": ("validation-save-question-dark.png", [(370, 300, 830, 480), (140, 56, 306, 94)]),
    "08-ciscenje-analiza.png": ("cleaning-file-dark.png", [(16, 106, 137, 141), (205, 104, 715, 145), (160, 226, 522, 260)]),
    "09-ciscenje-podesavanja.png": ("cleaning-settings-dark.png", [(145, 108, 177, 140), (8, 160, 1192, 290)]),
    "10-ciscenje-prikazi.png": ("cleaning-settings-show-960-dark.png", [(198, 184, 666, 218), (792, 443, 914, 550), (8, 697, 950, 742)]),
    "11-ciscenje-vise-faktura.png": ("cleaning-multiselect-dark.png", [(12, 299, 1188, 407), (514, 229, 748, 259)]),
    "12-ciscenje-sumnjiv-blok.png": ("cleaning-suspicious-dark.png", [(12, 334, 1188, 370), (724, 544, 1186, 606), (8, 697, 1192, 742)]),
    "13-ciscenje-uredi.png": ("cleaning-edit-dark.png", [(794, 286, 912, 313), (14, 390, 708, 678), (494, 398, 696, 433)]),
    "14-ciscenje-popravka.png": ("cleaning-repair-note-dark.png", [(12, 300, 1188, 334), (724, 580, 1186, 626)]),
    "15-ciscenje-pregled-ocisti.png": ("cleaning-file-dark.png", [(984, 230, 1178, 258)]),
    "15a-pregled-izmjena.png": ("changes-preview-view-dark.png", [(70, 128, 572, 156), (16, 170, 1024, 330), (932, 648, 1016, 684)]),
    "24-sacuvaj.png": ("cleaning-file-dark.png", [(862, 106, 1030, 141), (1038, 106, 1184, 141)]),
    "16-podesavanja-opste.png": ("settings-general-dark.png", [(208, 130, 916, 200), (208, 358, 916, 444), (208, 448, 916, 534)]),
    "17-podesavanja-opste-akcije.png": ("settings-general-bottom-dark.png", [(208, 58, 916, 262), (208, 268, 916, 336), (208, 349, 415, 384)]),
    "18-seme.png": ("settings-schemas-builtin-dark.png", [(380, 24, 571, 59), (208, 226, 916, 297)]),
    "25-seme-nova-verzija.png": ("startup-schema-update-dark.png", [(370, 300, 830, 480)]),
    "19-seme-uvoz.png": ("settings-schemas-import-dark.png", [(737, 24, 916, 59), (208, 235, 605, 268), (208, 460, 916, 572)]),
    "20-seme-link.png": ("settings-schemas-download-dark.png", [(580, 24, 729, 59), (208, 164, 916, 230)]),
    "21-zatvori.png": ("main-validation-file-dark.png", [(246, 8, 358, 42), (140, 56, 306, 94)]),
    "26-nadogradnja-pitanje.png": ("startup-app-update-dark.png", [(370, 300, 830, 480)]),
    "27-nadogradnja.png": ("about-update-dark.png", [(145, 131, 289, 168), (24, 193, 736, 288), (24, 301, 269, 335)]),
    "22-o-programu.png": ("about-shortcuts-dark.png", [(296, 131, 402, 168), (24, 193, 736, 590), (88, 92, 218, 108)]),
}

# Isjecanje (lijevo, gore, desno, dolje): donji dio podešavanja prikazuje privremenu putanju test okruženja.
CROPS = {"17-podesavanja-opste-akcije.png": (0, 0, None, 400), "03-nedavni-fajlovi.png": (0, 0, None, 215)}

# Prekrivanje teksta koji otkriva putanju na računaru (lista nedavnih fajlova): (x1, y1, x2, y2, zamjenski tekst).
MASKS = {"03-nedavni-fajlovi.png": [(434, 104, 712, 121, r"C:\Fakture")]}

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
