using Microsoft.AspNetCore.Identity;

namespace KozosRajzolo.Models;

public sealed class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
}
