using System.Globalization;

namespace KPIMonitor.Helpers
{
    public static class LocalizationHelper
    {
        public static string Get(string? ar, string en)
        {
            var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

            if (culture == "ar" && !string.IsNullOrWhiteSpace(ar))
                return ar;

            return en;
        }
    }
}