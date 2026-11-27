namespace KozosRajzolo.Models;

public sealed class DrawingStroke
{
    public long Id { get; set; }
    public string RoomCode { get; set; } = "";
    // A rajzoló fiókazonosítója (a visszavonás és a jogosultság ehhez kötődik).
    public string? UserId { get; set; }
    // A rajzoló megjelenített neve a vonal készítésekor.
    public string UserName { get; set; } = "";
    public string Color { get; set; } = "#222222";
    public double Width { get; set; } = 4;
    public bool IsEraser { get; set; }
    public string PointsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class StrokeDto
{
    // A mentett vonal azonosítója.
    public long Id { get; set; }
    public string? UserId { get; set; }
    public string UserName { get; set; } = "Vendég";
    public string Color { get; set; } = "#222222";
    public double Width { get; set; } = 4;
    public bool IsEraser { get; set; }
    public List<PointDto> Points { get; set; } = new();
    // A kliens saját, ideiglenes azonosítója a vonalhoz (nem kerül adatbázisba).
    public string? ClientKey { get; set; }
    // A szerver által képzett kulcs, amellyel a többiek az élő előnézetet a mentett vonalra cserélik.
    public string? LiveKey { get; set; }
}

/// <summary>Rajzolás közben küldött, nem mentett vonalrészlet.</summary>
public sealed class LiveStrokeDto
{
    public string ClientKey { get; set; } = "";
    public string Color { get; set; } = "#222222";
    public double Width { get; set; } = 4;
    public bool IsEraser { get; set; }
    // Igaz, ha a pontok a teljes eddigi vonalat jelentik (pl. ritkítás után), nem csak az újakat.
    public bool Reset { get; set; }
    public List<PointDto> Points { get; set; } = new();
}

public sealed class PointDto
{
    public double X { get; set; }
    public double Y { get; set; }
}
