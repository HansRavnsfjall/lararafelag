using System;
using System.Threading.Tasks;
using Lararafelagid.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Mail;
using Umbraco.Cms.Core.Models.Email;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Common.Controllers;
using Umbraco.Cms.Web.Website.Controllers;

namespace Lararafelagid.Controllers
{
    public class ContactSurfaceController : SurfaceController
    {
        private readonly IEmailSender _emailSender;
        private readonly GlobalSettings _globalSettings;

        public ContactSurfaceController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IEmailSender emailSender,
            IOptions<GlobalSettings> globalSettings)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _emailSender = emailSender;
            _globalSettings = globalSettings.Value;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]   // keep ASP.NET Core CSRF protection
        public async Task<IActionResult> Submit(ContactFormViewModel vm)
        {
            // Honeypot
            if (!string.IsNullOrWhiteSpace(vm.Website))
            {
                TempData["ContactForm.Success"] = "true";
                return CurrentUmbracoPage();
            }

            if (!ModelState.IsValid)
            {
                TempData["ContactForm.Success"] = "false";
                return CurrentUmbracoPage();
            }

            var from = string.IsNullOrWhiteSpace(_globalSettings.Smtp?.From)
                ? "no-reply@yourdomain.tld"
                : _globalSettings.Smtp.From;

            var to = "online@virka.fo";
            var subject = $"Contact form frá {vm.Name}";
            var body = $@"Nýggj boð komu inn umvegis kontaktformið:

Navn: {vm.Name}
Teldupostur: {vm.Email}
Telefon: {vm.Phone}

Boð:
{vm.Message}

— Sent {DateTimeOffset.Now:yyyy-MM-dd HH:mm}";

            var email = new EmailMessage(from, to, subject, body, false);

            await _emailSender.SendAsync(email, "ContactForm");

            TempData["ContactForm.Success"] = "true";
            return CurrentUmbracoPage();
        }
    }
}
