# XML Validator

Desktop program za rad sa IZIS XML fakturama zdravstva (šema `fakture-zdravstva.xsd`, namespace
`http://www.zdravstvo-srpske.org/rp/xml-sheme/fakture-zdravstva`).

Program objedinjuje dva ranija alata: stari WinForms „XML validator” i Python skriptu `clean.py` za
čišćenje faktura po pravilu Službe hitne medicinske pomoći (SHMP).

Uputstvo za korišćenje sa slikama za svaki korak: [`docs/uputstvo/Uputstvo-XML-Validator.pdf`](docs/uputstvo/Uputstvo-XML-Validator.pdf)
(izvor `uputstvo.html`, pravi ga `napravi-uputstvo.py`).

## Šta program radi

- **Validacija** (tab „Validacija”): provjera XML fajla prema aktivnoj XSD šemi. Prikazuju se sve
  greške (ozbiljnost, linija, kolona, vrsta, poruka); klik na grešku vodi na mjesto u editoru. Poruke su na srpskom i
  kažu gdje je problem i šta treba upisati (npr. „Stavka 1 fakture SIN-1: polje „usluga-nivo” je prazno. Upišite cijeli
  broj nivoa usluge, ili obrišite cijeli red stavke…”); originalna tehnička poruka je u opisu alata
  (miš iznad poruke) i u izvještaju. Sažetak kaže i na koliko faktura su greške.
  Sadržaj se može urediti i sačuvati u novi fajl dugmetom „Sačuvaj XML” na tabu (bez čišćenja; ako u fajlu ima
  faktura za čišćenje, program pita da li da pređe na čišćenje). Brojevi linija u nalazima prate izmjene u editoru, a kad nestane posljednji nalaz, validacija se pokreće sama; nalazi se kopiraju ili izvoze u `.txt`/`.csv`.
- **Čišćenje faktura (SHMP)** (tab „Čišćenje faktura”): analiza faktura po pravilima iz `clean.py`,
  automatske popravke polomljenih zagrada, izbor akcije po fakturi (Obriši / Uredi / Preskoči),
  „Pregled” izmjena, „Očisti XML” (čišćenje u memoriji i nova analiza, prije čuvanja) i detaljan izvještaj
  (`.txt`/`.csv`). „Sačuvaj XML” pita da li da očisti XML ako akcije još nisu primijenjene. Sačuvan očišćen fajl se
  odmah validira u tabu „Validacija”.
- **Upravljanje šemama** (Podešavanja → Šeme): uvoz nove verzije šeme iz `.xsd` fajla, `.zip` paketa
  kakav FZO objavljuje na svom sajtu ili direktno sa linka (`https`), izbor aktivne šeme, pregled
  razlika prema aktivnoj, povratak na ugrađenu. Program prati šemu `fakture-zdravstva.xsd` na sajtu FZO
  (`https://zdravstvo-srpske.org/rp/xml-sheme/`, „Last modified”): ubrzo poslije pokretanja nudi novu verziju i,
  uz pristanak, sam je preuzima, uvozi i postavlja kao aktivnu; ista provjera je i na dugmetu „Provjeri novu verziju”.
