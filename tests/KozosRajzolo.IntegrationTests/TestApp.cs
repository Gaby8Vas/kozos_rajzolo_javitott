using System.Security.Claims;
using System.Text.Encodings.Web;
using KozosRajzolo.Data;
using KozosRajzolo.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace KozosRajzolo.IntegrationTests;

/// <summary>
/// A teljes alkalmazás egy valódi MySQL-szerverrel, tesztenként saját, utána törölt adatbázissal.
/// A MySQL-kapcsolatot a KOZOS_RAJZOLO_TEST_MYSQL környezeti változó adja meg
/// (pl. "server=localhost;port=3306;user=root;password=..."); ha nincs megadva, a tesztek kimaradnak.
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public const string EnvVar = "KOZOS_RAJZOLO_TEST_MYSQL";
    private readonly string databaseName = "kozos_rajzolo_test_" + Guid.NewGuid().ToString("N")[..12];

    public static string? ServerConnection => Environment.GetEnvironmentVariable(EnvVar);

    public string ConnectionString
    {
        get
        {
            var builder = new MySqlConnectionStringBuilder(ServerConnection ?? "") { Database = databaseName };
            return builder.ConnectionString;
        }
    }

    public static TestApp Create()
    {
        Skip.If(string.IsNullOrWhiteSpace(ServerConnection),
            $"Nincs megadva a {EnvVar} környezeti változó, az integrációs tesztek kimaradnak.");
        return new TestApp();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:MySql", ConnectionString);
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = TestAuthHandler.SchemeName;
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });
        });
    }

    public async Task<TestUser> CreateUserAsync(string displayName)
    {
        // Az első hívás elindítja a szervert (és lefuttatja a migrációkat).
        _ = Server;
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = Guid.NewGuid().ToString("N")[..8] + "@example.org";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = displayName };
        var result = await users.CreateAsync(user, "Teszt12345");
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        return new TestUser(user.Id, email, displayName);
    }

    public async Task<HubConnection> ConnectAsync(TestUser user)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, "drawingHub"), options =>
            {
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.Headers[TestAuthHandler.UserHeader] = user.Header;
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    public async Task<DrawingDbContext> CreateDbAsync()
    {
        var factory = Services.GetRequiredService<IDbContextFactory<DrawingDbContext>>();
        return await factory.CreateDbContextAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await using var connection = new MySqlConnection(ServerConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS `{databaseName}`;";
        await command.ExecuteNonQueryAsync();
    }
}

public sealed record TestUser(string Id, string Email, string DisplayName)
{
    public string Header => $"{Id}|{Email}|{DisplayName}";
}

/// <summary>Tesztbeli hitelesítés: a felhasználót egy fejléc adja meg.</summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var value))
            return Task.FromResult(AuthenticateResult.NoResult());
        var parts = value.ToString().Split('|');
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, parts[0]),
            new Claim(ClaimTypes.Name, parts[1]),
            new Claim(AppClaimsPrincipalFactory.DisplayNameClaim, parts[2])
        ], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
