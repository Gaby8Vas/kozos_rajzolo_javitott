using KozosRajzolo.Components;
using KozosRajzolo.Data;
using KozosRajzolo.Hubs;
using KozosRajzolo.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorPages();
builder.Services.AddSignalR();

var connection = builder.Configuration.GetConnectionString("MySql")
    ?? throw new InvalidOperationException("Hiányzik a MySQL kapcsolat beállítása (ConnectionStrings:MySql).");
builder.Services.AddDbContextFactory<DrawingDbContext>(options =>
    options.UseMySql(connection, ServerVersion.AutoDetect(connection)));

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
})
.AddEntityFrameworkStores<DrawingDbContext>()
.AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Identity/Account/Login";
    options.AccessDeniedPath = "/Identity/Account/Login";
    options.Cookie.Name = "KozosRajzolo.Auth";
    options.SlidingExpiration = true;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
// A helyi HTTP-tesztben nincs HTTPS-átirányítás.
if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Indításkor lefutnak az EF Core migrációk (a régi, EnsureCreated-del készült adatbázist is átveszi).
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DrawingDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await DatabaseInitializer.MigrateAsync(db, app.Logger);
}

app.MapGet("/Error", () => Results.Problem("Váratlan szerverhiba történt.")).ExcludeFromDescription();
app.MapRazorPages();
app.MapHub<DrawingHub>("/drawingHub").RequireAuthorization();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();


