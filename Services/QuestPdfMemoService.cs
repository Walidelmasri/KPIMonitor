using System;
using System.Threading.Tasks;
using KPIMonitor.Models;
using KPIMonitor.Services.Abstractions;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using QuestPDF.Helpers;

namespace KPIMonitor.Services
{
    public sealed class QuestPdfMemoService : IMemoPdfService
    {
        // Tiny helper: detect Arabic characters to switch alignment/RTL per line
        static bool ContainsArabic(string? s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var ch in s)
            {
                int code = ch;
                if ((code >= 0x0600 && code <= 0x06FF) ||
                    (code >= 0x0750 && code <= 0x077F) ||
                    (code >= 0x08A0 && code <= 0x08FF))
                    return true;
            }
            return false;
        }

        public Task<byte[]> GenerateAsync(MemoDocument m)
        {
            QuestPDF.Settings.License = LicenseType.Community;

            // helpers
            static string HexOr(string? hex, string fallback)
                => string.IsNullOrWhiteSpace(hex) ? fallback : hex!.Trim();

            static string OrdinalDate(DateTime dt)
            {
                int d = dt.Day;
                string suffix = (d % 10 == 1 && d != 11) ? "st"
                              : (d % 10 == 2 && d != 12) ? "nd"
                              : (d % 10 == 3 && d != 13) ? "rd" : "th";
                return $"{dt:MMMM} {d}{suffix} {dt:yyyy}";
            }

            // colors
            var labelHex = HexOr(m.LabelColorHex, "#137B3C");
            var underlineHex = HexOr(m.UnderlineColorHex, "#137B3C");
            var bodyHex = "#000000";

            // layout numbers (preserved)
            const float FieldWidthPt = 420f; // To/From/Subject block width
            const float DateWidthPt = 260f;  // kept for compatibility if you reuse elsewhere
            const float LineThickness = 0.8f;
            const float FooterReservePt = 84f;  // visual gap above background footer image

            // text styles
            var baseText = TextStyle.Default.FontSize(11).FontColor(bodyHex);
            if (!string.IsNullOrWhiteSpace(m.FontFamilyLatin))
                baseText = baseText.FontFamily(m.FontFamilyLatin);

            var arabicText = TextStyle.Default.FontSize(11).FontColor(bodyHex);
            if (!string.IsNullOrWhiteSpace(m.FontFamilyArabic))
                arabicText = arabicText.FontFamily(m.FontFamilyArabic);

            // assets
            byte[]? bannerBytes = (m.BannerImage is { Length: > 0 }) ? m.BannerImage : null;
            byte[]? footerBytes = (m.FooterImage is { Length: > 0 }) ? m.FooterImage : null;

            // classification (value only; rendered compact in footer)
            string classificationValue = (m.Classification ?? "").Trim();

            // safe memo number + date (service-side fallbacks so UI/controller can omit them)
            string memoNumber = string.IsNullOrWhiteSpace(m.MemoNumber)
                ? $"M-{DateTime.UtcNow:yyyyMMdd-HHmmss}"
                : m.MemoNumber!.Trim();

            string dateText = string.IsNullOrWhiteSpace(m.DateText)
                ? OrdinalDate(DateTime.UtcNow)
                : m.DateText!.Trim();

            var pdf = Document.Create(doc =>
            {
                doc.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(m.PageMarginPt);   // all sides in points
                    page.DefaultTextStyle(baseText);

                    // Background: pin footer image at the bottom (behind content)
                    page.Background().Element(bg =>
                    {
                        if (footerBytes != null)
                            bg.AlignBottom().Image(footerBytes).FitWidth();
                    });

                    // Header: banner on first page
                    page.Header().ShowOnce().PaddingTop(2).Element(h =>
                    {
                        if (bannerBytes != null)
                            h.Image(bannerBytes).FitWidth();
                    });

                    // Content (reserves bottom space so nothing collides with footer art/classification)
                    page.Content()
                        .PaddingBottom(FooterReservePt)
                        .Column(col =>
                    {
                        col.Spacing(m.ContentSpacingPt);

                        // Memo No. (left) + Date (right)
                        col.Item().AlignCenter().Width(FieldWidthPt).Row(r =>
                        {
                            // Memo No.
                            r.RelativeItem().Column(c =>
                            {
                                c.Spacing(2);
                                c.Item().Row(rr =>
                                {
                                    rr.RelativeItem().Text(t => t.Span("Memo No.").SemiBold().FontColor(labelHex));
                                    rr.RelativeItem().AlignRight().Text(t => t.Span("رقم المذكرة").SemiBold().FontColor(labelHex).Style(arabicText));
                                });
                                c.Item()
                                 .PaddingTop(2)
                                 .BorderBottom(LineThickness).BorderColor(underlineHex)
                                 .PaddingBottom(2)
                                 .Text(t =>
                                 {
                                     t.AlignCenter();
                                     t.Span(memoNumber);
                                 });
                            });

                            r.ConstantItem(24).Text(""); // spacer

                            // Date
                            r.RelativeItem().Column(c =>
                            {
                                c.Spacing(2);
                                c.Item().Row(rr =>
                                {
                                    rr.RelativeItem().Text(t => t.Span("Date").SemiBold().FontColor(labelHex));
                                    rr.RelativeItem().AlignRight().Text(t => t.Span("التاريخ").SemiBold().FontColor(labelHex).Style(arabicText));
                                });
                                c.Item()
                                 .PaddingTop(2)
                                 .BorderBottom(LineThickness).BorderColor(underlineHex)
                                 .PaddingBottom(2)
                                 .Text(t =>
                                 {
                                     t.AlignCenter();
                                     t.Span(dateText);
                                 });
                            });
                        });

                        col.Item().Height(2);

                        // local helper to render a bilingual field block
                        void FieldBlock(string en, string ar, string? value)
                        {
                            col.Item().AlignCenter().Width(FieldWidthPt).Column(b =>
                            {
                                b.Spacing(0);

                                // bilingual labels on one line
                                b.Item().Row(r =>
                                {
                                    r.RelativeItem().Text(t =>
                                    {
                                        t.Span(en).SemiBold().FontColor(labelHex);
                                    });
                                    r.RelativeItem().AlignRight().Text(t =>
                                    {
                                        t.Span(ar).SemiBold().FontColor(labelHex).Style(arabicText);
                                    });
                                });

                                // short underline with centered value
                                b.Item().PaddingTop(0)
                                       .BorderBottom(LineThickness).BorderColor(underlineHex)
                                       .PaddingBottom(0)
                                       .Text(t =>
                                       {
                                           t.DefaultTextStyle(ds => ds.LineHeight(1.35f));
                                           t.AlignCenter();
                                           t.Span(value ?? "");
                                       });
                            });
                        }

                        // To → Through → From → Subject
                        FieldBlock("To", "إلى", m.To);
                        FieldBlock("Through", "بواسطة", m.Through);   // NEW
                        FieldBlock("From", "من", m.From);
                        FieldBlock("Subject", "الموضوع", m.Subject);

                        // Body — clamped to FieldWidthPt; per-line alignment (LTR left, RTL right)
                        col.Item()
                           .AlignCenter()
                           .Width(FieldWidthPt)
                           .PaddingTop(10)
                           .Element(body =>
                        {
                            var text = (m.Body ?? "").Replace("\r\n", "\n");

                            body.Column(c =>
                            {
                                foreach (var raw in text.Split('\n'))
                                {
                                    var line = raw.TrimEnd();

                                    if (string.IsNullOrWhiteSpace(line))
                                    {
                                        // approximate empty line spacing
                                        c.Item().Height(12);
                                        continue;
                                    }

                                    if (ContainsArabic(line))
                                    {
                                        // Arabic: RTL and right-aligned
                                        c.Item().Text(t =>
                                        {
                                            t.DefaultTextStyle(ds => ds.LineHeight(1.35f));
                                            t.AlignRight();
                                            t.Span(line).Style(arabicText);
                                        });
                                    }
                                    else
                                    {
                                        // English/Latin: LTR and left-aligned
                                        c.Item().Text(t =>
                                        {
                                            t.DefaultTextStyle(ds => ds.LineHeight(1.35f));
                                            t.AlignLeft();
                                            t.Span(line);
                                        });
                                    }
                                }
                            });
                        });

                        // (no classification block here — it's in the footer)
                    });

                    // Footer: compact (Classification: …) line above the background footer image
                    page.Footer().Column(f =>
                    {
                        if (!string.IsNullOrWhiteSpace(classificationValue))
                        {
                            f.Item().PaddingBottom(6).Text(t =>
                            {
                                t.AlignCenter();
                                t.Span("(");
                                t.Span("Classification").SemiBold().FontColor(labelHex);
                                t.Span(": ");
                                t.Span(classificationValue);
                                t.Span(")");
                            });
                        }
                    });
                });
            }).GeneratePdf();

            return Task.FromResult(pdf);
        }
    }
}
