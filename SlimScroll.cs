using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class SlimScroll : Control
{
    private readonly Func<int> total, visible, position;
    private readonly Action<int> move;
    private readonly Timer refresh = new Timer { Interval = 90 };
    private Rectangle thumb;
    private int grab;
    private bool hover;
    private int lastTotal = -1, lastVisible = -1, lastPosition = -1;

    public SlimScroll(Func<int> total, Func<int> visible, Func<int> position, Action<int> move)
    {
        this.total = total; this.visible = visible; this.position = position; this.move = move;
        Dock = DockStyle.Right; Width = 12; TabStop = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        refresh.Tick += (sender, args) =>
        {
            if (!Visible) return;
            int count = total(), view = visible(), offset = position();
            if (count == lastTotal && view == lastVisible && offset == lastPosition) return;
            lastTotal = count; lastVisible = view; lastPosition = offset;
            Invalidate();
        };
        refresh.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int count = total(), view = visible(), range = count - view;
        thumb = Rectangle.Empty;
        if (range <= 0 || Height < 16) return;
        int size = Math.Min(Height - 1, Math.Max(22, (int)((long)Height * view / count)));
        int y = Math.Max(0, Math.Min(Height - size, (int)((long)(Height - size) * position() / range)));
        thumb = new Rectangle(2, y, Width - 4, size);
        int width = hover || Capture ? 7 : 3;
        Color color = SystemInformation.HighContrast ? SystemColors.WindowText
            : BackColor.GetBrightness() < .5f ? Color.FromArgb(116, 116, 116) : Color.FromArgb(143, 143, 143);
        using (var pen = new Pen(color, width))
        {
            pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLine(pen, Width / 2, y + width / 2, Width / 2, y + size - width / 2);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || thumb.IsEmpty) return;
        grab = thumb.Contains(e.Location) ? e.Y - thumb.Top : thumb.Height / 2;
        Capture = true;
        DragTo(e.Y);
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (Capture) DragTo(e.Y); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) Capture = false; Invalidate(); }

    private void DragTo(int y)
    {
        int travel = Height - thumb.Height;
        int range = Math.Max(0, total() - visible());
        if (travel > 0) move((int)((long)Math.Max(0, Math.Min(travel, y - grab)) * range / travel));
        Invalidate();
    }

    protected override void Dispose(bool disposing) { if (disposing) refresh.Dispose(); base.Dispose(disposing); }
}

internal sealed class ThumbTypeList : ListBox
{
    protected override CreateParams CreateParams
    {
        get { var parameters = base.CreateParams; parameters.Style &= ~0x00200000; return parameters; }
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == 0x0085 || message.Msg == 0x0047 || message.Msg == 0x0115)
            ShowScrollBar(Handle, 1, false);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int lines = SystemInformation.MouseWheelScrollLines;
        int visible = Math.Max(1, ClientSize.Height / ItemHeight);
        if (lines < 0) lines = visible;
        int index = TopIndex - (int)Math.Round(e.Delta / 120.0 * lines);
        if (Items.Count > 0) TopIndex = Math.Max(0, Math.Min(Math.Max(0, Items.Count - visible), index));
    }

    [DllImport("user32.dll")]
    private static extern bool ShowScrollBar(IntPtr window, int bar, bool show);
}

internal sealed class ThumbScrollPanel : Panel
{
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == 0x0085 || message.Msg == 0x0047 || message.Msg == 0x0115)
            ShowScrollBar(Handle, 1, false);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowScrollBar(IntPtr window, int bar, bool show);
}
