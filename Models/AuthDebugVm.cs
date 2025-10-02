// Models/AuthDebugVm.cs
using System.Collections.Generic;

namespace KPIMonitor.Models
{
    public sealed class AuthDebugVm
    {
        public string Reason { get; set; } = "";
        public string UserName { get; set; } = "";
        public bool IsAuthenticated { get; set; }
        public string RequestUrl { get; set; } = "";
        public string ReturnUrl { get; set; } = "";
        public string Method { get; set; } = "";
        public string RemoteIp { get; set; } = "";
        public bool HasSteervisionClaim { get; set; }
        public string SteervisionClaimValue { get; set; } = "";
        public List<AuthDebugClaim> Claims { get; set; } = new();
        public Dictionary<string, string> Headers { get; set; } = new();
        public List<string> Cookies { get; set; } = new();
    }

    public sealed class AuthDebugClaim
    {
        public string Type { get; set; } = "";
        public string Value { get; set; } = "";
    }
}
