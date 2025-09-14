// KPIMonitor/Services/PeriodEditPolicy.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace KPIMonitor.Services
{
    public static class PeriodEditPolicy
    {
        // Cross-platform: try IANA first, then Windows, then safe UTC+3 fallback (KSA has no DST).
        private static readonly TimeZoneInfo RiyadhTz = ResolveRiyadhTz();

        private static TimeZoneInfo ResolveRiyadhTz()
        {
            // Prefer not to throw in static initialization — try multiple IDs.
            foreach (var id in new[] { "Asia/Riyadh", "Arab Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch { /* try next */ }
            }

            // Last resort: fixed offset, no DST (sufficient for Saudi).
            return TimeZoneInfo.CreateCustomTimeZone(
                id: "UTC+03",
                baseUtcOffset: TimeSpan.FromHours(3),
                displayName: "UTC+03 (fallback for Riyadh)",
                standardDisplayName: "UTC+03");
        }

        private static DateTime ToRiyadh(DateTime utcNow)
        {
            if (utcNow.Kind != DateTimeKind.Utc)
                utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

            return TimeZoneInfo.ConvertTimeFromUtc(utcNow, RiyadhTz);
        }

        public sealed class MonthlyWindow
        {
            public HashSet<int> ActualMonths   { get; } = new();
            public HashSet<int> ForecastMonths { get; } = new();
        }

        public sealed class QuarterlyWindow
        {
            public HashSet<int> ActualQuarters   { get; } = new();
            public HashSet<int> ForecastQuarters { get; } = new();
        }

        public static MonthlyWindow ComputeMonthlyWindow(int year, DateTime utcNow)
        {
            var now = ToRiyadh(utcNow);
            int currentMonth = now.Month;

            var w = new MonthlyWindow();

            // Always allow CURRENT month Actual (for this year)
            if (year == now.Year)
                w.ActualMonths.Add(currentMonth);

            // Also allow LAST CLOSED month within +1 month grace (even if last year)
            int lastClosedYear  = (now.Month == 1) ? now.Year - 1 : now.Year;
            int lastClosedMonth = (now.Month == 1) ? 12 : now.Month - 1;

            var lastClosedEndLocal = new DateTime(
                lastClosedYear,
                lastClosedMonth,
                DateTime.DaysInMonth(lastClosedYear, lastClosedMonth),
                23, 59, 59, DateTimeKind.Unspecified); // local wall-clock
            var graceEnd = lastClosedEndLocal.AddMonths(1);

            if (lastClosedYear == year && now <= graceEnd)
                w.ActualMonths.Add(lastClosedMonth);

            // Forecasts: current..Dec for this year, all months for future years
            if (year == now.Year)
            {
                for (int m = currentMonth; m <= 12; m++) w.ForecastMonths.Add(m);
            }
            else if (year > now.Year)
            {
                for (int m = 1; m <= 12; m++) w.ForecastMonths.Add(m);
            }

            return w;
        }

        public static QuarterlyWindow ComputeQuarterlyWindow(int year, DateTime utcNow)
        {
            var now = ToRiyadh(utcNow);
            int currentQ = ((now.Month - 1) / 3) + 1;

            var w = new QuarterlyWindow();

            // Always allow CURRENT quarter Actual (for this year)
            if (year == now.Year)
                w.ActualQuarters.Add(currentQ);

            // Also allow LAST CLOSED quarter within +1 month grace (even if last year)
            int lastClosedYear = (currentQ == 1) ? now.Year - 1 : now.Year;
            int lastClosedQ    = (currentQ == 1) ? 4 : currentQ - 1;

            var (qy, qm, qd) = QuarterEnd(lastClosedYear, lastClosedQ);
            var lastClosedEndLocal = new DateTime(qy, qm, qd, 23, 59, 59, DateTimeKind.Unspecified);
            var graceEnd = lastClosedEndLocal.AddMonths(1);

            if (lastClosedYear == year && now <= graceEnd)
                w.ActualQuarters.Add(lastClosedQ);

            // Forecasts: current..Q4 for this year, all quarters for future years
            if (year == now.Year)
            {
                for (int q = currentQ; q <= 4; q++) w.ForecastQuarters.Add(q);
            }
            else if (year > now.Year)
            {
                for (int q = 1; q <= 4; q++) w.ForecastQuarters.Add(q);
            }

            return w;
        }

        public static bool IsMonthly(int? monthsPerYear, int? quartersPerYear)
        {
            if (monthsPerYear.HasValue && monthsPerYear.Value == 12) return true;
            if (quartersPerYear.HasValue && quartersPerYear.Value == 4) return false;
            return true; // default monthly if ambiguous
        }

        private static (int year, int month, int day) QuarterEnd(int year, int q)
        {
            int month = q * 3;
            int day   = DateTime.DaysInMonth(year, month);
            return (year, month, day);
        }
    }
}
