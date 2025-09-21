using System;

namespace KPIMonitor.Models
{
    /// <summary>
    /// Strongly-typed input for memo PDF rendering.
    /// </summary>
    public sealed class MemoDocument
    {
        // --- Content (EN) ---
        public string? To { get; set; }
        public string? From { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public string? Classification { get; set; }
        /// <summary>Display string for the date (already formatted as you want it to appear).</summary>
        public string? DateText { get; set; }
        public string? MemoNumber { get; set; }
        public string? Through { get; set; }
        // --- Content (AR) ---
        /// <summary>Arabic counterpart values if you want different rendering; if null, fall back to EN value.</summary>
        public string? ToAr { get; set; }
        public string? FromAr { get; set; }
        public string? SubjectAr { get; set; }
        public string? ClassificationAr { get; set; }

        // --- Images ---
        /// <summary>Shown once (first page only), full width.</summary>
        public byte[]? BannerImage { get; set; }
        /// <summary>Shown on every page, full width.</summary>
        public byte[]? FooterImage { get; set; }

        // --- Layout (points) ---
        /// <summary>Page outer margin. Default ~0.5in (36pt).</summary>
        public float PageMarginPt { get; set; } = 36f;
        /// <summary>Small top padding before banner so it never clips.</summary>
        public float HeaderTopPaddingPt { get; set; } = 6f;
        /// <summary>Optional extra spacing between column items.</summary>
        public float ContentSpacingPt { get; set; } = 10f;
        /// <summary>Space between the bilingual meta table and the body.</summary>
        public float BodyTopSpacingPt { get; set; } = 12f;

        // --- Colors (HEX) ---
        /// <summary>Green used for labels and headings.</summary>
        public string LabelColorHex { get; set; } = "#137B3C";
        /// <summary>Thin underline color between meta rows.</summary>
        public string UnderlineColorHex { get; set; } = "#137B3C";
        /// <summary>Heading title color (bilingual "INTERNAL MEMO / مذكرة داخلية").</summary>
        public string TitleColorHex { get; set; } = "#137B3C";

        // --- Typography (optional) ---
        /// <summary>If you want to enforce specific fonts, set these to installed font family names.</summary>
        public string? FontFamilyLatin { get; set; } = null;
        public string? FontFamilyArabic { get; set; } = null;

        // --- Convenience (safe fallbacks for AR strings) ---
        public string ToArSafe => string.IsNullOrWhiteSpace(ToAr) ? (To ?? "") : ToAr!;
        public string FromArSafe => string.IsNullOrWhiteSpace(FromAr) ? (From ?? "") : FromAr!;
        public string SubjectArSafe => string.IsNullOrWhiteSpace(SubjectAr) ? (Subject ?? "") : SubjectAr!;
        public string ClassificationArSafe => string.IsNullOrWhiteSpace(ClassificationAr) ? (Classification ?? "") : ClassificationAr!;
    }
}
