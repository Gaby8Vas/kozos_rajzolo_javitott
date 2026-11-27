using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using KozosRajzolo.Data;
using KozosRajzolo.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace KozosRajzolo.Hubs;

/// <summary>
/// Rajzszobák és rajzolási események kezelése. A szobánkénti zárolás egy szerverpéldányra érvényes.
/// </summary>
[Authorize]
public sealed class DrawingHub(
    IDbContextFactory<DrawingDbContext> dbFactory,
    ILogger<DrawingHub> logger) : Hub
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RoomLocks = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static SemaphoreSlim Gate(string roomCode) =>
        RoomLocks.GetOrAdd(roomCode, static _ => new SemaphoreSlim(1, 1));

    public sealed record ParticipantInfo(string UserId, string DisplayName, string Role);
    public sealed record RoomSnapshot(string RoomCode, string RoomName, string Role, List<StrokeDto> Strokes);

    public async Task<RoomSnapshot> CreateRoom(string? roomName)
    {
        var userId = CurrentUserId();
        var name = string.IsNullOrWhiteSpace(roomName) ? "Közös rajzszoba" : roomName.Trim();
        name = name[..Math.Min(name.Length, 100)];

        await using var db = await dbFactory.CreateDbContextAsync();
        var code = await GenerateUniqueRoomCodeAsync(db);
        db.DrawingRooms.Add(new DrawingRoom { RoomCode = code, Name = name, OwnerUserId = userId });
        db.RoomParticipants.Add(new RoomParticipant { RoomCode = code, UserId = userId, Role = "Owner" });
        await db.SaveChangesAsync();
        return await JoinExistingRoomAsync(code, "Owner", name);
    }

    public async Task<RoomSnapshot> JoinRoom(string roomCode)
    {
        roomCode = CleanRoom(roomCode);
        if (roomCode.Length < 3) throw new HubException("Hibás rajzszobakód.");
        var userId = CurrentUserId();
        await using var db = await dbFactory.CreateDbContextAsync();
        var room = await db.DrawingRooms.AsNoTracking()
            .SingleOrDefaultAsync(r => r.RoomCode == roomCode);
        if (room is null) throw new HubException("A rajzszoba nem található.");

        var participant = await db.RoomParticipants
            .SingleOrDefaultAsync(p => p.RoomCode == roomCode && p.UserId == userId);
        if (participant is null)
        {
            // A szobakóddal csatlakozó résztvevő először csak megtekintő.
            participant = new RoomParticipant { RoomCode = roomCode, UserId = userId, Role = "Viewer" };
            db.RoomParticipants.Add(participant);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // A felhasználó és a szoba párosát egyedi index védi.
                db.Entry(participant).State = EntityState.Detached;
                participant = await db.RoomParticipants.AsNoTracking()
                    .SingleOrDefaultAsync(p => p.RoomCode == roomCode && p.UserId == userId)
                    ?? throw new HubException("A szobatagság mentése nem sikerült.");
            }
        }
        return await JoinExistingRoomAsync(roomCode, participant.Role, room.Name);
    }

    public async Task<StrokeDto> SendStroke(string roomCode, StrokeDto? stroke)
    {
        roomCode = EnsureCurrentRoom(roomCode);
        if (stroke is null || stroke.Points is null || stroke.Points.Count is < 1 or > 5000)
            throw new HubException("Érvénytelen vagy túl hosszú vonal.");
        if (!double.IsFinite(stroke.Width) || stroke.Width < 1 || stroke.Width > 40)
            throw new HubException("A vonalvastagság kívül esik a megengedett tartományon.");
        if (stroke.Points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)
                                   || p.X < -50 || p.X > 1450 || p.Y < -50 || p.Y > 900))
            throw new HubException("Érvénytelen rajzolási koordináták.");

        var gate = Gate(roomCode);
        await gate.WaitAsync();
        try
        {
            var userId = CurrentUserId();
            await using var db = await dbFactory.CreateDbContextAsync();
            var role = await GetRoleAsync(db, roomCode, userId);
            if (role is not ("Owner" or "Editor"))
                throw new HubException("Ehhez a művelethez szerkesztői jogosultság kell.");

            var userName = Context.User?.Identity?.Name ?? "Felhasználó";
            userName = userName[..Math.Min(userName.Length, 60)];
            var color = SafeColor(stroke.Color);
            var entity = new DrawingStroke
            {
                RoomCode = roomCode,
                UserName = userName,
                Color = color,
                Width = stroke.Width,
                IsEraser = stroke.IsEraser,
                PointsJson = JsonSerializer.Serialize(stroke.Points, JsonOptions)
            };
            db.Strokes.Add(entity);
            await db.SaveChangesAsync();
            // A küldő visszatérési értéket, a többi kapcsolat eseményt kap.
            var saved = ToDto(entity);
            try
            {
                await Clients.OthersInGroup(roomCode).SendAsync("ReceiveStroke", roomCode, saved);
            }
            catch (Exception ex)
            {
                // Az eseményküldési hiba a korábban sikeres adatbázismentést nem vonja vissza.
                logger.LogWarning(ex, "Rajz mentve, de a csoportos értesítés sikertelen: {RoomCode}", roomCode);
            }
            return saved;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> UndoLastStroke(string roomCode)
    {
        roomCode = EnsureCurrentRoom(roomCode);
        var gate = Gate(roomCode);
        await gate.WaitAsync();
        try
        {
            var userId = CurrentUserId();
            await using var db = await dbFactory.CreateDbContextAsync();
            var role = await GetRoleAsync(db, roomCode, userId);
            if (role is not ("Owner" or "Editor"))
                throw new HubException("A visszavonáshoz szerkesztői jogosultság kell.");

            var userName = Context.User?.Identity?.Name ?? "";
            userName = userName[..Math.Min(userName.Length, 60)];
            var previous = await db.Strokes
                .Where(s => s.RoomCode == roomCode && s.UserName == userName)
                .OrderByDescending(s => s.Id).FirstOrDefaultAsync();
            if (previous is null) return false;
            db.Strokes.Remove(previous);
            await db.SaveChangesAsync();
            await BroadcastSafeAsync("StrokeRemoved", roomCode, roomCode, previous.Id);
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task ClearRoom(string roomCode)
    {
        roomCode = EnsureCurrentRoom(roomCode);
        var gate = Gate(roomCode);
        await gate.WaitAsync();
        try
        {
            var userId = CurrentUserId();
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await GetRoleAsync(db, roomCode, userId) != "Owner")
                throw new HubException("A teljes rajzot csak a tulajdonos törölheti.");
            await db.Strokes.Where(s => s.RoomCode == roomCode).ExecuteDeleteAsync();
            await BroadcastSafeAsync("RoomCleared", roomCode, roomCode);
        }
        finally { gate.Release(); }
    }

    public async Task<List<ParticipantInfo>> GetParticipants(string roomCode)
    {
        roomCode = EnsureCurrentRoom(roomCode);
        var userId = CurrentUserId();
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.DrawingRooms.AnyAsync(x => x.RoomCode == roomCode && x.OwnerUserId == userId))
            throw new HubException("A tagságot csak a szoba tulajdonosa tekintheti meg.");
        return await (from participant in db.RoomParticipants
                      join user in db.Users on participant.UserId equals user.Id
                      where participant.RoomCode == roomCode
                      orderby user.Email
                      select new ParticipantInfo(user.Id,
                          user.DisplayName ?? user.Email ?? "Felhasználó", participant.Role))
            .ToListAsync();
    }

    public async Task SetParticipantRole(string roomCode, string targetUserId, string role)
    {
        roomCode = EnsureCurrentRoom(roomCode);
        if (role is not ("Editor" or "Viewer"))
            throw new HubException("Érvénytelen szerepkör.");
        var gate = Gate(roomCode);
        await gate.WaitAsync();
        try
        {
            var userId = CurrentUserId();
            if (targetUserId == userId)
                throw new HubException("A tulajdonos saját szerepköre nem változtatható meg.");
            await using var db = await dbFactory.CreateDbContextAsync();
            if (!await db.DrawingRooms.AnyAsync(x => x.RoomCode == roomCode && x.OwnerUserId == userId))
                throw new HubException("Csak a tulajdonos módosíthatja a jogosultságokat.");
            var participant = await db.RoomParticipants
                .SingleOrDefaultAsync(x => x.RoomCode == roomCode && x.UserId == targetUserId);
            if (participant is null || participant.Role == "Owner")
                throw new HubException("Érvénytelen célfelhasználó.");
            participant.Role = role;
            await db.SaveChangesAsync();
            await BroadcastSafeAsync("ParticipantRoleChanged", roomCode, roomCode, targetUserId, role);
        }
        finally { gate.Release(); }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items["room"] is string room)
        {
            try
            {
                await Clients.OthersInGroup(room).SendAsync("UserLeft", room,
                    Context.User?.Identity?.Name ?? "Felhasználó");
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Kapcsolatbontási értesítés sikertelen.");
            }
        }
        await base.OnDisconnectedAsync(exception);
    }

    private async Task<RoomSnapshot> JoinExistingRoomAsync(string roomCode, string role, string name)
    {
        var gate = Gate(roomCode);
        await gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var history = await db.Strokes.AsNoTracking()
                .Where(s => s.RoomCode == roomCode).OrderBy(s => s.Id).ToListAsync();
            var result = history.Select(ToDto).ToList();
            if (Context.Items["room"] is string previousRoom && previousRoom != roomCode)
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, previousRoom);
            await Groups.AddToGroupAsync(Context.ConnectionId, roomCode);
            Context.Items["room"] = roomCode;
            await BroadcastSafeAsync("UserJoined", roomCode, roomCode,
                Context.User?.Identity?.Name ?? "Felhasználó");
            return new RoomSnapshot(roomCode, name, role, result);
        }
        finally { gate.Release(); }
    }

    private async Task BroadcastSafeAsync(string eventName, string roomCode, params object[] args)
    {
        try { await Clients.Group(roomCode).SendCoreAsync(eventName, args); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SignalR-csoportesemény sikertelen: {EventName}", eventName);
        }
    }

    private static async Task<string?> GetRoleAsync(DrawingDbContext db, string roomCode, string userId) =>
        await db.RoomParticipants.AsNoTracking().Where(x => x.RoomCode == roomCode && x.UserId == userId)
            .Select(x => x.Role).SingleOrDefaultAsync();

    private static StrokeDto ToDto(DrawingStroke s) => new()
    {
        Id = s.Id, UserName = s.UserName, Color = s.Color,
        Width = s.Width, IsEraser = s.IsEraser,
        Points = JsonSerializer.Deserialize<List<PointDto>>(s.PointsJson, JsonOptions) ?? new()
    };

    private string EnsureCurrentRoom(string roomCode)
    {
        roomCode = CleanRoom(roomCode);
        if (Context.Items["room"] is not string joinedRoom || joinedRoom != roomCode)
            throw new HubException("Előbb csatlakozz ehhez a rajzszobához.");
        return roomCode;
    }

    private string CurrentUserId() => Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new HubException("Bejelentkezés szükséges.");

    private static async Task<string> GenerateUniqueRoomCodeAsync(DrawingDbContext db)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        for (var attempt = 0; attempt < 15; attempt++)
        {
            var code = new string(Enumerable.Range(0, 8)
                .Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
            if (!await db.DrawingRooms.AnyAsync(r => r.RoomCode == code)) return code;
        }
        throw new HubException("Nem sikerült új szobakódot létrehozni.");
    }

    private static string CleanRoom(string? value) =>
        new string((value ?? "").Where(c => (c >= 'A' && c <= 'Z') ||
                                         (c >= 'a' && c <= 'z') ||
                                         (c >= '0' && c <= '9'))
            .Take(32).ToArray()).ToUpperInvariant();

    private static string SafeColor(string? value) =>
        value is not null && Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$") ? value : "#222222";
}
