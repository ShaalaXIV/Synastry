using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace EmoteLink.LabelAdmin;

public sealed class MainForm : Form
{
    private readonly TextBox endpoint = new() { Text = "http://127.0.0.1:25080", Width = 210 };
    private readonly TextBox token = new() { UseSystemPasswordChar = true, Width = 210 };
    private readonly TextBox search = new() { PlaceholderText = "Search labels, group, option, fingerprint...", Dock = DockStyle.Fill };
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoGenerateColumns = false, AllowUserToAddRows = false };
    private readonly ToolStripStatusLabel status = new("Ready");
    private List<LabelRecord> records = [];

    public MainForm()
    {
        Text = "EmoteLink Community Label Administration";
        Width = 1100; Height = 650; MinimumSize = new Size(800, 450);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Accepted", DataPropertyName = nameof(LabelRow.Accepted), Width = 150 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Leading suggestion", DataPropertyName = nameof(LabelRow.Suggested), Width = 170 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Votes", DataPropertyName = nameof(LabelRow.Votes), Width = 55 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Group", DataPropertyName = nameof(LabelRow.Group), Width = 160 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Option", DataPropertyName = nameof(LabelRow.Option), Width = 180 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Fingerprint", DataPropertyName = nameof(LabelRow.Fingerprint), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

        var connect = Button("Refresh", async (_, _) => await RefreshRecords());
        var approve = Button("Approve leading vote", async (_, _) => await Act("approve", HttpMethod.Post));
        var clear = Button("Clear votes", async (_, _) => await Act("votes", HttpMethod.Delete));
        var edit = Button("Edit accepted label", async (_, _) => await EditLabel());
        var delete = Button("Delete record", async (_, _) => await DeleteRecord());
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        top.Controls.AddRange([new Label { Text = "Tunnel URL:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, endpoint,
            new Label { Text = "Admin token:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }, token, connect]);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        actions.Controls.AddRange([approve, clear, edit, delete]);
        var searchPanel = new Panel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(8, 4, 8, 4) };
        searchPanel.Controls.Add(search);
        search.TextChanged += (_, _) => ApplyFilter();
        Controls.AddRange([grid, actions, searchPanel, top, new StatusStrip { Items = { status } }]);
        LoadSettings();
    }

    private static Button Button(string text, EventHandler action) { var button = new Button { Text = text, AutoSize = true }; button.Click += action; return button; }
    private HttpClient Client()
    {
        var client = new HttpClient { BaseAddress = new Uri(endpoint.Text.Trim().TrimEnd('/') + "/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Text);
        return client;
    }
    private LabelRecord? Selected() => grid.CurrentRow?.DataBoundItem is LabelRow row ? records.FirstOrDefault(r => r.Key == row.Key) : null;

    private async Task RefreshRecords()
    {
        try
        {
            status.Text = "Loading...";
            using var client = Client();
            records = await client.GetFromJsonAsync<List<LabelRecord>>("admin/community-labels/") ?? [];
            SaveSettings(); ApplyFilter(); status.Text = $"Loaded {records.Count:N0} records";
        }
        catch (Exception ex) { Error(ex); }
    }
    private void ApplyFilter()
    {
        var term = search.Text.Trim();
        grid.DataSource = records.Where(r => term.Length == 0 ||
            $"{r.AcceptedLabel} {r.Fingerprint} {r.Group} {r.Option} {string.Join(' ', r.Votes.Select(v => v.Label))}".Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(r => new LabelRow(r.Key, r.AcceptedLabel, r.Votes.FirstOrDefault()?.Label ?? "", r.Votes.Sum(v => v.Count), r.Group, r.Option, r.Fingerprint)).ToList();
    }
    private async Task Act(string suffix, HttpMethod method)
    {
        var record = Selected(); if (record is null) return;
        try { using var client = Client(); using var response = await client.SendAsync(new HttpRequestMessage(method, $"admin/community-labels/{suffix}?key={Uri.EscapeDataString(record.Key)}")); response.EnsureSuccessStatusCode(); await RefreshRecords(); }
        catch (Exception ex) { Error(ex); }
    }
    private async Task EditLabel()
    {
        var record = Selected(); if (record is null) return;
        var value = Microsoft.VisualBasic.Interaction.InputBox("New accepted label:", "Edit label", record.AcceptedLabel).Trim();
        if (value.Length == 0) return;
        try { using var client = Client(); using var response = await client.PutAsJsonAsync($"admin/community-labels/accepted?key={Uri.EscapeDataString(record.Key)}", new { Label = value }); response.EnsureSuccessStatusCode(); await RefreshRecords(); }
        catch (Exception ex) { Error(ex); }
    }
    private async Task DeleteRecord()
    {
        var record = Selected(); if (record is null || MessageBox.Show($"Delete '{record.AcceptedLabel}' permanently?", "Confirm delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await Act("record", HttpMethod.Delete);
    }
    private void Error(Exception ex) { status.Text = "Request failed"; MessageBox.Show(ex.Message, "EmoteLink Label Admin", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    private string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmoteLink.LabelAdmin", "settings.json");
    private void LoadSettings() { try { var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)); if (s is not null) { endpoint.Text = s.Endpoint; token.Text = s.Token; } } catch { } }
    private void SaveSettings() { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(endpoint.Text, token.Text))); }

    private sealed record Settings(string Endpoint, string Token);
    private sealed record LabelRecord(string Key, string Fingerprint, string Group, string Option, string AcceptedLabel, List<Vote> Votes);
    private sealed record Vote(string Label, int Count);
    private sealed record LabelRow(string Key, string Accepted, string Suggested, int Votes, string Group, string Option, string Fingerprint);
}
