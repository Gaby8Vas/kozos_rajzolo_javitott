namespace KozosRajzolo.Models;

public sealed class RoomParticipant
{
    public int Id { get; set; }
    public string RoomCode { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    // Szobán belüli szerepkör: Owner, Editor, Viewer.
    public string Role { get; set; } = "Viewer";
    public DateTime JoinedAtUtc { get; set; } = DateTime.UtcNow;
}
