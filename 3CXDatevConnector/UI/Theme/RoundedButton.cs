using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DatevConnector.UI.Theme
{
    /// <summary>
    /// Owner-drawn button with rounded corners and smooth hover/press color transitions.
    /// Drop-in replacement for standard Button — uses same Text, ForeColor, BackColor, Size, Cursor.
    /// </summary>
    public class RoundedButton : Button
    {
        private int _cornerRadius = 6;
        private bool _isHovered;
        private bool _isPressed;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int CornerRadius
        {
            get => _cornerRadius;
            set { _cornerRadius = value; Invalidate(); }
        }

        public RoundedButton()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer,
                true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Color.Transparent;
            FlatAppearance.MouseDownBackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _isHovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _isHovered = false;
            _isPressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _isPressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _isPressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? UITheme.FormBackground);

            Color bgColor = GetCurrentBackColor();

            using (var path = CreateRoundedRect(ClientRectangle, _cornerRadius))
            using (var brush = new SolidBrush(bgColor))
            {
                g.FillPath(brush, path);
            }

            // Draw image if present
            if (Image != null)
            {
                var imgRect = GetImageBounds();
                g.DrawImage(Image, imgRect);
            }

            // Draw text
            var textRect = ClientRectangle;
            if (Image != null)
            {
                int imgWidth = Image.Width + Padding.Left + 4;
                textRect = new Rectangle(imgWidth, 0, Width - imgWidth - Padding.Right, Height);
            }

            TextRenderer.DrawText(g, Text, Font, textRect, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);
        }

        private Color GetCurrentBackColor()
        {
            if (!Enabled)
                return Blend(BackColor, Color.Black, 0.3f);
            if (_isPressed)
                return Blend(BackColor, Color.Black, 0.15f);
            if (_isHovered)
                return Blend(BackColor, Color.White, 0.12f);
            return BackColor;
        }

        private Rectangle GetImageBounds()
        {
            if (Image == null) return Rectangle.Empty;
            int y = (Height - Image.Height) / 2;
            return new Rectangle(Padding.Left + 4, y, Image.Width, Image.Height);
        }

        private static Color Blend(Color baseColor, Color blendColor, float amount)
        {
            int r = (int)(baseColor.R + (blendColor.R - baseColor.R) * amount);
            int g = (int)(baseColor.G + (blendColor.G - baseColor.G) * amount);
            int b = (int)(baseColor.B + (blendColor.B - baseColor.B) * amount);
            return Color.FromArgb(
                Math.Max(0, Math.Min(255, r)),
                Math.Max(0, Math.Min(255, g)),
                Math.Max(0, Math.Min(255, b)));
        }

        private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            if (d <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }

            // Shrink by 1px to avoid clipping
            var r = new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);

            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();

            return path;
        }
    }
}
