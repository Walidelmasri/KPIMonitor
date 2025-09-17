using System.IO;
using System.Threading.Tasks;
using KPIMonitor.Models;
using KPIMonitor.Services.Abstractions;
using KPIMonitor.ViewModels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace KPIMonitor.Controllers
{
    public class MemoController : Controller
    {
        private readonly IMemoPdfService _pdf;
        private readonly IWebHostEnvironment _env;

        public MemoController(IMemoPdfService pdf, IWebHostEnvironment env)
        {
            _pdf = pdf;
            _env = env;
        }

        [HttpGet]
        public IActionResult Create() => View(new MemoCreateVm());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(MemoCreateVm vm)
        {
            if (!ModelState.IsValid)
                return View(vm);

            // ---- LINK YOUR PICTURES HERE ----
            // Option A: Defaults from wwwroot/images (set your file names)
            var defaultBannerPath = Path.Combine(_env.WebRootPath, "images", "memo-banner.png");
            var defaultFooterPath = Path.Combine(_env.WebRootPath, "images", "memo-footer.png");
            // Put your actual files at: wwwroot/images/memo-banner.png and memo-footer.png

            byte[]? banner = null;
            byte[]? footer = null;

            if (vm.UseDefaultImages)
            {
                if (System.IO.File.Exists(defaultBannerPath))
                    banner = await System.IO.File.ReadAllBytesAsync(defaultBannerPath);
                if (System.IO.File.Exists(defaultFooterPath))
                    footer = await System.IO.File.ReadAllBytesAsync(defaultFooterPath);
            }
            else
            {
                if (vm.BannerUpload is { Length: > 0 })
                {
                    using var ms = new MemoryStream();
                    await vm.BannerUpload.CopyToAsync(ms);
                    banner = ms.ToArray();
                }
                if (vm.FooterUpload is { Length: > 0 })
                {
                    using var ms = new MemoryStream();
                    await vm.FooterUpload.CopyToAsync(ms);
                    footer = ms.ToArray();
                }
            }
            // ---- END LINK SECTION ----

            var doc = new MemoDocument
            {
                To = vm.To,
                From = vm.From,
                Subject = vm.Subject,
                Body = vm.Body,
                Classification = vm.Classification,
                BannerImage = banner,
                FooterImage = footer
            };

            var pdf = await _pdf.GenerateAsync(doc);
            var fileName = $"Memo_{System.DateTime.UtcNow:yyyyMMdd_HHmm}.pdf";
            return File(pdf, "application/pdf", fileName);
        }
    }
}
