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
            var labelHex     = HexOr(m.LabelColorHex,     "#137B3C");
            var underlineHex = HexOr(m.UnderlineColorHex, "#137B3C");
            var bodyHex      = "#000000";

            // layout numbers
            const float FieldWidthPt  = 420f; // To/From/Subject block width
            const float DateWidthPt   = 260f; // Date block width
            const float LineThickness = 0.8f;
            const float FooterReservePt = 84f; // visual gap above background footer image

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

            // classification ticks
            const string CheckedBox   = "☑";
            const string UncheckedBox = "☐";

            string pick = (m.Classification ?? "").Trim().ToLowerInvariant();
            bool isConfidential = pick is "confidential" or "confidentiel" or "سري" or "secret";
            bool isInternal     = pick is "internal" or "interne" or "داخلي" or "for internal use";
            bool isPublic       = pick is "public" or "publique" or "عام" or "general";

            // auto date if not provided
            string dateText = string.IsNullOrWhiteSpace(m.DateText)
                ? OrdinalDate(DateTime.UtcNow)
                : m.DateText!.Trim();

            var pdf = Document.Create(doc =>
            {
                doc.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(m.PageMarginPt);   // all sides the same (points)
                    page.DefaultTextStyle(baseText);

                    // Background: pin footer image at the bottom (behind content)
                    page.Background().Element(bg =>
                    {
                        if (footerBytes != null)
                            bg.AlignBottom().Image(footerBytes).FitWidth();
                    });

                    // Header: banner on first page, add small top padding so it never clips
                    page.Header().ShowOnce().PaddingTop(6).Element(h =>
                    {
                        if (bannerBytes != null)
                            h.Image(bannerBytes).FitWidth();
                    });

                    // (EDIT 1) Classification moved OUT of Footer and INTO Content near the end

                    // Content
                    page.Content().Column(col =>
                    {
                        col.Spacing(m.ContentSpacingPt);

                        // DATE (centered, narrow block, short underline)
                        col.Item().AlignCenter().Width(DateWidthPt).Column(b =>
                        {
                            b.Spacing(2);
                            // bilingual label row
                            b.Item().Row(r =>
                            {
                                r.RelativeItem().Text(t =>
                                {
                                    t.Span("Date").SemiBold().FontColor(labelHex);
                                });
                                r.RelativeItem().AlignRight().Text(t =>
                                {
                                    t.Span("التاريخ").SemiBold().FontColor(labelHex).Style(arabicText);
                                });
                            });

                            // short underline with centered date
                            b.Item().PaddingTop(2)
                                   .BorderBottom(LineThickness).BorderColor(underlineHex)
                                   .PaddingBottom(2)
                                   .Text(t =>
                                   {
                                       t.DefaultTextStyle(ds => ds.LineHeight(1.35f));
                                       t.AlignCenter();
                                       t.Span(dateText);
                                   });
                        });

                        col.Item().Height(6);

                        // local helper to render a bilingual field block
                        void FieldBlock(string en, string ar, string? value)
                        {
                            col.Item().AlignCenter().Width(FieldWidthPt).Column(b =>
                            {
                                b.Spacing(2);

                                // bilingual labels on one line (ends not full width)
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
                                b.Item().PaddingTop(2)
                                       .BorderBottom(LineThickness).BorderColor(underlineHex)
                                       .PaddingBottom(2)
                                       .Text(t =>
                                       {
                                           t.DefaultTextStyle(ds => ds.LineHeight(1.35f));
                                           t.AlignCenter();
                                           t.Span(value ?? "");
                                       });
                            });
                        }

                        // To / From / Subject (title is part of the banner; not repeated)
                        FieldBlock("To",      "إلى",     m.To);
                        FieldBlock("From",    "من",      m.From);
                        FieldBlock("Subject", "الموضوع", m.Subject);

                        // (EDIT 2) Body respects line breaks: use Line() per line (not Span)
                        col.Item().PaddingTop(10).Element(body =>
                        {
                            var text = (m.Body ?? "").Replace("\r\n", "\n");
                            body.Text(t =>
                            {
                                t.DefaultTextStyle(ds => ds.LineHeight(1.35f));
                                foreach (var line in text.Split('\n'))
                                {
                                    if (string.IsNullOrWhiteSpace(line)) t.EmptyLine();
                                    else t.Line(line.TrimEnd());
                                }
                            });
                        });

                        // push classification down near page bottom, then render it ABOVE the footer image
                        // col.Item().ExtendVertical();

                        // Classification block (centered, bilingual, with ticks) + bottom padding above footer image
                        col.Item().AlignCenter().Width(FieldWidthPt).PaddingBottom(FooterReservePt).Column(c =>
                        {
                            c.Spacing(3);

                            c.Item().Text(t =>
                            {
                                t.AlignCenter();
                                t.Span("Classification").SemiBold().FontColor(labelHex);
                                t.Span(" / ");
                                t.Span("التصنيف").SemiBold().FontColor(labelHex).Style(arabicText);
                            });

                            c.Item().Text(t =>
                            {
                                t.AlignCenter();

                                // Arabic options
                                t.Span(isConfidential ? CheckedBox : UncheckedBox).FontSize(11);
                                t.Span(" سري  ").Style(arabicText);
                                t.Span(isInternal ? CheckedBox : UncheckedBox).FontSize(11);
                                t.Span(" للاستخدام الداخلي  ").Style(arabicText);
                                t.Span(isPublic ? CheckedBox : UncheckedBox).FontSize(11);
                                t.Span(" عام   ").Style(arabicText);

                                // spacer
                                t.Span("   ");

                                // English options
                                t.Span(isConfidential ? CheckedBox : UncheckedBox).FontSize(11);
                                t.Span(" Confidential   ").SemiBold().FontColor(labelHex);
                                t.Span(isInternal ? CheckedBox : UncheckedBox).FontSize(11);
                                t.Span(" For Internal use   ").SemiBold().FontColor(labelHex);
                                t.Span(isPublic ? CheckedBox : UncheckedBox).FontSize(11);
                                t.Span(" General").SemiBold().FontColor(labelHex);
                            });
                        });
                    });
                });
            }).GeneratePdf();

            return Task.FromResult(pdf);
        }
    }
}
