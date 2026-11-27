using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace KozosRajzolo.Models;

/// <summary>A belépési sütibe a megjelenített nevet is beleteszi.</summary>
public sealed class AppClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, options)
{
    public const string DisplayNameClaim = "display_name";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
            identity.AddClaim(new Claim(DisplayNameClaim, user.DisplayName));
        return identity;
    }
}

public static class UserNameExtensions
{
    /// <summary>Megjelenített név, ennek hiányában az e-mail-cím (régi sütiknél).</summary>
    public static string DisplayName(this ClaimsPrincipal? user)
    {
        var name = user?.FindFirst(AppClaimsPrincipalFactory.DisplayNameClaim)?.Value
                   ?? user?.Identity?.Name ?? "Felhasználó";
        return name[..Math.Min(name.Length, 60)];
    }
}
