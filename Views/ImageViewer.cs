using System.Drawing;
using System.Windows.Forms;
using Photon.Engine;

namespace Photon.Views;

// Scrollable full-size image view with zoom, pan and rotation.
// All rendering is WIC (System.Windows.Media.Imaging) via WicBitmap.
public sealed class ImageViewer : UserControl
{
    private readonly Panel _scroll;
    private readonly PictureBox _pic;
    private readonly Label _empty;

    private BitmapSourceHolder? _source;   // frozen original BitmapSource
    private System.Drawing.Bitmap? _current; // rotated+zoom-rendered GDI bitmap
    private double _zoom = 1.0;
    private bool _fitToWindow = true;
    private int _rotation; // 0/90/180/270
    private bool _dragging;
    private Point _dragStart;
    private Point _scrollStart;

    public ImageViewer()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(24, 24, 28);

        _scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.FromArgb(24, 24, 28),
        };

        _pic = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(24, 24, 28),
        };
        _pic.MouseWheel += OnPicMouseWheel;
        _pic.MouseDown += OnPicMouseDown;
        _pic.MouseMove += OnPicMouseMove;
        _pic.MouseUp += OnPicMouseUp;

        _empty = new Label
        {
            Text = "Open a folder to begin.\r\n\r\n" +
                   "Keys:  \u2190 \u2192 navigate   +/- zoom   F fit   ][ rotate   Space slideshow",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(160, 160, 160),
            BackColor = Color.Transparent,
        };

        _scroll.Controls.Add(_pic);
        Controls.Add(_scroll);
        Controls.Add(_empty);
        _empty.BringToFront();
    }

    public double Zoom => _zoom;
    public bool FitToWindow => _fitToWindow;
    public bool HasImage => _source != null;
    public event EventHandler? ViewChanged;

    public void ShowImage(string path)
    {
        ClearImage();
        var src = WicBitmap.LoadSource(path);
        _source = new BitmapSourceHolder(src);
        _rotation = 0;
        _fitToWindow = true;
        Render(recenter: true);
        _empty.Visible = false;
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        ClearImage();
        _empty.Visible = true;
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomIn() => SetZoom(_zoom * 1.25, recenter: false);
    public void ZoomOut() => SetZoom(_zoom / 1.25, recenter: false);
    public void ActualSize() => SetZoom(1.0, recenter: true);
    public void FitToWindowView() { _fitToWindow = true; Render(recenter: true); }

    public void RotateClockwise()
    {
        if (_source == null) return;
        _rotation = (_rotation + 90) % 360;
        Render(recenter: true);
    }

    private void SetZoom(double zoom, bool recenter)
    {
        if (_source == null) return;
        _fitToWindow = false;
        _zoom = Math.Clamp(zoom, 0.05, 32.0);
        Render(recenter);
    }

    private void Render(bool recenter)
    {
        if (_source == null)
            return;

        BitmapSourceHolder rotated = _rotation switch
        {
            90 => _source.Rotate90Cached,
            180 => _source.Rotate180Cached,
            270 => _source.Rotate270Cached,
            _ => _source,
        };

        var bmp = WicBitmap.ToGdi(rotated.Value);
        int w = bmp.Width;
        int h = bmp.Height;

        if (_fitToWindow)
        {
            int cw = Math.Max(8, _scroll.ClientSize.Width - 8);
            int ch = Math.Max(8, _scroll.ClientSize.Height - 8);
            _zoom = Math.Min((double)cw / w, (double)ch / h);
            _zoom = Math.Clamp(_zoom, 0.02, 1.0);
        }

        int dw = (int)Math.Max(1, Math.Round(w * _zoom));
        int dh = (int)Math.Max(1, Math.Round(h * _zoom));

        _pic.Image?.Dispose();
        _current = bmp;
        _pic.Image = bmp;
        _pic.Size = new Size(dw, dh);
        _scroll.AutoScrollPosition = Point.Empty;

        if (recenter)
        {
            int sx = Math.Max(0, (dw - _scroll.ClientSize.Width) / 2);
            int sy = Math.Max(0, (dh - _scroll.ClientSize.Height) / 2);
            _scroll.AutoScrollPosition = new Point(-sx, -sy);
        }
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearImage()
    {
        _source?.Dispose();
        _source = null;
        _current?.Dispose();
        _current = null;
        if (_pic.Image != null)
        {
            _pic.Image.Dispose();
            _pic.Image = null;
        }
        _pic.Size = Size.Empty;
        _zoom = 1.0;
        _rotation = 0;
    }

    // --- Zoom to mouse wheel position --------------------------------
    private void OnPicMouseWheel(object? sender, MouseEventArgs e)
    {
        if (_source == null || _fitToWindow)
            return;
        double factor = e.Delta > 0 ? 1.25 : 1.0 / 1.25;
        double oldZoom = _zoom;
        double newZoom = Math.Clamp(oldZoom * factor, 0.05, 32.0);
        if (Math.Abs(newZoom - oldZoom) < 0.0001)
            return;

        // Keep the pixel under the cursor anchored.
        var pt = _scroll.PointToClient(Cursor.Position);
        double oldImgX = Math.Abs(_scroll.AutoScrollPosition.X) + pt.X;
        double oldImgY = Math.Abs(_scroll.AutoScrollPosition.Y) + pt.Y;
        double scale = newZoom / oldZoom;
        _zoom = newZoom;
        _fitToWindow = false;
        Render(recenter: false);
        int nx = (int)Math.Round(oldImgX * scale - pt.X);
        int ny = (int)Math.Round(oldImgY * scale - pt.Y);
        _scroll.AutoScrollPosition = new Point(-Math.Max(0, nx), -Math.Max(0, ny));
    }

    // --- Drag to pan ------------------------------------------------
    private void OnPicMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _source == null)
            return;
        _dragging = true;
        _dragStart = e.Location;
        _scrollStart = new Point(Math.Abs(_scroll.AutoScrollPosition.X), Math.Abs(_scroll.AutoScrollPosition.Y));
        _pic.Cursor = Cursors.SizeAll;
    }

    private void OnPicMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;
        int dx = e.X - _dragStart.X;
        int dy = e.Y - _dragStart.Y;
        int nx = Math.Max(0, _scrollStart.X - dx);
        int ny = Math.Max(0, _scrollStart.Y - dy);
        _scroll.AutoScrollPosition = new Point(-nx, -ny);
    }

    private void OnPicMouseUp(object? sender, MouseEventArgs e)
    {
        _dragging = false;
        _pic.Cursor = Cursors.Default;
    }

    // Holds the frozen source plus lazily computed rotation variants so
    // rotation never re-decodes from disk.
    private sealed class BitmapSourceHolder : IDisposable
    {
        public System.Windows.Media.Imaging.BitmapSource Value { get; }
        public System.Windows.Media.Imaging.BitmapSource? Rotate90 { get; private set; }
        public System.Windows.Media.Imaging.BitmapSource? Rotate180 { get; private set; }
        public System.Windows.Media.Imaging.BitmapSource? Rotate270 { get; private set; }

        public BitmapSourceHolder(System.Windows.Media.Imaging.BitmapSource value) => Value = value;

        public System.Windows.Media.Imaging.BitmapSource Rotate90Cached =>
            Rotate90 ??= WicBitmap.Rotate(Value, 90);
        public System.Windows.Media.Imaging.BitmapSource Rotate180Cached =>
            Rotate180 ??= WicBitmap.Rotate(Value, 180);
        public System.Windows.Media.Imaging.BitmapSource Rotate270Cached =>
            Rotate270 ??= WicBitmap.Rotate(Value, 270);

        public void Dispose() { /* Value is shared/frozen; GC handles it. */ }
    }
}
