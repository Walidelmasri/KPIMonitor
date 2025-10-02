namespace KPIMonitor.Models
{
    public sealed class AuthDebugVm
    {
        public int StatusCode { get; set; } = 403;

        public string? UserName { get; set; }
        public bool IsAuthenticated { get; set; }

        public string? ReturnUrl { get; set; }
        public string? RequestUrl { get; set; }
        public string? Method { get; set; }
        public string? RemoteIp { get; set; }

        public List<(string Type, string Value)> Claims { get; set; } = new();

        public bool HasSteervisionClaim { get; set; }
        public string? SteervisionClaimValue { get; set; }

        public string Reason { get; set; } = "Unknown";

        // A few useful headers (don’t dump everything)
        public Dictionary<string, string> Headers { get; set; } = new();
        public List<string> Cookies { get; set; } = new();
    }
}
