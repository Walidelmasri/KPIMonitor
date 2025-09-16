using System;

namespace KPIMonitor.Services
{
    /// <summary>
    /// Canonical status codes used across UI and services.
    /// </summary>
    public static class StatusCodes
    {
        // Green
        public const string Ok = "conforme";

        // Orange
        public const string CatchingUp = "rattrapage";

        // Red
        public const string NeedsAttention = "ecart";

        // Blue
        public const string DataMissing = "attente";
    }
}
