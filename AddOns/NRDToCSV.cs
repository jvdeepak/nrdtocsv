#region Using declarations
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Gui.Tools;
using Microsoft.Win32;
#endregion

namespace NinjaTrader.Gui.NinjaScript
{
    public class NRDToCSV : AddOnBase
    {
        private NTMenuItem menuItem;
        private NTMenuItem existingMenuItemInControlCenter;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "NRDToCSV";
                Description = "*.nrd to *.csv market replay files convertion";
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            ControlCenter cc = window as ControlCenter;
            if (cc == null) return;

            existingMenuItemInControlCenter = cc.FindFirst("ControlCenterMenuItemTools") as NTMenuItem;
            if (existingMenuItemInControlCenter == null) return;

            menuItem = new NTMenuItem { Header = "NRD to CSV", Style = Application.Current.TryFindResource("MainMenuItem") as Style };
            existingMenuItemInControlCenter.Items.Add(menuItem);
            menuItem.Click += OnMenuItemClick;
        }

        protected override void OnWindowDestroyed(Window window)
        {
            if (menuItem != null && window is ControlCenter)
            {
                if (existingMenuItemInControlCenter != null && existingMenuItemInControlCenter.Items.Contains(menuItem))
                    existingMenuItemInControlCenter.Items.Remove(menuItem);
                menuItem.Click -= OnMenuItemClick;
                menuItem = null;
            }
        }

