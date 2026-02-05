using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DatevBridge.UI
{
    /// <summary>
    /// Unified theme constants for all popup forms.
    /// Ensures consistent look and feel across CallerPopup, ContactSelection, and Journal.
    /// </summary>
    internal static class UITheme
    {
        // Background colors
        public static readonly Color FormBackground = Color.FromArgb(45, 45, 48);
        public static readonly Color PanelBackground = Color.FromArgb(37, 37, 38);
        public static readonly Color InputBackground = Color.FromArgb(30, 30, 30);
        public static readonly Color CardBackground = Color.FromArgb(50, 50, 55);
        public static readonly Color CardBorder = Color.FromArgb(70, 70, 75);

        // Text colors
        public static readonly Color TextPrimary = Color.White;
        public static readonly Color TextSecondary = Color.FromArgb(200, 200, 200);
        public static readonly Color TextMuted = Color.FromArgb(150, 150, 150);
        public static readonly Color TextAccentInfo = Color.FromArgb(86, 156, 214);

        // Accent colors (used for accent bars, headers, direction labels)
        public static readonly Color AccentIncoming = Color.FromArgb(0, 122, 204);
        public static readonly Color AccentOutgoing = Color.FromArgb(0, 122, 204);  // Same as incoming
        public static readonly Color AccentDatev = Color.FromArgb(40, 167, 69);
        public static readonly Color AccentBridge = Color.FromArgb(156, 89, 182);   // Purple for Bridge section
        public static readonly Color AccentTapi = Color.FromArgb(33, 150, 243);    // Blue for TAPI section

        // Button colors
        public static readonly Color ButtonPrimary = Color.FromArgb(0, 122, 204);
        public static readonly Color ButtonSecondary = Color.FromArgb(63, 63, 70);
        public static readonly Color ButtonBorder = Color.FromArgb(100, 100, 100);

        // Fonts (non-readonly to allow proper cleanup with null assignment)
        public static Font FontTitle = new Font("Segoe UI", 11F, FontStyle.Bold);
        public static Font FontMedium = new Font("Segoe UI", 11F);
        public static Font FontLarge = new Font("Segoe UI", 14F, FontStyle.Bold);
        public static Font FontBody = new Font("Segoe UI", 9F);
        public static Font FontSmall = new Font("Segoe UI", 8F);
        public static Font FontLabel = new Font("Segoe UI", 9F, FontStyle.Bold);
        public static Font FontItalic = new Font("Segoe UI", 9F, FontStyle.Italic);
        public static Font FontMono = new Font("Consolas", 9F);

        // Spacing
        public const int SpacingS = 8;
        public const int SpacingM = 12;
        public const int SpacingL = 16;

        // Status badge colors
        public static readonly Color StatusOk = Color.FromArgb(40, 167, 69);
        public static readonly Color StatusWarn = Color.FromArgb(255, 193, 7);
        public static readonly Color StatusBad = Color.FromArgb(220, 53, 69);

        // Progress/status text background (subtle darker shade)
        public static readonly Color ProgressBackground = Color.FromArgb(30, 30, 32);

        // Accent bar height
        public const int AccentBarHeight = 4;

        /// <summary>
        /// Create a styled progress/status label with subtle background
        /// </summary>
        public static Label CreateProgressLabel(int width)
        {
            return new Label
            {
                Text = "",
                ForeColor = TextSecondary,
                BackColor = ProgressBackground,
                Font = FontSmall,
                Size = new Size(width, 18),
                Padding = new Padding(4, 2, 4, 2),
                AutoEllipsis = true
            };
        }

        /// <summary>
        /// Create the standard accent bar panel at the top of the form
        /// </summary>
        public static Panel CreateAccentBar(Color accentColor)
        {
            return new Panel
            {
                Dock = DockStyle.Top,
                Height = AccentBarHeight,
                BackColor = accentColor
            };
        }

        /// <summary>
        /// Create a standard primary action button
        /// </summary>
        public static Button CreatePrimaryButton(string text, int width = 120)
        {
            var btn = new Button
            {
                Text = text,
                ForeColor = Color.White,
                BackColor = ButtonPrimary,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(width, 32),
                Cursor = Cursors.Hand,
                Font = FontBody
            };
            btn.FlatAppearance.BorderSize = 0;
            // Ensure white text even when disabled
            btn.EnabledChanged += (s, e) =>
            {
                var b = s as Button;
                if (b != null) b.ForeColor = Color.White;
            };
            return btn;
        }

        /// <summary>
        /// Create a standard secondary action button
        /// </summary>
        public static Button CreateSecondaryButton(string text, int width = 100)
        {
            var btn = new Button
            {
                Text = text,
                ForeColor = TextPrimary,
                BackColor = ButtonSecondary,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(width, 32),
                Cursor = Cursors.Hand,
                Font = FontBody
            };
            btn.FlatAppearance.BorderColor = ButtonBorder;
            return btn;
        }

        /// <summary>
        /// Get the accent color based on call direction
        /// </summary>
        public static Color GetDirectionColor(bool isIncoming)
        {
            return isIncoming ? AccentIncoming : AccentOutgoing;
        }

        // ========== APPLICATION ICON ==========

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        private static Image _baseIcon;
        private static Image _datevLogo;
        private static Icon _formIcon;

        /// <summary>
        /// Load the base bridge icon. Tries embedded resource first (baked into exe),
        /// then falls back to bridge_icon.png next to the executable.
        /// In Visual Studio: add bridge_icon.png to project, set Build Action = Embedded Resource.
        /// </summary>
        private static Image GetBaseIcon()
        {
            if (_baseIcon != null) return _baseIcon;

            // Try embedded resource first (namespace.filename)
            var assembly = Assembly.GetExecutingAssembly();
            var stream = assembly.GetManifestResourceStream("DatevBridge.UI.Assets.bridge_icon.png");
            if (stream != null)
            {
                using (stream)
                {
                    _baseIcon = Image.FromStream(stream);
                }
                return _baseIcon;
            }

            // Fallback: file next to executable
            string iconPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "bridge_icon.png");

            if (File.Exists(iconPath))
            {
                using (var fs = new FileStream(iconPath, FileMode.Open, FileAccess.Read))
                {
                    _baseIcon = Image.FromStream(fs);
                }
            }
            return _baseIcon;
        }

        /// <summary>
        /// Load the DATEV logo. Tries embedded resource first,
        /// then falls back to DATEVLogo.png next to the executable.
        /// </summary>
        public static Image GetDatevLogo()
        {
            if (_datevLogo != null) return _datevLogo;

            var assembly = Assembly.GetExecutingAssembly();
            var stream = assembly.GetManifestResourceStream("DatevBridge.UI.Assets.DATEVLogo.png");
            if (stream != null)
            {
                using (stream)
                {
                    _datevLogo = Image.FromStream(stream);
                }
                return _datevLogo;
            }

            string logoPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "DATEVLogo.png");

            if (File.Exists(logoPath))
            {
                using (var fs = new FileStream(logoPath, FileMode.Open, FileAccess.Read))
                {
                    _datevLogo = Image.FromStream(fs);
                }
            }
            return _datevLogo;
        }

        /// <summary>
        /// Get the base bridge icon image for use in UI (e.g. About dialog).
        /// </summary>
        public static Image GetBaseIconPublic()
        {
            return GetBaseIcon();
        }

        /// <summary>
        /// Create a 16x16 tray icon with the bridge image and a colored status ring.
        /// Falls back to a simple colored circle if bridge_icon.png is not found.
        /// </summary>
        public static Icon CreateTrayIcon(Color ringColor)
        {
            var baseImg = GetBaseIcon();

            using (var bmp = new Bitmap(16, 16))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.Transparent);

                    if (baseImg != null)
                    {
                        // Draw base icon scaled into center (leaving 2px for ring)
                        g.DrawImage(baseImg, 2, 2, 12, 12);

                        // Draw status ring around the icon
                        using (var pen = new Pen(ringColor, 1.8f))
                        {
                            g.DrawEllipse(pen, 0.5f, 0.5f, 14.5f, 14.5f);
                        }
                    }
                    else
                    {
                        // Fallback: simple colored circle
                        using (var brush = new SolidBrush(ringColor))
                            g.FillEllipse(brush, 1, 1, 14, 14);
                        using (var pen = new Pen(Color.DarkGray, 1))
                            g.DrawEllipse(pen, 1, 1, 13, 13);
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    return (Icon)Icon.FromHandle(hIcon).Clone();
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        /// <summary>
        /// Get the application form icon (32x32 with green ring).
        /// Used for all form title bars.
        /// </summary>
        public static Icon GetFormIcon()
        {
            if (_formIcon != null) return _formIcon;

            var baseImg = GetBaseIcon();
            if (baseImg == null) return null;

            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.Transparent);

                    // Draw base icon scaled into center
                    g.DrawImage(baseImg, 3, 3, 26, 26);

                    // Draw green ring (operational state for form icon)
                    using (var pen = new Pen(StatusOk, 2f))
                    {
                        g.DrawEllipse(pen, 1f, 1f, 29.5f, 29.5f);
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    _formIcon = (Icon)Icon.FromHandle(hIcon).Clone();
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }

            return _formIcon;
        }

        /// <summary>
        /// Apply base form settings (background, border style, center, topmost)
        /// and set the application icon.
        /// </summary>
        public static void ApplyFormDefaults(Form form)
        {
            form.BackColor = FormBackground;
            form.ForeColor = TextPrimary;
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.TopMost = true;
            form.ShowInTaskbar = true;
            form.ShowIcon = true;
            form.Font = FontBody;

            var icon = GetFormIcon();
            if (icon != null)
                form.Icon = icon;
        }

        /// <summary>
        /// Dispose static resources at application shutdown.
        /// Call from TrayApplication.Dispose().
        /// </summary>
        public static void Cleanup()
        {
            _baseIcon?.Dispose();
            _baseIcon = null;

            _datevLogo?.Dispose();
            _datevLogo = null;

            _formIcon?.Dispose();
            _formIcon = null;

            FontTitle?.Dispose();
            FontTitle = null;
            FontMedium?.Dispose();
            FontMedium = null;
            FontLarge?.Dispose();
            FontLarge = null;
            FontBody?.Dispose();
            FontBody = null;
            FontSmall?.Dispose();
            FontSmall = null;
            FontLabel?.Dispose();
            FontLabel = null;
            FontItalic?.Dispose();
            FontItalic = null;
            FontMono?.Dispose();
            FontMono = null;
        }
    }
}