- **Nadogradnja programa** („O programu” → „Nadogradnja”): program poredi svoju verziju sa posljednjim
  [GitHub release-om](https://github.com/mladengavric/XML-Validator/releases), nudi nadogradnju ubrzo poslije
  pokretanja, preuzima novu verziju za Windows ili Linux, zamjenjuje sebe njome, pokreće je i javlja da je
  instalirana. Podešavanja i šeme ostaju isti.

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
| `%AppData%\XmlValidator\settings.json` | podešavanja (tema, grupa SHMP i usluga pregleda, podrazumijevane akcije, nedavni fajlovi) |
| `%AppData%\XmlValidator\Schemas\` | uvezene šeme i izbor aktivne šeme |
| `%AppData%\XmlValidator\logs\` | log (posljednjih 14 dana; bez JMB-ova i sadržaja faktura) |

Ugrađena šema je dio programa i uvijek je dostupna kao rezerva: ako uvezena aktivna šema nedostaje ili je
oštećena, program koristi ugrađenu i to prikazuje u statusnoj traci. Brisanjem foldera
`%AppData%\XmlValidator` program se vraća na početno stanje.

## Pravila čišćenja (SHMP)

FZO prihvata usluge Službe hitne medicinske pomoći (SHMP, `usluga-grupa` 100) i usluge drugih službi samo za
pacijente koji su pregledani u SHMP (pregled je `usluga-oznaka` 183 u grupi 100; pacijent se prepoznaje po JMB-u).

Fajl se dijeli na zaglavlje, niz blokova `<faktura …>…</faktura>` i podnožje. Svaka faktura se
svrstava u jednu kategoriju:

| Kategorija | Uslov | Podrazumijevana akcija |
|---|---|---|
| Usluga SHMP | bar jedna stavka ima `usluga-grupa` 100 (podesivo) | ostaje |
| Pregledan u SHMP | samo usluge drugih službi, ali isti `osiguranik-jmb` ima pregled u SHMP (stavka grupe 100 sa `usluga-oznaka` 183, podesivo) na bilo kojoj fakturi, bilo kog datuma | ostaje |
| Prazna faktura | nema nijednu `<stavka` | Obriši |
| Bez pregleda u SHMP | samo usluge drugih službi, a pacijent (JMB) nigdje nema pregled u SHMP (i faktura bez JMB-a) | Obriši |
| Sumnjiv blok | blok koji se ne može pouzdano razgraničiti (npr. faktura bez zatvarajućeg taga koja „guta” sljedeću) | mora ručna odluka |

- Grupa se poredi po vrijednosti (XSD tip `xs:unsignedShort`): `" 100"` i `"0100"` se računaju kao 100,
  uz upozorenje; `"1000"` nije 100.
- Za kandidate se bira akcija **Obriši**, **Preskoči** (blok ostaje netaknut) ili **Uredi** (ručna
  izmjena bloka u editoru, poslije koje se faktura provjerava i ponovo klasifikuje). Podrazumijevana akcija
  se bira po kategoriji, a može se promijeniti za jednu ili više označenih faktura.
- Podrazumijevana akcija po kategoriji može biti i **Prikaži**: fakture ostaju bez akcije i za svaku se odlučuje
  pojedinačno (čuvanje je moguće tek kad sve imaju odluku). Dugme **„Podrazumijevano”** vraća grupu 100, pregled 183,
  Obriši za obje kategorije i popravke.
- `clean.py` je za isti JMB računao bilo koju uslugu SHMP, ne samo pregled; taj rezultat daje konzolni alat sa
  `--exam any` (odstupanje O18).
- **Automatske popravke zagrada:** `</faktura` bez `>`, `<faktura …` bez `>` ispred `<stavka` ili
  `</faktura`, linija `faktura …` bez vodećeg `<` (i uvučena). Svaka popravka se bilježi sa brojem linije;
  popravke se mogu isključiti.
- Rezultat se prije čuvanja provjerava na ispravnost XML-a i prema aktivnoj XSD šemi.

Program namjerno odstupa od `clean.py` samo tamo gdje je skripta imala grešku; svako odstupanje je pokriveno
testom.

## Prečice

| Prečica | Komanda |
|---|---|
| Ctrl+O | Otvori XML fajl |
| Ctrl+Shift+S | Sačuvaj kao |
| Ctrl+W | Zatvori fajl (program ostaje otvoren) |
| Ctrl+1 / Ctrl+2 | Tab „Validacija” / „Čišćenje faktura” |
| Ctrl+, | Podešavanja |
| F1 | O programu (verzija, licence, sve prečice) |
| F5 | Validiraj / Analiziraj (u aktivnom tabu) |
| F8 / Shift+F8 | Sljedeća / prethodna greška |
| Ctrl+S | Sačuvaj očišćeni fajl |
| Ctrl+R | Sačuvaj izvještaj |

## Konzolni alat

`xmlvalidator-cli` radi isto što i aplikacija, iz komandne linije, za serijsku obradu i automatizaciju:

```
xmlvalidator-cli validate fakture.xml --report nalazi.txt
xmlvalidator-cli clean fakture.xml [izlaz.xml] [--group 100] [--exam 183|any] [--empty delete|skip|ask] [--nomatch delete|skip|ask] [--report izvjestaj.txt]
xmlvalidator-cli help clean
```

Važe ista pravila: ulaz se nikad ne mijenja, postojeći fajl se prepisuje samo uz `--overwrite`, sumnjivi blokovi
traže izričitu odluku (`--suspicious delete|skip`), a izlaz koji ne prolazi provjeru prema šemi čuva se samo uz
`--allow-invalid`. Izlazni kod je 0 za uspjeh, 1 kad fajl ima greške ili izlaz nije sačuvan, 2 za pogrešne
argumente i 3 kad fajl ne može da se pročita ili upiše. Sve opcije sa objašnjenjem daje
`xmlvalidator-cli help clean`.

## Gradnja i testovi

```
dotnet build
dotnet test
```

Build ima uključeno `TreatWarningsAsErrors`. Testovi (xUnit v3, Core, aplikacija sa UI testovima bez prozora
i konzolni alat) i testni podaci se ne objavljuju u javnom repozitorijumu.

## Objavljivanje (pakovanje)

```
dotnet publish src/XmlValidator.App -p:PublishProfile=win-x64
```

Rezultat je jedan fajl `artifacts/publish/win-x64/XmlValidator.exe` (≈ 50 MB): self-contained,
single-file, sa ugrađenim nativnim bibliotekama i simbolima, bez trimovanja. Profili su u
`src/XmlValidator.App/Properties/PublishProfiles/`.

Konzolni alat: `dotnet publish src/XmlValidator.Cli -p:PublishProfile=win-x64` daje
`artifacts/publish/win-x64/xmlvalidator-cli.exe` (≈ 36 MB) u istom folderu.

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

Za novo izdanje: promijeniti `Version`, napraviti commit, označiti ga (`git tag v1.0.1`), objaviti
program komandom iz prethodnog odjeljka i napraviti GitHub release sa oznakom `v<verzija>`. Nadogradnja u
programu traži u release-u tačno ove fajlove: `XmlValidator.exe` (Windows) i
`XmlValidator-<verzija>-linux-x64.tar.gz` (Linux, sa programom `XmlValidator` u arhivi).

## Struktura repozitorijuma

```
src/XmlValidator.Core/        logika: validacija, šeme, čišćenje, izvještaji (bez zavisnosti od UI-ja)
src/XmlValidator.App/         Avalonia 12 aplikacija (MVVM, CommunityToolkit.Mvvm)
src/XmlValidator.Cli/         konzolni alat xmlvalidator-cli (validate, clean)
docs/uputstvo/                 PDF uputstvo za korišćenje (izvor uputstvo.html, skripta napravi-uputstvo.py)
```

## Lični podaci

Fakture sadrže JMB i zdravstvene podatke. U repozitorijumu nema faktura: `.gitignore` isključuje sve `*.xml`
fajlove. Program nikad ne šalje sadržaj faktura na mrežu. Na internet pristupa samo da bi provjerio i preuzeo
novu verziju programa (GitHub) i novu šemu (sajt FZO), i kad korisnik pokrene „Uvezi sa linka”.

## Licence

Korišćeni paketi (Avalonia, AvaloniaEdit, Avalonia.Controls.DataGrid, CommunityToolkit.Mvvm,
Microsoft.Extensions.DependencyInjection, .NET) su pod licencom MIT; font Inter pod SIL Open Font License 1.1;
ikonice su nacrtane po uzoru na Material Design Icons (Apache 2.0). Spisak sa verzijama je u prozoru
„O programu”.
