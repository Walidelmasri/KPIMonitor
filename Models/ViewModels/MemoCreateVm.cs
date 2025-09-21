using Microsoft.AspNetCore.Http;

namespace KPIMonitor.ViewModels
{
    public sealed class MemoCreateVm
    {
        // Text
        public string To { get; set; } = "";
        public string From { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Body { get; set; } = "";
        public string Classification { get; set; } = "";
        public string MemoNumber { get; set; } = "";
        public string? Through { get; set; } = "";
        /// <summary>Optional: override the printed date (else service will auto-generate)</summary>
        public string DateText { get; set; } = "";
        // Images: choose either upload OR defaults in wwwroot
        public bool UseDefaultImages { get; set; } = true;
        public IFormFile? BannerUpload { get; set; }
        public IFormFile? FooterUpload { get; set; }
    }
}
