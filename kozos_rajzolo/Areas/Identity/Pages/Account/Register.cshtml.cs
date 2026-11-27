using System.ComponentModel.DataAnnotations;
using KozosRajzolo.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KozosRajzolo.Areas.Identity.Pages.Account;

public sealed class RegisterModel(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        [Required(ErrorMessage = "A megjelenített név megadása kötelező.")]
        [StringLength(60, MinimumLength = 2, ErrorMessage = "A név 2–60 karakter lehet.")]
        public string DisplayName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Az e-mail-cím megadása kötelező.")]
        [EmailAddress(ErrorMessage = "Érvényes e-mail-címet adj meg.")]
        public string Email { get; set; } = string.Empty;

        [Required, DataType(DataType.Password)]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "A jelszó legalább 8 karakter legyen.")]
        public string Password { get; set; } = string.Empty;

        [Required, DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "A két jelszó nem egyezik.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var user = new ApplicationUser
        {
            UserName = Input.Email.Trim(), Email = Input.Email.Trim(),
            DisplayName = Input.DisplayName.Trim()
        };
        var result = await userManager.CreateAsync(user, Input.Password);
        if (result.Succeeded)
        {
            await signInManager.SignInAsync(user, isPersistent: false);
            return Redirect("/");
        }
        foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
        return Page();
    }
}
