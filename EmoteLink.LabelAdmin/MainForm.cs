using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace EmoteLink.LabelAdmin;

public sealed class MainForm : Form
{
    private readonly TextBox endpoint = new() { Text = "http://127.0.0.1:25081", Width = 190 };
    private readonly TextBox token = new() { UseSystemPasswordChar = true, Width = 190 };
    private readonly TextBox sshHost = new() { Text = "root@74.208.141.184", Width = 190 };
    private readonly TextBox sshKey = new()
    {
        Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "codex_aethercast"),
        Width = 300
    };
    private readonly TextBox search = new()
    {
        PlaceholderText = "Search mod, animation, labels, group, option, fingerprint...",
        Dock = DockStyle.Fill
    };
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoGenerateColumns = false,
        AllowUserToAddRows = false
    };
    private readonly ToolStripStatusLabel status = new("Ready");
    private readonly Button connect = new() { Text = "Connect / Refresh", AutoSize = true };
    private List<LabelRecord> records = [];
    private Process? tunnelProcess;
    private bool connecting;

    public MainForm()
    {
        Text = "Synastry Community Label Administration";
        Width = 1320;
        Height = 700;
        MinimumSize = new Size(900, 500);

        grid.Columns.Add(new DataGridViewTextBoxColumn
            { HeaderText = "Mod", DataPropertyName = nameof(LabelRow.ModName), Width = 230 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
            { HeaderText = "Animation", DataPropertyName = nameof(LabelRow.AnimationName), Width = 170 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
            { HeaderText = "Accepted", DataPropertyName = nameof(LabelRow.Accepted), Width = 135 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
            { HeaderText = "Leading suggestion", DataPropertyName = nameof(LabelRow.Suggested), Width = 150 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
            { HeaderText = "Votes", DataPropertyName = nameof(LabelRow.Votes), Width = 55 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
            { HeaderText = "Group", DataPropertyName = nameof(LabelRow.Group), Width = 125 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
            { HeaderText = "Option", DataPropertyName = nameof(LabelRow.Option), Width = 130 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Fingerprint",
            DataPropertyName = nameof(LabelRow.Fingerprint),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });

        connect.Click += async (_, _) => await ConnectAndRefresh();
        var approve = Button("Approve leading vote", async (_, _) => await Act("approve", HttpMethod.Post));
        var clear = Button("Clear votes", async (_, _) => await Act("votes", HttpMethod.Delete));
        var edit = Button("Edit accepted label", async (_, _) => await EditLabel());
        var delete = Button("Delete record", async (_, _) => await DeleteRecord());

        var connectionRows = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(6)
        };
        var relayRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        relayRow.Controls.AddRange([
            FieldLabel("Tunnel URL:"), endpoint,
            FieldLabel("Admin token:"), token,
            connect
        ]);
        var sshRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        sshRow.Controls.AddRange([
            FieldLabel("SSH host:"), sshHost,
            FieldLabel("SSH key:"), sshKey
        ]);
        connectionRows.Controls.AddRange([relayRow, sshRow]);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        actions.Controls.AddRange([approve, clear, edit, delete]);
        var searchPanel = new Panel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(8, 4, 8, 4) };
        searchPanel.Controls.Add(search);
        search.TextChanged += (_, _) => ApplyFilter();
        Controls.AddRange([grid, actions, searchPanel, connectionRows, new StatusStrip { Items = { status } }]);

        LoadSettings();
        Shown += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(token.Text)) await ConnectAndRefresh();
        };
        FormClosed += (_, _) => StopOwnedTunnel();
    }

    private static Label FieldLabel(string text) =>
        new() { Text = text, AutoSize = true, Padding = new Padding(8, 6, 0, 0) };

    private static Button Button(string text, EventHandler action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += action;
        return button;
    }

    private HttpClient Client(TimeSpan? timeout = null)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(endpoint.Text.Trim().TrimEnd('/') + "/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Text);
        return client;
    }

    private LabelRecord? Selected() =>
        grid.CurrentRow?.DataBoundItem is LabelRow row
            ? records.FirstOrDefault(record => record.Key == row.Key)
            : null;

    private async Task ConnectAndRefresh()
    {
        if (connecting) return;
        connecting = true;
        connect.Enabled = false;
        try
        {
            SaveSettings();
            if (!await RelayReachable()) await StartTunnel();
            await RefreshRecords();
        }
        catch (Exception exception)
        {
            Error(exception);
        }
        finally
        {
            connect.Enabled = true;
            connecting = false;
        }
    }

    private async Task<bool> RelayReachable()
    {
        try
        {
            using var client = Client(TimeSpan.FromMilliseconds(750));
            using var response = await client.GetAsync("health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task StartTunnel()
    {
        if (tunnelProcess is { HasExited: false }) return;
        if (!Uri.TryCreate(endpoint.Text.Trim(), UriKind.Absolute, out var tunnelUri) ||
            !tunnelUri.IsLoopback || tunnelUri.Port < 1)
            throw new InvalidOperationException("Tunnel URL must be a local address such as http://127.0.0.1:25081.");
        var host = sshHost.Text.Trim();
        if (host.Length == 0) throw new InvalidOperationException("Enter the SSH host used for the relay server.");
        var keyPath = Environment.ExpandEnvironmentVariables(sshKey.Text.Trim());
        if (!File.Exists(keyPath)) throw new FileNotFoundException("The SSH private key was not found.", keyPath);

        status.Text = "Starting secure SSH tunnel...";
        var start = new ProcessStartInfo("ssh.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "-N", "-L", $"{tunnelUri.Port}:127.0.0.1:25080", "-i", keyPath,
                     "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes", "-o", "ExitOnForwardFailure=yes",
                     "-o", "ServerAliveInterval=30", "-o", "ServerAliveCountMax=3", host
                 })
            start.ArgumentList.Add(argument);
        tunnelProcess = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start ssh.exe.");

        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (tunnelProcess.HasExited)
            {
                var details = (await tunnelProcess.StandardError.ReadToEndAsync()).Trim();
                throw new InvalidOperationException(details.Length == 0
                    ? "The SSH tunnel exited before connecting."
                    : details);
            }
            if (await RelayReachable())
            {
                status.Text = "Secure tunnel connected";
                return;
            }
            await Task.Delay(200);
        }
        StopOwnedTunnel();
        throw new TimeoutException("The SSH tunnel started, but the relay did not answer within eight seconds.");
    }

    private void StopOwnedTunnel()
    {
        if (tunnelProcess is null) return;
        try
        {
            if (!tunnelProcess.HasExited) tunnelProcess.Kill(true);
        }
        catch
        {
            // The process may already have exited while the window was closing.
        }
        tunnelProcess.Dispose();
        tunnelProcess = null;
    }

    private async Task RefreshRecords()
    {
        status.Text = "Loading labels...";
        using var client = Client();
        records = await client.GetFromJsonAsync<List<LabelRecord>>("admin/community-labels/") ?? [];
        SaveSettings();
        ApplyFilter();
        status.Text = $"Loaded {records.Count:N0} records";
    }

    private void ApplyFilter()
    {
        var term = search.Text.Trim();
        grid.DataSource = records.Where(record => term.Length == 0 ||
                ($"{record.ModName} {record.AnimationName} {record.AcceptedLabel} {record.Fingerprint} " +
                 $"{record.Group} {record.Option} {string.Join(' ', record.Votes.Select(vote => vote.Label))}")
                .Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(record => record.ModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.AnimationName, StringComparer.OrdinalIgnoreCase)
            .Select(record => new LabelRow(
                record.Key,
                string.IsNullOrWhiteSpace(record.ModName) ? "(awaiting plugin metadata)" : record.ModName,
                string.IsNullOrWhiteSpace(record.AnimationName) ? record.Option : record.AnimationName,
                record.AcceptedLabel,
                record.Votes.FirstOrDefault()?.Label ?? "",
                record.Votes.Sum(vote => vote.Count),
                record.Group,
                record.Option,
                record.Fingerprint))
            .ToList();
    }

    private async Task Act(string suffix, HttpMethod method)
    {
        var record = Selected();
        if (record is null) return;
        try
        {
            using var client = Client();
            using var response = await client.SendAsync(new HttpRequestMessage(method,
                $"admin/community-labels/{suffix}?key={Uri.EscapeDataString(record.Key)}"));
            response.EnsureSuccessStatusCode();
            await RefreshRecords();
        }
        catch (Exception exception)
        {
            Error(exception);
        }
    }

    private async Task EditLabel()
    {
        var record = Selected();
        if (record is null) return;
        var value = Microsoft.VisualBasic.Interaction.InputBox(
            "New accepted label:", "Edit label", record.AcceptedLabel).Trim();
        if (value.Length == 0) return;
        try
        {
            using var client = Client();
            using var response = await client.PutAsJsonAsync(
                $"admin/community-labels/accepted?key={Uri.EscapeDataString(record.Key)}", new { Label = value });
            response.EnsureSuccessStatusCode();
            await RefreshRecords();
        }
        catch (Exception exception)
        {
            Error(exception);
        }
    }

    private async Task DeleteRecord()
    {
        var record = Selected();
        if (record is null || MessageBox.Show(
                $"Delete '{record.AcceptedLabel}' for {record.AnimationName} permanently?",
                "Confirm delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await Act("record", HttpMethod.Delete);
    }

    private void Error(Exception exception)
    {
        status.Text = "Request failed";
        MessageBox.Show(exception.Message, "Synastry Label Admin", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EmoteLink.LabelAdmin", "settings.json");

    private void LoadSettings()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            if (settings is null) return;
            if (!string.IsNullOrWhiteSpace(settings.Endpoint)) endpoint.Text = settings.Endpoint;
            if (!string.IsNullOrWhiteSpace(settings.Token)) token.Text = settings.Token;
            if (!string.IsNullOrWhiteSpace(settings.SshHost)) sshHost.Text = settings.SshHost;
            if (!string.IsNullOrWhiteSpace(settings.SshKey)) sshKey.Text = settings.SshKey;
        }
        catch
        {
            // First launch or an obsolete settings file: keep the safe defaults.
        }
    }

    private void SaveSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings
        {
            Endpoint = endpoint.Text,
            Token = token.Text,
            SshHost = sshHost.Text,
            SshKey = sshKey.Text
        }));
    }

    private sealed class Settings
    {
        public string Endpoint { get; set; } = "";
        public string Token { get; set; } = "";
        public string SshHost { get; set; } = "";
        public string SshKey { get; set; } = "";
    }

    private sealed class LabelRecord
    {
        public string Key { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public string ModName { get; set; } = "";
        public string AnimationName { get; set; } = "";
        public string Group { get; set; } = "";
        public string Option { get; set; } = "";
        public string AcceptedLabel { get; set; } = "";
        public List<Vote> Votes { get; set; } = [];
    }

    private sealed class Vote
    {
        public string Label { get; set; } = "";
        public int Count { get; set; }
    }

    private sealed record LabelRow(
        string Key,
        string ModName,
        string AnimationName,
        string Accepted,
        string Suggested,
        int Votes,
        string Group,
        string Option,
        string Fingerprint);
}
