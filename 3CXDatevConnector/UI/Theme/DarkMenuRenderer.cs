using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DatevConnector.UI.Theme
{
    /// <summary>
    /// Custom ToolStripRenderer for dark themed context menus.
    /// Uses UITheme colors to match the dark form style.
    /// </summary>
    internal class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color MenuBackground = UITheme.FormBackground;            // (45,45,48)
        private static readonly Color MenuBorder = UITheme.CardBorder;                    // (70,70,75)
        private static readonly Color ItemHover = Color.FromArgb(62, 62, 66);
        private static readonly Color ItemPressed = Color.FromArgb(27, 27, 28);
        private static readonly Color SeparatorColor = Color.FromArgb(62, 62, 66);
        private static readonly Color TextNormal = UITheme.TextPrimary;                   // White
        private static readonly Color TextDisabled = UITheme.TextMuted;                   // (150,150,150)
        private static readonly Color CheckBackground = Color.FromArgb(62, 62, 66);
        private static readonly Color SubmenuBackground = UITheme.FormBackground;

        private static readonly bool IsWin11 =
            Environment.OSVersion.Version.Build >= 22000;

        public DarkMenuRenderer() : base(new DarkColorTable())
        {
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var brush = new SolidBrush(MenuBackground))
            {
                e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // On Win11, DWM draws the native rounded border — skip custom border
            if (IsWin11) return;

            // Fallback: draw rounded border on older Windows
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
            using (var pen = new Pen(MenuBorder))
            using (var path = CreateRoundedPath(rect, 8))
            {
                g.DrawPath(pen, path);
            }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            var item = e.Item;
            var rect = new Rectangle(4, 2, item.Width - 8, item.Height - 4);

            if (!item.Enabled)
            {
                // Disabled items: no background change
                return;
            }

            if (item.Selected || item.Pressed)
            {
                var color = item.Pressed ? ItemPressed : ItemHover;
                using (var brush = new SolidBrush(color))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    FillRoundedRect(g, brush, rect, 6);
                }
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? TextNormal : TextDisabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var g = e.Graphics;
            int y = e.Item.Height / 2;
            using (var pen = new Pen(SeparatorColor))
            {
                g.DrawLine(pen, 12, y, e.Item.Width - 12, y);
            }
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Draw check background
            var bgRect = new Rectangle(e.ImageRectangle.X - 2, e.ImageRectangle.Y - 2,
                e.ImageRectangle.Width + 4, e.ImageRectangle.Height + 4);
            using (var brush = new SolidBrush(CheckBackground))
            {
                FillRoundedRect(g, brush, bgRect, 4);
            }

            // Draw checkmark
            using (var pen = new Pen(UITheme.StatusOk, 2f))
            {
                var cx = e.ImageRectangle.X + e.ImageRectangle.Width / 2;
                var cy = e.ImageRectangle.Y + e.ImageRectangle.Height / 2;
                g.DrawLines(pen, new[]
                {
                    new Point(cx - 4, cy),
                    new Point(cx - 1, cy + 3),
                    new Point(cx + 5, cy - 4)
                });
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item.Enabled ? TextNormal : TextDisabled;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // No image margin background (keeps it clean)
        }

        private static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void FillRoundedRect(Graphics g, Brush brush, Rectangle rect, int radius)
        {
            using (var path = CreateRoundedPath(rect, radius))
            {
                g.FillPath(brush, path);
            }
        }

        private class DarkColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected => ItemHover;
            public override Color MenuItemSelectedGradientBegin => ItemHover;
            public override Color MenuItemSelectedGradientEnd => ItemHover;
            public override Color MenuItemPressedGradientBegin => ItemPressed;
            public override Color MenuItemPressedGradientEnd => ItemPressed;
            public override Color MenuBorder => DarkMenuRenderer.MenuBorder;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color ToolStripDropDownBackground => MenuBackground;
            public override Color ImageMarginGradientBegin => MenuBackground;
            public override Color ImageMarginGradientMiddle => MenuBackground;
            public override Color ImageMarginGradientEnd => MenuBackground;
            public override Color SeparatorDark => SeparatorColor;
            public override Color SeparatorLight => SeparatorColor;
            public override Color CheckBackground => DarkMenuRenderer.CheckBackground;
            public override Color CheckSelectedBackground => DarkMenuRenderer.CheckBackground;
            public override Color CheckPressedBackground => DarkMenuRenderer.CheckBackground;
        }
    }
}
