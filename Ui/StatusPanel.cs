using System.Drawing;
using System.Windows.Forms;
using Photon.Engine;

namespace Photon.Ui;

// Live status panel: one row per WMMR engine DLL showing load state,
// probed exports and the last HRESULT observed. State is honest — stubs are
// labeled "engine stub", missing DLLs "not loaded".
public sealed class StatusPanel : UserControl
{
    private readonly Label _header;
    private readonly DataGridView _grid;
    public event EventHandler? RefreshRequested;

    public StatusPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(38, 38, 44);
        Padding = new Padding(6);

        _header = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            ForeColor = Color.FromArgb(210, 210, 210),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Text = "WMMR Photo Engines",
        };
        var refresh = new Button
        {
            Text = "Re-probe",
            Dock = DockStyle.Top,
            Height = 24,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(56, 56, 64),
            ForeColor = Color.FromArgb(220, 220, 220),
        };
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = Color.FromArgb(30, 30, 36),
            BorderStyle = BorderStyle.None,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeight = 26,
        };
        _grid.Columns.Add("Dll", "DLL");
        _grid.Columns.Add("State", "State");
        _grid.Columns.Add("Exports", "Exports");
        _grid.Columns.Add("HResult", "Last HRESULT");
        _grid.Columns.Add("Notes", "Notes");
        _grid.Columns["Dll"].FillWeight = 16;
        _grid.Columns["State"].FillWeight = 10;
        _grid.Columns["Exports"].FillWeight = 7;
        _grid.Columns["HResult"].FillWeight = 20;
        _grid.Columns["Notes"].FillWeight = 47;

        Controls.Add(_grid);
        Controls.Add(refresh);
        Controls.Add(_header);
    }

    public void SetStatus(IReadOnlyList<DllStatus> statuses, string? dllDirectory, bool found)
    {
        _header.Text = found
            ? $"WMMR Photo Engines  —  {dllDirectory}"
            : $"WMMR Photo Engines  —  {dllDirectory ?? "no DLL directory"}";

        _grid.Rows.Clear();
        foreach (var st in statuses)
        {
            int row = _grid.Rows.Add(
                st.Name,
                st.StateLabel,
                st.IsLoaded ? st.ExportsFound.ToString() : "—",
                st.HResultLabel,
                st.Notes);
            var cell = _grid.Rows[row].Cells["State"];
            cell.Style.ForeColor = st.Kind switch
            {
                EngineKind.EnginePresent => Color.FromArgb(110, 200, 120),
                EngineKind.EngineStub => Color.FromArgb(230, 180, 80),
                _ => Color.FromArgb(170, 170, 170),
            };
        }
    }
}
