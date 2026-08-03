using System.Drawing;
using System.Windows.Forms;
using Photon.Engine;

namespace Photon.Views;

// Thumbnail strip for the current folder. Decodes small WIC bitmaps in the
// background so large folders stay responsive.
public sealed class ThumbnailGrid : UserControl
{
    private static readonly string[] ImageExtensions =
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".bmp", ".gif",
        ".tif", ".tiff", ".wdp", ".jxr", ".ico",
    };

    private readonly FlowLayoutPanel _flow;
    private readonly List<ImageInfo> _items = new();
    private readonly List<Panel> _cells = new();
    private CancellationTokenSource? _cts;
    private int _selectedIndex = -1;

    public event EventHandler<ImageInfo>? ImageSelected;

    public int Count => _items.Count;
    public int SelectedIndex => _selectedIndex;

    public ThumbnailGrid()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(32, 32, 38);
        _flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(6),
            BackColor = Color.FromArgb(32, 32, 38),
        };
        Controls.Add(_flow);
    }

    public ImageInfo? Current =>
        _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : null;

    public ImageInfo? ItemAt(int index) =>
        index >= 0 && index < _items.Count ? _items[index] : null;

    public async void LoadFolder(string folder)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        Clear();

        var files = Directory.EnumerateFiles(folder)
            .Where(f => ImageExtensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = 0; i < files.Count; i++)
        {
            if (cts.IsCancellationRequested)
                return;
            _items.Add(new ImageInfo(files[i], i));
            _cells.Add(CreateCellPlaceholder());
            await LoadThumbAsync(i, files[i], cts.Token);
        }

        if (files.Count == 0)
        {
            var lbl = new Label
            {
                Text = "No images in this folder.",
                AutoSize = true,
                ForeColor = Color.FromArgb(140, 140, 140),
                Margin = new Padding(8),
            };
            _flow.Controls.Add(lbl);
        }
        else
        {
            SelectIndex(0);
        }
    }

    public void SelectIndex(int index)
    {
        if (index < 0 || index >= _cells.Count)
            return;
        _selectedIndex = index;
        for (int i = 0; i < _cells.Count; i++)
            _cells[i].BackColor = i == index ? Color.FromArgb(46, 106, 196) : Color.FromArgb(48, 48, 56);
        _cells[index].ScrollControlIntoView(_cells[index]);
        var item = _items[index];
        ImageSelected?.Invoke(this, item);
    }

    public bool SelectNext()
    {
        if (_selectedIndex + 1 >= _items.Count)
            return false;
        SelectIndex(_selectedIndex + 1);
        return true;
    }

    public bool SelectPrevious()
    {
        if (_selectedIndex - 1 < 0)
            return false;
        SelectIndex(_selectedIndex - 1);
        return true;
    }

    private void Clear()
    {
        _selectedIndex = -1;
        _items.Clear();
        _cells.Clear();
        _flow.SuspendLayout();
        foreach (Control c in _flow.Controls)
            c.Dispose();
        _flow.Controls.Clear();
        _flow.ResumeLayout();
    }

    private Panel CreateCellPlaceholder()
    {
        var cell = new Panel
        {
            Size = new Size(128, 132),
            Margin = new Padding(4),
            BackColor = Color.FromArgb(48, 48, 56),
        };
        var box = new PictureBox
        {
            Size = new Size(120, 92),
            Location = new Point(4, 4),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(40, 40, 48),
        };
        var label = new Label
        {
            Location = new Point(4, 100),
            Size = new Size(120, 28),
            Text = "",
            TextAlign = ContentAlignment.TopCenter,
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(200, 200, 200),
            Font = new Font("Segoe UI", 7.5f),
        };
        cell.Controls.Add(box);
        cell.Controls.Add(label);
        cell.Tag = label;
        cell.Click += OnCellClick;
        box.Click += OnCellClick;
        _flow.Controls.Add(cell);
        return cell;
    }

    private async Task LoadThumbAsync(int index, string path, CancellationToken ct)
    {
        var cell = _cells[index];
        var label = (Label)cell.Tag!;
        label.Text = System.IO.Path.GetFileName(path);

        try
        {
            // Decode on background thread; only the final System.Drawing.Bitmap
            // crosses the thread boundary.
            var thumb = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var src = WicBitmap.LoadSource(path, decodePixelWidth: 220);
                var b = WicBitmap.ToGdi(src);
                return b;
            }, ct);

            if (ct.IsCancellationRequested)
            {
                thumb.Dispose();
                return;
            }
            if (index >= _cells.Count)
            {
                thumb.Dispose();
                return;
            }
            var box = (PictureBox)cell.Controls[0];
            var old = box.Image;
            box.Image = thumb;
            old?.Dispose();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            label.Text = System.IO.Path.GetFileName(path) + " (!)";
            label.Tag = $"Failed to decode: {ex.Message}";
        }
    }

    private void OnCellClick(object? sender, EventArgs e)
    {
        int index = _cells.FindIndex(c => ReferenceEquals(c, sender));
        if (index < 0 && sender is Control ctl)
            index = _cells.FindIndex(c => c == ctl.Parent);
        if (index >= 0)
            SelectIndex(index);
    }
}
