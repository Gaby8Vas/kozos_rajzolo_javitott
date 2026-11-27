using System.Collections.Concurrent;
using KozosRajzolo.Hubs;
using KozosRajzolo.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;

namespace KozosRajzolo.IntegrationTests;

public sealed class HubTests
{
    private static StrokeDto Line(double x, string? clientKey = null) => new()
    {
        Color = "#e74c3c", Width = 5, ClientKey = clientKey,
        Points = [new PointDto { X = x, Y = 10 }, new PointDto { X = x + 20, Y = 30 }]
    };

    private static async Task<T> WithTimeout<T>(Task<T> task) =>
        await task.WaitAsync(TimeSpan.FromSeconds(10));

    [SkippableFact]
    public async Task Megtekinto_csak_jogosultsag_utan_rajzolhat()
    {
        await using var app = TestApp.Create();
        var owner = await app.CreateUserAsync("Tulaj Tamás");
        var guest = await app.CreateUserAsync("Vendég Vera");
        await using var ownerConn = await app.ConnectAsync(owner);
        await using var guestConn = await app.ConnectAsync(guest);

        var room = await ownerConn.InvokeAsync<DrawingHub.RoomSnapshot>("CreateRoom", "Teszt szoba");
        Assert.Equal("Owner", room.Role);
        var joined = await guestConn.InvokeAsync<DrawingHub.RoomSnapshot>("JoinRoom", room.RoomCode);
        Assert.Equal("Viewer", joined.Role);

        await Assert.ThrowsAsync<HubException>(() => guestConn.InvokeAsync<StrokeDto>("SendStroke", room.RoomCode, Line(10)));

        var roleChanged = new TaskCompletionSource<string>();
        guestConn.On<string, string, string>("ParticipantRoleChanged", (_, userId, role) =>
        {
            if (userId == guest.Id) roleChanged.TrySetResult(role);
        });
        var received = new TaskCompletionSource<StrokeDto>();
        ownerConn.On<string, StrokeDto>("ReceiveStroke", (_, stroke) => received.TrySetResult(stroke));

        await ownerConn.InvokeAsync("SetParticipantRole", room.RoomCode, guest.Id, "Editor");
        Assert.Equal("Editor", await WithTimeout(roleChanged.Task));

        var saved = await guestConn.InvokeAsync<StrokeDto>("SendStroke", room.RoomCode, Line(10));
        Assert.True(saved.Id > 0);
        Assert.Equal(guest.Id, saved.UserId);
        Assert.Equal("Vendég Vera", saved.UserName);
        Assert.Equal(saved.Id, (await WithTimeout(received.Task)).Id);
    }

    [SkippableFact]
    public async Task Visszavonas_es_ujra_a_sajat_vonalakon()
    {
        await using var app = TestApp.Create();
        var owner = await app.CreateUserAsync("Tulaj");
        await using var conn = await app.ConnectAsync(owner);
        var room = await conn.InvokeAsync<DrawingHub.RoomSnapshot>("CreateRoom", "Undo");

        var first = await conn.InvokeAsync<StrokeDto>("SendStroke", room.RoomCode, Line(10));
        var second = await conn.InvokeAsync<StrokeDto>("SendStroke", room.RoomCode, Line(50));

        Assert.True(await conn.InvokeAsync<bool>("UndoLastStroke", room.RoomCode));
        Assert.True(await conn.InvokeAsync<bool>("RedoLastStroke", room.RoomCode));
        Assert.False(await conn.InvokeAsync<bool>("RedoLastStroke", room.RoomCode));

        Assert.True(await conn.InvokeAsync<bool>("UndoLastStroke", room.RoomCode));
        var rejoin = await conn.InvokeAsync<DrawingHub.RoomSnapshot>("JoinRoom", room.RoomCode);
        Assert.Equal([first.Id], rejoin.Strokes.Select(s => s.Id));

        // Új vonal után a visszavont vonal már nem hozható vissza.
        await conn.InvokeAsync<StrokeDto>("SendStroke", room.RoomCode, Line(90));
        Assert.False(await conn.InvokeAsync<bool>("RedoLastStroke", room.RoomCode));
        await using var db = await app.CreateDbAsync();
        Assert.False(await db.Strokes.AnyAsync(s => s.Id == second.Id));
    }

