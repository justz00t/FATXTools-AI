using FATX.FileSystem;
using FATXTools.Controls;
using FATXTools.Dialogs;
using FATXTools.DiskTypes;
using FATXTools.Utilities;
using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace FATXTools.Forms
{
    public partial class MainWindow : Form
    {
        private DriveView driveView;

        private const string ApplicationTitle = "FATX-Recover";

        public MainWindow()
        {
            InitializeComponent();

            this.Text = ApplicationTitle;

            DisableDatabaseOptions();
            AddAssistantMenu();
            AddImagingMenu();

            Console.SetOut(new LogWriter(this.textBox1));
            Console.WriteLine("--------------------------------");
            Console.WriteLine("FATX-Tools v0.3");
            Console.WriteLine("--------------------------------");
        }

        /// <summary>
        /// Thread safe, buffered log writer. Analyzers log from background
        /// threads; previously Write(char)/Write(string) touched the TextBox
        /// from those threads directly and every line was marshalled to the
        /// UI thread on its own, which froze the window during scans.
        /// </summary>
        public class LogWriter : TextWriter
        {
            private const int MaxLength = 2 * 1024 * 1024;
            private readonly TextBox textBox;
            private readonly StringBuilder pending = new StringBuilder();
            private readonly Timer timer;

            public LogWriter(TextBox textBox)
            {
                this.textBox = textBox;
                timer = new Timer { Interval = 200 };
                timer.Tick += (s, e) => Flush();
                timer.Start();
            }

            public override void Write(char value)
            {
                lock (pending) pending.Append(value);
            }

            public override void Write(string value)
            {
                lock (pending) pending.Append(value);
            }

            public override void WriteLine(string value)
            {
                lock (pending) pending.Append(value).Append(NewLine);
            }

            public override void Flush()
            {
                string text;
                lock (pending)
                {
                    if (pending.Length == 0)
                        return;
                    text = pending.ToString();
                    pending.Clear();
                }

                if (textBox.IsDisposed)
                    return;

                if (textBox.InvokeRequired)
                {
                    textBox.BeginInvoke(new Action(() => Append(text)));
                }
                else
                {
                    Append(text);
                }
            }

            private void Append(string text)
            {
                // Keep the log bounded so the TextBox stays responsive.
                if (textBox.TextLength + text.Length > MaxLength)
                {
                    var keep = textBox.Text;
                    keep = keep.Substring(Math.Max(0, keep.Length - MaxLength / 2));
                    textBox.Text = keep;
                }
                textBox.AppendText(text);
            }

            public override Encoding Encoding
            {
                get { return Encoding.UTF8; }
            }
        }

        private ToolStripMenuItem assistantMenuItem;

        private void AddAssistantMenu()
        {
            var claudeMenu = new ToolStripMenuItem("Claude");
            assistantMenuItem = new ToolStripMenuItem("Recovery Assistant...");
            assistantMenuItem.Enabled = false;
            assistantMenuItem.Click += (s, e) =>
            {
                if (driveView?.Drive == null)
                    return;
                var dialog = new AssistantDialog(driveView.Drive, driveView.DriveName);
                dialog.Show(this);
            };
            claudeMenu.DropDownItems.Add(assistantMenuItem);
            menuStrip1.Items.Insert(Math.Max(0, menuStrip1.Items.IndexOf(helpToolStripMenuItem)), claudeMenu);
        }

        /// <summary>
        /// File > Dump Drive to Image / Convert Image.
        /// </summary>
        private void AddImagingMenu()
        {
            var dumpItem = new ToolStripMenuItem("Dump Drive to Image...");
            dumpItem.Click += (s, e) => ShowDumpDialog(false);
            var convertItem = new ToolStripMenuItem("Convert Image...");
            convertItem.Click += (s, e) => ShowDumpDialog(true);

            int index = fileToolStripMenuItem.DropDownItems.IndexOf(openDeviceToolStripMenuItem) + 1;
            fileToolStripMenuItem.DropDownItems.Insert(index, convertItem);
            fileToolStripMenuItem.DropDownItems.Insert(index, dumpItem);
        }

        private void ShowDumpDialog(bool convert)
        {
            using (var dialog = new DumpDialog(convert))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK && dialog.OpenWhenFinished && dialog.ResultPath != null)
                {
                    try
                    {
                        OpenDiskImage(dialog.ResultPath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Cannot open image", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void CreateNewDriveView(string path)
        {
            this.Text = $"{ApplicationTitle} - {Path.GetFileName(path)}";

            // Destroy the current drive view
            splitContainer1.Panel1.Controls.Remove(driveView);

            // Create a new view for this drive
            driveView = new DriveView();
            driveView.Dock = DockStyle.Fill;
            driveView.TabSelectionChanged += DriveView_TabSelectionChanged;
            driveView.TaskStarted += DriveView_TaskStarted;
            driveView.TaskCompleted += DriveView_TaskCompleted;

            // Add the view to the panel
            splitContainer1.Panel1.Controls.Add(driveView);
        }

        private void DriveView_TaskCompleted(object sender, EventArgs e)
        {
            EnableOpenOptions();
            EnableDatabaseOptions();
        }

        private void DriveView_TaskStarted(object sender, EventArgs e)
        {
            DisableOpenOptions();
            DisableDatabaseOptions();
        }

        private void DriveView_TabSelectionChanged(object sender, PartitionSelectedEventArgs e)
        {
            if (e == null)
            {
                statusStrip1.Items.Clear();
            }
            else
            {
                var volume = e.volume;

                statusStrip1.Items.Clear();
                if (volume.Mounted)
                {
                    var usedSpace = volume.GetUsedSpace();
                    var freeSpace = volume.GetFreeSpace();
                    var totalSpace = volume.GetTotalSpace();

                    statusStrip1.Items.Add($"Volume Offset: 0x{volume.Offset:X}");
                    statusStrip1.Items.Add($"Volume Length: 0x{volume.Length:X}");
                    statusStrip1.Items.Add($"Used Space: {Utility.FormatBytes(usedSpace)}");
                    statusStrip1.Items.Add($"Free Space: {Utility.FormatBytes(freeSpace)}");
                    statusStrip1.Items.Add($"Total Space: {Utility.FormatBytes(totalSpace)}");
                }
            }
        }

        private void EnableDatabaseOptions()
        {
            if (assistantMenuItem != null) assistantMenuItem.Enabled = true;
            loadToolStripMenuItem.Enabled = true;
            saveToolStripMenuItem.Enabled = true;

            addPartitionToolStripMenuItem.Enabled = true;
            //searchForPartitionsToolStripMenuItem.Enabled = true;
            //managePartitionsToolStripMenuItem.Enabled = true;
        }

        private void DisableDatabaseOptions()
        {
            if (assistantMenuItem != null) assistantMenuItem.Enabled = false;
            loadToolStripMenuItem.Enabled = false;
            saveToolStripMenuItem.Enabled = false;

            addPartitionToolStripMenuItem.Enabled = false;
            //searchForPartitionsToolStripMenuItem.Enabled = false;
            //managePartitionsToolStripMenuItem.Enabled = false;
        }

        private void EnableOpenOptions()
        {
            openImageToolStripMenuItem.Enabled = true;
            openDeviceToolStripMenuItem.Enabled = true;
        }

        private void DisableOpenOptions()
        {
            openImageToolStripMenuItem.Enabled = false;
            openDeviceToolStripMenuItem.Enabled = false;
        }

        private void OpenDiskImage(string path)
        {
            CreateNewDriveView(path);

            string fileName = Path.GetFileName(path);

            RawImage rawImage = new RawImage(path);
            driveView.AddDrive(fileName, rawImage);

            EnableDatabaseOptions();
        }

        private void OpenDisk(string device)
        {
            CreateNewDriveView(device);

            SafeFileHandle handle = WinApi.CreateFile(device,
                       FileAccess.Read,
                       FileShare.None,
                       IntPtr.Zero,
                       FileMode.Open,
                       0,
                       IntPtr.Zero);
            long length = WinApi.GetDiskCapactity(handle);
            long sectorLength = WinApi.GetSectorSize(handle);
            PhysicalDisk drive = new PhysicalDisk(handle, length, sectorLength);
            driveView.AddDrive(device, drive);

            EnableDatabaseOptions();
        }

        private void openImageToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog()
            {
                Filter = "Drive images (*.img;*.imgc;*.bin)|*.img;*.imgc;*.bin|All files (*.*)|*.*"
            };
            if (ofd.ShowDialog() == DialogResult.OK)
            {
                OpenDiskImage(ofd.FileName);
            }
        }

        private void openDeviceToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);

            if (!isAdmin)
            {
                MessageBox.Show("You must re-run this program with Administrator privileges\n" +
                                "in order to read from physical drives.",
                                "Cannot perform operation", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            DeviceSelectionDialog ds = new DeviceSelectionDialog();
            if (ds.ShowDialog() == DialogResult.OK)
            {
                OpenDisk(ds.SelectedDevice);
            }
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            MessageBox.Show(
                "Developed by aerosoul94\n" +
                "Source code: https://github.com/aerosoul94/FATXTools\n" +
                "Please report any bugs\n",
                "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void MainWindow_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Only 1 file is allowed.
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 1)
                {
                    e.Effect = DragDropEffects.None;
                }
                else
                {
                    e.Effect = DragDropEffects.Link;
                }
            }
            else
            {
                // Not a file.
                e.Effect = DragDropEffects.None;
            }
        }

        private void MainWindow_DragDrop(object sender, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length > 1)
            {
                MessageBox.Show("You may only drop one file!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                string path = files[0];
                OpenDiskImage(path);
            }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void managePartitionsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (driveView != null)
            {
                //PartitionManagerForm partitionManagerForm = new PartitionManagerForm(driveView.GetDrive(), driveView.GetDrive().GetPartitions());
                //partitionManagerForm.ShowDialog();
            }
        }

        private void addPartitionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            NewPartitionDialog partitionDialog = new NewPartitionDialog();
            var dialogResult = partitionDialog.ShowDialog();
            if (dialogResult == DialogResult.OK)
            {
                driveView.AddPartition(new Volume(driveView.GetDrive(),
                    partitionDialog.PartitionName,
                    partitionDialog.PartitionOffset,
                    partitionDialog.PartitionLength));
            }
        }

        private void settingsToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            SettingsDialog settings = new SettingsDialog();
            if (settings.ShowDialog() == DialogResult.OK)
            {
                Properties.Settings.Default.FileCarverInterval = settings.FileCarverInterval;
                Properties.Settings.Default.LogFile = settings.LogFile;

                Properties.Settings.Default.Save();
            }
        }

        private void saveToJSONToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog()
            {
                Filter = "JSON File (*.json)|*.json"
            };

            if (saveFileDialog.ShowDialog() == DialogResult.OK)
            {
                driveView.Save(saveFileDialog.FileName);

                Console.WriteLine($"Finished saving database: {saveFileDialog.FileName}");
            }
        }

        private void loadFromJSONToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog()
            {
                Filter = "JSON File (*.json)|*.json"
            };

            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                var dialogResult = MessageBox.Show($"Loading a database will overwrite current analysis progress.\n"
                    + $"Are you sure you want to load \'{Path.GetFileName(openFileDialog.FileName)}\'?",
                    "Load File", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (dialogResult == DialogResult.Yes)
                {
                    driveView.LoadFromJson(openFileDialog.FileName);

                    Console.WriteLine($"Finished loading database: {openFileDialog.FileName}");
                }
            }
        }

        private void MainWindow_FormClosing(object sender, FormClosingEventArgs e)
        {
            // TODO: For any partition, if any analysis was made, then we should ask.
            // TODO: Add setting for auto-saving (maybe at run-time or while closing)
            if (driveView != null)
            {
                var dialogResult = MessageBox.Show("Would you like to save progress before closing?", "Save Progress", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (dialogResult == DialogResult.Yes)
                {
                    SaveFileDialog saveFileDialog = new SaveFileDialog()
                    {
                        Filter = "JSON File (*.json)|*.json"
                    };

                    if (saveFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        driveView.Save(saveFileDialog.FileName);

                        Console.WriteLine($"Finished saving database: {saveFileDialog.FileName}");
                    }
                    else
                    {
                        // User may have accidentally cancelled? Maybe try again?
                    }
                }
                else if (dialogResult == DialogResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
            }

            // TODO: handle closing dialogs
            e.Cancel = false;
        }
    }
}
