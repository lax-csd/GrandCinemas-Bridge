// =====================================================================
// ReceiptLayoutConfig.cs — GrandCinemasPrintBridge
//
// SINGLE SOURCE OF TRUTH for every width/margin/spacing/typography
// value used by GdiReceiptPrinter.cs. This file replaces the previous
// version that had TWO separate margin concepts (OuterMarginPx used
// for the border, SideMarginPx used inside UsableWidthPx) which could
// silently disagree once font/logo sizes changed — that mismatch was
// the root cause of content overflowing the physical paper edge.
//
// From here on there is exactly ONE safe content rectangle:
//   ContentLeft .. ContentRight  (width = ContentWidth = 552px)
// Every renderer (logo, text, row, divider, QR, footer, border) reads
// ONLY these three values for horizontal layout. Nothing computes its
// own independent width.
// =====================================================================
namespace GrandCinemasPrintBridge
{
    public static class ReceiptLayoutConfig
    {
        // ── Physical paper — RE-CALIBRATED from actual DRIVER data ──────
        // The 80mm-roll / 640px value was based on the physical PAPER
        // width measured by ruler. But TWO separate /calibrate and /print
        // runs (one requesting 576px, one requesting 640px) both show the
        // POS80 driver reporting the SAME real printable clip regardless
        // of what page width we ask for:
        //   e.Graphics.VisibleClipBounds.Width = 283.2512 (hundredths-inch)
        //     = 2.832512in × 203 DPI ≈ 575px ≈ 71.9mm
        // That means the paper roll is 80mm, but the POS80's actual print
        // head / driver-enforced printable strip is only ~72mm wide — a
        // ~4mm dead margin on each side is standard for "80mm" thermal
        // printers. This is a genuine, repeated hardware/driver limit,
        // not a one-off reading, so ContentRight must stay safely UNDER
        // 575px regardless of PrintableWidthPx.
        public const int PrintableWidthPx = 576;   // requested page width — driver clips beyond ~575px anyway
        public const float Dpi = 203f;

        // ── THE safe content rectangle ──────────────────────────────
        // Kept safely inside the confirmed ~575px driver printable clip
        // (11px / ~1.4mm margin under the cap on the right).
        public const int ContentLeft = 12;                                 // ≈1.5mm
        public const int ContentRight = 564;                               // ≈70.6mm — under the 575px driver cap
        public const int ContentWidth = ContentRight - ContentLeft;        // 552px ≈ 69mm

        // Inner breathing room between the border line and actual content
        // (text/logo/QR/rows). The border itself still sits exactly on
        // ContentLeft/ContentRight (the calibrated hard boundary) — this
        // only insets what gets DRAWN inside it, so nothing ever touches
        // the border line. ~2.5mm each side.
        public const int BorderInnerPaddingPx = 20;

        // ── Row columns — safe split of ContentWidth (must sum to 1.0) ──
        public const float RowLeftWidthRatio = 0.42f;
        public const float RowRightWidthRatio = 0.58f;
        public const int RowColumnGapPx = 8; // guarantees left/right text never touch even at max width

        // ── Typography — approved 1.25x baseline, UNCHANGED by this fix ──
        public const float TitleFontSize = 30f;      // "Entrance Ticket" / "Purchase Receipt" heading
        public const float CinemaFontSize = 22f;      // "Grand Cinemas Al Andalus"
        public const float LocationFontSize = 18f;
        public const float MovieFontSize = 26f;      // movie title — bold
        public const float RatingFontSize = 18f;
        public const float LabelFontSize = 20f;      // row left labels
        public const float NormalFontSize = 22f;      // row right values
        public const float TotalFontSize = 24f;      // amount — bold
        public const float FooterFontSize = 19f;      // kiosk id / location / timestamp
        public const float QrLabelFontSize = 17f;

        // Hard floor for the auto-shrink-to-fit safety net — text never
        // goes below this, no matter how long the value is.
        public const float MinFitFontSize = 14f;

        // ── Logo — large/premium, but hard-capped to ContentWidth ──────
        private const int LogoSafePaddingPx = 40; // visible breathing room from the border even at max size
        public const int LogoMaxWidthPx = ContentWidth - LogoSafePaddingPx; // 512 — was previously able to exceed 552
        public const int LogoTopSpacingPx = 16;
        public const int LogoBottomSpacingPx = 14;

        // ── Vertical spacing / padding ──────────────────────────────
        public const int TopPaddingPx = 10;
        public const int BottomPaddingPx = 24;
        public const int SectionSpacingPx = 18;
        public const int DividerSpacingPx = 14;
        public const int FooterSpacingPx = 16;
        public const int RowLineSpacingPx = 6;   // gap between wrapped lines inside one Row
        public const int TextLineSpacingPx = 4;   // gap between wrapped lines inside one Text element

        // ── QR ────────────────────────────────────────────────────
        // 240px is comfortably inside the 552px ContentWidth (plenty of
        // quiet zone on both sides) while staying well above the
        // ~120-150px minimum for reliable thermal scanning.
        public const int QrDisplaySizePx = 240;
        public const int QrTopSpacingPx = 10;
        public const int QrLabelSpacingPx = 8;

        // ── Border ────────────────────────────────────────────────
        // Drawn at ContentLeft/ContentRight — i.e. INSIDE the safe area,
        // never at the raw PrintableWidthPx edge.
        public const int BorderInsetPx = 0; // border sits exactly on the content rectangle

        // Extra BLANK paper feed reserved strictly AFTER the bottom border
        // line — not to be confused with BottomPaddingPx (whitespace
        // between the last content element and the border itself, which
        // already exists and is fine). This exists because physical
        // photos showed the side/top/left/right border present but the
        // BOTTOM edge of the same DrawRectangle() call missing — DrawRectangle
        // always closes all 4 sides, so the code was already asking for a
        // bottom line; the most likely explanation for it not showing up
        // on paper is the POS80's auto-cut mechanism cutting before the
        // very last drawn row has fully fed past the print head (a common
        // thermal-printer behavior when content ends close to the
        // declared page length). Giving the page more blank length AFTER
        // the border — not moving the border itself — gives the cutter
        // definite clearance. ~3.7mm.
        public const int BorderBottomFeedPx = 30;
    }
}