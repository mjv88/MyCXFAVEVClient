using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DatevConnector.UI.Theme
{
    /// <summary>
    /// Panel with rounded corner border and optional top accent stripe.
    /// Replaces the CardBorder_Paint handler pattern used in StatusForm and SettingsForm.
    /// </summary>
    public class RoundedPanel : Panel
    {
        private int _cornerRadius = 8;
        private Color _accentColor = Color.Empty;
        private const int AccentStripeHeight = 5;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int CornerRadius
        {
            get => _cornerRadius;
            set { _cornerRadius = value; Invalidate(); }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color AccentColor
        {
            get => _accentColor;
            set { _accentColor = value; Invalidate(); }
        }

        public RoundedPanel() : this(Color.Empty) { }

        public RoundedPanel(Color accentColor)
        {
            _accentColor = accentColor;
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
            BackColor = UITheme.CardBackground;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? UITheme.FormBackground);

            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);

            // Fill rounded rectangle background
            using (var path = CreateRoundedRect(bounds, _cornerRadius))
            {
                using (var brush = new SolidBrush(BackColor))
                    g.FillPath(brush, path);

                // Draw border
                using (var pen = new Pen(UITheme.CardBorder))
                    g.DrawPath(pen, path);
            }

            // Draw accent stripe at top (clipped to rounded corners)
            if (_accentColor != Color.Empty)
            {
                var stripeRect = new Rectangle(1, 0, Width - 3, AccentStripeHeight);
                using (var clipPath = CreateRoundedRect(bounds, _cornerRadius))
                {
                    var oldClip = g.Clip;
                    g.SetClip(clipPath);
                    using (var brush = new SolidBrush(_accentColor))
                        g.FillRectangle(brush, stripeRect);
                    g.Clip = oldClip;
                }
            }

            // Paint child controls
            base.OnPaint(e);
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

            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();

            return path;
        }
    }
}
