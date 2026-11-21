using FATX;
using FATX.AI;
using FATX.Recovery;
using System;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FATXTools.Dialogs
{
    /// <summary>
    /// Chat window that lets Claude drive the recovery tools on the open drive.
    /// </summary>
    public class AssistantDialog : Form
    {
        private readonly DriveReader drive;
        private readonly string driveName;

        private RichTextBox transcript;
        private TextBox input;
        private Button sendButton;
        private Button stopButton;
        private TextBox apiKeyBox;
        private CheckBox rememberKey;
        private TextBox outputBox;
        private ComboBox modelBox;
        private ComboBox effortBox;
        private Label statusLabel;

        private RecoverySession session;
        private RecoveryAgent agent;
        private CancellationTokenSource cts;

        private static readonly string KeyFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FATXTools", "anthropic.key");

        private static readonly (string label, string prompt)[] Presets =
        {
            ("Find deleted game builds", "Run the metadata analysis on the main data partition and find deleted game builds (folders with XEX/XBE executables and their companion files). Summarize each build with title id, version, file count, and how intact it looks. Do not recover anything yet."),
            ("Verify deleted executables", "Find every deleted XEX and XBE on the main data partition, verify them, and if any fail verification try the other chain strategy and tell me which strategy recovers more verified files."),
            ("Carve lost executables", "Run the file carver on free space of the main data partition and list the executables it finds whose directory entries are gone, with verification results."),
            ("Recover verified executables", "Recover every deleted executable that passes verification, together with the rest of its build folder, into subfolders named by title id and build folder. Then summarize what was written."),
        };

        public AssistantDialog(DriveReader drive, string driveName)
        {
            this.drive = drive;
            this.driveName = driveName;
            BuildUi();
            LoadKey();
        }

        private void BuildUi()
        {
            Text = "Claude Recovery Assistant - " + Path.GetFileName(driveName);
            Size = new Size(1000, 760);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            var settings = new TableLayoutPanel { Dock = DockStyle.Top, Height = 92, ColumnCount = 6, Padding = new Padding(6) };
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            apiKeyBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
            rememberKey = new CheckBox { Text = "Remember (encrypted for this Windows user)", AutoSize = true };
            modelBox = new ComboBox { Dock = DockStyle.Fill };
            modelBox.Items.AddRange(new object[] { "claude-opus-5-5", "claude-sonnet-5-5", "claude-fable-5-2" });
            modelBox.Text = AgentConfig.DefaultModel;
            effortBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
            effortBox.Items.AddRange(new object[] { "low", "medium", "high", "xhigh", "max" });
            effortBox.SelectedItem = "high";
            outputBox = new TextBox { Dock = DockStyle.Fill, Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FATXTools Recovered") };
            var browse = new Button { Text = "...", Width = 32 };
            browse.Click += (s, e) =>
            {
                using (var fbd = new FolderBrowserDialog { SelectedPath = outputBox.Text })
                    if (fbd.ShowDialog(this) == DialogResult.OK) outputBox.Text = fbd.SelectedPath;
            };

            settings.Controls.Add(new Label { Text = "API key:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            settings.Controls.Add(apiKeyBox, 1, 0);
            settings.Controls.Add(rememberKey, 2, 0);
            settings.SetColumnSpan(rememberKey, 2);
            settings.Controls.Add(new Label { Text = "Model:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            settings.Controls.Add(modelBox, 1, 1);
            settings.Controls.Add(new Label { Text = "Effort:", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 1);
            settings.Controls.Add(effortBox, 3, 1);
            settings.Controls.Add(new Label { Text = "Output:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            settings.Controls.Add(outputBox, 1, 2);
            settings.SetColumnSpan(outputBox, 3);
            settings.Controls.Add(browse, 4, 2);

            var presets = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 2, 6, 2) };
            foreach (var (label, prompt) in Presets)
            {
                var b = new Button { Text = label, AutoSize = true };
                b.Click += (s, e) => { input.Text = prompt; Send(); };
                presets.Controls.Add(b);
            }

            transcript = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = SystemColors.Window, Font = new Font("Consolas", 9.5f) };

            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 96, ColumnCount = 2, Padding = new Padding(6) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            input = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
            input.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; Send(); }
            };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
            sendButton = new Button { Text = "Send (Ctrl+Enter)", Width = 130 };
            sendButton.Click += (s, e) => Send();
            stopButton = new Button { Text = "Stop", Width = 130, Enabled = false };
            stopButton.Click += (s, e) => cts?.Cancel();
            var newChat = new Button { Text = "New conversation", Width = 130 };
            newChat.Click += (s, e) => { agent?.Reset(); transcript.Clear(); };
            buttons.Controls.Add(sendButton);
            buttons.Controls.Add(stopButton);
            buttons.Controls.Add(newChat);
            bottom.Controls.Add(input, 0, 0);
            bottom.Controls.Add(buttons, 1, 0);

            statusLabel = new Label { Dock = DockStyle.Bottom, Height = 20, Padding = new Padding(6, 0, 0, 0), Text = "Images are read only. Only short tool summaries (file names, sizes, header fields) are sent to Claude, never file contents in bulk." };

            Controls.Add(transcript);
            Controls.Add(presets);
            Controls.Add(settings);
            Controls.Add(bottom);
            Controls.Add(statusLabel);
        }

        private void Append(string text, Color color, bool bold = false)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => Append(text, color, bold)));
                return;
            }
            transcript.SelectionStart = transcript.TextLength;
            transcript.SelectionColor = color;
            transcript.SelectionFont = new Font(transcript.Font, bold ? FontStyle.Bold : FontStyle.Regular);
            transcript.AppendText(text + Environment.NewLine);
            transcript.ScrollToCaret();
        }

        private async void Send()
        {
            var request = input.Text.Trim();
            if (request.Length == 0 || cts != null)
                return;

            var key = apiKeyBox.Text.Trim();
            if (key.Length == 0)
                key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
            if (key.Length == 0)
            {
                MessageBox.Show(this, "Enter an Anthropic API key (or set ANTHROPIC_API_KEY).", "API key required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SaveKey(key);

            try
            {
                if (session == null)
                    session = new RecoverySession(drive, driveName);

                var config = new AgentConfig
                {
                    ApiKey = key,
                    Model = modelBox.Text.Trim(),
                    Effort = (string)effortBox.SelectedItem,
                    OutputDirectory = outputBox.Text,
                };

                if (agent == null || agentConfigChanged(config))
                {
                    agent = new RecoveryAgent(session, config);
                    agent.OnText += t => Append(t, Color.Black);
                    agent.OnToolCall += (n, a) => Append($"  > {n} {a}", Color.SteelBlue);
                    agent.OnToolResult += (n, r) => Append($"    {r}", Color.Gray);
                    lastConfig = config;
                }
            }
            catch (Exception e)
            {
                Append("Error: " + e.Message, Color.DarkRed);
                return;
            }

            Append("You: " + request, Color.DarkGreen, true);
            input.Clear();
            cts = new CancellationTokenSource();
            sendButton.Enabled = false;
            stopButton.Enabled = true;
            try
            {
                await agent.RunAsync(request, cts.Token);
                Append($"[tokens: in {agent.InputTokens:N0}, out {agent.OutputTokens:N0}]", Color.Gray);
            }
            catch (OperationCanceledException)
            {
                Append("Stopped.", Color.DarkOrange);
                // The last request has no answer; start over to keep the conversation valid.
                agent.Reset();
            }
            catch (Exception e)
            {
                Append("Error: " + e.Message, Color.DarkRed);
                agent.Reset();
            }
            finally
            {
                cts.Dispose();
                cts = null;
                sendButton.Enabled = true;
                stopButton.Enabled = false;
            }
        }

        private AgentConfig lastConfig;

        private bool agentConfigChanged(AgentConfig c)
            => lastConfig == null || lastConfig.Model != c.Model || lastConfig.Effort != c.Effort ||
               lastConfig.OutputDirectory != c.OutputDirectory || lastConfig.ApiKey != c.ApiKey;

        private void LoadKey()
        {
            try
            {
                if (File.Exists(KeyFile))
                {
                    var data = ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), null, DataProtectionScope.CurrentUser);
                    apiKeyBox.Text = Encoding.UTF8.GetString(data);
                    rememberKey.Checked = true;
                }
            }
            catch (Exception)
            {
                // Unreadable key file, ignore.
            }
        }

        private void SaveKey(string key)
        {
            try
            {
                if (rememberKey.Checked)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(KeyFile));
                    File.WriteAllBytes(KeyFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
                }
                else if (File.Exists(KeyFile))
                {
                    File.Delete(KeyFile);
                }
            }
            catch (Exception)
            {
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            cts?.Cancel();
            base.OnFormClosing(e);
        }
    }
}
