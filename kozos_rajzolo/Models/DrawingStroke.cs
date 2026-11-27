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
}

public sealed class PointDto
{
    public double X { get; set; }
    public double Y { get; set; }
}