    [SkippableFact]
    public async Task Elo_vonal_megerkezik_de_nem_mentodik()
    {
        await using var app = TestApp.Create();
        var owner = await app.CreateUserAsync("Tulaj");
        var guest = await app.CreateUserAsync("Néző");
        await using var ownerConn = await app.ConnectAsync(owner);
        await using var guestConn = await app.ConnectAsync(guest);
        var room = await ownerConn.InvokeAsync<DrawingHub.RoomSnapshot>("CreateRoom", "Élő");
        await guestConn.InvokeAsync<DrawingHub.RoomSnapshot>("JoinRoom", room.RoomCode);

        var liveSeen = new TaskCompletionSource<LiveStrokeDto>();
        guestConn.On<string, LiveStrokeDto>("LiveStroke", (_, live) => liveSeen.TrySetResult(live));
        var ownerLive = new ConcurrentBag<LiveStrokeDto>();
        ownerConn.On<string, LiveStrokeDto>("LiveStroke", (_, live) => ownerLive.Add(live));

        await ownerConn.InvokeAsync("StreamStroke", room.RoomCode, new LiveStrokeDto
        {
            ClientKey = "c1", Color = "#3498db", Width = 4,
            Points = [new PointDto { X = 5, Y = 5 }, new PointDto { X = 8, Y = 9 }]
        });
        var live = await WithTimeout(liveSeen.Task);
        Assert.EndsWith(":c1", live.ClientKey);
        Assert.Equal(2, live.Points.Count);

        // A megtekintő élő vonala nem jut el a többiekhez.
        await guestConn.InvokeAsync("StreamStroke", room.RoomCode, new LiveStrokeDto
        {
            ClientKey = "g1", Width = 4, Points = [new PointDto { X = 1, Y = 1 }]
        });
        await Task.Delay(300);
        Assert.Empty(ownerLive);

        var saved = await ownerConn.InvokeAsync<StrokeDto>("SendStroke", room.RoomCode, Line(5, "c1"));
        Assert.Equal(live.ClientKey, saved.LiveKey);
        await using var db = await app.CreateDbAsync();
        Assert.Equal(1, await db.Strokes.CountAsync());
    }

    [SkippableFact]
    public async Task Elozmenyek_lapozva_toltodnek_be()
    {
        await using var app = TestApp.Create();
        var owner = await app.CreateUserAsync("Tulaj");
        await using var conn = await app.ConnectAsync(owner);
        var room = await conn.InvokeAsync<DrawingHub.RoomSnapshot>("CreateRoom", "Nagy rajz");

        await using (var db = await app.CreateDbAsync())
        {
            for (var i = 0; i < 650; i++)
                db.Strokes.Add(new DrawingStroke
                {
                    RoomCode = room.RoomCode, UserId = owner.Id, UserName = "Tulaj",
                    PointsJson = $"[{{\"x\":{i % 1400},\"y\":1}}]"
                });
            await db.SaveChangesAsync();
        }

        var snapshot = await conn.InvokeAsync<DrawingHub.RoomSnapshot>("JoinRoom", room.RoomCode);
        Assert.Equal(DrawingHub.HistoryPageSize, snapshot.Strokes.Count);
        Assert.True(snapshot.HasMore);

        var all = snapshot.Strokes.ToList();
        var hasMore = snapshot.HasMore;
        while (hasMore)
        {
            var page = await conn.InvokeAsync<DrawingHub.StrokePage>("GetStrokePage", room.RoomCode, all[^1].Id);
            all.AddRange(page.Strokes);
            hasMore = page.HasMore;
        }
        Assert.Equal(650, all.Count);
        Assert.Equal(all.Select(s => s.Id).OrderBy(x => x), all.Select(s => s.Id));
    }

    [SkippableFact]
    public async Task Sajat_szobak_listaja()
    {
        await using var app = TestApp.Create();
        var owner = await app.CreateUserAsync("Tulaj");
        var guest = await app.CreateUserAsync("Vendég");
        await using var ownerConn = await app.ConnectAsync(owner);
        await using var guestConn = await app.ConnectAsync(guest);

        var a = await ownerConn.InvokeAsync<DrawingHub.RoomSnapshot>("CreateRoom", "Első");
        var b = await ownerConn.InvokeAsync<DrawingHub.RoomSnapshot>("CreateRoom", "Második");
        await guestConn.InvokeAsync<DrawingHub.RoomSnapshot>("JoinRoom", b.RoomCode);

        var ownerRooms = await ownerConn.InvokeAsync<List<DrawingHub.MyRoomInfo>>("GetMyRooms");
        Assert.Equal(new[] { a.RoomCode, b.RoomCode }.Order(), ownerRooms.Select(r => r.RoomCode).Order());
        var guestRooms = await guestConn.InvokeAsync<List<DrawingHub.MyRoomInfo>>("GetMyRooms");
        var only = Assert.Single(guestRooms);
        Assert.Equal(("Második", "Viewer"), (only.Name, only.Role));
    }
}
