# XML Validator

Desktop program za rad sa IZIS XML fakturama zdravstva (šema `fakture-zdravstva.xsd`, namespace
`http://www.zdravstvo-srpske.org/rp/xml-sheme/fakture-zdravstva`).

Program objedinjuje dva ranija alata: stari WinForms „XML validator” i Python skriptu `clean.py` za
čišćenje faktura po pravilu Službe hitne medicinske pomoći (SHMP).

## Šta program radi

- **Validacija** (tab „Validacija”): provjera XML fajla prema aktivnoj XSD šemi. Prikazuju se sve
  greške (ozbiljnost, linija, kolona, vrsta, poruka); klik na grešku vodi na mjesto u editoru.
  Sadržaj se može urediti i sačuvati u novi fajl; nalazi se kopiraju ili izvoze u `.txt`/`.csv`.
- **Čišćenje faktura (SHMP)** (tab „Čišćenje faktura”): analiza faktura po pravilima iz `clean.py`,
  automatske popravke polomljenih zagrada, izbor akcije po fakturi (Obriši / Uredi / Preskoči),
  pregled izmjena prije čuvanja i detaljan izvještaj (`.txt`/`.csv`, uz opciju „Maskiraj JMB”).
- **Upravljanje šemama** (Podešavanja → Šeme): uvoz nove verzije šeme iz `.xsd` fajla, `.zip` paketa
  kakav FZO objavljuje na svom sajtu ili direktno sa linka (`https`), izbor aktivne šeme, pregled
  razlika prema aktivnoj, povratak na ugrađenu.

**Ulazni fajl se nikad ne mijenja.** Rezultat čišćenja uvijek ide u novi fajl (podrazumijevano
`<ime>_ociscen.xml`), a kodiranje, BOM, završeci linija i tekst faktura koje ostaju čuvaju se bajt po bajt.
Ništa se ne briše tiho: svaka obrisana, preskočena, uređena ili popravljena faktura je u izvještaju.

## Pokretanje

### Gotov program (Windows)

`XmlValidator.exe` je jedan fajl sa ugrađenim .NET okruženjem: ne traži instalaciju ni instaliran .NET.
Dovoljno ga je kopirati u bilo koji folder i pokrenuti. Fajl se može otvoriti i iz komandne linije ili
preko „Otvori sa…”:

```
XmlValidator.exe "C:\Fakture\fakture-2026-10.xml"
```

Pri prvom pokretanju program raspakuje svoje nativne biblioteke u privremeni folder, pa je start nešto
sporiji (nekoliko sekundi); svako sljedeće pokretanje traje oko dvije sekunde.

Zahtjevi: Windows 10 ili noviji, 64-bitni.

### Iz izvornog koda