        private void OnMenuItemClick(object sender, RoutedEventArgs e)
        {
            Core.Globals.RandomDispatcher.BeginInvoke(new Action(() => new NRDToCSVWindow().Show()));
        }
    }

    public class NRDToCSVWindow : NTWindow, IWorkspacePersistence
    {
        private static readonly int PARALLEL_THREADS_COUNT = 4;

        private TextBox tbCsvRootDir;
        private ListBox lbSelectedPaths;
        private Button bAddFolder;
        private Button bAddFiles;
        private Button bRemove;
        private Button bClear;
        private Button bConvert;
        private TextBox tbOutput;
        private Label lProgress;
        private ProgressBar pbProgress;
        private DateTime startTimestamp;
        private long completeFilesLength;
        private long totalFilesLength;
        private int completedFiles;
        private bool running = false;
        private bool scanning = false;
        private CancellationTokenSource cts;
        private readonly object progressLock = new object();

        public NRDToCSVWindow()
        {
            Caption = "NRD to CSV";
            Width = 512;
            Height = 512;
            Content = BuildContent();
            Loaded += (o, e) =>
            {
                if (WorkspaceOptions == null)
                    WorkspaceOptions = new WorkspaceOptions("NRDToCSV-" + Guid.NewGuid().ToString("N"), this);
            };
            Closing += (o, e) =>
            {
                if (bConvert != null)
                    bConvert.Click -= OnConvertButtonClick;
            };
        }

        protected override void OnClosed(EventArgs e)
        {
            cts?.Cancel();
            base.OnClosed(e);
        }


        private DependencyObject BuildContent()
        {
            double margin = (double)FindResource("MarginBase");
            tbCsvRootDir = new TextBox()
            {
                Margin = new Thickness(margin, 0, margin, margin),
                Text = Path.Combine(Globals.UserDataDir, "db", "replay.csv"),
            };
            Label lCsvRootDir = new Label()
            {
                Foreground = FindResource("FontLabelBrush") as Brush,
                Margin = new Thickness(margin, 0, margin, 0),
                Content = "Root directory of converted CSV files:",
            };

            Label lSelectedPaths = new Label()
            {
                Foreground = FindResource("FontLabelBrush") as Brush,
                Margin = new Thickness(margin, margin, margin, 0),
                Content = "Files/folders to convert (leave empty to convert all):",
            };

            lbSelectedPaths = new ListBox()
            {
                Margin = new Thickness(margin, 0, margin, 0),
                Height = 80,
                SelectionMode = SelectionMode.Extended,
                AllowDrop = true,
            };
            lbSelectedPaths.DragOver += OnDragOver;
            lbSelectedPaths.Drop += OnDrop;

            StackPanel buttonPanel = new StackPanel()
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(margin, margin / 2, margin, margin),
            };

            bAddFolder = new Button() { Content = "Add Folder...", Margin = new Thickness(0, 0, margin, 0), Padding = new Thickness(8, 2, 8, 2) };
            bAddFolder.Click += OnAddFolderClick;
            bAddFiles = new Button() { Content = "Add Files...", Margin = new Thickness(0, 0, margin, 0), Padding = new Thickness(8, 2, 8, 2) };
            bAddFiles.Click += OnAddFilesClick;
            bRemove = new Button() { Content = "Remove", Margin = new Thickness(0, 0, margin, 0), Padding = new Thickness(8, 2, 8, 2) };
            bRemove.Click += OnRemoveClick;
            bClear = new Button() { Content = "Clear", Padding = new Thickness(8, 2, 8, 2) };
            bClear.Click += OnClearClick;

            buttonPanel.Children.Add(bAddFolder);
            buttonPanel.Children.Add(bAddFiles);
            buttonPanel.Children.Add(bRemove);
            buttonPanel.Children.Add(bClear);

            bConvert = new Button() { Margin = new Thickness(margin), IsDefault = true, Content = "_Convert" };
            bConvert.Click += OnConvertButtonClick;
            tbOutput = new TextBox()
            {
                IsReadOnly = true,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(margin),
            };
            pbProgress = new ProgressBar()
            {
                Height = 0,
            };
            lProgress = new Label()
            {
                Foreground = FindResource("FontLabelBrush") as Brush,
                Height = 0,
            };

            Grid grid = new Grid() { Background = new SolidColorBrush(Colors.Transparent) };
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            Grid.SetRow(lCsvRootDir, 0);
            Grid.SetRow(tbCsvRootDir, 1);
            Grid.SetRow(lSelectedPaths, 2);
            Grid.SetRow(lbSelectedPaths, 3);
            Grid.SetRow(buttonPanel, 4);
            Grid.SetRow(bConvert, 5);
            Grid.SetRow(tbOutput, 6);
            Grid.SetRow(lProgress, 7);
            Grid.SetRow(pbProgress, 8);
            grid.Children.Add(lCsvRootDir);
            grid.Children.Add(tbCsvRootDir);
            grid.Children.Add(lSelectedPaths);
            grid.Children.Add(lbSelectedPaths);
            grid.Children.Add(buttonPanel);
            grid.Children.Add(bConvert);
            grid.Children.Add(tbOutput);
            grid.Children.Add(lProgress);
            grid.Children.Add(pbProgress);
            return grid;
        }

        private void OnAddFolderClick(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select folder containing .nrd files";
                dialog.SelectedPath = Path.Combine(Globals.UserDataDir, "db", "replay");
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    if (!lbSelectedPaths.Items.Contains(dialog.SelectedPath))
                        lbSelectedPaths.Items.Add(dialog.SelectedPath);
                    bConvert.Content = "_Convert";
                }
            }
        }

        private void OnAddFilesClick(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog()
            {
                Filter = "NRD files (*.nrd)|*.nrd",
                Multiselect = true,
                InitialDirectory = Path.Combine(Globals.UserDataDir, "db", "replay"),
                Title = "Select .nrd files to convert"
            };

            if (dialog.ShowDialog() == true)
            {
                foreach (string file in dialog.FileNames)
                {
                    if (!lbSelectedPaths.Items.Contains(file))
                        lbSelectedPaths.Items.Add(file);
                }
                if (dialog.FileNames.Length > 0)
                    bConvert.Content = "_Convert";
            }
        }

        private void OnRemoveClick(object sender, RoutedEventArgs e)
        {
            var selectedItems = lbSelectedPaths.SelectedItems.Cast<object>().ToList();
            foreach (var item in selectedItems)
                lbSelectedPaths.Items.Remove(item);
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            lbSelectedPaths.Items.Clear();
            tbOutput.Clear();
            bConvert.Content = "_Convert";
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (string path in paths)
                {
                    // Accept folders or .nrd files
                    if (Directory.Exists(path) ||
                        (File.Exists(path) && path.EndsWith(".nrd", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!lbSelectedPaths.Items.Contains(path))
                            lbSelectedPaths.Items.Add(path);
                    }
                }
                if (paths.Length > 0)
                    bConvert.Content = "_Convert";
            }
        }

        private void OnConvertButtonClick(object sender, RoutedEventArgs e)
        {
            if (tbOutput == null) return;

            // If button shows "Close", close the window
            if (bConvert.Content.ToString() == "_Close")
            {
                Close();
                return;
            }

            if (running || scanning)
            {
                // Cancel immediately
                cts?.Cancel();
                logout("Canceling...");
                bConvert.IsEnabled = false;
                bConvert.Content = "Canceling...";
                return;
            }

            tbOutput.Clear();
            logout("Starting conversion...");

            string nrdDir = Path.Combine(Globals.UserDataDir, "db", "replay");
            string csvDir = tbCsvRootDir.Text;

            // Get selected paths from ListBox
            List<string> selectedPaths = lbSelectedPaths.Items.Cast<string>().ToList();

            if (!Directory.Exists(csvDir))
            {
                try
                {
                    Directory.CreateDirectory(csvDir);
                }
                catch (Exception error)
                {
                    logout(string.Format("ERROR: Unable to create the CSV root directory \"{0}\": {1}", csvDir, error.ToString()));
                    return;
                }
            }

            // Create new cancellation token
            cts = new CancellationTokenSource();
            var token = cts.Token;

            // Show scanning progress immediately
            startScanning();

            // Run scanning on background thread
            Task.Run(() =>
            {
                try
                {
                    completeFilesLength = 0;
                    totalFilesLength = 0;
                    List<DumpEntry> entries = new List<DumpEntry>();

                    if (selectedPaths.Count == 0)
                    {
                        // No selection - scan all directories in replay folder
                        if (!Directory.Exists(nrdDir))
                        {
                            logout(string.Format("ERROR: The NRD root directory \"{0}\" not found", nrdDir));
                            Dispatcher.Invoke(() => completeScanning());
                            return;
                        }

                        string[] nrdSubDirs = Directory.GetDirectories(nrdDir);
                        if (nrdSubDirs.Length == 0)
                        {
                            logout(string.Format("WARNING: The NRD root directory \"{0}\" is empty", nrdDir));
                            Dispatcher.Invoke(() => completeScanning());
                            return;
                        }

                        for (int i = 0; i < nrdSubDirs.Length; i++)
                        {
                            if (token.IsCancellationRequested) break;
                            ProceedDirectory(entries, nrdDir, nrdSubDirs[i], csvDir);
                            updateScanProgress(i + 1, nrdSubDirs.Length);
                        }
                    }
                    else
                    {
                        // Process selected files and folders
                        for (int i = 0; i < selectedPaths.Count; i++)
                        {
                            if (token.IsCancellationRequested) break;
                            string path = selectedPaths[i];

                            if (File.Exists(path) && path.EndsWith(".nrd", StringComparison.OrdinalIgnoreCase))
                            {
                                // Single file
                                ProceedFile(entries, path, csvDir);
                            }
                            else if (Directory.Exists(path))
                            {
                                // Folder - check if it contains .nrd files directly or has subdirectories
                                string[] nrdFiles = Directory.GetFiles(path, "*.nrd");
                                if (nrdFiles.Length > 0)
                                {
                                    // Folder contains .nrd files directly
                                    ProceedDirectory(entries, Path.GetDirectoryName(path), path, csvDir);
                                }
                                else
                                {
                                    // Check subdirectories
                                    string[] subDirs = Directory.GetDirectories(path);
                                    foreach (string subDir in subDirs)
                                    {
                                        if (token.IsCancellationRequested) break;
                                        ProceedDirectory(entries, path, subDir, csvDir);
                                    }
                                }
                            }
                            updateScanProgress(i + 1, selectedPaths.Count);
                        }
                    }

                    Dispatcher.Invoke(() => completeScanning());

                    if (token.IsCancellationRequested)
                    {
                        logout("Canceled");
                        Dispatcher.Invoke(() => complete());
                        return;
                    }

                    if (entries.Count == 0)
                    {
                        logout("No *.nrd files found to convert");
                    }
                    else
                    {
                        logout(string.Format("Converting {0} files ({1})...", entries.Count, ToBytes(totalFilesLength)));
                        Dispatcher.Invoke(() => run(entries.Count));
                        RunConversionAsync(entries, token);
                    }
                }
                catch (Exception ex)
                {
                    logout(string.Format("ERROR: {0}", ex.Message));
                    Dispatcher.Invoke(() => complete());
                }
            }, token);
        }

        private void ProceedDirectory(List<DumpEntry> entries, string nrdRoot, string nrdDir, string csvDir)
        {
            string[] fileEntries = Directory.GetFiles(nrdDir, "*.nrd");
            if (fileEntries.Length == 0)
            {
                logout(string.Format("WARNING: No *.nrd files found in \"{0}\" directory. Skipped", nrdDir));
                return;
            }

            foreach (string fileName in fileEntries)
            {
                ProceedFile(entries, fileName, csvDir);
            }
        }

        private void ProceedFile(List<DumpEntry> entries, string fileName, string csvDir)
        {
            string fullName = Path.GetFileName(Path.GetDirectoryName(fileName));
            string displayName = Path.GetFileName(fileName);

            Collection<Instrument> instruments = InstrumentList.GetInstruments(fullName);
            if (instruments.Count == 0)
            {
                logout(string.Format("Unable to find an instrument named \"{0}\". Skipped", fullName));
                return;
            }
            else if (instruments.Count > 1)
            {
                logout(string.Format("More than one instrument identified for name \"{0}\". Skipped", fullName));
                return;
            }
            Cbi.Instrument instrument = instruments[0];
            string name = Path.GetFileNameWithoutExtension(fileName);
            string csvFileName = string.Format("{0}.csv", Path.Combine(csvDir, instrument.FullName, name));
            if (File.Exists(csvFileName))
            {
                logout(string.Format("Already converted: {0}. Skipped", displayName));
                return;
            }
            long nrdFileLength = new FileInfo(fileName).Length;
            totalFilesLength += nrdFileLength;
            entries.Add(new DumpEntry()
            {
                NrdLength = nrdFileLength,
                Instrument = instrument,
                Date = new DateTime(
                    Convert.ToInt16(name.Substring(0, 4)),
                    Convert.ToInt16(name.Substring(4, 2)),
                    Convert.ToInt16(name.Substring(6, 2))),
                CsvFileName = csvFileName,
                FromName = Path.Combine(fullName, displayName),
                ToName = csvFileName.Substring(csvDir.Length + 1),
            });
        }

        private void RunConversionAsync(List<DumpEntry> entries, CancellationToken token)
        {
            int totalCount = entries.Count;
            completedFiles = 0;
            completeFilesLength = 0;

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = PARALLEL_THREADS_COUNT,
                CancellationToken = token
            };

            try
            {
                Parallel.ForEach(entries, options, (entry, state) =>
                {
                    if (token.IsCancellationRequested)
                    {
                        state.Stop();
                        return;
                    }

                    ConvertNrd(entry, token);

                    // Thread-safe progress update
                    lock (progressLock)
                    {
                        completedFiles++;
                        completeFilesLength += entry.NrdLength;
                        int currentCompleted = completedFiles;
                        long currentBytes = completeFilesLength;

                        Dispatcher.InvokeAsync(() =>
                        {
                            pbProgress.Value = currentCompleted;
                            string eta = "";
                            if (currentBytes > 0 && totalFilesLength > 0)
                            {
                                double ratio = (double)totalFilesLength / currentBytes - 1;
                                if (ratio > 0)
                                {
                                    TimeSpan elapsed = DateTime.Now - startTimestamp;
                                    TimeSpan remaining = TimeSpan.FromTicks((long)(elapsed.Ticks * ratio));
                                    eta = string.Format(" ETA: {0:D2}:{1:D2}:{2:D2}",
                                        (int)remaining.TotalHours, remaining.Minutes, remaining.Seconds);
                                }
                            }
                            lProgress.Content = string.Format("{0} of {1} files converted ({2} of {3}){4}",
                                currentCompleted, totalCount, ToBytes(currentBytes), ToBytes(totalFilesLength), eta);
                        });
                    }
                });

                if (token.IsCancellationRequested)
                {
                    logout("Conversion canceled");
                }
                else
                {
                    logout("Conversion complete");
                }
            }
            catch (OperationCanceledException)
            {
                logout("Conversion canceled");
            }
            catch (Exception ex)
            {
                logout(string.Format("ERROR: {0}", ex.Message));
            }
            finally
            {
                Dispatcher.Invoke(() => complete());
            }
        }

        private void ConvertNrd(DumpEntry entry, CancellationToken token)
        {
            if (token.IsCancellationRequested) return;

            logout(string.Format("Converting \"{0}\"...", entry.FromName));

            string csvFileDir = Path.GetDirectoryName(entry.CsvFileName);
            if (!Directory.Exists(csvFileDir))
            {
                try
                {
                    Directory.CreateDirectory(csvFileDir);
                }
                catch (Exception error)
                {
                    logout(string.Format("ERROR: Unable to create directory \"{0}\": {1}",
                        csvFileDir, error.Message));
                    return;
                }
            }

            if (token.IsCancellationRequested) return;

            try
            {
                MarketReplay.DumpMarketDepth(entry.Instrument, entry.Date, entry.Date, entry.CsvFileName);
                if (!token.IsCancellationRequested)
                    logout(string.Format("Converted \"{0}\"", entry.FromName));
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                    logout(string.Format("ERROR: Failed \"{0}\": {1}", entry.FromName, error.Message));
            }
        }

        public void Restore(XDocument document, XElement element)
        {
            foreach (XElement elRoot in element.Elements())
            {
                if (elRoot.Name.LocalName.Contains("NRDToCSV"))
                {
                    XElement elCsvRootDir = elRoot.Element("CsvRootDir");
                    if (elCsvRootDir != null)
                        tbCsvRootDir.Text = elCsvRootDir.Value;

                    XElement elSelectedPaths = elRoot.Element("SelectedPaths");
                    if (elSelectedPaths != null)
                    {
                        lbSelectedPaths.Items.Clear();
                        foreach (XElement pathEl in elSelectedPaths.Elements("Path"))
                        {
                            if (!string.IsNullOrEmpty(pathEl.Value))
                                lbSelectedPaths.Items.Add(pathEl.Value);
                        }
                    }
                }
            }
        }

        public void Save(XDocument document, XElement element)
        {
            element.Elements().Where(el => el.Name.LocalName.Equals("NRDToCSV")).Remove();
            XElement elRoot = new XElement("NRDToCSV");
            XElement elCsvRootDir = new XElement("CsvRootDir", tbCsvRootDir.Text);
            XElement elSelectedPaths = new XElement("SelectedPaths");
            foreach (string path in lbSelectedPaths.Items)
            {
                elSelectedPaths.Add(new XElement("Path", path));
            }
            elRoot.Add(elCsvRootDir);
            elRoot.Add(elSelectedPaths);
            element.Add(elRoot);
        }

        public WorkspaceOptions WorkspaceOptions { get; set; }

        private void logout(string text)
        {
            Dispatcher.InvokeAsync(() =>
            {
                tbOutput.AppendText(text + Environment.NewLine);
                tbOutput.ScrollToEnd();
            });
        }

        private void startScanning()
        {
            Dispatcher.InvokeAsync(() =>
            {
                scanning = true;
                bConvert.Content = "_Cancel";
                tbCsvRootDir.IsReadOnly = true;
                lbSelectedPaths.IsEnabled = false;
                bAddFolder.IsEnabled = false;
                bAddFiles.IsEnabled = false;
                bRemove.IsEnabled = false;
                bClear.IsEnabled = false;
                double margin = (double)FindResource("MarginBase");
                lProgress.Margin = new Thickness(margin, 0, margin, 0);
                lProgress.Height = 24;
                lProgress.Content = "Scanning directories...";
                pbProgress.Margin = new Thickness(margin);
                pbProgress.Height = 16;
                pbProgress.IsIndeterminate = true;
            });
        }

        private void updateScanProgress(int current, int total)
        {
            Dispatcher.InvokeAsync(() =>
            {
                lProgress.Content = string.Format("Scanning... ({0} of {1})", current, total);
            });
        }

        private void completeScanning()
        {
            Dispatcher.InvokeAsync(() =>
            {
                scanning = false;
                pbProgress.IsIndeterminate = false;
                if (!running)
                {
                    lProgress.Margin = new Thickness(0);
                    lProgress.Height = 0;
                    pbProgress.Margin = new Thickness(0);
                    pbProgress.Height = 0;
                    tbCsvRootDir.IsReadOnly = false;
                    lbSelectedPaths.IsEnabled = true;
                    bAddFolder.IsEnabled = true;
                    bAddFiles.IsEnabled = true;
                    bRemove.IsEnabled = true;
                    bClear.IsEnabled = true;
                    bConvert.IsEnabled = true;
                    bConvert.Content = "_Convert";
                }
            });
        }

        private void run(int filesCount)
        {
            Dispatcher.InvokeAsync(() =>
            {
                running = true;
                bConvert.IsEnabled = true;
                bConvert.Content = "_Cancel";
                tbCsvRootDir.IsReadOnly = true;
                lbSelectedPaths.IsEnabled = false;
                bAddFolder.IsEnabled = false;
                bAddFiles.IsEnabled = false;
                bRemove.IsEnabled = false;
                bClear.IsEnabled = false;
                double margin = (double)FindResource("MarginBase");
                lProgress.Margin = new Thickness(margin, 0, margin, 0);
                lProgress.Height = 24;
                pbProgress.Margin = new Thickness(margin);
                pbProgress.Height = 16;
                pbProgress.IsIndeterminate = false;
                pbProgress.Minimum = 0;
                pbProgress.Maximum = filesCount;
                pbProgress.Value = 0;
                startTimestamp = DateTime.Now;
            });
        }

        private void complete()
        {
            Dispatcher.InvokeAsync(() =>
            {
                running = false;
                lProgress.Margin = new Thickness(0);
                lProgress.Height = 0;
                pbProgress.Margin = new Thickness(0);
                pbProgress.Height = 0;
                tbCsvRootDir.IsReadOnly = false;
                lbSelectedPaths.IsEnabled = true;
                bAddFolder.IsEnabled = true;
                bAddFiles.IsEnabled = true;
                bRemove.IsEnabled = true;
                bClear.IsEnabled = true;
                bConvert.IsEnabled = true;
                bConvert.Content = "_Close";
            });
        }

        public static string ToBytes(long bytes)
        {
            if (bytes < 1024) return string.Format("{0} B", bytes);
            double exp = (int)(Math.Log(bytes) / Math.Log(1024));
            return string.Format("{0:F1} {1}iB", bytes / Math.Pow(1024, exp), "KMGTPE"[(int)exp - 1]);
        }
    }

    public class DumpEntry
    {
        public long NrdLength { get; set; }
        public Cbi.Instrument Instrument { get; set; }
        public DateTime Date { get; set; }
        public string CsvFileName { get; set; }
        public string FromName { get; set; }
        public string ToName { get; set; }
    }
}
