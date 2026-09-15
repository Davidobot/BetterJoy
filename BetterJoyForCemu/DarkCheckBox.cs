using System;
using System.Drawing;
using System.Windows.Forms;

namespace BetterJoyForCemu {
    // WinForms' flat CheckBox uses ForeColor for both its label and check glyph. On the dark UI
    // that made the required white label produce a nearly invisible white-on-white check. Draw
    // the box separately so labels stay light while the check itself is always black.
    internal sealed class DarkCheckBox : CheckBox {
        public DarkCheckBox() {
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnPaint(PaintEventArgs e) {
            OnPaintBackground(e);

            const int boxSize = 13;
            Rectangle box = new Rectangle(0, (Height - boxSize) / 2, boxSize, boxSize);
            Color boxColor = Enabled ? Color.White : Color.FromArgb(170, 170, 170);
            using (SolidBrush fill = new SolidBrush(boxColor))
                e.Graphics.FillRectangle(fill, box);
            using (Pen border = new Pen(Color.FromArgb(105, 105, 105)))
                e.Graphics.DrawRectangle(border, box);

            if (CheckState == CheckState.Checked) {
                Point[] check = {
                    new Point(box.Left + 3, box.Top + 6),
                    new Point(box.Left + 6, box.Top + 9),
                    new Point(box.Left + 11, box.Top + 3),
                };
                using (Pen pen = new Pen(Color.Black, 2F)) {
                    pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    e.Graphics.DrawLines(pen, check);
                }
            } else if (CheckState == CheckState.Indeterminate) {
                using (SolidBrush fill = new SolidBrush(Color.Black))
                    e.Graphics.FillRectangle(fill, box.Left + 3, box.Top + 3, 7, 7);
            }

            Rectangle textBounds = new Rectangle(
                box.Right + 5, 0, Math.Max(0, Width - box.Right - 5), Height);
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds,
                Enabled ? ForeColor : SystemColors.GrayText, flags);

            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, textBounds, ForeColor, BackColor);
        }
    }
}
