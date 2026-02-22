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
        private CheckBox cbForceExport;
        private Button bAnalyze;
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
        private ExportManifest manifest;

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

                // Load manifest and restore saved CSV root directory
                try
                {
                    var savedManifest = new ExportManifest();
                    if (!string.IsNullOrEmpty(savedManifest.CsvRootDir) && Directory.Exists(savedManifest.CsvRootDir))
                    {
                        tbCsvRootDir.Text = savedManifest.CsvRootDir;
                    }
                    else
                    {
                        // Save current destination to manifest immediately
                        savedManifest.CsvRootDir = tbCsvRootDir.Text;
                        savedManifest.Save();
                    }
                }
                catch { /* ignore manifest load errors on startup */ }
            };
            Closing += (o, e) =>
            {
                if (bConvert != null)
                    bConvert.Click -= OnConvertButtonClick;
                if (bAnalyze != null)
                    bAnalyze.Click -= OnAnalyzeButtonClick;

                // Save destination path to manifest on close
                try
                {
                    var manifestToSave = new ExportManifest();
                    manifestToSave.CsvRootDir = tbCsvRootDir.Text;
                    manifestToSave.Save();
                }
                catch { /* ignore save errors on close */ }
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

            cbForceExport = new CheckBox()
            {
                Content = "Force re-export (ignore manifest)",
                Margin = new Thickness(margin, 0, margin, margin),
                VerticalAlignment = VerticalAlignment.Center,
            };

            StackPanel actionPanel = new StackPanel()
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(margin),
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            bAnalyze = new Button() { Content = "_Analyze", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, margin, 0) };
            bAnalyze.Click += OnAnalyzeButtonClick;
            bConvert = new Button() { IsDefault = true, Content = "_Convert", Padding = new Thickness(16, 4, 16, 4) };
            bConvert.Click += OnConvertButtonClick;
            actionPanel.Children.Add(bAnalyze);
            actionPanel.Children.Add(bConvert);
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
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            Grid.SetRow(lCsvRootDir, 0);
            Grid.SetRow(tbCsvRootDir, 1);
            Grid.SetRow(lSelectedPaths, 2);
            Grid.SetRow(lbSelectedPaths, 3);
            Grid.SetRow(buttonPanel, 4);
            Grid.SetRow(cbForceExport, 5);
            Grid.SetRow(actionPanel, 6);
            Grid.SetRow(tbOutput, 7);
            Grid.SetRow(lProgress, 8);
            Grid.SetRow(pbProgress, 9);
            grid.Children.Add(lCsvRootDir);
            grid.Children.Add(tbCsvRootDir);
            grid.Children.Add(lSelectedPaths);
            grid.Children.Add(lbSelectedPaths);
            grid.Children.Add(buttonPanel);
            grid.Children.Add(cbForceExport);
            grid.Children.Add(actionPanel);
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

        private void OnAnalyzeButtonClick(object sender, RoutedEventArgs e)
        {
            if (running || scanning)
            {
                logout("Cannot analyze while conversion is in progress");
                return;
            }

            string csvDir = tbCsvRootDir.Text;
            string nrdDir = Path.Combine(Globals.UserDataDir, "db", "replay");

            if (!Directory.Exists(csvDir))
            {
                logout(string.Format("CSV directory does not exist: {0}", csvDir));
                return;
            }

            // Get selected paths from ListBox
            List<string> selectedPaths = lbSelectedPaths.Items.Cast<string>().ToList();

            tbOutput.Clear();
            logout("Analyzing destination folder...");

            // Disable UI during analysis
            bAnalyze.IsEnabled = false;
            bConvert.IsEnabled = false;

            // Create new cancellation token
            cts = new CancellationTokenSource();
            var token = cts.Token;

            Task.Run(() =>
            {
                try
                {
                    var analysisStartTime = DateTime.Now;

                    // Load manifest
                    var analyzeManifest = new ExportManifest(csvDir);
                    logout(string.Format("Loaded manifest with {0} entries from: {1}",
                        analyzeManifest.EntryCount, ExportManifest.GetManifestPath()));

                    int complete = 0, partial = 0, outdated = 0, missing = 0, orphaned = 0;
                    int updated = 0;
                    int verified = 0, cached = 0;

                    // Build a map of (InstrumentFullName, Date) -> NRD file path
                    // This is needed because NRD folders use different naming (e.g., "MNQ 06-24")
                    // than the instrument FullName used for CSV folders (e.g., "MNQ JUN24")
                    var nrdFileMap = new Dictionary<string, string>();
                    logout("Building NRD file index...");

                    if (selectedPaths.Count == 0)
                    {
                        // No selection - scan all directories in replay folder
                        if (Directory.Exists(nrdDir))
                        {
                            string[] nrdInstrumentDirs = Directory.GetDirectories(nrdDir);
                            foreach (string nrdInstrumentDir in nrdInstrumentDirs)
                            {
                                if (token.IsCancellationRequested) break;
                                IndexNrdDirectory(nrdFileMap, nrdInstrumentDir, token);
                            }
                        }
                    }
                    else
                    {
                        // Process only selected files and folders
                        foreach (string path in selectedPaths)
                        {
                            if (token.IsCancellationRequested) break;

                            if (File.Exists(path) && path.EndsWith(".nrd", StringComparison.OrdinalIgnoreCase))
                            {
                                // Single file
                                IndexNrdFile(nrdFileMap, path);
                            }
                            else if (Directory.Exists(path))
                            {
                                // Folder - check if it contains .nrd files directly or has subdirectories
                                string[] nrdFiles = Directory.GetFiles(path, "*.nrd");
                                if (nrdFiles.Length > 0)
                                {
                                    // Folder contains .nrd files directly (instrument folder)
                                    IndexNrdDirectory(nrdFileMap, path, token);
                                }
                                else
                                {
                                    // Check subdirectories (parent folder containing instrument folders)
                                    string[] subDirs = Directory.GetDirectories(path);
                                    foreach (string subDir in subDirs)
                                    {
                                        if (token.IsCancellationRequested) break;
                                        IndexNrdDirectory(nrdFileMap, subDir, token);
                                    }
                                }
                            }
                        }
                    }

                    logout(string.Format("Found {0} NRD files", nrdFileMap.Count));

                    // Get instrument folders to analyze in CSV directory
                    // If specific folders selected, only analyze those instruments
                    HashSet<string> instrumentsToAnalyze = null;
                    if (selectedPaths.Count > 0)
                    {
                        // Extract unique instrument names from nrdFileMap
                        instrumentsToAnalyze = new HashSet<string>();
                        foreach (var key in nrdFileMap.Keys)
                        {
                            int slashIndex = key.IndexOf('/');
                            if (slashIndex > 0)
                                instrumentsToAnalyze.Add(key.Substring(0, slashIndex));
                        }
                    }

                    string[] csvInstrumentDirs = Directory.GetDirectories(csvDir);

                    // Count total CSV files to analyze for progress tracking
                    int totalCsvFiles = 0;
                    int analyzedFiles = 0;
                    foreach (string dir in csvInstrumentDirs)
                    {
                        string instName = Path.GetFileName(dir);
                        if (instrumentsToAnalyze != null && !instrumentsToAnalyze.Contains(instName))
                            continue;
                        totalCsvFiles += Directory.GetFiles(dir, "*.csv").Length;
                    }

                    if (totalCsvFiles > 0)
                    {
                        logout(string.Format("Analyzing {0} CSV files...", totalCsvFiles));
                        Dispatcher.Invoke(() =>
                        {
                            double margin = (double)FindResource("MarginBase");
                            lProgress.Margin = new Thickness(margin, 0, margin, 0);
                            lProgress.Height = 24;
                            lProgress.Content = "Analyzing...";
                            pbProgress.Margin = new Thickness(margin);
                            pbProgress.Height = 16;
                            pbProgress.IsIndeterminate = false;
                            pbProgress.Minimum = 0;
                            pbProgress.Maximum = totalCsvFiles;
                            pbProgress.Value = 0;
                        });
                    }

                    foreach (string csvInstrumentDir in csvInstrumentDirs)
                    {
                        if (token.IsCancellationRequested) break;

                        string instrumentName = Path.GetFileName(csvInstrumentDir);

                        // Skip instruments not in selection
                        if (instrumentsToAnalyze != null && !instrumentsToAnalyze.Contains(instrumentName))
                            continue;

                        string[] csvFiles = Directory.GetFiles(csvInstrumentDir, "*.csv");

                        foreach (string csvFile in csvFiles)
                        {
                            if (token.IsCancellationRequested) break;

                            var fileStartTime = DateTime.Now;
                            string dateName = Path.GetFileNameWithoutExtension(csvFile);
                            string nrdKey = instrumentName + "/" + dateName;
                            string nrdFile;
                            bool hasNrd = nrdFileMap.TryGetValue(nrdKey, out nrdFile);

                            // Check if corresponding NRD exists
                            if (!hasNrd || !File.Exists(nrdFile))
                            {
                                orphaned++;
                                logout(string.Format("Orphaned (no NRD): {0}/{1}.csv", instrumentName, dateName));
                                continue;
                            }

                            FileInfo nrdInfo = new FileInfo(nrdFile);
                            FileInfo csvInfo = new FileInfo(csvFile);

                            // Check manifest for existing entry first
                            var existingEntry = analyzeManifest.Get(instrumentName, dateName);

                            // Determine status - check if we can trust manifest first (fast path)
                            string status;
                            bool needsVerification = false;
                            long csvRecords = 0;
                            string lastTimestamp = "";
                            string lastOffset = "";

                            if (existingEntry != null &&
                                existingEntry.NrdSize == nrdInfo.Length &&
                                Math.Abs((existingEntry.NrdModified - nrdInfo.LastWriteTime).TotalSeconds) <= 1)
                            {
                                // NRD unchanged since last manifest entry - trust manifest (FAST PATH)
                                cached++;
                                csvRecords = existingEntry.CsvRecords;
                                lastTimestamp = existingEntry.LastTimestamp;
                                lastOffset = existingEntry.LastOffset;

                                if (existingEntry.Status == "complete")
                                {
                                    complete++;
                                    status = "complete";
                                    // Trusted from manifest - no log to reduce noise
                                }
                                else
                                {
                                    partial++;
                                    status = "partial";
                                    logout(string.Format("Partial (cached): {0}/{1}.csv", instrumentName, dateName));
                                }
                            }
                            else if (existingEntry != null &&
                                existingEntry.NrdSize != nrdInfo.Length)
                            {
                                // NRD size changed since last export - needs verification
                                needsVerification = true;
                                outdated++;
                                status = "partial";
                                logout(string.Format("Outdated: {0}/{1}.csv (NRD size changed)", instrumentName, dateName));
                            }
                            else
                            {
                                // No manifest entry or NRD modified date changed - need to verify
                                needsVerification = true;
                                status = "unknown";
                            }

                            // Only read CSV if we need to verify (SLOW PATH)
                            if (needsVerification)
                            {
                                var csvAnalysis = AnalyzeCsvFileEfficient(csvFile);
                                csvRecords = csvAnalysis.LineCount;
                                lastTimestamp = csvAnalysis.LastTimestamp;
                                lastOffset = csvAnalysis.LastOffset;
                            }

                            // Perform verification if needed
                            if (needsVerification)
                            {
                                verified++;
                                var verifyStartTime = DateTime.Now;

                                // Get instrument for verification
                                string folderName = Path.GetFileName(Path.GetDirectoryName(nrdFile));
                                Collection<Instrument> instruments = InstrumentList.GetInstruments(folderName);

                                if (instruments.Count == 1)
                                {
                                    // Parse date from filename
                                    DateTime fileDate;
                                    try
                                    {
                                        fileDate = new DateTime(
                                            Convert.ToInt32(dateName.Substring(0, 4)),
                                            Convert.ToInt32(dateName.Substring(4, 2)),
                                            Convert.ToInt32(dateName.Substring(6, 2)));
                                    }
                                    catch
                                    {
                                        // Invalid date format - skip verification
                                        complete++;
                                        status = "complete";
                                        logout(string.Format("Skipped (invalid date): {0}/{1}.csv", instrumentName, dateName));
                                        goto updateManifest;
                                    }

                                    long expectedRecords;
                                    string expectedTimestamp, expectedOffset;
                                    var verifyResult = VerifyCsvCompleteness(
                                        instruments[0],
                                        fileDate,
                                        lastTimestamp,
                                        lastOffset,
                                        csvRecords,
                                        out expectedRecords,
                                        out expectedTimestamp,
                                        out expectedOffset);

                                    var verifyTime = (DateTime.Now - verifyStartTime).TotalSeconds;

                                    switch (verifyResult)
                                    {
                                        case VerificationResult.Complete:
                                            complete++;
                                            status = "complete";
                                            logout(string.Format("Verified: {0}/{1}.csv - Complete ({2} records) [{3:F1}s]",
                                                instrumentName, dateName, csvRecords, verifyTime));
                                            break;
                                        case VerificationResult.Partial:
                                            partial++;
                                            status = "partial";
                                            logout(string.Format("Verified: {0}/{1}.csv - PARTIAL (has {2}, expected {3}) [{4:F1}s]",
                                                instrumentName, dateName, csvRecords, expectedRecords, verifyTime));
                                            break;
                                        case VerificationResult.Error:
                                            // Verification failed - assume complete if has records
                                            if (csvRecords > 0)
                                            {
                                                complete++;
                                                status = "complete";
                                                logout(string.Format("Verified: {0}/{1}.csv - Assumed complete (error) [{2:F1}s]",
                                                    instrumentName, dateName, verifyTime));
                                            }
                                            else
                                            {
                                                partial++;
                                                status = "partial";
                                                logout(string.Format("Verified: {0}/{1}.csv - Empty (error) [{2:F1}s]",
                                                    instrumentName, dateName, verifyTime));
                                            }
                                            break;
                                    }
                                }
                                else
                                {
                                    // Can't find instrument - assume complete if has records
                                    var verifyTime = (DateTime.Now - verifyStartTime).TotalSeconds;
                                    if (csvRecords > 0)
                                    {
                                        complete++;
                                        status = "complete";
                                        logout(string.Format("Skipped: {0}/{1}.csv - instrument not found [{2:F1}s]",
                                            instrumentName, dateName, verifyTime));
                                    }
                                    else
                                    {
                                        partial++;
                                        status = "partial";
                                    }
                                }
                            }

                            updateManifest:
                            // Update manifest entry
                            var newEntry = new ManifestEntry
                            {
                                Instrument = instrumentName,
                                Date = dateName,
                                Status = status,
                                NrdSize = nrdInfo.Length,
                                NrdModified = nrdInfo.LastWriteTime,
                                CsvRecords = csvRecords,
                                LastTimestamp = lastTimestamp,
                                LastOffset = lastOffset,
                                ExportedAt = existingEntry?.ExportedAt ?? csvInfo.LastWriteTime
                            };

                            if (existingEntry == null ||
                                existingEntry.Status != newEntry.Status ||
                                existingEntry.CsvRecords != newEntry.CsvRecords)
                            {
                                updated++;
                            }

                            analyzeManifest.Update(newEntry);

                            // Update progress
                            analyzedFiles++;
                            int currentProgress = analyzedFiles;
                            Dispatcher.InvokeAsync(() =>
                            {
                                pbProgress.Value = currentProgress;
                                lProgress.Content = string.Format("Analyzed {0} of {1} files...", currentProgress, totalCsvFiles);
                            });
                        }
                    }

                    // Hide progress bar
                    Dispatcher.Invoke(() =>
                    {
                        lProgress.Margin = new Thickness(0);
                        lProgress.Height = 0;
                        pbProgress.Margin = new Thickness(0);
                        pbProgress.Height = 0;
                    });

                    // Check for NRD files without corresponding CSV (missing exports)
                    // Use the nrdFileMap which already has the correct instrument FullName mapping
                    foreach (var kvp in nrdFileMap)
                    {
                        if (token.IsCancellationRequested) break;

                        // kvp.Key is "InstrumentFullName/DateName"
                        string[] parts = kvp.Key.Split('/');
                        if (parts.Length != 2) continue;

                        string instrumentFullName = parts[0];
                        string dateName = parts[1];
                        string csvFile = Path.Combine(csvDir, instrumentFullName, dateName + ".csv");

                        if (!File.Exists(csvFile))
                        {
                            missing++;
                        }
                    }

                    // Save updated manifest
                    analyzeManifest.Save();

                    if (!token.IsCancellationRequested)
                    {
                        var totalTime = (DateTime.Now - analysisStartTime).TotalSeconds;
                        logout("");
                        logout("=== Analysis Complete ===");
                        logout(string.Format("  Complete:  {0} files", complete));
                        logout(string.Format("  Partial:   {0} files", partial));
                        logout(string.Format("  Outdated:  {0} files", outdated));
                        logout(string.Format("  Missing:   {0} files (NRD exists, no CSV)", missing));
                        logout(string.Format("  Orphaned:  {0} files (CSV exists, no NRD)", orphaned));
                        logout("");
                        logout(string.Format("Performance: {0} cached, {1} verified, {2:F1}s total",
                            cached, verified, totalTime));
                        logout(string.Format("Manifest: {0} entries modified, {1} total entries",
                            updated, analyzeManifest.EntryCount));
                        logout(string.Format("Manifest saved to: {0}", ExportManifest.GetManifestPath()));
                    }
                }
                catch (Exception ex)
                {
                    logout(string.Format("ERROR: {0}", ex.Message));
                }
                finally
                {
                    Dispatcher.Invoke(() =>
                    {
                        bAnalyze.IsEnabled = true;
                        bConvert.IsEnabled = true;
                    });
                }
            }, token);
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
            bool forceExport = cbForceExport.IsChecked == true;

            // Get selected paths from ListBox
            List<string> selectedPaths = lbSelectedPaths.Items.Cast<string>().ToList();

            // Initialize manifest
            manifest = new ExportManifest(csvDir);
            logout(string.Format("Loaded manifest with {0} entries", manifest.EntryCount));

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
                            ProceedDirectory(entries, nrdDir, nrdSubDirs[i], csvDir, forceExport);
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
                                ProceedFile(entries, path, csvDir, forceExport);
                            }
                            else if (Directory.Exists(path))
                            {
                                // Folder - check if it contains .nrd files directly or has subdirectories
                                string[] nrdFiles = Directory.GetFiles(path, "*.nrd");
                                if (nrdFiles.Length > 0)
                                {
                                    // Folder contains .nrd files directly
                                    ProceedDirectory(entries, Path.GetDirectoryName(path), path, csvDir, forceExport);
                                }
                                else
                                {
                                    // Check subdirectories
                                    string[] subDirs = Directory.GetDirectories(path);
                                    foreach (string subDir in subDirs)
                                    {
                                        if (token.IsCancellationRequested) break;
                                        ProceedDirectory(entries, path, subDir, csvDir, forceExport);
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

        private void ProceedDirectory(List<DumpEntry> entries, string nrdRoot, string nrdDir, string csvDir, bool forceExport)
        {
            string[] fileEntries = Directory.GetFiles(nrdDir, "*.nrd");
            if (fileEntries.Length == 0)
            {
                logout(string.Format("WARNING: No *.nrd files found in \"{0}\" directory. Skipped", nrdDir));
                return;
            }

            foreach (string fileName in fileEntries)
            {
                ProceedFile(entries, fileName, csvDir, forceExport);
            }
        }

        private void ProceedFile(List<DumpEntry> entries, string fileName, string csvDir, bool forceExport)
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

            // Get NRD file info
            FileInfo nrdInfo = new FileInfo(fileName);
            long nrdSize = nrdInfo.Length;
            DateTime nrdModified = nrdInfo.LastWriteTime;

            // Use manifest to determine action
            var decision = manifest.GetDecision(instrument.FullName, name, nrdSize, nrdModified, forceExport);

            bool appendMode = false;
            switch (decision)
            {
                case ExportManifest.ExportDecision.Skip:
                    logout(string.Format("Skipped: {0} (complete in manifest)", displayName));
                    return;

                case ExportManifest.ExportDecision.AppendMode:
                    if (!File.Exists(csvFileName))
                    {
                        // Manifest says append but CSV doesn't exist - do full export
                        logout(string.Format("Will export: {0} (CSV missing)", displayName));
                    }
                    else
                    {
                        appendMode = true;
                        logout(string.Format("Will update: {0} (new data available)", displayName));
                    }
                    break;

                case ExportManifest.ExportDecision.FullExport:
                    if (forceExport)
                        logout(string.Format("Will export: {0} (forced)", displayName));
                    else
                        logout(string.Format("Will export: {0} (new file)", displayName));
                    break;
            }

            totalFilesLength += nrdSize;
            entries.Add(new DumpEntry()
            {
                NrdLength = nrdSize,
                Instrument = instrument,
                Date = new DateTime(
                    Convert.ToInt16(name.Substring(0, 4)),
                    Convert.ToInt16(name.Substring(4, 2)),
                    Convert.ToInt16(name.Substring(6, 2))),
                CsvFileName = csvFileName,
                FromName = Path.Combine(fullName, displayName),
                ToName = csvFileName.Substring(csvDir.Length + 1),
                AppendMode = appendMode,
                ForceExport = forceExport,
                NrdFilePath = fileName,
            });
        }

        private void IndexNrdDirectory(Dictionary<string, string> nrdFileMap, string nrdInstrumentDir, CancellationToken token)
        {
            string folderName = Path.GetFileName(nrdInstrumentDir);
            Collection<Instrument> instruments = InstrumentList.GetInstruments(folderName);
            if (instruments.Count != 1) return;

            string instrumentFullName = instruments[0].FullName;
            string[] nrdFiles = Directory.GetFiles(nrdInstrumentDir, "*.nrd");

            foreach (string nrdFile in nrdFiles)
            {
                if (token.IsCancellationRequested) break;
                string dateName = Path.GetFileNameWithoutExtension(nrdFile);
                string key = instrumentFullName + "/" + dateName;
                nrdFileMap[key] = nrdFile;
            }
        }

        private void IndexNrdFile(Dictionary<string, string> nrdFileMap, string nrdFilePath)
        {
            string folderName = Path.GetFileName(Path.GetDirectoryName(nrdFilePath));
            Collection<Instrument> instruments = InstrumentList.GetInstruments(folderName);
            if (instruments.Count != 1) return;

            string instrumentFullName = instruments[0].FullName;
            string dateName = Path.GetFileNameWithoutExtension(nrdFilePath);
            string key = instrumentFullName + "/" + dateName;
            nrdFileMap[key] = nrdFilePath;
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
                // Save manifest
                try
                {
                    manifest?.Save();
                    logout(string.Format("Manifest saved with {0} entries", manifest?.EntryCount ?? 0));
                }
                catch (Exception ex)
                {
                    logout(string.Format("WARNING: Failed to save manifest: {0}", ex.Message));
                }

                Dispatcher.Invoke(() => complete());
            }
        }

        private void ConvertNrd(DumpEntry entry, CancellationToken token)
        {
            if (token.IsCancellationRequested) return;

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
                if (entry.AppendMode && !entry.ForceExport)
                {
                    logout(string.Format("Updating \"{0}\"...", entry.FromName));
                    AppendNewRecords(entry, token);
                }
                else
                {
                    logout(string.Format("Converting \"{0}\"...", entry.FromName));

                    // Delete existing file if force export
                    if (entry.ForceExport && File.Exists(entry.CsvFileName))
                        File.Delete(entry.CsvFileName);

                    MarketReplay.DumpMarketDepth(entry.Instrument, entry.Date, entry.Date, entry.CsvFileName);

                    if (!token.IsCancellationRequested)
                    {
                        // Update manifest with complete status
                        UpdateManifestEntry(entry, "complete");
                        logout(string.Format("Converted \"{0}\"", entry.FromName));
                    }
                }
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                {
                    // Mark as partial in manifest
                    UpdateManifestEntry(entry, "partial");
                    logout(string.Format("ERROR: Failed \"{0}\": {1}", entry.FromName, error.Message));
                }
            }
        }

        private void UpdateManifestEntry(DumpEntry entry, string status)
        {
            try
            {
                // Get NRD file info
                FileInfo nrdInfo = new FileInfo(entry.NrdFilePath);

                // Count records and get last line from CSV efficiently
                long csvRecords = 0;
                string lastTimestamp = "";
                string lastOffset = "";

                if (File.Exists(entry.CsvFileName))
                {
                    var csvAnalysis = AnalyzeCsvFileEfficient(entry.CsvFileName);
                    csvRecords = csvAnalysis.LineCount;
                    lastTimestamp = csvAnalysis.LastTimestamp;
                    lastOffset = csvAnalysis.LastOffset;
                }

                var manifestEntry = new ManifestEntry
                {
                    Instrument = entry.Instrument.FullName,
                    Date = entry.Date.ToString("yyyyMMdd"),
                    Status = status,
                    NrdSize = nrdInfo.Length,
                    NrdModified = nrdInfo.LastWriteTime,
                    CsvRecords = csvRecords,
                    LastTimestamp = lastTimestamp,
                    LastOffset = lastOffset,
                    ExportedAt = DateTime.Now
                };

                manifest.Update(manifestEntry);
            }
            catch (Exception ex)
            {
                logout(string.Format("WARNING: Failed to update manifest: {0}", ex.Message));
            }
        }

        private void AppendNewRecords(DumpEntry entry, CancellationToken token)
        {
            const int OVERLAP_LINES = 100;
            string tempFileName = entry.CsvFileName + ".tmp";

            try
            {
                // Step 1: Read last N lines from existing CSV for overlap matching
                List<string> existingTail = ReadLastLines(entry.CsvFileName, OVERLAP_LINES);
                if (existingTail.Count == 0)
                {
                    logout(string.Format("WARNING: Existing CSV is empty, doing full export: {0}", entry.FromName));
                    MarketReplay.DumpMarketDepth(entry.Instrument, entry.Date, entry.Date, entry.CsvFileName);
                    return;
                }

                if (token.IsCancellationRequested) return;

                // Step 2: Export full day to temp file
                MarketReplay.DumpMarketDepth(entry.Instrument, entry.Date, entry.Date, tempFileName);

                if (token.IsCancellationRequested)
                {
                    if (File.Exists(tempFileName)) File.Delete(tempFileName);
                    return;
                }

                // Step 3: Find overlap point in temp file
                int overlapIndex = FindOverlapIndex(tempFileName, existingTail);

                if (overlapIndex == -1)
                {
                    // No overlap found - this could indicate a gap or corruption
                    logout(string.Format("WARNING: No overlap found for {0}. Possible data gap. Appending all new data.", entry.FromName));
                    // Append all records from temp file that are newer than last existing record
                    AppendRecordsAfterTimestamp(entry.CsvFileName, tempFileName, existingTail.Last());
                    // Mark as partial since we can't verify data integrity
                    UpdateManifestEntry(entry, "partial");
                }
                else
                {
                    // Step 4: Append only records after the overlap
                    int newRecordsCount = AppendRecordsFromIndex(entry.CsvFileName, tempFileName, overlapIndex + existingTail.Count);
                    if (!token.IsCancellationRequested)
                    {
                        // Update manifest - complete since we verified overlap
                        UpdateManifestEntry(entry, "complete");

                        if (newRecordsCount > 0)
                            logout(string.Format("Updated \"{0}\" (+{1} records)", entry.FromName, newRecordsCount));
                        else
                            logout(string.Format("No new records for \"{0}\"", entry.FromName));
                    }
                }
            }
            finally
            {
                // Clean up temp file
                if (File.Exists(tempFileName))
                {
                    try { File.Delete(tempFileName); }
                    catch { /* ignore cleanup errors */ }
                }
            }
        }

        private List<string> ReadLastLines(string filePath, int lineCount)
        {
            List<string> lines = new List<string>();
            try
            {
                // Read file and get last N non-empty lines
                string[] allLines = File.ReadAllLines(filePath);
                int startIndex = Math.Max(0, allLines.Length - lineCount);
                for (int i = startIndex; i < allLines.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(allLines[i]))
                        lines.Add(allLines[i]);
                }
            }
            catch (Exception ex)
            {
                logout(string.Format("ERROR reading CSV: {0}", ex.Message));
            }
            return lines;
        }

        private int FindOverlapIndex(string tempFilePath, List<string> existingTail)
        {
            // Find where existingTail[0] appears in temp file
            // Then verify subsequent lines match
            string firstOverlapLine = existingTail[0];

            try
            {
                string[] tempLines = File.ReadAllLines(tempFilePath);

                for (int i = 0; i < tempLines.Length; i++)
                {
                    if (tempLines[i] == firstOverlapLine)
                    {
                        // Check if subsequent lines match
                        bool fullMatch = true;
                        for (int j = 1; j < existingTail.Count && (i + j) < tempLines.Length; j++)
                        {
                            if (tempLines[i + j] != existingTail[j])
                            {
                                fullMatch = false;
                                break;
                            }
                        }

                        if (fullMatch)
                            return i; // Found overlap starting at index i
                    }
                }
            }
            catch (Exception ex)
            {
                logout(string.Format("ERROR finding overlap: {0}", ex.Message));
            }

            return -1; // No overlap found
        }

        private int AppendRecordsFromIndex(string csvFilePath, string tempFilePath, int startIndex)
        {
            int count = 0;
            try
            {
                string[] tempLines = File.ReadAllLines(tempFilePath);
                if (startIndex >= tempLines.Length)
                    return 0; // No new records

                using (StreamWriter writer = File.AppendText(csvFilePath))
                {
                    for (int i = startIndex; i < tempLines.Length; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(tempLines[i]))
                        {
                            writer.WriteLine(tempLines[i]);
                            count++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logout(string.Format("ERROR appending records: {0}", ex.Message));
            }
            return count;
        }

        private void AppendRecordsAfterTimestamp(string csvFilePath, string tempFilePath, string lastExistingLine)
        {
            // Parse timestamp from last existing line and append all records with later timestamps
            try
            {
                string[] parts = lastExistingLine.Split(';');
                if (parts.Length < 4) return;

                string lastTimestamp = parts[2];
                string lastOffset = parts[3];

                string[] tempLines = File.ReadAllLines(tempFilePath);
                int count = 0;

                using (StreamWriter writer = File.AppendText(csvFilePath))
                {
                    foreach (string line in tempLines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        string[] lineParts = line.Split(';');
                        if (lineParts.Length < 4) continue;

                        string lineTimestamp = lineParts[2];
                        string lineOffset = lineParts[3];

                        // Compare timestamps (string comparison works for YYYYMMDDhhmmss format)
                        int cmp = string.Compare(lineTimestamp, lastTimestamp, StringComparison.Ordinal);
                        if (cmp > 0 || (cmp == 0 && string.Compare(lineOffset, lastOffset, StringComparison.Ordinal) > 0))
                        {
                            writer.WriteLine(line);
                            count++;
                        }
                    }
                }

                if (count > 0)
                    logout(string.Format("Appended {0} new records after gap", count));
            }
            catch (Exception ex)
            {
                logout(string.Format("ERROR appending after timestamp: {0}", ex.Message));
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
                bAnalyze.IsEnabled = false;
                tbCsvRootDir.IsReadOnly = true;
                lbSelectedPaths.IsEnabled = false;
                bAddFolder.IsEnabled = false;
                bAddFiles.IsEnabled = false;
                bRemove.IsEnabled = false;
                bClear.IsEnabled = false;
                cbForceExport.IsEnabled = false;
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
                    cbForceExport.IsEnabled = true;
                    bAnalyze.IsEnabled = true;
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
                bAnalyze.IsEnabled = false;
                tbCsvRootDir.IsReadOnly = true;
                lbSelectedPaths.IsEnabled = false;
                bAddFolder.IsEnabled = false;
                bAddFiles.IsEnabled = false;
                bRemove.IsEnabled = false;
                bClear.IsEnabled = false;
                cbForceExport.IsEnabled = false;
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
                cbForceExport.IsEnabled = true;
                bAnalyze.IsEnabled = true;
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

        /// <summary>
        /// Result of CSV file analysis
        /// </summary>
        private class CsvAnalysisResult
        {
            public long LineCount;
            public string LastTimestamp;
            public string LastOffset;
        }

        /// <summary>
        /// Efficiently analyze a CSV file by streaming instead of loading all into memory.
        /// </summary>
        private static CsvAnalysisResult AnalyzeCsvFileEfficient(string filePath)
        {
            var result = new CsvAnalysisResult
            {
                LineCount = 0,
                LastTimestamp = "",
                LastOffset = ""
            };

            // For very large files, read last line from end of file first
            // Then stream count the lines
            try
            {
                // Step 1: Get last line by reading from end of file (much faster for large files)
                string lastLine = ReadLastLineFromEnd(filePath);
                if (!string.IsNullOrEmpty(lastLine))
                {
                    string[] parts = lastLine.Split(';');
                    if (parts.Length >= 4)
                    {
                        result.LastTimestamp = parts[2];
                        result.LastOffset = parts[3];
                    }
                }

                // Step 2: Count lines by streaming (doesn't load all into memory)
                using (var reader = new StreamReader(filePath))
                {
                    while (reader.ReadLine() != null)
                    {
                        result.LineCount++;
                    }
                }
            }
            catch
            {
                // Fallback to simple approach on error
                result.LineCount = 0;
            }

            return result;
        }

        /// <summary>
        /// Read the last non-empty line from a file by seeking to the end.
        /// Much faster than reading the entire file for large files.
        /// </summary>
        private static string ReadLastLineFromEnd(string filePath, int bufferSize = 8192)
        {
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length == 0) return "";

                long position = fs.Length;
                byte[] buffer = new byte[bufferSize];
                List<byte> lineBytes = new List<byte>();

                while (position > 0)
                {
                    int bytesToRead = (int)Math.Min(bufferSize, position);
                    position -= bytesToRead;
                    fs.Seek(position, SeekOrigin.Begin);
                    int bytesRead = fs.Read(buffer, 0, bytesToRead);

                    // Process bytes from end to start
                    for (int i = bytesRead - 1; i >= 0; i--)
                    {
                        byte b = buffer[i];
                        if (b == '\n' || b == '\r')
                        {
                            if (lineBytes.Count > 0)
                            {
                                // We have a complete line
                                lineBytes.Reverse();
                                string line = System.Text.Encoding.UTF8.GetString(lineBytes.ToArray()).Trim();
                                if (!string.IsNullOrWhiteSpace(line))
                                    return line;
                                lineBytes.Clear();
                            }
                        }
                        else
                        {
                            lineBytes.Add(b);
                        }
                    }
                }

                // Handle case where file doesn't end with newline
                if (lineBytes.Count > 0)
                {
                    lineBytes.Reverse();
                    return System.Text.Encoding.UTF8.GetString(lineBytes.ToArray()).Trim();
                }

                return "";
            }
        }

        /// <summary>
        /// Result of CSV verification against NRD source
        /// </summary>
        private enum VerificationResult
        {
            Complete,       // CSV has all records from NRD
            Partial,        // CSV is missing records
            Error           // Verification failed
        }

        /// <summary>
        /// Verify if a CSV file is complete by comparing against a fresh export from NRD.
        /// Exports to temp file, compares last records, then deletes temp.
        /// </summary>
        private VerificationResult VerifyCsvCompleteness(
            Cbi.Instrument instrument,
            DateTime date,
            string existingLastTimestamp,
            string existingLastOffset,
            long existingRecordCount,
            out long expectedRecordCount,
            out string expectedLastTimestamp,
            out string expectedLastOffset)
        {
            expectedRecordCount = 0;
            expectedLastTimestamp = "";
            expectedLastOffset = "";

            string tempFile = Path.Combine(Path.GetTempPath(), $"nrdverify_{Guid.NewGuid():N}.csv");

            try
            {
                // Export NRD to temp file
                MarketReplay.DumpMarketDepth(instrument, date, date, tempFile);

                if (!File.Exists(tempFile))
                    return VerificationResult.Error;

                // Analyze the temp file
                var tempAnalysis = AnalyzeCsvFileEfficient(tempFile);
                expectedRecordCount = tempAnalysis.LineCount;
                expectedLastTimestamp = tempAnalysis.LastTimestamp;
                expectedLastOffset = tempAnalysis.LastOffset;

                // Compare last timestamp and offset
                if (string.IsNullOrEmpty(expectedLastTimestamp))
                {
                    // NRD produced empty export - if existing CSV is also empty, it's complete
                    return existingRecordCount == 0 ? VerificationResult.Complete : VerificationResult.Partial;
                }

                // Check if existing CSV has the same last record as fresh export
                if (existingLastTimestamp == expectedLastTimestamp && existingLastOffset == expectedLastOffset)
                {
                    // Last records match - verify record counts are similar
                    // Allow small variance due to potential line ending differences
                    if (Math.Abs(existingRecordCount - expectedRecordCount) <= 1)
                        return VerificationResult.Complete;
                    else
                        return VerificationResult.Partial; // Same end but different count - unusual
                }

                // Compare timestamps to see if existing is behind
                int cmpTimestamp = string.Compare(existingLastTimestamp, expectedLastTimestamp, StringComparison.Ordinal);
                if (cmpTimestamp < 0)
                {
                    // Existing CSV ends earlier than expected - partial
                    return VerificationResult.Partial;
                }
                else if (cmpTimestamp == 0)
                {
                    // Same timestamp but different offset
                    int cmpOffset = string.Compare(existingLastOffset, expectedLastOffset, StringComparison.Ordinal);
                    if (cmpOffset < 0)
                        return VerificationResult.Partial;
                }

                // Existing has same or later timestamp - consider complete
                return VerificationResult.Complete;
            }
            catch
            {
                return VerificationResult.Error;
            }
            finally
            {
                // Clean up temp file
                try { if (File.Exists(tempFile)) File.Delete(tempFile); }
                catch { /* ignore cleanup errors */ }
            }
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
        public bool AppendMode { get; set; }
        public bool ForceExport { get; set; }
        public string NrdFilePath { get; set; }
    }

    public class ManifestEntry
    {
        public string Instrument { get; set; }
        public string Date { get; set; }
        public string Status { get; set; }  // "complete" or "partial"
        public long NrdSize { get; set; }
        public DateTime NrdModified { get; set; }
        public long CsvRecords { get; set; }
        public string LastTimestamp { get; set; }
        public string LastOffset { get; set; }
        public DateTime ExportedAt { get; set; }

        public string Key => $"{Instrument}/{Date}";

        public static ManifestEntry Parse(string line)
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 9) return null;
            try
            {
                return new ManifestEntry
                {
                    Instrument = parts[0],
                    Date = parts[1],
                    Status = parts[2],
                    NrdSize = long.Parse(parts[3]),
                    NrdModified = DateTime.Parse(parts[4]),
                    CsvRecords = long.Parse(parts[5]),
                    LastTimestamp = parts[6],
                    LastOffset = parts[7],
                    ExportedAt = DateTime.Parse(parts[8])
                };
            }
            catch { return null; }
        }

        public override string ToString()
        {
            return string.Join("\t",
                Instrument, Date, Status,
                NrdSize.ToString(), NrdModified.ToString("o"),
                CsvRecords.ToString(), LastTimestamp, LastOffset,
                ExportedAt.ToString("o"));
        }
    }

    public class ExportManifest
    {
        private readonly string manifestPath;
        private Dictionary<string, ManifestEntry> entries = new Dictionary<string, ManifestEntry>();
        private readonly object lockObj = new object();

        public string CsvRootDir { get; set; }

        public int EntryCount => entries.Count;

        // Fixed location for manifest - in NinjaTrader user data folder
        public static string GetManifestPath()
        {
            return Path.Combine(Globals.UserDataDir, "NRDToCSV", "export_manifest.tsv");
        }

        public ExportManifest()
        {
            manifestPath = GetManifestPath();
            Load();
        }

        public ExportManifest(string csvRootDir) : this()
        {
            CsvRootDir = csvRootDir;
        }

        private void Load()
        {
            lock (lockObj)
            {
                entries.Clear();
                if (!File.Exists(manifestPath)) return;

                try
                {
                    string[] lines = File.ReadAllLines(manifestPath);
                    foreach (string line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        // Parse settings line
                        if (line.StartsWith("#CSVROOT:"))
                        {
                            CsvRootDir = line.Substring(9).Trim();
                            continue;
                        }

                        if (line.StartsWith("#")) continue;

                        var entry = ManifestEntry.Parse(line);
                        if (entry != null)
                            entries[entry.Key] = entry;
                    }
                }
                catch { /* ignore load errors, will rebuild */ }
            }
        }

        public void Save()
        {
            lock (lockObj)
            {
                try
                {
                    string dir = Path.GetDirectoryName(manifestPath);
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    using (StreamWriter writer = new StreamWriter(manifestPath))
                    {
                        writer.WriteLine("# NRDToCSV Export Manifest - DO NOT EDIT");
                        if (!string.IsNullOrEmpty(CsvRootDir))
                            writer.WriteLine("#CSVROOT:" + CsvRootDir);
                        writer.WriteLine("# Instrument\tDate\tStatus\tNrdSize\tNrdModified\tCsvRecords\tLastTimestamp\tLastOffset\tExportedAt");
                        foreach (var entry in entries.Values.OrderBy(e => e.Key))
                        {
                            writer.WriteLine(entry.ToString());
                        }
                    }
                }
                catch { /* ignore save errors */ }
            }
        }

        public ManifestEntry Get(string instrument, string date)
        {
            lock (lockObj)
            {
                string key = $"{instrument}/{date}";
                return entries.TryGetValue(key, out var entry) ? entry : null;
            }
        }

        public void Update(ManifestEntry entry)
        {
            lock (lockObj)
            {
                entries[entry.Key] = entry;
            }
        }

        public enum ExportDecision
        {
            Skip,           // Already complete and unchanged
            FullExport,     // New file or force
            AppendMode      // Partial or NRD changed
        }

        public ExportDecision GetDecision(string instrument, string date, long nrdSize, DateTime nrdModified, bool force)
        {
            if (force) return ExportDecision.FullExport;

            var entry = Get(instrument, date);
            if (entry == null) return ExportDecision.FullExport;

            // Check if NRD file changed
            if (entry.NrdSize != nrdSize || Math.Abs((entry.NrdModified - nrdModified).TotalSeconds) > 1)
                return ExportDecision.AppendMode;

            // Check status
            if (entry.Status == "partial")
                return ExportDecision.AppendMode;

            // Complete and unchanged
            return ExportDecision.Skip;
        }
    }
}
