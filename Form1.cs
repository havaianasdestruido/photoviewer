namespace Photon;

public partial class Form1 : Form
{
    private OpenFileDialog _dialog = new();
    private PictureBox _pic = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Controls.Add(_pic);
        _dialog.Filter = "Image Files|*.bmp;*.jpg;*.jpeg;*.png;*.gif";
        _pic.Click += (s, a) => OpenImage();
    }

    private void OpenImage()
    {
        if (_dialog.ShowDialog() == DialogResult.OK)
        {
            using var img = System.Drawing.Image.FromFile(_dialog.FileName);
            _pic.Image?.Dispose();
            _pic.Image = new Bitmap(img);
        }
    }
}
{
    public Form1()
    {
        InitializeComponent();
    }
}
