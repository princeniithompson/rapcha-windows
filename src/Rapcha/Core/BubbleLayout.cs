using System;

namespace Rapcha.Core
{
    /// <summary>
    /// Layout of the Words bubble at its fixed outer size (420 x 120 logical px): four lines of live text in a
    /// tight line height above one compact row of buttons. Font sizes follow the DPI and Windows "Make text
    /// bigger" (TextScaleFactor), shrinking within limits so four lines always fit and the buttons never clip.
    /// </summary>
    public sealed class BubbleLayout
    {
        public const int WidthDip = 420, HeightDip = 120, Lines = 4;
        public const double TextPt = 11.0, MinTextPt = 8.0, ButtonPt = 9.0, MaxButtonPt = 10.5, MinButtonPt = 7.5;
        public const double LineFactor = 1.2;      // line height / font size (tight)

        public double Scale;
        public int Width, Height, PadX, TextTop, TextHeight, RowY, RowHeight, Corner;
        public float TextPx, ButtonPx, LineHeight;

        public static double PtToPx(double pt, int dpi) => pt * dpi / 72.0;

        public static BubbleLayout Compute(int dpi, double textScale)
        {
            if (dpi <= 0) dpi = 96;
            if (textScale < 1) textScale = 1;
            if (textScale > 2.25) textScale = 2.25;
            double s = dpi / 96.0;
            var l = new BubbleLayout { Scale = s };
            l.Width = (int)Math.Round(WidthDip * s);
            l.Height = (int)Math.Round(HeightDip * s);
            l.PadX = (int)Math.Round(14 * s);
            l.Corner = (int)Math.Round(20 * s);
            int top = (int)Math.Round(10 * s), bottom = (int)Math.Round(9 * s), gap = (int)Math.Round(5 * s);

            double buttonPx = Math.Min(PtToPx(ButtonPt * textScale, dpi), PtToPx(MaxButtonPt, dpi));
            double minText = PtToPx(MinTextPt, dpi);
            int row;
            while (true)
            {
                row = (int)Math.Ceiling(Math.Max(22 * s, buttonPx * 1.9));
                int avail = l.Height - top - bottom - gap - row;
                double fit = avail / (Lines * LineFactor);
                if (fit >= minText || buttonPx <= PtToPx(MinButtonPt, dpi)) break;
                buttonPx -= 0.5 * s; // give the text room before going below the smallest text size
            }
            l.ButtonPx = (float)buttonPx;
            l.RowHeight = row;
            l.RowY = l.Height - bottom - row;
            l.TextTop = top;
            l.TextHeight = l.RowY - gap - top;
            double want = PtToPx(TextPt * textScale, dpi);
            double maxFit = l.TextHeight / (Lines * LineFactor);
            l.TextPx = (float)Math.Max(Math.Min(want, maxFit), Math.Min(minText, maxFit));
            l.LineHeight = (float)(l.TextPx * LineFactor);
            return l;
        }
    }
}
