#region Using declarations
using System;
using System.Collections.Concurrent;
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
using System.Diagnostics;
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
        private const int DEFAULT_PARALLEL_THREADS_COUNT = 4;
        private const int DEFAULT_MAX_CPU_PERCENT = 70;
        private const int MIN_MAX_CPU_PERCENT = 10;
        private const int MAX_MAX_CPU_PERCENT = 100;

        private TextBox tbCsvRootDir;
        private ListBox lbSelectedPaths;
        private Button bAddFolder;
        private Button bAddFiles;
        private Button bRemove;
        private Button bClear;
        private CheckBox cbForceExport;
        private CheckBox cbEnableParquetPipeline;
        private CheckBox cbDeleteTempCsv;
        private CheckBox cbEnableCpuThrottling;
        private TextBox tbParquetRootDir;
        private TextBox tbParquetBridgeCommand;
        private TextBox tbParquetBridgeWorkingDir;
        private TextBox tbParallelThreads;
        private TextBox tbMaxCpuPercent;
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
        private readonly object cpuSampleLock = new object();
        private ExportManifest manifest;
        private int parallelThreadsCount = DEFAULT_PARALLEL_THREADS_COUNT;
        private int maxCpuPercent = DEFAULT_MAX_CPU_PERCENT;
        private bool enableCpuThrottling = true;
        private DateTime cpuSampleWallClockUtc = DateTime.MinValue;
        private TimeSpan cpuSampleProcessCpu = TimeSpan.Zero;
        private double cachedProcessCpuPercent = 0;

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
                    }

                    if (!string.IsNullOrEmpty(savedManifest.ParquetRootDir))
                        tbParquetRootDir.Text = savedManifest.ParquetRootDir;
                    if (!string.IsNullOrEmpty(savedManifest.ParquetBridgeCommand))
                        tbParquetBridgeCommand.Text = savedManifest.ParquetBridgeCommand;
                    if (!string.IsNullOrEmpty(savedManifest.ParquetBridgeWorkingDir))
                        tbParquetBridgeWorkingDir.Text = savedManifest.ParquetBridgeWorkingDir;
                    if (savedManifest.ForceExport.HasValue)
                        cbForceExport.IsChecked = savedManifest.ForceExport.Value;
                    if (savedManifest.EnableParquetPipeline.HasValue)
                        cbEnableParquetPipeline.IsChecked = savedManifest.EnableParquetPipeline.Value;
                    if (savedManifest.DeleteTempCsvOnSuccess.HasValue)
                        cbDeleteTempCsv.IsChecked = savedManifest.DeleteTempCsvOnSuccess.Value;
                    if (savedManifest.EnableCpuThrottling.HasValue)
                    {
                        enableCpuThrottling = savedManifest.EnableCpuThrottling.Value;
                        cbEnableCpuThrottling.IsChecked = enableCpuThrottling;
                    }
                    if (savedManifest.ParallelThreads.HasValue && savedManifest.ParallelThreads.Value > 0)
                    {
                        parallelThreadsCount = savedManifest.ParallelThreads.Value;
                        tbParallelThreads.Text = parallelThreadsCount.ToString();
                    }
                    if (savedManifest.MaxCpuPercent.HasValue &&
                        savedManifest.MaxCpuPercent.Value >= MIN_MAX_CPU_PERCENT &&
                        savedManifest.MaxCpuPercent.Value <= MAX_MAX_CPU_PERCENT)
                    {
                        maxCpuPercent = savedManifest.MaxCpuPercent.Value;
                        tbMaxCpuPercent.Text = maxCpuPercent.ToString();
                    }
                    if (savedManifest.SelectedPaths.Count > 0)
                    {
                        lbSelectedPaths.Items.Clear();
                        foreach (string path in savedManifest.SelectedPaths)
                        {
                            if (!string.IsNullOrWhiteSpace(path))
                                lbSelectedPaths.Items.Add(path);
                        }
                    }

                    UpdateCpuThrottleUiState();
                    savedManifest.Save();
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
                    RefreshParallelThreadsFromUi();
                    RefreshMaxCpuPercentFromUi();
                    var manifestToSave = new ExportManifest();
                    manifestToSave.CsvRootDir = tbCsvRootDir.Text;
                    manifestToSave.ParquetRootDir = tbParquetRootDir.Text;
                    manifestToSave.ParquetBridgeCommand = tbParquetBridgeCommand.Text;
                    manifestToSave.ParquetBridgeWorkingDir = tbParquetBridgeWorkingDir.Text;
                    manifestToSave.ForceExport = cbForceExport.IsChecked == true;
                    manifestToSave.EnableParquetPipeline = cbEnableParquetPipeline.IsChecked == true;
                    manifestToSave.DeleteTempCsvOnSuccess = cbDeleteTempCsv.IsChecked == true;
                    manifestToSave.EnableCpuThrottling = cbEnableCpuThrottling.IsChecked == true;
                    manifestToSave.ParallelThreads = parallelThreadsCount;
                    manifestToSave.MaxCpuPercent = maxCpuPercent;
                    manifestToSave.SelectedPaths = lbSelectedPaths.Items.Cast<string>().ToList();
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

            cbEnableParquetPipeline = new CheckBox()
            {
                Content = "Enable Parquet pipeline (NRD -> Parquet)",
                Margin = new Thickness(margin, 0, margin, margin / 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Label lParquetRootDir = new Label()
            {
                Foreground = FindResource("FontLabelBrush") as Brush,
                Margin = new Thickness(margin, 0, margin, 0),
                Content = "Parquet destination root directory:",
            };
            tbParquetRootDir = new TextBox()
            {
                Margin = new Thickness(margin, 0, margin, margin / 2),
                Text = Path.Combine(Globals.UserDataDir, "db", "replay.parquet"),
            };
            Label lParquetBridgeCommand = new Label()
            {
                Foreground = FindResource("FontLabelBrush") as Brush,
                Margin = new Thickness(margin, 0, margin, 0),
                Content = "Bridge command (example: uv run csv-to-parquet-bridge):",
            };
            tbParquetBridgeCommand = new TextBox()
            {
                Margin = new Thickness(margin, 0, margin, margin / 2),
                Text = "uv run csv-to-parquet-bridge",
            };
            Label lParquetBridgeWorkingDir = new Label()
            {
                Foreground = FindResource("FontLabelBrush") as Brush,
                Margin = new Thickness(margin, 0, margin, 0),
                Content = "Bridge working directory (csv_to_parquet project):",
            };
            tbParquetBridgeWorkingDir = new TextBox()
            {
                Margin = new Thickness(margin, 0, margin, margin / 2),
                Text = "",
            };
            cbDeleteTempCsv = new CheckBox()
            {
                Content = "Delete temp CSV after successful Parquet conversion",
                Margin = new Thickness(margin, 0, margin, margin),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = true,
            };
            Label lParallelThreads = new Label()
            {
                Foreground = FindResource("FontLabelBrush") as Brush,
                Margin = new Thickness(margin, 0, margin, 0),
                Content = "Parallel workers (1+):",
            };
            tbParallelThreads = new TextBox()
            {
                Margin = new Thickness(margin, 0, margin, margin / 2),
                Text = DEFAULT_PARALLEL_THREADS_COUNT.ToString(),
            };
            cbEnableCpuThrottling = new CheckBox()
            {
                Content = "Enable CPU throttling",
                Margin = new Thickness(0, 0, margin / 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = true,
            };
            cbEnableCpuThrottling.Checked += (o, e) => UpdateCpuThrottleUiState();
            cbEnableCpuThrottling.Unchecked += (o, e) => UpdateCpuThrottleUiState();
            Label lMaxCpuPercent = new Label()
            {
                Foreground = FindResource("FontLabelBrush") as Brush,
                Margin = new Thickness(0, 0, margin / 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Content = "Max %:",
            };
            tbMaxCpuPercent = new TextBox()
            {
                Margin = new Thickness(0, 0, 0, 0),
                Width = 56,
                Text = DEFAULT_MAX_CPU_PERCENT.ToString(),
            };
            StackPanel cpuThrottlePanel = new StackPanel()
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(margin, 0, margin, margin),
            };
            cpuThrottlePanel.Children.Add(cbEnableCpuThrottling);
            cpuThrottlePanel.Children.Add(lMaxCpuPercent);
            cpuThrottlePanel.Children.Add(tbMaxCpuPercent);

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
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
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
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
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
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            Grid.SetRow(lCsvRootDir, 0);
            Grid.SetRow(tbCsvRootDir, 1);
            Grid.SetRow(lSelectedPaths, 2);
            Grid.SetRow(lbSelectedPaths, 3);
            Grid.SetRow(buttonPanel, 4);
            Grid.SetRow(cbForceExport, 5);
            Grid.SetRow(cbEnableParquetPipeline, 6);
            Grid.SetRow(lParquetRootDir, 7);
            Grid.SetRow(tbParquetRootDir, 8);
            Grid.SetRow(lParquetBridgeCommand, 9);
            Grid.SetRow(tbParquetBridgeCommand, 10);
            Grid.SetRow(lParquetBridgeWorkingDir, 11);
            Grid.SetRow(tbParquetBridgeWorkingDir, 12);
            Grid.SetRow(cbDeleteTempCsv, 13);
            Grid.SetRow(lParallelThreads, 14);
            Grid.SetRow(tbParallelThreads, 15);
            Grid.SetRow(cpuThrottlePanel, 16);
            Grid.SetRow(tbOutput, 17);
            Grid.SetRow(actionPanel, 18);
            Grid.SetRow(lProgress, 19);
            Grid.SetRow(pbProgress, 20);
            grid.Children.Add(lCsvRootDir);
            grid.Children.Add(tbCsvRootDir);
            grid.Children.Add(lSelectedPaths);
            grid.Children.Add(lbSelectedPaths);
            grid.Children.Add(buttonPanel);
            grid.Children.Add(cbForceExport);
            grid.Children.Add(cbEnableParquetPipeline);
            grid.Children.Add(lParquetRootDir);
            grid.Children.Add(tbParquetRootDir);
            grid.Children.Add(lParquetBridgeCommand);
            grid.Children.Add(tbParquetBridgeCommand);
            grid.Children.Add(lParquetBridgeWorkingDir);
            grid.Children.Add(tbParquetBridgeWorkingDir);
            grid.Children.Add(cbDeleteTempCsv);
            grid.Children.Add(lParallelThreads);
            grid.Children.Add(tbParallelThreads);
            grid.Children.Add(cpuThrottlePanel);
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

            bool parquetPipelineEnabled = cbEnableParquetPipeline.IsChecked == true;
            string csvDir = tbCsvRootDir.Text;
            string parquetDir = tbParquetRootDir.Text;
            string nrdDir = Path.Combine(Globals.UserDataDir, "db", "replay");
            string analysisRootDir = parquetPipelineEnabled ? parquetDir : csvDir;

            if (!Directory.Exists(analysisRootDir))
            {
                logout(string.Format("{0} directory does not exist: {1}",
                    parquetPipelineEnabled ? "Parquet" : "CSV", analysisRootDir));
                return;
            }

            // Get selected paths from ListBox
            List<string> selectedPaths = lbSelectedPaths.Items.Cast<string>().ToList();
            RefreshParallelThreadsFromUi();
            RefreshMaxCpuPercentFromUi();
            enableCpuThrottling = cbEnableCpuThrottling.IsChecked == true;
            string parquetRootForManifest = tbParquetRootDir.Text;
            string bridgeCommandForManifest = tbParquetBridgeCommand.Text;
            string bridgeWorkDirForManifest = tbParquetBridgeWorkingDir.Text;
            bool forceExportForManifest = cbForceExport.IsChecked == true;
            bool enableParquetForManifest = cbEnableParquetPipeline.IsChecked == true;
            bool deleteTempForManifest = cbDeleteTempCsv.IsChecked == true;
            bool enableCpuThrottlingForManifest = cbEnableCpuThrottling.IsChecked == true;
            int parallelThreadsForManifest = parallelThreadsCount;
            int maxCpuPercentForManifest = maxCpuPercent;

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
                    if (parquetPipelineEnabled)
                    {
                        AnalyzeParquetDestination(
                            parquetDir,
                            nrdDir,
                            selectedPaths,
                            token,
                            parquetRootForManifest,
                            bridgeCommandForManifest,
                            bridgeWorkDirForManifest,
                            forceExportForManifest,
                            enableParquetForManifest,
                            deleteTempForManifest,
                            enableCpuThrottlingForManifest,
                            parallelThreadsForManifest,
                            maxCpuPercentForManifest);
                        return;
                    }

                    var analysisStartTime = DateTime.Now;

                    // Load manifest
                    var analyzeManifest = new ExportManifest(csvDir);
                    analyzeManifest.ParquetRootDir = parquetRootForManifest;
                    analyzeManifest.ParquetBridgeCommand = bridgeCommandForManifest;
                    analyzeManifest.ParquetBridgeWorkingDir = bridgeWorkDirForManifest;
                    analyzeManifest.ForceExport = forceExportForManifest;
                    analyzeManifest.EnableParquetPipeline = enableParquetForManifest;
                    analyzeManifest.DeleteTempCsvOnSuccess = deleteTempForManifest;
                    analyzeManifest.EnableCpuThrottling = enableCpuThrottlingForManifest;
                    analyzeManifest.ParallelThreads = parallelThreadsForManifest;
                    analyzeManifest.MaxCpuPercent = maxCpuPercentForManifest;
                    analyzeManifest.SelectedPaths = selectedPaths;
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
            bool parquetPipelineEnabled = cbEnableParquetPipeline.IsChecked == true;
            string parquetRootDir = tbParquetRootDir.Text;
            string parquetBridgeCommand = tbParquetBridgeCommand.Text;
            string parquetBridgeWorkingDir = tbParquetBridgeWorkingDir.Text;
            bool deleteTempCsvOnSuccess = cbDeleteTempCsv.IsChecked == true;

            // Get selected paths from ListBox
            List<string> selectedPaths = lbSelectedPaths.Items.Cast<string>().ToList();
            RefreshParallelThreadsFromUi();
            RefreshMaxCpuPercentFromUi();
            enableCpuThrottling = cbEnableCpuThrottling.IsChecked == true;

            // Initialize manifest
            manifest = new ExportManifest(csvDir);
            manifest.ParquetRootDir = tbParquetRootDir.Text;
            manifest.ParquetBridgeCommand = tbParquetBridgeCommand.Text;
            manifest.ParquetBridgeWorkingDir = tbParquetBridgeWorkingDir.Text;
            manifest.ForceExport = cbForceExport.IsChecked == true;
            manifest.EnableParquetPipeline = cbEnableParquetPipeline.IsChecked == true;
            manifest.DeleteTempCsvOnSuccess = cbDeleteTempCsv.IsChecked == true;
            manifest.EnableCpuThrottling = enableCpuThrottling;
            manifest.ParallelThreads = parallelThreadsCount;
            manifest.MaxCpuPercent = maxCpuPercent;
            manifest.SelectedPaths = lbSelectedPaths.Items.Cast<string>().ToList();
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

            if (parquetPipelineEnabled)
            {
                if (string.IsNullOrWhiteSpace(parquetRootDir))
                {
                    logout("ERROR: Parquet destination root directory is required when pipeline is enabled");
                    return;
                }

                if (string.IsNullOrWhiteSpace(parquetBridgeCommand))
                {
                    logout("ERROR: Bridge command is required when pipeline is enabled");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(parquetBridgeWorkingDir) && !Directory.Exists(parquetBridgeWorkingDir))
                {
                    logout(string.Format("ERROR: Bridge working directory not found: {0}", parquetBridgeWorkingDir));
                    return;
                }

                try
                {
                    if (!Directory.Exists(parquetRootDir))
                        Directory.CreateDirectory(parquetRootDir);
                }
                catch (Exception error)
                {
                    logout(string.Format("ERROR: Unable to create Parquet root directory \"{0}\": {1}", parquetRootDir, error.Message));
                    return;
                }

                logout(string.Format("Parquet pipeline enabled -> {0}", parquetRootDir));
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
                            ProceedDirectory(entries, nrdDir, nrdSubDirs[i], csvDir, forceExport,
                                parquetPipelineEnabled, parquetRootDir, parquetBridgeCommand, parquetBridgeWorkingDir, deleteTempCsvOnSuccess);
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
                                ProceedFile(entries, path, csvDir, forceExport,
                                    parquetPipelineEnabled, parquetRootDir, parquetBridgeCommand, parquetBridgeWorkingDir, deleteTempCsvOnSuccess);
                            }
                            else if (Directory.Exists(path))
                            {
                                // Folder - check if it contains .nrd files directly or has subdirectories
                                string[] nrdFiles = Directory.GetFiles(path, "*.nrd");
                                if (nrdFiles.Length > 0)
                                {
                                    // Folder contains .nrd files directly
                                    ProceedDirectory(entries, Path.GetDirectoryName(path), path, csvDir, forceExport,
                                        parquetPipelineEnabled, parquetRootDir, parquetBridgeCommand, parquetBridgeWorkingDir, deleteTempCsvOnSuccess);
                                }
                                else
                                {
                                    // Check subdirectories
                                    string[] subDirs = Directory.GetDirectories(path);
                                    foreach (string subDir in subDirs)
                                    {
                                        if (token.IsCancellationRequested) break;
                                        ProceedDirectory(entries, path, subDir, csvDir, forceExport,
                                            parquetPipelineEnabled, parquetRootDir, parquetBridgeCommand, parquetBridgeWorkingDir, deleteTempCsvOnSuccess);
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
                        if (enableCpuThrottling)
                        {
                            logout(string.Format("Converting {0} files ({1}) using {2} worker(s), max CPU {3}%...",
                                entries.Count, ToBytes(totalFilesLength), Math.Max(1, parallelThreadsCount), maxCpuPercent));
                        }
                        else
                        {
                            logout(string.Format("Converting {0} files ({1}) using {2} worker(s), CPU throttling disabled...",
                                entries.Count, ToBytes(totalFilesLength), Math.Max(1, parallelThreadsCount)));
                        }
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

        private void AnalyzeParquetDestination(
            string parquetDir,
            string nrdDir,
            List<string> selectedPaths,
            CancellationToken token,
            string parquetRootForManifest,
            string bridgeCommandForManifest,
            string bridgeWorkDirForManifest,
            bool forceExportForManifest,
            bool enableParquetForManifest,
            bool deleteTempForManifest,
            bool enableCpuThrottlingForManifest,
            int parallelThreadsForManifest,
            int maxCpuPercentForManifest)
        {
            var analysisStartTime = DateTime.Now;

            var analyzeManifest = new ExportManifest(parquetDir);
            analyzeManifest.ParquetRootDir = parquetRootForManifest;
            analyzeManifest.ParquetBridgeCommand = bridgeCommandForManifest;
            analyzeManifest.ParquetBridgeWorkingDir = bridgeWorkDirForManifest;
            analyzeManifest.ForceExport = forceExportForManifest;
            analyzeManifest.EnableParquetPipeline = enableParquetForManifest;
            analyzeManifest.DeleteTempCsvOnSuccess = deleteTempForManifest;
            analyzeManifest.EnableCpuThrottling = enableCpuThrottlingForManifest;
            analyzeManifest.ParallelThreads = parallelThreadsForManifest;
            analyzeManifest.MaxCpuPercent = maxCpuPercentForManifest;
            analyzeManifest.SelectedPaths = selectedPaths;
            logout(string.Format("Loaded manifest with {0} entries from: {1}",
                analyzeManifest.EntryCount, ExportManifest.GetManifestPath()));

            // Build NRD index first (instrument/date -> nrd file path)
            var nrdFileMap = new Dictionary<string, string>();
            logout("Building NRD file index...");

            if (selectedPaths.Count == 0)
            {
                if (Directory.Exists(nrdDir))
                {
                    foreach (string nrdInstrumentDir in Directory.GetDirectories(nrdDir))
                    {
                        if (token.IsCancellationRequested) break;
                        IndexNrdDirectory(nrdFileMap, nrdInstrumentDir, token);
                    }
                }
            }
            else
            {
                foreach (string path in selectedPaths)
                {
                    if (token.IsCancellationRequested) break;

                    if (File.Exists(path) && path.EndsWith(".nrd", StringComparison.OrdinalIgnoreCase))
                    {
                        IndexNrdFile(nrdFileMap, path);
                    }
                    else if (Directory.Exists(path))
                    {
                        string[] nrdFiles = Directory.GetFiles(path, "*.nrd");
                        if (nrdFiles.Length > 0)
                        {
                            IndexNrdDirectory(nrdFileMap, path, token);
                        }
                        else
                        {
                            foreach (string subDir in Directory.GetDirectories(path))
                            {
                                if (token.IsCancellationRequested) break;
                                IndexNrdDirectory(nrdFileMap, subDir, token);
                            }
                        }
                    }
                }
            }

            logout(string.Format("Found {0} NRD files", nrdFileMap.Count));
            if (nrdFileMap.Count == 0)
            {
                logout("No NRD files found to analyze");
                return;
            }

            // Progress setup
            int total = nrdFileMap.Count;
            int processed = 0;
            Dispatcher.Invoke(() =>
            {
                double margin = (double)FindResource("MarginBase");
                lProgress.Margin = new Thickness(margin, 0, margin, 0);
                lProgress.Height = 24;
                lProgress.Content = "Analyzing parquet outputs...";
                pbProgress.Margin = new Thickness(margin);
                pbProgress.Height = 16;
                pbProgress.IsIndeterminate = false;
                pbProgress.Minimum = 0;
                pbProgress.Maximum = total;
                pbProgress.Value = 0;
            });

            int complete = 0, partial = 0, outdated = 0, missing = 0, orphaned = 0;
            int updated = 0, cached = 0;
            var nrdKeys = new HashSet<string>(nrdFileMap.Keys);

            foreach (var kvp in nrdFileMap)
            {
                if (token.IsCancellationRequested) break;

                string key = kvp.Key;
                string[] parts = key.Split('/');
                if (parts.Length != 2) continue;

                string instrumentName = parts[0];
                string dateName = parts[1];
                string nrdFile = kvp.Value;
                FileInfo nrdInfo = new FileInfo(nrdFile);

                string l1Path = Path.Combine(parquetDir, instrumentName, dateName + "_L1.parquet");
                string l2Path = Path.Combine(parquetDir, instrumentName, dateName + "_L2.parquet");

                bool hasL1 = File.Exists(l1Path);
                bool hasL2 = File.Exists(l2Path);
                bool hasAnyParquet = hasL1 || hasL2;

                var existingEntry = analyzeManifest.Get(instrumentName, dateName);
                bool nrdUnchanged = existingEntry != null
                    && existingEntry.NrdSize == nrdInfo.Length
                    && Math.Abs((existingEntry.NrdModified - nrdInfo.LastWriteTime).TotalSeconds) <= 1;

                string status;
                if (!hasAnyParquet)
                {
                    missing++;
                    status = "partial";
                    logout(string.Format("Missing parquet: {0}/{1}", instrumentName, dateName));
                }
                else if (nrdUnchanged && existingEntry != null && existingEntry.Status == "complete")
                {
                    complete++;
                    cached++;
                    status = "complete";
                }
                else if (nrdUnchanged && existingEntry != null && existingEntry.Status == "partial")
                {
                    partial++;
                    cached++;
                    status = "partial";
                }
                else if (!nrdUnchanged && hasAnyParquet)
                {
                    outdated++;
                    status = "partial";
                    logout(string.Format("Outdated parquet: {0}/{1} (NRD changed)", instrumentName, dateName));
                }
                else
                {
                    complete++;
                    status = "complete";
                }

                long csvRecords = existingEntry != null ? existingEntry.CsvRecords : 0;
                string lastTimestamp = existingEntry != null ? existingEntry.LastTimestamp : "";
                string lastOffset = existingEntry != null ? existingEntry.LastOffset : "";

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
                    ExportedAt = existingEntry?.ExportedAt ?? DateTime.Now
                };

                if (existingEntry == null ||
                    existingEntry.Status != newEntry.Status ||
                    existingEntry.NrdSize != newEntry.NrdSize ||
                    Math.Abs((existingEntry.NrdModified - newEntry.NrdModified).TotalSeconds) > 1)
                {
                    updated++;
                }

                analyzeManifest.Update(newEntry);

                processed++;
                int progressValue = processed;
                Dispatcher.InvokeAsync(() =>
                {
                    pbProgress.Value = progressValue;
                    lProgress.Content = string.Format("Analyzed {0} of {1} files...", progressValue, total);
                });
            }

            // Orphaned parquet: parquet files with no matching NRD key
            if (!token.IsCancellationRequested && Directory.Exists(parquetDir))
            {
                var orphanedKeys = new HashSet<string>();
                foreach (string instrumentDir in Directory.GetDirectories(parquetDir))
                {
                    string instrumentName = Path.GetFileName(instrumentDir);
                    foreach (string parquetFile in Directory.GetFiles(instrumentDir, "*_L*.parquet"))
                    {
                        string fileName = Path.GetFileNameWithoutExtension(parquetFile);
                        string datePart = fileName.EndsWith("_L1") || fileName.EndsWith("_L2")
                            ? fileName.Substring(0, fileName.Length - 3)
                            : fileName;
                        string k = instrumentName + "/" + datePart;
                        if (!nrdKeys.Contains(k))
                            orphanedKeys.Add(k);
                    }
                }
                orphaned = orphanedKeys.Count;
            }

            analyzeManifest.Save();

            Dispatcher.Invoke(() =>
            {
                lProgress.Margin = new Thickness(0);
                lProgress.Height = 0;
                pbProgress.Margin = new Thickness(0);
                pbProgress.Height = 0;
            });

            if (!token.IsCancellationRequested)
            {
                var totalTime = (DateTime.Now - analysisStartTime).TotalSeconds;
                logout("");
                logout("=== Parquet Analysis Complete ===");
                logout(string.Format("  Complete:  {0} files", complete));
                logout(string.Format("  Partial:   {0} files", partial));
                logout(string.Format("  Outdated:  {0} files", outdated));
                logout(string.Format("  Missing:   {0} files (NRD exists, no Parquet)", missing));
                logout(string.Format("  Orphaned:  {0} files (Parquet exists, no NRD)", orphaned));
                logout("");
                logout(string.Format("Performance: {0} cached, {1:F1}s total", cached, totalTime));
                logout(string.Format("Manifest: {0} entries modified, {1} total entries",
                    updated, analyzeManifest.EntryCount));
                logout(string.Format("Manifest saved to: {0}", ExportManifest.GetManifestPath()));
            }
        }

        private void RefreshParallelThreadsFromUi()
        {
            int parsed;
            if (!int.TryParse(tbParallelThreads.Text, out parsed) || parsed < 1)
            {
                parsed = DEFAULT_PARALLEL_THREADS_COUNT;
                tbParallelThreads.Text = parsed.ToString();
            }
            parallelThreadsCount = parsed;
        }

        private void RefreshMaxCpuPercentFromUi()
        {
            int parsed;
            if (!int.TryParse(tbMaxCpuPercent.Text, out parsed))
            {
                parsed = DEFAULT_MAX_CPU_PERCENT;
            }

            if (parsed < MIN_MAX_CPU_PERCENT)
                parsed = MIN_MAX_CPU_PERCENT;
            else if (parsed > MAX_MAX_CPU_PERCENT)
                parsed = MAX_MAX_CPU_PERCENT;

            maxCpuPercent = parsed;
            tbMaxCpuPercent.Text = parsed.ToString();
        }

        private void UpdateCpuThrottleUiState()
        {
            bool enabled = cbEnableCpuThrottling.IsChecked == true;
            tbMaxCpuPercent.IsEnabled = enabled;
        }

        private void ProceedDirectory(
            List<DumpEntry> entries,
            string nrdRoot,
            string nrdDir,
            string csvDir,
            bool forceExport,
            bool parquetPipelineEnabled,
            string parquetRootDir,
            string parquetBridgeCommand,
            string parquetBridgeWorkingDir,
            bool deleteTempCsvOnSuccess)
        {
            string[] fileEntries = Directory.GetFiles(nrdDir, "*.nrd");
            if (fileEntries.Length == 0)
            {
                logout(string.Format("WARNING: No *.nrd files found in \"{0}\" directory. Skipped", nrdDir));
                return;
            }

            foreach (string fileName in fileEntries)
            {
                ProceedFile(entries, fileName, csvDir, forceExport,
                    parquetPipelineEnabled, parquetRootDir, parquetBridgeCommand, parquetBridgeWorkingDir, deleteTempCsvOnSuccess);
            }
        }

        private void ProceedFile(
            List<DumpEntry> entries,
            string fileName,
            string csvDir,
            bool forceExport,
            bool parquetPipelineEnabled,
            string parquetRootDir,
            string parquetBridgeCommand,
            string parquetBridgeWorkingDir,
            bool deleteTempCsvOnSuccess)
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
            string relativeCsvPath = Path.Combine(instrument.FullName, name + ".csv");
            string csvFileName = string.Format("{0}.csv", Path.Combine(csvDir, instrument.FullName, name));
            if (parquetPipelineEnabled)
            {
                csvFileName = string.Format("{0}.csv", Path.Combine(csvDir, "__temp_csv", instrument.FullName, name));
            }

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
                    if (parquetPipelineEnabled)
                    {
                        logout(string.Format("Will re-export: {0} (pipeline mode)", displayName));
                    }
                    else if (!File.Exists(csvFileName))
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
                UseParquetPipeline = parquetPipelineEnabled,
                ParquetRootDir = parquetRootDir,
                ParquetBridgeCommand = parquetBridgeCommand,
                ParquetBridgeWorkingDir = parquetBridgeWorkingDir,
                DeleteTempCsvOnSuccess = deleteTempCsvOnSuccess,
                RelativeCsvPath = relativeCsvPath.Replace("\\", "/"),
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
            int workersCount = Math.Max(1, parallelThreadsCount);
            var queue = new ConcurrentQueue<DumpEntry>(entries);

            try
            {
                ResetCpuSampling();
                var workers = new List<Task>(workersCount);
                for (int i = 0; i < workersCount; i++)
                {
                    workers.Add(Task.Run(() =>
                    {
                        while (!token.IsCancellationRequested)
                        {
                            DumpEntry entry;
                            if (!queue.TryDequeue(out entry))
                                break;

                            WaitForCpuBudget(token);
                            if (token.IsCancellationRequested)
                                break;

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
                        }
                    }, token));
                }

                Task.WaitAll(workers.ToArray());

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

        private void ResetCpuSampling()
        {
            lock (cpuSampleLock)
            {
                cpuSampleWallClockUtc = DateTime.UtcNow;
                cpuSampleProcessCpu = Process.GetCurrentProcess().TotalProcessorTime;
                cachedProcessCpuPercent = 0;
            }
        }

        private void WaitForCpuBudget(CancellationToken token)
        {
            if (!enableCpuThrottling || maxCpuPercent >= MAX_MAX_CPU_PERCENT)
                return;

            while (!token.IsCancellationRequested)
            {
                if (GetCurrentProcessCpuPercent() <= maxCpuPercent)
                    return;
                Thread.Sleep(120);
            }
        }

        private double GetCurrentProcessCpuPercent()
        {
            lock (cpuSampleLock)
            {
                DateTime nowUtc = DateTime.UtcNow;
                TimeSpan nowCpu = Process.GetCurrentProcess().TotalProcessorTime;
                TimeSpan wallDelta = nowUtc - cpuSampleWallClockUtc;

                if (wallDelta.TotalMilliseconds < 250)
                    return cachedProcessCpuPercent;

                TimeSpan cpuDelta = nowCpu - cpuSampleProcessCpu;
                double cpuPercent = 0;
                if (wallDelta.TotalMilliseconds > 0 && Environment.ProcessorCount > 0)
                {
                    cpuPercent = (cpuDelta.TotalMilliseconds /
                        (wallDelta.TotalMilliseconds * Environment.ProcessorCount)) * 100.0;
                }

                if (cpuPercent < 0)
                    cpuPercent = 0;

                cachedProcessCpuPercent = cpuPercent;
                cpuSampleWallClockUtc = nowUtc;
                cpuSampleProcessCpu = nowCpu;
                return cachedProcessCpuPercent;
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
                if (entry.UseParquetPipeline)
                {
                    logout(string.Format("Converting \"{0}\" to Parquet...", entry.FromName));

                    if (entry.ForceExport && File.Exists(entry.CsvFileName))
                        File.Delete(entry.CsvFileName);

                    MarketReplay.DumpMarketDepth(entry.Instrument, entry.Date, entry.Date, entry.CsvFileName);

                    string bridgeError;
                    bool parquetSuccess = ConvertTempCsvToParquet(entry, token, out bridgeError);
                    if (!parquetSuccess)
                    {
                        UpdateManifestEntry(entry, "partial");
                        logout(string.Format("ERROR: Parquet conversion failed for \"{0}\": {1}", entry.FromName, bridgeError));
                        return;
                    }

                    if (entry.DeleteTempCsvOnSuccess)
                    {
                        try
                        {
                            if (File.Exists(entry.CsvFileName))
                                File.Delete(entry.CsvFileName);
                        }
                        catch (Exception ex)
                        {
                            logout(string.Format("WARNING: Unable to delete temp CSV \"{0}\": {1}", entry.CsvFileName, ex.Message));
                        }
                    }

                    if (!token.IsCancellationRequested)
                    {
                        UpdateManifestEntry(entry, "complete");
                        logout(string.Format("Converted to Parquet \"{0}\"", entry.FromName));
                    }
                }
                else if (entry.AppendMode && !entry.ForceExport)
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

        private bool ConvertTempCsvToParquet(DumpEntry entry, CancellationToken token, out string error)
        {
            error = "";
            if (token.IsCancellationRequested)
            {
                error = "Canceled";
                return false;
            }

            try
            {
                string bridgeExe;
                string bridgeArgs;
                if (!SplitCommand(entry.ParquetBridgeCommand, out bridgeExe, out bridgeArgs))
                {
                    error = "Invalid bridge command";
                    return false;
                }

                string callArgs = string.Format(
                    "{0} --source-file \"{1}\" --relative-path \"{2}\" --dest \"{3}\"",
                    bridgeArgs,
                    entry.CsvFileName,
                    entry.RelativeCsvPath,
                    entry.ParquetRootDir).Trim();

                var processStart = new ProcessStartInfo
                {
                    FileName = bridgeExe,
                    Arguments = callArgs,
                    WorkingDirectory = string.IsNullOrWhiteSpace(entry.ParquetBridgeWorkingDir)
                        ? Directory.GetCurrentDirectory()
                        : entry.ParquetBridgeWorkingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using (var process = Process.Start(processStart))
                {
                    if (process == null)
                    {
                        error = "Failed to start parquet bridge process";
                        return false;
                    }

                    ApplyChildProcessThrottle(process);
                    string stdOut = process.StandardOutput.ReadToEnd();
                    string stdErr = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (token.IsCancellationRequested)
                    {
                        error = "Canceled";
                        return false;
                    }

                    if (process.ExitCode != 0)
                    {
                        error = !string.IsNullOrWhiteSpace(stdErr) ? FormatBridgeOutput(stdErr) :
                            (!string.IsNullOrWhiteSpace(stdOut) ? FormatBridgeOutput(stdOut) : string.Format("Exit code {0}", process.ExitCode));
                        return false;
                    }

                    if (!string.IsNullOrWhiteSpace(stdOut))
                        logout(string.Format("Parquet bridge output:{0}{1}",
                            Environment.NewLine,
                            FormatBridgeOutput(stdOut)));

                    if (!string.IsNullOrWhiteSpace(stdErr))
                        logout(string.Format("Parquet bridge stderr:{0}{1}",
                            Environment.NewLine,
                            FormatBridgeOutput(stdErr)));
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private void ApplyChildProcessThrottle(Process process)
        {
            if (!enableCpuThrottling || maxCpuPercent >= MAX_MAX_CPU_PERCENT || process == null)
                return;

            try
            {
                if (maxCpuPercent <= 30)
                    process.PriorityClass = ProcessPriorityClass.Idle;
                else
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // Ignore priority failures on restricted environments.
            }

            try
            {
                int cpuCount = Environment.ProcessorCount;
                if (cpuCount <= 1)
                    return;

                int targetCores = (int)Math.Ceiling(cpuCount * (maxCpuPercent / 100.0));
                if (targetCores < 1)
                    targetCores = 1;
                if (targetCores > cpuCount)
                    targetCores = cpuCount;

                if (IntPtr.Size == 4)
                {
                    int maxBits = Math.Min(targetCores, 31);
                    uint mask = 0;
                    for (int i = 0; i < maxBits; i++)
                        mask |= (uint)(1u << i);
                    process.ProcessorAffinity = new IntPtr(unchecked((int)mask));
                }
                else
                {
                    int maxBits = Math.Min(targetCores, 63);
                    ulong mask = 0;
                    for (int i = 0; i < maxBits; i++)
                        mask |= (1UL << i);
                    process.ProcessorAffinity = new IntPtr(unchecked((long)mask));
                }
            }
            catch
            {
                // Ignore affinity failures if OS/runtime blocks changes.
            }
        }

        private static string FormatBridgeOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            string trimmed = text.Trim();
            if (IsLikelyJson(trimmed))
            {
                string summary;
                if (TrySummarizeJson(trimmed, out summary))
                    return TruncateForLog(summary, 220);
            }

            return TruncateForLog(CollapseWhitespace(trimmed), 220);
        }

        private static bool IsLikelyJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            char first = text[0];
            return first == '{' || first == '[';
        }

        private static bool TrySummarizeJson(string json, out string summary)
        {
            summary = "";
            try
            {
                if (json.StartsWith("{"))
                {
                    summary = SummarizeTopLevelJsonObject(json);
                    return !string.IsNullOrWhiteSpace(summary);
                }

                if (json.StartsWith("["))
                {
                    summary = string.Format("json array ({0} item(s))", CountTopLevelArrayItems(json));
                    return true;
                }
            }
            catch
            {
                // Fall through and let caller use raw output.
            }

            return false;
        }

        private static string SummarizeTopLevelJsonObject(string json)
        {
            var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            SkipWhitespace(json, ref i);
            if (i >= json.Length || json[i] != '{')
                return "json object";
            i++;

            while (i < json.Length)
            {
                SkipWhitespace(json, ref i);
                if (i >= json.Length) break;
                if (json[i] == '}') break;
                if (json[i] == ',')
                {
                    i++;
                    continue;
                }

                string key;
                if (!ReadJsonString(json, ref i, out key))
                    break;

                SkipWhitespace(json, ref i);
                if (i >= json.Length || json[i] != ':')
                    break;
                i++;
                SkipWhitespace(json, ref i);

                string value = ReadTopLevelJsonValue(json, ref i);
                if (!pairs.ContainsKey(key))
                    pairs[key] = value;

                SkipWhitespace(json, ref i);
                if (i < json.Length && json[i] == ',')
                    i++;
            }

            string[] priorityKeys = new[]
            {
                "status", "instrument", "date", "source_file", "sourceFile",
                "relative_path", "relativePath", "output_file", "outputFile",
                "rows", "records", "processed", "duration_ms", "durationMs", "message", "error"
            };

            var parts = new List<string>();
            foreach (string key in priorityKeys)
            {
                if (!pairs.ContainsKey(key))
                    continue;

                string value = NormalizeSummaryValue(key, pairs[key]);
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                parts.Add(string.Format("{0}={1}", key, value));
                if (parts.Count >= 4)
                    break;
            }

            if (parts.Count == 0)
                return string.Format("json object ({0} field(s))", pairs.Count);

            return string.Join(", ", parts);
        }

        private static string NormalizeSummaryValue(string key, string value)
        {
            if (value == null)
                return "";

            string normalized = CollapseWhitespace(value.Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(normalized))
                return normalized;

            if (key.IndexOf("path", StringComparison.OrdinalIgnoreCase) >= 0 ||
                key.IndexOf("file", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    string name = Path.GetFileName(normalized);
                    if (!string.IsNullOrWhiteSpace(name))
                        normalized = name;
                }
                catch
                {
                    // Keep original value if path parsing fails.
                }
            }

            return TruncateForLog(normalized, 48);
        }

        private static int CountTopLevelArrayItems(string json)
        {
            int i = 0;
            SkipWhitespace(json, ref i);
            if (i >= json.Length || json[i] != '[')
                return 0;
            i++;

            int depth = 1;
            bool inString = false;
            bool escaped = false;
            int count = 0;
            bool hasValue = false;

            for (; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '"') { inString = true; hasValue = true; continue; }
                if (c == '[' || c == '{') { depth++; hasValue = true; continue; }
                if (c == ']' || c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return hasValue ? count + 1 : count;
                    continue;
                }
                if (depth == 1 && c == ',')
                {
                    count++;
                    hasValue = false;
                    continue;
                }
                if (!char.IsWhiteSpace(c) && depth == 1)
                    hasValue = true;
            }

            return hasValue ? count + 1 : count;
        }

        private static string ReadTopLevelJsonValue(string json, ref int i)
        {
            if (i >= json.Length)
                return "";

            char c = json[i];
            if (c == '"')
            {
                string s;
                return ReadJsonString(json, ref i, out s) ? s : "";
            }

            if (c == '{' || c == '[')
            {
                int depth = 0;
                bool inString = false;
                bool escaped = false;
                int start = i;
                for (; i < json.Length; i++)
                {
                    char ch = json[i];
                    if (inString)
                    {
                        if (escaped) escaped = false;
                        else if (ch == '\\') escaped = true;
                        else if (ch == '"') inString = false;
                        continue;
                    }

                    if (ch == '"') inString = true;
                    else if (ch == '{' || ch == '[') depth++;
                    else if (ch == '}' || ch == ']')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            i++;
                            break;
                        }
                    }
                }
                return json.Substring(start, Math.Max(0, i - start));
            }

            int tokenStart = i;
            while (i < json.Length && json[i] != ',' && json[i] != '}')
                i++;
            return json.Substring(tokenStart, i - tokenStart).Trim();
        }

        private static bool ReadJsonString(string json, ref int i, out string value)
        {
            value = "";
            if (i >= json.Length || json[i] != '"')
                return false;
            i++;

            var chars = new List<char>();
            bool escaped = false;
            while (i < json.Length)
            {
                char c = json[i++];
                if (escaped)
                {
                    switch (c)
                    {
                        case '"': chars.Add('"'); break;
                        case '\\': chars.Add('\\'); break;
                        case '/': chars.Add('/'); break;
                        case 'b': chars.Add('\b'); break;
                        case 'f': chars.Add('\f'); break;
                        case 'n': chars.Add('\n'); break;
                        case 'r': chars.Add('\r'); break;
                        case 't': chars.Add('\t'); break;
                        default: chars.Add(c); break;
                    }
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (c == '"')
                {
                    value = new string(chars.ToArray());
                    return true;
                }
                chars.Add(c);
            }

            return false;
        }

        private static void SkipWhitespace(string text, ref int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
        }

        private static string CollapseWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            var chars = new List<char>(text.Length);
            bool prevSpace = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    if (!prevSpace)
                        chars.Add(' ');
                    prevSpace = true;
                }
                else
                {
                    chars.Add(c);
                    prevSpace = false;
                }
            }

            return new string(chars.ToArray()).Trim();
        }

        private static string TruncateForLog(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;
            if (maxLength <= 3)
                return text.Substring(0, maxLength);
            return text.Substring(0, maxLength - 3) + "...";
        }

        private static bool SplitCommand(string command, out string exe, out string args)
        {
            exe = "";
            args = "";

            if (string.IsNullOrWhiteSpace(command))
                return false;

            string trimmed = command.Trim();
            if (trimmed.StartsWith("\""))
            {
                int end = trimmed.IndexOf('"', 1);
                if (end <= 1) return false;
                exe = trimmed.Substring(1, end - 1);
                args = trimmed.Substring(end + 1).Trim();
                return true;
            }

            int firstSpace = trimmed.IndexOf(' ');
            if (firstSpace < 0)
            {
                exe = trimmed;
                return true;
            }

            exe = trimmed.Substring(0, firstSpace);
            args = trimmed.Substring(firstSpace + 1).Trim();
            return true;
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

                    XElement elParquetRootDir = elRoot.Element("ParquetRootDir");
                    if (elParquetRootDir != null)
                        tbParquetRootDir.Text = elParquetRootDir.Value;

                    XElement elParquetBridgeCommand = elRoot.Element("ParquetBridgeCommand");
                    if (elParquetBridgeCommand != null)
                        tbParquetBridgeCommand.Text = elParquetBridgeCommand.Value;

                    XElement elParquetBridgeWorkingDir = elRoot.Element("ParquetBridgeWorkingDir");
                    if (elParquetBridgeWorkingDir != null)
                        tbParquetBridgeWorkingDir.Text = elParquetBridgeWorkingDir.Value;

                    XElement elEnableParquetPipeline = elRoot.Element("EnableParquetPipeline");
                    if (elEnableParquetPipeline != null)
                    {
                        bool enabled;
                        if (bool.TryParse(elEnableParquetPipeline.Value, out enabled))
                            cbEnableParquetPipeline.IsChecked = enabled;
                    }

                    XElement elDeleteTempCsv = elRoot.Element("DeleteTempCsvOnSuccess");
                    if (elDeleteTempCsv != null)
                    {
                        bool deleteTemp;
                        if (bool.TryParse(elDeleteTempCsv.Value, out deleteTemp))
                            cbDeleteTempCsv.IsChecked = deleteTemp;
                    }

                    XElement elEnableCpuThrottling = elRoot.Element("EnableCpuThrottling");
                    if (elEnableCpuThrottling != null)
                    {
                        bool enabled;
                        if (bool.TryParse(elEnableCpuThrottling.Value, out enabled))
                        {
                            enableCpuThrottling = enabled;
                            cbEnableCpuThrottling.IsChecked = enabled;
                        }
                    }

                    XElement elParallelThreads = elRoot.Element("ParallelThreads");
                    if (elParallelThreads != null)
                    {
                        int parsed;
                        if (int.TryParse(elParallelThreads.Value, out parsed) && parsed > 0)
                        {
                            parallelThreadsCount = parsed;
                            tbParallelThreads.Text = parsed.ToString();
                        }
                    }

                    XElement elMaxCpuPercent = elRoot.Element("MaxCpuPercent");
                    if (elMaxCpuPercent != null)
                    {
                        int parsed;
                        if (int.TryParse(elMaxCpuPercent.Value, out parsed))
                        {
                            maxCpuPercent = parsed;
                            tbMaxCpuPercent.Text = parsed.ToString();
                        }
                    }

                    UpdateCpuThrottleUiState();
                }
            }
        }

        public void Save(XDocument document, XElement element)
        {
            RefreshParallelThreadsFromUi();
            RefreshMaxCpuPercentFromUi();
            element.Elements().Where(el => el.Name.LocalName.Equals("NRDToCSV")).Remove();
            XElement elRoot = new XElement("NRDToCSV");
            XElement elCsvRootDir = new XElement("CsvRootDir", tbCsvRootDir.Text);
            XElement elSelectedPaths = new XElement("SelectedPaths");
            foreach (string path in lbSelectedPaths.Items)
            {
                elSelectedPaths.Add(new XElement("Path", path));
            }
            XElement elParquetRootDir = new XElement("ParquetRootDir", tbParquetRootDir.Text);
            XElement elParquetBridgeCommand = new XElement("ParquetBridgeCommand", tbParquetBridgeCommand.Text);
            XElement elParquetBridgeWorkingDir = new XElement("ParquetBridgeWorkingDir", tbParquetBridgeWorkingDir.Text);
            XElement elEnableParquetPipeline = new XElement("EnableParquetPipeline", cbEnableParquetPipeline.IsChecked == true);
            XElement elDeleteTempCsv = new XElement("DeleteTempCsvOnSuccess", cbDeleteTempCsv.IsChecked == true);
            XElement elEnableCpuThrottling = new XElement("EnableCpuThrottling", cbEnableCpuThrottling.IsChecked == true);
            XElement elParallelThreads = new XElement("ParallelThreads", parallelThreadsCount.ToString());
            XElement elMaxCpuPercent = new XElement("MaxCpuPercent", maxCpuPercent.ToString());
            elRoot.Add(elCsvRootDir);
            elRoot.Add(elSelectedPaths);
            elRoot.Add(elParquetRootDir);
            elRoot.Add(elParquetBridgeCommand);
            elRoot.Add(elParquetBridgeWorkingDir);
            elRoot.Add(elEnableParquetPipeline);
            elRoot.Add(elDeleteTempCsv);
            elRoot.Add(elEnableCpuThrottling);
            elRoot.Add(elParallelThreads);
            elRoot.Add(elMaxCpuPercent);
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
                cbEnableParquetPipeline.IsEnabled = false;
                cbDeleteTempCsv.IsEnabled = false;
                cbEnableCpuThrottling.IsEnabled = false;
                tbParquetRootDir.IsReadOnly = true;
                tbParquetBridgeCommand.IsReadOnly = true;
                tbParquetBridgeWorkingDir.IsReadOnly = true;
                tbParallelThreads.IsReadOnly = true;
                tbMaxCpuPercent.IsReadOnly = true;
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
                    cbEnableParquetPipeline.IsEnabled = true;
                    cbDeleteTempCsv.IsEnabled = true;
                    cbEnableCpuThrottling.IsEnabled = true;
                    tbParquetRootDir.IsReadOnly = false;
                    tbParquetBridgeCommand.IsReadOnly = false;
                    tbParquetBridgeWorkingDir.IsReadOnly = false;
                    tbParallelThreads.IsReadOnly = false;
                    tbMaxCpuPercent.IsReadOnly = false;
                    UpdateCpuThrottleUiState();
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
                cbEnableParquetPipeline.IsEnabled = false;
                cbDeleteTempCsv.IsEnabled = false;
                cbEnableCpuThrottling.IsEnabled = false;
                tbParquetRootDir.IsReadOnly = true;
                tbParquetBridgeCommand.IsReadOnly = true;
                tbParquetBridgeWorkingDir.IsReadOnly = true;
                tbParallelThreads.IsReadOnly = true;
                tbMaxCpuPercent.IsReadOnly = true;
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
                cbEnableParquetPipeline.IsEnabled = true;
                cbDeleteTempCsv.IsEnabled = true;
                cbEnableCpuThrottling.IsEnabled = true;
                tbParquetRootDir.IsReadOnly = false;
                tbParquetBridgeCommand.IsReadOnly = false;
                tbParquetBridgeWorkingDir.IsReadOnly = false;
                tbParallelThreads.IsReadOnly = false;
                tbMaxCpuPercent.IsReadOnly = false;
                UpdateCpuThrottleUiState();
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
        public bool UseParquetPipeline { get; set; }
        public string ParquetRootDir { get; set; }
        public string ParquetBridgeCommand { get; set; }
        public string ParquetBridgeWorkingDir { get; set; }
        public bool DeleteTempCsvOnSuccess { get; set; }
        public string RelativeCsvPath { get; set; }
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
        public string ParquetRootDir { get; set; }
        public string ParquetBridgeCommand { get; set; }
        public string ParquetBridgeWorkingDir { get; set; }
        public bool? ForceExport { get; set; }
        public bool? EnableParquetPipeline { get; set; }
        public bool? DeleteTempCsvOnSuccess { get; set; }
        public bool? EnableCpuThrottling { get; set; }
        public int? ParallelThreads { get; set; }
        public int? MaxCpuPercent { get; set; }
        public List<string> SelectedPaths { get; set; } = new List<string>();

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
                SelectedPaths.Clear();
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
                        if (line.StartsWith("#PARQUETROOT:"))
                        {
                            ParquetRootDir = line.Substring(13).Trim();
                            continue;
                        }
                        if (line.StartsWith("#BRIDGECOMMAND:"))
                        {
                            ParquetBridgeCommand = line.Substring(15).Trim();
                            continue;
                        }
                        if (line.StartsWith("#BRIDGEWORKDIR:"))
                        {
                            ParquetBridgeWorkingDir = line.Substring(15).Trim();
                            continue;
                        }
                        if (line.StartsWith("#FORCEEXPORT:"))
                        {
                            bool parsed;
                            if (bool.TryParse(line.Substring(13).Trim(), out parsed))
                                ForceExport = parsed;
                            continue;
                        }
                        if (line.StartsWith("#ENABLEPARQUETPIPELINE:"))
                        {
                            bool parsed;
                            if (bool.TryParse(line.Substring(23).Trim(), out parsed))
                                EnableParquetPipeline = parsed;
                            continue;
                        }
                        if (line.StartsWith("#DELETETEMPCSV:"))
                        {
                            bool parsed;
                            if (bool.TryParse(line.Substring(14).Trim(), out parsed))
                                DeleteTempCsvOnSuccess = parsed;
                            continue;
                        }
                        if (line.StartsWith("#ENABLECPUTHROTTLING:"))
                        {
                            bool parsed;
                            if (bool.TryParse(line.Substring(21).Trim(), out parsed))
                                EnableCpuThrottling = parsed;
                            continue;
                        }
                        if (line.StartsWith("#PARALLELTHREADS:"))
                        {
                            int parsed;
                            if (int.TryParse(line.Substring(17).Trim(), out parsed) && parsed > 0)
                                ParallelThreads = parsed;
                            continue;
                        }
                        if (line.StartsWith("#MAXCPUPERCENT:"))
                        {
                            int parsed;
                            if (int.TryParse(line.Substring(15).Trim(), out parsed))
                                MaxCpuPercent = parsed;
                            continue;
                        }
                        if (line.StartsWith("#SELECTEDPATH:"))
                        {
                            string selectedPath = line.Substring(14).Trim();
                            if (!string.IsNullOrWhiteSpace(selectedPath))
                                SelectedPaths.Add(selectedPath);
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
                        if (!string.IsNullOrEmpty(ParquetRootDir))
                            writer.WriteLine("#PARQUETROOT:" + ParquetRootDir);
                        if (!string.IsNullOrEmpty(ParquetBridgeCommand))
                            writer.WriteLine("#BRIDGECOMMAND:" + ParquetBridgeCommand);
                        if (!string.IsNullOrEmpty(ParquetBridgeWorkingDir))
                            writer.WriteLine("#BRIDGEWORKDIR:" + ParquetBridgeWorkingDir);
                        if (ForceExport.HasValue)
                            writer.WriteLine("#FORCEEXPORT:" + ForceExport.Value.ToString().ToLowerInvariant());
                        if (EnableParquetPipeline.HasValue)
                            writer.WriteLine("#ENABLEPARQUETPIPELINE:" + EnableParquetPipeline.Value.ToString().ToLowerInvariant());
                        if (DeleteTempCsvOnSuccess.HasValue)
                            writer.WriteLine("#DELETETEMPCSV:" + DeleteTempCsvOnSuccess.Value.ToString().ToLowerInvariant());
                        if (EnableCpuThrottling.HasValue)
                            writer.WriteLine("#ENABLECPUTHROTTLING:" + EnableCpuThrottling.Value.ToString().ToLowerInvariant());
                        if (ParallelThreads.HasValue && ParallelThreads.Value > 0)
                            writer.WriteLine("#PARALLELTHREADS:" + ParallelThreads.Value);
                        if (MaxCpuPercent.HasValue)
                            writer.WriteLine("#MAXCPUPERCENT:" + MaxCpuPercent.Value);
                        foreach (string selectedPath in SelectedPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct())
                            writer.WriteLine("#SELECTEDPATH:" + selectedPath);
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
