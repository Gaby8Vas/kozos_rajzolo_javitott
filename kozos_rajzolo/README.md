# Közös Rajzoló

A böngészőből használható alkalmazásban több felhasználó dolgozhat egy rajzszobán. A felhasználói fiókokat az ASP.NET Core Identity kezeli, a rajzolási események a DrawingHub végponton jutnak el a többi résztvevőhöz, a rajzvonalak pedig MySQL-adatbázisba kerülnek.

## Felépítés

- .NET 10, ASP.NET Core, Blazor Interactive Server
- SignalR-hub: `Hubs/DrawingHub.cs`
- Adatmodell: `Data/DrawingDbContext.cs` és `Models`
- Rajzolófelület: `Components/Pages/Home.razor` és `wwwroot/js/drawing.js`
- Adattárolás: EF Core 9.0.22, Pomelo MySQL 9.0.0

A rendszer három szobaszerepkört használ: **Owner**, **Editor** és **Viewer**. A szobakóddal csatlakozó új felhasználó megtekintőként lép be. Szerkesztési jogot a szoba tulajdonosa adhat neki.

## Funkciók

- **Élő rajzolás:** rajzolás közben a pontok kb. 40 ms-onként eljutnak a többiekhez (`StreamStroke`), előnézetként. Felengedéskor a vonal mentésre kerül, és a végleges vonal váltja fel az előnézetet.
- **Radír:** valóban törli a képpontokat (`destination-out`), nem fehérrel takar. A PNG-export fehér hátteret kap.
- **Visszavonás és újra:** a saját vonalakon, fiókazonosító alapján. Gombok és billentyűk: Ctrl+Z, illetve Ctrl+Y / Ctrl+Shift+Z. Új vonal rajzolása után a visszavont vonalak véglegesen törlődnek.
- **Saját szobáim:** a fejléc alatti lista mutatja a szobáidat a szerepköröddel; kattintásra belépsz.
- **Lapozott betöltés:** nagy rajzoknál az előzmények 300-as adagokban töltődnek be.
- **Megjelenített név:** a regisztrációkor megadott név látszik a fejlécben, a vonalak szerzőjénél és a be-/kilépési üzenetekben.

## Adatbázis és migrációk

A séma EF Core migrációkkal készül (`Data/Migrations`), indításkor automatikusan lefutnak. A korábbi, `EnsureCreated`-del létrehozott adatbázisokat az alkalmazás felismeri, és az adatok megtartásával frissíti. Új migráció a projektkönyvtárban:

```bash
dotnet tool install --global dotnet-ef --version 9.0.*
dotnet ef migrations add <Nev> -o Data/Migrations
```

## Indítás

A projektkönyvtárban:

```powershell
dotnet restore
dotnet build
dotnet run --urls "http://localhost:5050"
```

Szükség van a .NET 10 SDK-ra, egy futó MySQL-szerverre, valamint a `ConnectionStrings:MySql` konfigurációra (minta: `appsettings.example.json`). A böngészős SignalR-kliens a `wwwroot/lib/signalr/signalr.min.js` útvonalon van; ha hiányzik, az `install-signalr.ps1` letölti.

## Tesztek

- JS füstteszt (böngésző és szerver szimulálva): `node tests/frontend_smoke_test.js`
- Integrációs tesztek valódi MySQL-lel (tesztenként saját, utána törölt adatbázis):

```bash
export KOZOS_RAJZOLO_TEST_MYSQL="server=localhost;port=3306;user=root;password=..."
dotnet test tests/KozosRajzolo.IntegrationTests
```

A környezeti változó nélkül az integrációs tesztek kimaradnak (Skipped).

## Ismert korlátok

A szobák zárolása, a szerepkör-gyorsítótár és a vonalak rendezése egy alkalmazáspéldányon belül működik; több szerverpéldányhoz (pl. Redis backplane) további koordináció kell. A radír a teljes vonalat nem törli objektumként, hanem képpontonként takarít; a visszavonással az egész radírvonás visszavonható.

## Futtatás Dockerben (pl. Macen)

A repó gyökerében:

```bash
cp .env.example .env   # írj bele saját teszt jelszavakat
docker compose up -d --build
```

Az alkalmazás a http://localhost:5050 címen érhető el. A MySQL-adatbázis a `db` konténerben fut, a sémát első indításkor a migrációk hozzák létre. Leállítás: `docker compose down` (az adatokkal együtt: `docker compose down -v`).
