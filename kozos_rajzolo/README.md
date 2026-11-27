# Közös Rajzoló

A böngészőből használható alkalmazásban több felhasználó dolgozhat egy rajzszobán. A felhasználói fiókokat az ASP.NET Core Identity kezeli, a rajzolási események a DrawingHub végponton jutnak el a többi résztvevőhöz, a rajzvonalak pedig MySQL-adatbázisba kerülnek.

## Felépítés

- .NET 10, ASP.NET Core, Blazor Interactive Server
- SignalR-hub: `Hubs/DrawingHub.cs`
- Adatmodell: `Data/DrawingDbContext.cs` és `Models`
- Rajzolófelület: `Components/Pages/Home.razor` és `wwwroot/js/drawing.js`
- Adattárolás: EF Core 9.0.22, Pomelo MySQL 9.0.0

A rendszer három szobaszerepkört használ: **Owner**, **Editor** és **Viewer**. A szobakóddal csatlakozó új felhasználó megtekintőként lép be. Szerkesztési jogot a szoba tulajdonosa adhat neki. A vonalak mentés után azonosítót kapnak; új csatlakozáskor a kliens a mentett vonalakból állítja vissza a rajzot. A visszavonás a felhasználó utolsó saját, szerveren tárolt vonalát távolítja el.

## Indítás

A projektkönyvtárban:

```powershell
dotnet restore
dotnet build
dotnet run --urls "http://localhost:5050"
```

Szükség van a .NET 10 SDK-ra, egy futó MySQL-szerverre, valamint a `ConnectionStrings:MySql` konfigurációra. A jelszót helyi fejlesztésnél User Secretsben kell beállítani. A böngészős SignalR-kliensnek a `wwwroot/lib/signalr/signalr.min.js` útvonalon kell lennie. A telepítés részletes leírása a csomag gyökerében, a `README_ELSOKENT.md` fájlban található.

## Ismert korlátok

A radír fehér vonallal takar, a rajzot nem vektoros objektumonként törli. Általános redo nincs. A szobák lezárása és a vonalak rendezése egy alkalmazáspéldányon belül működik; több szerverpéldányhoz további koordináció szükséges. Az előzmények minden csatlakozáskor teljes egészükben betöltődnek.

A csomag C#-fordítása és MySQL-lel végzett integrációs tesztje még nincs dokumentálva. Az első helyi indítás után a fordítási kimenetet és a funkcionális tesztek eredményeit külön kell rögzíteni.