Potreban je [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet run --project src/XmlValidator.App
```

## Gdje program čuva podatke

| Putanja | Sadržaj |
|---|---|
| `%AppData%\XmlValidator\settings.json` | podešavanja (tema, ciljna grupa, podrazumijevane akcije, nedavni fajlovi) |
| `%AppData%\XmlValidator\Schemas\` | uvezene šeme i izbor aktivne šeme |
| `%AppData%\XmlValidator\logs\` | log (posljednjih 14 dana; bez JMB-ova i sadržaja faktura) |

Ugrađena šema je dio programa i uvijek je dostupna kao rezerva: ako uvezena aktivna šema nedostaje ili je
oštećena, program koristi ugrađenu i to prikazuje u statusnoj traci. Brisanjem foldera
`%AppData%\XmlValidator` program se vraća na početno stanje.

## Pravila čišćenja (SHMP)

Fajl se dijeli na zaglavlje, niz blokova `<faktura …>…</faktura>` i podnožje. Svaka faktura se
svrstava u jednu kategoriju:

| Kategorija | Uslov | Podrazumijevana akcija |
|---|---|---|
| Ima ciljnu grupu | bar jedna stavka ima `usluga-grupa` jednaku ciljnoj grupi (podrazumijevano `100`) | ostaje |
| Isti JMB ima ciljnu grupu | nema ciljnu grupu, ali isti `osiguranik-jmb` ima drugu fakturu sa ciljnom grupom (bilo koji datum) | ostaje |
| Prazna faktura | nema nijednu `<stavka` | Obriši |
| Bez grupe i bez podudaranja JMB-a | nema ciljnu grupu i njen JMB nigdje nema ciljnu grupu (i faktura bez JMB-a) | Obriši |
| Sumnjiv blok | blok koji se ne može pouzdano razgraničiti (npr. faktura bez zatvarajućeg taga koja „guta” sljedeću) | mora ručna odluka |

- Grupa se poredi po vrijednosti (XSD tip `xs:unsignedShort`): `" 100"` i `"0100"` se računaju kao 100,
  uz upozorenje; `"1000"` nije 100.
- Za kandidate se bira akcija **Obriši**, **Preskoči** (blok ostaje netaknut) ili **Uredi** (ručna
  izmjena bloka u editoru, poslije koje se faktura provjerava i ponovo klasifikuje). Podrazumijevana akcija
  se bira po kategoriji, a može se promijeniti za jednu ili više označenih faktura.
- Dugme **„Kao skripta”** postavlja Obriši za obje kategorije kandidata, što daje isti rezultat kao `clean.py`
  (uz dokumentovana odstupanja).
- **Automatske popravke zagrada:** `</faktura` bez `>`, `<faktura …` bez `>` ispred `<stavka` ili
  `</faktura`, linija `faktura …` bez vodećeg `<` (i uvučena). Svaka popravka se bilježi sa brojem linije;
  popravke se mogu isključiti.
- Rezultat se prije čuvanja provjerava na ispravnost XML-a i prema aktivnoj XSD šemi.

Detaljna pravila su u [`PLAN.md`](PLAN.md), a namjerne razlike u odnosu na `clean.py` (ispravke grešaka
skripte) u [`docs/odstupanja-od-clean-py.md`](docs/odstupanja-od-clean-py.md).

## Prečice

| Prečica | Komanda |
|---|---|
| Ctrl+O | Otvori XML fajl |
| Ctrl+Shift+S | Sačuvaj kao |
| Ctrl+1 / Ctrl+2 | Tab „Validacija” / „Čišćenje faktura” |
| Ctrl+, | Podešavanja |
| F1 | O programu (verzija, licence, sve prečice) |
| F5 | Validiraj / Analiziraj (u aktivnom tabu) |
| F8 / Shift+F8 | Sljedeća / prethodna greška |
| Ctrl+D | Pregled izmjena (čišćenje) |
| Ctrl+S | Sačuvaj očišćeni fajl |
| Ctrl+R | Sačuvaj izvještaj |

## Gradnja i testovi

```
dotnet build
dotnet test
```

Build ima uključeno `TreatWarningsAsErrors`; testovi (xUnit v3) pokrivaju Core (validacija, šeme,
čišćenje, paritet sa `clean.py`) i aplikaciju (ViewModel-i i UI testovi bez prozora, sa snimcima ekrana u
`artifacts/screenshots/`).

Referentno ponašanje skripte (za testove pariteta):

```
python legacy/clean.py <ulaz.xml> <izlaz.xml>
```

## Objavljivanje (pakovanje)

```
dotnet publish src/XmlValidator.App -p:PublishProfile=win-x64
```

Rezultat je jedan fajl `artifacts/publish/win-x64/XmlValidator.exe` (≈ 50 MB): self-contained,
single-file, sa ugrađenim nativnim bibliotekama i simbolima, bez trimovanja. Profili su u
`src/XmlValidator.App/Properties/PublishProfiles/`.

Postoji i profil `linux-x64` (`-p:PublishProfile=linux-x64`), ali Windows je primarna platforma i
program na Linuxu nije provjeren.

## Verzionisanje

Verzija se upisuje na jednom mjestu, u [`Directory.Build.props`](Directory.Build.props):

```xml
<Version>1.0.0</Version>
```

Koristi se semantičko verzionisanje `MAJOR.MINOR.PATCH`: PATCH za ispravke, MINOR za nove mogućnosti,
MAJOR za promjene koje mijenjaju rezultat čišćenja ili rad sa postojećim podacima. Verzija se prikazuje u
prozoru „O programu” (F1) i u svojstvima `.exe` fajla (Windows → Properties → Details); u svojstvima je
uz verziju i kratak hash commita iz kojeg je program napravljen.

Za novo izdanje: promijeniti `Version`, napraviti commit, označiti ga (`git tag v1.0.1`) i objaviti
program komandom iz prethodnog odjeljka.

## Struktura repozitorijuma

```
src/XmlValidator.Core/        logika: validacija, šeme, čišćenje, izvještaji (bez zavisnosti od UI-ja)
src/XmlValidator.App/         Avalonia 12 aplikacija (MVVM, CommunityToolkit.Mvvm)
tests/                        testovi i ISKLJUČIVO sintetički testni podaci
docs/                         analize i tehnička dokumentacija
legacy/                       stari WinForms program i clean.py (samo za čitanje)
```

Dokumentacija: [`docs/aplikacija.md`](docs/aplikacija.md) (arhitektura aplikacije),
[`docs/ciscenje-faktura.md`](docs/ciscenje-faktura.md), [`docs/sheme-i-validacija.md`](docs/sheme-i-validacija.md),
analize starog programa, FZO paketa i `clean.py` u `docs/`. Plan rada i sve odluke su u [`PLAN.md`](PLAN.md).

## Lični podaci

Fakture sadrže JMB i zdravstvene podatke. U repozitorijumu nema stvarnih faktura: `.gitignore` isključuje
sve `*.xml` fajlove osim sintetičkih testnih podataka u `tests/TestData/` (i odobrenog anonimizovanog
primjera u `tests/TestData/samples/`). Program ne šalje ništa na mrežu, osim kad korisnik izričito
pokrene „Uvezi sa linka” za šemu.

## Licence

Korišćeni paketi (Avalonia, AvaloniaEdit, Avalonia.Controls.DataGrid, CommunityToolkit.Mvvm,
Microsoft.Extensions.DependencyInjection, .NET) su pod licencom MIT; font Inter pod SIL Open Font License 1.1;
ikonice su nacrtane po uzoru na Material Design Icons (Apache 2.0). Spisak sa verzijama je u prozoru
„O programu”.
