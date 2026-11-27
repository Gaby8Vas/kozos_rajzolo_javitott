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
    // Szobánkénti szerepkör-gyorsítótár az élő (nem mentett) vonalrészletekhez,
    // hogy ne kelljen minden pár pontnál adatbázist olvasni.
    private static readonly ConcurrentDictionary<(string Room, string User), string> RoleCache = new();
    private static SemaphoreSlim Gate(string roomCode) =>
        RoomLocks.GetOrAdd(roomCode, static _ => new SemaphoreSlim(1, 1));

    public sealed record ParticipantInfo(string UserId, string DisplayName, string Role);
    public sealed record MyRoomInfo(string RoomCode, string Name, string Role, DateTime LastActivityUtc);
    public sealed record RoomSnapshot(string RoomCode, string RoomName, string Role, List<StrokeDto> Strokes, bool HasMore);
    public sealed record StrokePage(List<StrokeDto> Strokes, bool HasMore);

    // Az előzmények ekkora adagokban töltődnek be csatlakozáskor.
    public const int HistoryPageSize = 300;

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
        ValidateGeometry(stroke.Width, stroke.Points);

        var gate = Gate(roomCode);
        await gate.WaitAsync();
        try
        {
            var userId = CurrentUserId();
            await using var db = await dbFactory.CreateDbContextAsync();
            var role = await GetRoleAsync(db, roomCode, userId);
            if (role is not ("Owner" or "Editor"))
                throw new HubException("Ehhez a művelethez szerkesztői jogosultság kell.");

            var color = SafeColor(stroke.Color);
            var entity = new DrawingStroke
            {
                RoomCode = roomCode,
                UserId = userId,
                UserName = Context.User.DisplayName(),
                Color = color,
                Width = stroke.Width,
                IsEraser = stroke.IsEraser,
                PointsJson = JsonSerializer.Serialize(stroke.Points, JsonOptions)
            };
            // Új vonal után a korábban visszavont saját vonalak már nem állíthatók vissza.
            await db.Strokes
                .Where(s => s.RoomCode == roomCode && s.UserId == userId && s.UndoneAtUtc != null)
                .ExecuteDeleteAsync();
            db.Strokes.Add(entity);
            await db.SaveChangesAsync();
            // A küldő visszatérési értéket, a többi kapcsolat eseményt kap.
            var saved = ToDto(entity);
            // Az élő előnézetet a többiek ez alapján cserélik a mentett vonalra.
            saved.LiveKey = LiveKey(stroke.ClientKey);
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

            var previous = await db.Strokes
                .Where(s => s.RoomCode == roomCode && s.UserId == userId && s.UndoneAtUtc == null)
                .OrderByDescending(s => s.Id).FirstOrDefaultAsync();
            if (previous is null) return false;
            // A vonal nem törlődik, csak visszavontnak jelöljük, így az „Újra” visszahozhatja.
            previous.UndoneAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await BroadcastSafeAsync("StrokeRemoved", roomCode, roomCode, previous.Id);
            return true;
        }
        finally { gate.Release(); }
    }

    /// <summary>A legutóbb visszavont saját vonal visszaállítása (redo).</summary>
    public async Task<bool> RedoLastStroke(string roomCode)
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
                throw new HubException("Az újra végrehajtáshoz szerkesztői jogosultság kell.");

            var undone = await db.Strokes
                .Where(s => s.RoomCode == roomCode && s.UserId == userId && s.UndoneAtUtc != null)
                .OrderByDescending(s => s.UndoneAtUtc).ThenByDescending(s => s.Id)
                .FirstOrDefaultAsync();
            if (undone is null) return false;
            undone.UndoneAtUtc = null;
            await db.SaveChangesAsync();
            // Az eredeti azonosítóval tér vissza, így a rajzolási sorrendben a helyére kerül.
            await BroadcastSafeAsync("ReceiveStroke", roomCode, roomCode, ToDto(undone));
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

    /// <summary>A bejelentkezett felhasználó szobái, a legutóbb aktívval kezdve.</summary>
    public async Task<List<MyRoomInfo>> GetMyRooms()
    {
        var userId = CurrentUserId();
        await using var db = await dbFactory.CreateDbContextAsync();
        var rooms = await (from participant in db.RoomParticipants
                           join room in db.DrawingRooms on participant.RoomCode equals room.RoomCode
                           where participant.UserId == userId
                           select new
                           {
                               room.RoomCode, room.Name, participant.Role, room.CreatedAtUtc,
                               LastStroke = db.Strokes.Where(s => s.RoomCode == room.RoomCode)
                                   .Max(s => (DateTime?)s.CreatedAt)
                           })
            .ToListAsync();
        return rooms
            .Select(r => new MyRoomInfo(r.RoomCode, r.Name, r.Role,
                r.LastStroke is { } last && last > r.CreatedAtUtc ? last : r.CreatedAtUtc))
            .OrderByDescending(r => r.LastActivityUtc)
            .Take(50)
            .ToList();
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
            RoleCache[(roomCode, targetUserId)] = role;
            await BroadcastSafeAsync("ParticipantRoleChanged", roomCode, roomCode, targetUserId, role);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Rajzolás közben küldött vonalrészlet. Nem kerül adatbázisba, csak a szoba
    /// többi résztvevője látja előnézetként, amíg a kész vonal meg nem érkezik.
    /// </summary>
    public async Task StreamStroke(string roomCode, LiveStrokeDto? live)
    {
        roomCode = CleanRoom(roomCode);
        if (Context.Items["room"] is not string joined || joined != roomCode) return;
        if (live is null || live.Points is null || live.Points.Count is < 1 or > 1000) return;
        if (string.IsNullOrEmpty(live.ClientKey) || live.ClientKey.Length > 40) return;
        try { ValidateGeometry(live.Width, live.Points); }
        catch (HubException) { return; }
        if (!RoleCache.TryGetValue((roomCode, CurrentUserId()), out var role) || role is not ("Owner" or "Editor"))
            return;

        var outgoing = new LiveStrokeDto
        {
            ClientKey = LiveKey(live.ClientKey)!,
            Color = SafeColor(live.Color), Width = live.Width, IsEraser = live.IsEraser,
            Reset = live.Reset, Points = live.Points
        };
        try { await Clients.OthersInGroup(roomCode).SendAsync("LiveStroke", roomCode, outgoing); }
        catch (Exception ex) { logger.LogDebug(ex, "Élő vonalrészlet küldése sikertelen."); }
    }

    /// <summary>A félbehagyott (meg nem mentett) élő vonal eltávolítása a többieknél.</summary>
    public async Task CancelLiveStroke(string roomCode, string? clientKey)
    {
        roomCode = CleanRoom(roomCode);
        if (Context.Items["room"] is not string joined || joined != roomCode) return;
        if (string.IsNullOrEmpty(clientKey) || clientKey.Length > 40) return;
        try { await Clients.OthersInGroup(roomCode).SendAsync("LiveStrokeCancelled", roomCode, LiveKey(clientKey)); }
        catch (Exception ex) { logger.LogDebug(ex, "Élő vonal visszavonása sikertelen."); }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items["room"] is string room)
        {
            try
            {
                await Clients.OthersInGroup(room).SendAsync("LiveConnectionGone", room, Context.ConnectionId + ":");
                await Clients.OthersInGroup(room).SendAsync("UserLeft", room, Context.User.DisplayName());
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
            var (result, hasMore) = await LoadPageAsync(db, roomCode, 0);
            if (Context.Items["room"] is string previousRoom && previousRoom != roomCode)
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, previousRoom);
            await Groups.AddToGroupAsync(Context.ConnectionId, roomCode);
            Context.Items["room"] = roomCode;
            var userId = CurrentUserId();
            RoleCache[(roomCode, userId)] = role;
            await BroadcastSafeAsync("UserJoined", roomCode, roomCode, Context.User.DisplayName());
            return new RoomSnapshot(roomCode, name, role, result, hasMore);
        }
        finally { gate.Release(); }
    }

    /// <summary>Az előzmények következő adagja az adott azonosító után.</summary>
    public async Task<StrokePage> GetStrokePage(string roomCode, long afterId)
    {
        roomCode = EnsureCurrentRoom(roomCode);
        await using var db = await dbFactory.CreateDbContextAsync();
        var (strokes, hasMore) = await LoadPageAsync(db, roomCode, afterId);
        return new StrokePage(strokes, hasMore);
    }

    private static async Task<(List<StrokeDto> Strokes, bool HasMore)> LoadPageAsync(
        DrawingDbContext db, string roomCode, long afterId)
    {
        var rows = await db.Strokes.AsNoTracking()
            .Where(s => s.RoomCode == roomCode && s.UndoneAtUtc == null && s.Id > afterId)
            .OrderBy(s => s.Id)
            .Take(HistoryPageSize + 1)
            .ToListAsync();
        var hasMore = rows.Count > HistoryPageSize;
        return (rows.Take(HistoryPageSize).Select(ToDto).ToList(), hasMore);
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
        Id = s.Id, UserId = s.UserId, UserName = s.UserName, Color = s.Color,
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

    private string? LiveKey(string? clientKey) =>
        string.IsNullOrEmpty(clientKey) || clientKey.Length > 40 ? null : Context.ConnectionId + ":" + clientKey;

    private static void ValidateGeometry(double width, List<PointDto> points)
    {
        if (!double.IsFinite(width) || width < 1 || width > 40)
            throw new HubException("A vonalvastagság kívül esik a megengedett tartományon.");
        if (points.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y)
                            || p.X < -50 || p.X > 1450 || p.Y < -50 || p.Y > 900))
            throw new HubException("Érvénytelen rajzolási koordináták.");
    }

    private static string SafeColor(string? value) =>
        value is not null && Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$") ? value : "#222222";
}
