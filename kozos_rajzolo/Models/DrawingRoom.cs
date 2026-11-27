namespace KozosRajzolo.Models;

public sealed class DrawingRoom
{
    public string RoomCode { get; set; } = string.Empty;
    public string Name { get; set; } = "Közös rajzszoba";
    public string OwnerUserId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
