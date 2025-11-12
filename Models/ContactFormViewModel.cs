using System.ComponentModel.DataAnnotations;

namespace Lararafelagid.Models
{
    public class ContactFormViewModel
    {
        [Required(ErrorMessage = "Skriva navnið.")]
        [Display(Name = "Navn")]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "Skriva teldupost.")]
        [EmailAddress(ErrorMessage = "Ógildigur teldupostur.")]
        [Display(Name = "Teldupostur")]
        public string Email { get; set; } = string.Empty;

        [Phone(ErrorMessage = "Ógildigt telefonnummar.")]
        [Display(Name = "Telefon")]
        public string? Phone { get; set; }

        [Required(ErrorMessage = "Skriva boðini.")]
        [Display(Name = "Boð")]
        public string Message { get; set; } = string.Empty;

        // Simple honeypot (bots fill it in; humans never see it)
        public string? Website { get; set; }
    }
}
