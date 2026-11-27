using System.ComponentModel.DataAnnotations;
using KozosRajzolo.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KozosRajzolo.Areas.Identity.Pages.Account;

public sealed class LoginModel(SignInManager<ApplicationUser> signInManager) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public sealed class InputModel
    {
        [Required, EmailAddress(ErrorMessage = "Érvényes e-mail-címet adj meg.")]
        public string Email { get; set; } = string.Empty;
        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
        public bool RememberMe { get; set; }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var result = await signInManager.PasswordSignInAsync(Input.Email.Trim(), Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
            return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
        if (result.IsLockedOut) ModelState.AddModelError(string.Empty, "Túl sok sikertelen próbálkozás. Próbáld újra néhány perc múlva.");
        else ModelState.AddModelError(string.Empty, "Hibás e-mail-cím vagy jelszó.");
        return Page();
    }
}
