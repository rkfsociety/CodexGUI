using System;
using System.Drawing;
using System.Windows.Forms;
using Forms = System.Windows.Forms;

namespace CodexGUI;

internal sealed class ScreenshotSelectionForm : Forms.Form
{
    private Point _start;
    private Point _current;
    private bool _selecting;

    public Rectangle SelectionBounds { get; private set; }

    public ScreenshotSelectionForm()
    {
        Bounds = Forms.SystemInformation.VirtualScreen;
        StartPosition = Forms.FormStartPosition.Manual;
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Color.Black;
        Opacity = 0.48;
        Cursor = Forms.Cursors.Cross;

        KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape) return;
            DialogResult = Forms.DialogResult.Cancel;
            Close();
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_selecting)
        {
            const string instruction = "Перетащите мышь, чтобы выбрать область · Esc — отмена";
            var size = e.Graphics.MeasureString(instruction, Font);
            e.Graphics.DrawString(instruction, Font, Brushes.White,
                Math.Max(12, (ClientSize.Width - size.Width) / 2), Math.Max(12, ClientSize.Height * 0.12f));
            return;
        }

        var selection = GetSelectionRectangle(_start, _current);
        if (selection.Width > 0 && selection.Height > 0)
        {
            using var pen = new Pen(Color.White, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            e.Graphics.DrawRectangle(pen, selection);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _start = e.Location;
        _current = e.Location;
        _selecting = true;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selecting) return;
        _current = e.Location;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_selecting || e.Button != MouseButtons.Left) return;
        _current = e.Location;
        var selection = GetSelectionRectangle(_start, _current);
        Capture = false;
        _selecting = false;
        if (selection.Width < 2 || selection.Height < 2)
        {
            Invalidate();
            return;
        }

        SelectionBounds = new Rectangle(Location.X + selection.X, Location.Y + selection.Y, selection.Width, selection.Height);
        DialogResult = Forms.DialogResult.OK;
        Close();
    }

    private static Rectangle GetSelectionRectangle(Point first, Point second) => new(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Abs(first.X - second.X),
        Math.Abs(first.Y - second.Y));
}
