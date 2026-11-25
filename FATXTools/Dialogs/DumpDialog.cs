using FATX.Imaging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FATXTools.Dialogs
{
    /// <summary>
    /// Copies a physical drive (or converts an image) to a raw ".img" or
    /// compressed ".imgc" image.
    /// </summary>
    public class DumpDialog : Form
    {
        private readonly bool convert;
        private readonly ComboBox sourceBox = new ComboBox();
        private readonly Button sourceBrowse = new Button();
        private readonly TextBox targetBox = new TextBox();
        private readonly Button targetBrowse = new Button();
        private readonly CheckBox md5Box = new CheckBox();
        private readonly CheckBox sha1Box = new CheckBox();
        private readonly CheckBox sha256Box = new CheckBox();
        private readonly CheckBox resumeBox = new CheckBox();
        private readonly CheckBox openBox = new CheckBox();
        private readonly ProgressBar progressBar = new ProgressBar();
        private readonly Label statusLabel = new Label();
        private readonly Button startButton = new Button();
        private readonly Button closeButton = new Button();

        private List<DiskInfo> disks = new List<DiskInfo>();
        private CancellationTokenSource cancellation;
        private bool running;

        public DumpDialog(bool convert)
        {
            this.convert = convert;

            Text = convert ? "Convert Image" : "Dump Drive to Image";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(10);

            var table = new TableLayoutPanel
            {
                ColumnCount = 3,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 520));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            sourceBox.Dock = DockStyle.Fill;
            sourceBox.DropDownStyle = convert ? ComboBoxStyle.Simple : ComboBoxStyle.DropDown;
            if (convert)
                sourceBox.Height = sourceBox.PreferredHeight;
            sourceBrowse.Text = convert ? "Browse..." : "Image file...";
            sourceBrowse.AutoSize = true;
            sourceBrowse.Click += (s, e) =>
            {
                using (var ofd = new OpenFileDialog { Filter = "Drive images (*.img;*.imgc;*.bin)|*.img;*.imgc;*.bin|All files (*.*)|*.*" })
                {
                    if (ofd.ShowDialog(this) == DialogResult.OK)
                        sourceBox.Text = ofd.FileName;
                }
            };

            targetBox.Dock = DockStyle.Fill;
            targetBrowse.Text = "Browse...";
            targetBrowse.AutoSize = true;
            targetBrowse.Click += (s, e) =>
            {
                using (var sfd = new SaveFileDialog
                {
                    Filter = "Compressed image (*.imgc)|*.imgc|Raw image (*.img)|*.img",
                    OverwritePrompt = false,
                })
                {
                    if (sfd.ShowDialog(this) == DialogResult.OK)
                        targetBox.Text = sfd.FileName;
                }
            };

            md5Box.Text = "MD5";
            sha1Box.Text = "SHA-1";
            sha256Box.Text = "SHA-256";
            foreach (var box in new[] { md5Box, sha1Box, sha256Box })
                box.AutoSize = true;
            sha1Box.Checked = true;

            var hashPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            hashPanel.Controls.AddRange(new Control[] { md5Box, sha1Box, sha256Box });

            resumeBox.Text = "Resume an interrupted dump to this file";
            resumeBox.AutoSize = true;
            openBox.Text = "Open the image when finished";
            openBox.AutoSize = true;
            openBox.Checked = true;

            progressBar.Dock = DockStyle.Fill;
            progressBar.Maximum = 10000;
            statusLabel.AutoSize = true;
            statusLabel.Text = convert
                ? "Choose the image to convert and where to save it."
                : "Choose the drive to copy and where to save the image. Unreadable sectors never stop a dump.";

            startButton.Text = "Start";
            startButton.AutoSize = true;
            startButton.Click += (s, e) => StartOrCancel();
            closeButton.Text = "Close";
            closeButton.AutoSize = true;
            closeButton.Click += (s, e) => Close();
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0) };
            buttons.Controls.AddRange(new Control[] { closeButton, startButton });

            int row = 0;
            table.Controls.Add(MakeLabel("Source:"), 0, row);
            table.Controls.Add(sourceBox, 1, row);
            table.Controls.Add(sourceBrowse, 2, row++);
            table.Controls.Add(MakeLabel("Save as:"), 0, row);
            table.Controls.Add(targetBox, 1, row);
            table.Controls.Add(targetBrowse, 2, row++);
            table.Controls.Add(MakeLabel("Hashes:"), 0, row);
            table.Controls.Add(hashPanel, 1, row++);
            table.Controls.Add(resumeBox, 1, row++);
            table.Controls.Add(openBox, 1, row++);
            table.Controls.Add(progressBar, 0, row);
            table.SetColumnSpan(progressBar, 3);
            row++;
            table.Controls.Add(statusLabel, 0, row);
            table.SetColumnSpan(statusLabel, 3);
            row++;
            table.Controls.Add(buttons, 0, row);
            table.SetColumnSpan(buttons, 3);

            Controls.Add(table);

            if (!convert)
                LoadDrives();
        }

        /// <summary>
        /// The finished image, or null when nothing was completed.
        /// </summary>
        public string ResultPath { get; private set; }

        public bool OpenWhenFinished => openBox.Checked;

        private static Label MakeLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
        }

        private void LoadDrives()
        {
            try
            {
                disks = PhysicalDiskSource.ListDisks();
            }
            catch (Exception e)
            {
                statusLabel.Text = "Cannot list drives: " + e.Message;
            }

            foreach (var disk in disks)
                sourceBox.Items.Add(disk.ToString());
        }

        private string SourcePath
        {
            get
            {
                // A drive picked from the list, or a typed / browsed path.
                int index = sourceBox.SelectedIndex;
                if (index >= 0 && index < disks.Count && sourceBox.Text == disks[index].ToString())
                    return disks[index].Path;
                return sourceBox.Text.Trim();
            }
        }

        private void StartOrCancel()
        {
            if (running)
            {
                startButton.Enabled = false;
                statusLabel.Text = "Stopping...";
                cancellation?.Cancel();
                return;
            }

            string sourcePath = SourcePath;
            string target = targetBox.Text.Trim();
            if (sourcePath.Length == 0 || target.Length == 0)
            {
                MessageBox.Show(this, "Choose a source and a file to save to.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (File.Exists(target) && !resumeBox.Checked)
            {
                var answer = MessageBox.Show(this, $"{Path.GetFileName(target)} already exists. Replace it?\n\n" +
                    "(Tick \"Resume\" instead to continue an interrupted dump.)", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes)
                    return;
            }

            var options = new ImagingOptions { Resume = resumeBox.Checked };
            if (md5Box.Checked) options.Hashes.Add("md5");
            if (sha1Box.Checked) options.Hashes.Add("sha1");
            if (sha256Box.Checked) options.Hashes.Add("sha256");

            IDiskSource source;
            try
            {
                source = ImageFile.OpenSource(sourcePath);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            SetRunning(true);
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            var progress = new Progress<ImagingProgress>(ShowProgress);

            Task.Run(() =>
            {
                using (source)
                    return new DiskImager(source, target, options).Run(progress, token);
            }).ContinueWith(task =>
            {
                if (IsDisposed)
                    return;
                BeginInvoke(new Action(() => Finished(task, target)));
            });
        }

        private void ShowProgress(ImagingProgress p)
        {
            if (!running || IsDisposed)
                return;

            progressBar.Value = (int)Math.Max(0, Math.Min(10000, 10000.0 * p.BytesDone / Math.Max(1, p.BytesTotal)));
            var eta = p.Remaining.HasValue ? p.Remaining.Value.ToString(@"hh\:mm\:ss") : "--:--:--";
            statusLabel.Text = $"{p.Phase}: {p.BytesDone / 1048576.0:N0} of {p.BytesTotal / 1048576.0:N0} MB, " +
                               $"{p.BytesPerSecond / 1048576.0:F1} MB/s, {eta} left" +
                               (p.BadBytes > 0 ? $", {p.BadBytes:N0} bytes unreadable" : "");
        }

        private void Finished(Task<ImagingResult> task, string target)
        {
            SetRunning(false);

            if (task.IsFaulted)
            {
                var error = task.Exception.GetBaseException();
                statusLabel.Text = "Failed: " + error.Message;
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var result = task.Result;
            if (!result.Completed)
            {
                statusLabel.Text = $"Stopped at {result.BytesDone / 1048576.0:N0} of {result.BytesTotal / 1048576.0:N0} MB. Tick \"Resume\" to continue later.";
                resumeBox.Checked = true;
                return;
            }

            progressBar.Value = progressBar.Maximum;
            var lines = new List<string>
            {
                $"Finished in {result.Elapsed:hh\\:mm\\:ss} " +
                $"({result.BytesTotal / 1048576.0 / Math.Max(0.001, result.Elapsed.TotalSeconds):F1} MB/s average).",
                $"Image file: {result.TargetFileSize / 1048576.0:N0} MB for a {result.BytesTotal / 1048576.0:N0} MB drive.",
            };
            lines.Add(result.BadRanges.Count == 0
                ? "Every sector was read."
                : $"{result.BadBytes:N0} bytes in {result.BadRanges.Count} areas could not be read and are zero filled (listed in {Path.GetFileName(target)}.dump.json).");
            lines.AddRange(result.Hashes.Select(h => $"{h.Key}: {h.Value}"));

            statusLabel.Text = lines[0];
            Console.WriteLine($"Image {target}:");
            foreach (var line in lines)
                Console.WriteLine("  " + line);

            ResultPath = target;
            MessageBox.Show(this, string.Join(Environment.NewLine, lines), Text, MessageBoxButtons.OK,
                result.BadRanges.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            DialogResult = DialogResult.OK;
            Close();
        }

        private void SetRunning(bool value)
        {
            running = value;
            startButton.Text = value ? "Stop" : "Start";
            startButton.Enabled = true;
            closeButton.Enabled = !value;
            sourceBox.Enabled = sourceBrowse.Enabled = targetBox.Enabled = targetBrowse.Enabled = !value;
            md5Box.Enabled = sha1Box.Enabled = sha256Box.Enabled = resumeBox.Enabled = !value;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running)
            {
                // Stop first so the dump stays resumable.
                e.Cancel = true;
                cancellation?.Cancel();
                statusLabel.Text = "Stopping...";
            }
            base.OnFormClosing(e);
        }
    }
}
