using System;
using System.Collections.Generic;

namespace KPIMonitor.Services
{
    public static class StatusPalette
    {
        private static readonly HashSet<string> Reds    = new(StringComparer.OrdinalIgnoreCase){ "red", "ecart" };
        private static readonly HashSet<string> Oranges = new(StringComparer.OrdinalIgnoreCase){ "orange", "rattrapage" };
        private static readonly HashSet<string> Blues   = new(StringComparer.OrdinalIgnoreCase){ "blue", "attente" };
        private static readonly HashSet<string> Greens  = new(StringComparer.OrdinalIgnoreCase){ "green", "conforme" };

        public static string Canonicalize(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "";
            if (Reds.Contains(code))    return "red";
            if (Oranges.Contains(code)) return "orange";
            if (Blues.Contains(code))   return "blue";
            if (Greens.Contains(code))  return "green";
            return "";
        }

        // Severity for “worst wins”
        public static int Severity(string? canonical)
            => canonical switch
            {
                "red"    => 3,
                "orange" => 2,
                "blue"   => 1,
                "green"  => 0,
                _        => -1
            };

        public static (string Label, string Hex) Visual(string? canonical)
            => canonical switch
            {
                "green"  => ("Ok",               "#28a745"),
                "red"    => ("Needs Attention",  "#dc3545"),
                "orange" => ("Catching Up",      "#fd7e14"),
                "blue"   => ("Data Missing",     "#0d6efd"),
                _        => ("—",                "#6c757d")
            };
    }
}
