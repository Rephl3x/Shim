using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Reflection;

namespace RigBridge
{
    public sealed class UiPreferences { public string Mode { get; set; } public UiPreferences() { Mode = "Day"; } }
    public sealed class Palette
    {
        public Color Background, Surface, Raised, Input, Border, Text, Muted, Accent, AccentSoft, Success, Danger, DangerSoft;
        public bool Night;
        public static Palette For(string mode)
        {
            bool dark = mode == "Night";
            return new Palette { Night = dark,
                Background = ColorTranslator.FromHtml(dark ? "#101010" : "#F4F3F1"), Surface = ColorTranslator.FromHtml(dark ? "#191919" : "#FFFFFF"),
                Raised = ColorTranslator.FromHtml(dark ? "#242321" : "#EFEDE9"), Input = ColorTranslator.FromHtml(dark ? "#121212" : "#FAF9F7"),
                Border = ColorTranslator.FromHtml(dark ? "#383632" : "#E3DFD8"), Text = ColorTranslator.FromHtml(dark ? "#F5F2EC" : "#27231D"),
                Muted = ColorTranslator.FromHtml(dark ? "#B2AAA0" : "#746C62"), Accent = ColorTranslator.FromHtml(dark ? "#FF981A" : "#A85500"),
                AccentSoft = ColorTranslator.FromHtml(dark ? "#402807" : "#FFF0D9"), Success = ColorTranslator.FromHtml(dark ? "#75D4A3" : "#167B4D"),
                Danger = ColorTranslator.FromHtml(dark ? "#FFAAA0" : "#AE3D32"), DangerSoft = ColorTranslator.FromHtml(dark ? "#3B2421" : "#FFF0ED") };
        }
    }
    public static class Appearance
    {
        private static bool loaded;
        public static string Mode { get; private set; }
        public static Palette Colors { get; private set; }
        private static Image brandImage;
        public static Image BrandImage
        {
            get
            {
                if (brandImage == null)
                {
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Shim.Graphics.Brand.png"))
                        if (stream != null) using (Image source = Image.FromStream(stream)) brandImage = new Bitmap(source);
                }
                return brandImage;
            }
        }
        public static event Action Changed;
        public static string Path { get { return System.IO.Path.Combine(Store.DirectoryPath, "appearance.json"); } }
        static Appearance() { Mode = "Day"; Colors = Palette.For(Mode); }
        public static void Load()
        {
            if (loaded) return; loaded = true;
            try { if (File.Exists(Path)) { var preferences = Store.Read<UiPreferences>(Path); if (preferences != null) Set(preferences.Mode, false); } }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        public static void Set(string mode, bool save)
        {
            if (mode != "Day" && mode != "Night") throw new ArgumentException("Choose Day or Night mode.");
            if (save) Store.Write(Path, new UiPreferences { Mode = mode });
            Mode = mode; Colors = Palette.For(mode);
            var changed = Changed; if (changed != null) changed();
        }
        public static void Apply(Control root)
        {
            var p = Colors; string role = root.Tag as string;
            Color background = role == "Surface" || root is SurfaceCard ? p.Surface : role == "Raised" ? p.Raised : role == "Page" || root is Form ? p.Background : root.Parent == null ? p.Background : root.Parent.BackColor;
            root.BackColor = background; root.ForeColor = role == "Muted" ? p.Muted : role == "Accent" ? p.Accent : role == "Success" ? p.Success : role == "Danger" ? p.Danger : p.Text;
            var label = root as Label; if (label != null) label.UseMnemonic = false;
            var box = root as TextBox;
            if (box != null) { box.BackColor = p.Input; box.ForeColor = p.Text; box.BorderStyle = BorderStyle.None; }
            var list = root as ListBox;
            if (list != null) { list.BackColor = p.Surface; list.ForeColor = p.Text; list.BorderStyle = BorderStyle.None; list.ItemHeight = 28; }
            var combo = root as ComboBox;
            if (combo != null)
            {
                combo.BackColor = p.Input; combo.ForeColor = p.Text; combo.FlatStyle = FlatStyle.Flat;
                if (combo.DrawMode != DrawMode.OwnerDrawFixed) { combo.DrawMode = DrawMode.OwnerDrawFixed; combo.ItemHeight = 25; combo.DrawItem += DrawChoice; }
            }
            var number = root as NumericUpDown;
            if (number != null) { number.BackColor = p.Input; number.ForeColor = p.Text; number.BorderStyle = BorderStyle.FixedSingle; }
            var check = root as CheckBox;
            if (check != null) { check.FlatStyle = FlatStyle.Flat; check.FlatAppearance.BorderColor = p.Border; check.UseVisualStyleBackColor = false; }
            var grid = root as DataGridView;
            if (grid != null)
            {
                grid.EnableHeadersVisualStyles = false; grid.BorderStyle = BorderStyle.None; grid.BackgroundColor = p.Surface; grid.GridColor = p.Border;
                grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
                grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; grid.ColumnHeadersHeight = 38;
                grid.ColumnHeadersDefaultCellStyle.BackColor = p.Raised; grid.ColumnHeadersDefaultCellStyle.ForeColor = p.Muted;
                grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.Raised; grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
                grid.DefaultCellStyle.BackColor = p.Surface; grid.DefaultCellStyle.ForeColor = p.Text;
                grid.DefaultCellStyle.SelectionBackColor = p.AccentSoft; grid.DefaultCellStyle.SelectionForeColor = p.Text;
                grid.DefaultCellStyle.Padding = new Padding(8, 0, 8, 0); grid.RowTemplate.Height = 34;
                grid.AlternatingRowsDefaultCellStyle.BackColor = p.Surface;
            }
            var button = root as Button;
            if (button != null && !(button is ModernButton))
            { button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = p.Border; button.BackColor = p.Surface; }
            foreach (Control child in root.Controls) Apply(child);
            var form = root as Form;
            if (form != null && form.IsHandleCreated) ApplyFrame(form);
            root.Invalidate(true);
        }
        private static void DrawChoice(object sender, DrawItemEventArgs e)
        {
            var box = (ComboBox)sender; var p = Colors; bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(selected ? p.AccentSoft : p.Input)) e.Graphics.FillRectangle(brush, e.Bounds);
            string text = e.Index >= 0 && e.Index < box.Items.Count ? box.Items[e.Index].ToString() : box.Text;
            var bounds = e.Bounds; bounds.Inflate(-7, 0);
            TextRenderer.DrawText(e.Graphics, text, e.Font, bounds, box.Enabled ? p.Text : p.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        private static void ApplyFrame(Form form)
        {
            try
            {
                int dark = Colors.Night ? 1 : 0; DwmSetWindowAttribute(form.Handle, 20, ref dark, 4);
                int caption = ColorRef(Colors.Background), text = ColorRef(Colors.Text); DwmSetWindowAttribute(form.Handle, 35, ref caption, 4); DwmSetWindowAttribute(form.Handle, 36, ref text, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
        private static int ColorRef(Color color) { return color.R | color.G << 8 | color.B << 16; }
        internal static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            float size = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)); var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, size, size, 180, 90); path.AddArc(bounds.Right - size, bounds.Y, size, size, 270, 90);
            path.AddArc(bounds.Right - size, bounds.Bottom - size, size, size, 0, 90); path.AddArc(bounds.X, bounds.Bottom - size, size, size, 90, 90); path.CloseFigure(); return path;
        }
    }
    public sealed class SurfaceCard : Panel
    {
        public SurfaceCard() { DoubleBuffered = true; Padding = new Padding(20); Margin = new Padding(0); Tag = "Surface"; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Appearance.Colors.Background : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Appearance.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 12))
            using (var brush = new SolidBrush(Appearance.Colors.Surface))
            using (var pen = new Pen(Appearance.Colors.Border)) { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); }
        }
    }
    public sealed class ModernButton : Button
    {
        private bool hover, pressed;
        public ModernButton()
        {
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Appearance.Colors; string role = Tag as string; bool primary = role == "Primary", active = role == "Active", danger = role == "Danger", plain = role == "Nav" || role == "Ghost";
            Color fill = !Enabled ? p.Raised : primary ? p.Accent : active ? p.AccentSoft : danger ? p.DangerSoft : plain ? (Parent == null ? p.Background : Parent.BackColor) : p.Surface;
            Color ink = !Enabled ? p.Muted : primary ? (p.Night ? Color.FromArgb(22, 17, 10) : Color.White) : active ? p.Accent : danger ? p.Danger : p.Text;
            if (Enabled && (hover || pressed)) fill = ControlPaint.Light(fill, pressed ? 0.12f : 0.06f);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.Clear(Parent == null ? p.Background : Parent.BackColor);
            using (var path = Appearance.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(primary || active || danger || plain ? fill : p.Border)) { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); }
            var bounds = new Rectangle(Padding.Left + 1, 1, Math.Max(1, Width - Padding.Horizontal - 2), Height - 2);
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | (TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
            if (Focused && ShowFocusCues) { var focus = ClientRectangle; focus.Inflate(-4, -4); ControlPaint.DrawFocusRectangle(e.Graphics, focus, ink, fill); }
        }
    }
    public sealed class StrengthSlider : Control
    {
        private int value;
        public event EventHandler ValueChanged;
        public int Value { get { return value; } set { int next = Math.Max(0, Math.Min(100, value)); if (this.value == next) return; this.value = next; Invalidate(); if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1); var changed = ValueChanged; if (changed != null) changed(this, EventArgs.Empty); } }
        public StrengthSlider() { Height = 46; Width = 400; TabStop = true; AccessibleName = "FFB strength"; AccessibleRole = AccessibleRole.Slider; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true); }
        protected override bool IsInputKey(Keys keyData) { Keys key = keyData & Keys.KeyCode; return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) { Value--; e.Handled = true; } else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) { Value++; e.Handled = true; } else if (e.KeyCode == Keys.PageUp) { Value += 5; e.Handled = true; } else if (e.KeyCode == Keys.PageDown) { Value -= 5; e.Handled = true; } else if (e.KeyCode == Keys.Home) { Value = 0; e.Handled = true; } else if (e.KeyCode == Keys.End) { Value = 100; e.Handled = true; } base.OnKeyDown(e); }
        protected override AccessibleObject CreateAccessibilityInstance() { return new StrengthAccessibility(this); }
        private sealed class StrengthAccessibility : ControlAccessibleObject
        {
            private readonly StrengthSlider slider;
            public StrengthAccessibility(StrengthSlider slider) : base(slider) { this.slider = slider; }
            public override string Value { get { return slider.Value + "%"; } set { int parsed; if (slider.Enabled && Int32.TryParse((value ?? "").TrimEnd('%'), out parsed)) slider.Value = parsed; } }
            public override string Description { get { return "Force feedback strength, 0 to 100 percent. Arrow keys adjust by one percent; Page Up and Page Down by five."; } }
        }
        protected override void OnMouseDown(MouseEventArgs e) { if (Enabled && e.Button == MouseButtons.Left) { Focus(); Capture = true; SetPointer(e.X); } base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (Enabled && Capture) SetPointer(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Capture = false; base.OnMouseUp(e); }
        private void SetPointer(int x) { Value = (int)Math.Round((x - 12) * 100.0 / Math.Max(1, Width - 24)); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Appearance.Colors; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.Clear(BackColor);
            int y = Height / 2, x = 12 + (Width - 24) * Value / 100;
            using (var rail = new Pen(p.Border, 6)) { rail.StartCap = rail.EndCap = LineCap.Round; e.Graphics.DrawLine(rail, 12, y, Width - 12, y); }
            using (var track = new Pen(Enabled ? p.Accent : p.Muted, 6)) { track.StartCap = track.EndCap = LineCap.Round; e.Graphics.DrawLine(track, 12, y, x, y); }
            using (var brush = new SolidBrush(Enabled ? p.Accent : p.Muted)) e.Graphics.FillEllipse(brush, x - 9, y - 9, 18, 18);
            if (Focused) using (var pen = new Pen(p.Accent)) e.Graphics.DrawEllipse(pen, x - 13, y - 13, 26, 26);
        }
    }
}
