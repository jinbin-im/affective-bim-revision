// Suppress IDE style suggestions (Roslyn suggestions, not build errors)
#pragma warning disable IDE0017
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ALIS
{
    public partial class MyWindow : Window
    {
        private readonly ExternalEvent _exEvent;
        private readonly MyEventHandler _handler;

        // ================================================================
        // Paths are resolved from the DLL location
        // Layout: ALIS/ALIS/  <- ProjectRoot
        //         ├── bin/Debug/ALIS.dll
        //         ├── py/
        //         ├── Results/
        //         └── materials/
        // ================================================================
        internal static readonly string ProjectRoot = ResolveProjectRoot();
        private static readonly string PYTHON_PATH = FindPython();
        private static readonly string BERT_SCRIPT_PATH = System.IO.Path.Combine(ProjectRoot, "py", "NLP.py");
        private static readonly string AFFECTIVE_SCRIPT_PATH = System.IO.Path.Combine(ProjectRoot, "py", "Inference.py");
        private static readonly string OPTIMIZATION_SCRIPT_PATH = System.IO.Path.Combine(ProjectRoot, "py", "Optimizer.py");
        private static readonly string RENDER_SCRIPT_PATH = System.IO.Path.Combine(ProjectRoot, "py", "Rendering.py");

        /// <summary>
        /// Finds the project root (the folder that holds py/, materials/ and Results/).
        ///
        /// Order:
        ///   1) Environment variable ALIS_PROJECT_ROOT (recommended for deployment)
        ///   2) A folder with py/ one to five levels above the DLL folder (leaves bin/Debug)
        ///   3) [CallerFilePath]: source path on the build machine (development fallback)
        /// </summary>
        private static string ResolveProjectRoot()
        {
            // 1) Environment variable first; on another PC, setting ALIS_PROJECT_ROOT is enough.
            string envRoot = Environment.GetEnvironmentVariable("ALIS_PROJECT_ROOT");
            if (!string.IsNullOrWhiteSpace(envRoot) &&
                Directory.Exists(System.IO.Path.Combine(envRoot, "py")))
            {
                System.Diagnostics.Debug.WriteLine($"[ALIS] ProjectRoot from env: {envRoot}");
                return envRoot;
            }

            // 2) Search upward from the assembly location.
            string dllDir = null;
            try
            {
                dllDir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                string current = dllDir;
                for (int i = 0; i < 5 && current != null; i++)
                {
                    if (Directory.Exists(System.IO.Path.Combine(current, "py")))
                    {
                        System.Diagnostics.Debug.WriteLine($"[ALIS] ProjectRoot from DLL: {current}");
                        return current;
                    }
                    current = System.IO.Path.GetDirectoryName(current);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ALIS] DLL path search failed: {ex.Message}");
            }

            // 3) [CallerFilePath]: source path at build time (development fallback).
            string sourceDir = GetSourceFileDir();
            if (sourceDir != null && Directory.Exists(System.IO.Path.Combine(sourceDir, "py")))
            {
                System.Diagnostics.Debug.WriteLine($"[ALIS] ProjectRoot from source: {sourceDir}");
                return sourceDir;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[ALIS] WARNING: py/ not found. env={envRoot}, dllDir={dllDir}, sourceDir={sourceDir}");
            return sourceDir ?? dllDir;
        }

        /// <summary>
        /// Returns the source file directory at compile time.
        /// [CallerFilePath] injects the absolute path of this .cs file as a constant at build time.
        /// This gives the original project path regardless of Revit shadow copies.
        /// </summary>
        private static string GetSourceFileDir(
            [System.Runtime.CompilerServices.CallerFilePath] string path = "")
        {
            return System.IO.Path.GetDirectoryName(path);
        }

        /// <summary>
        /// Finds the Python executable.
        ///
        /// Order:
        ///   1) Environment variable ALIS_PYTHON (absolute path)
        ///   2) UserProfile\anaconda3\python.exe / miniconda3\python.exe
        ///   3) ProgramData\Anaconda3\python.exe
        ///   4) First python.exe on PATH
        ///   If none is found, returns an empty string.
        /// </summary>
        private static string FindPython()
        {
            // 1) Environment variable
            string envPy = Environment.GetEnvironmentVariable("ALIS_PYTHON");
            if (!string.IsNullOrWhiteSpace(envPy) && File.Exists(envPy))
            {
                System.Diagnostics.Debug.WriteLine($"[ALIS] Python from env: {envPy}");
                return envPy;
            }

            // 2-3) Common Anaconda/Miniconda locations
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new[]
            {
                System.IO.Path.Combine(userProfile, "anaconda3", "python.exe"),
                System.IO.Path.Combine(userProfile, "miniconda3", "python.exe"),
                @"C:\ProgramData\Anaconda3\python.exe",
                @"C:\ProgramData\Miniconda3\python.exe",
            };
            foreach (var p in candidates)
                if (File.Exists(p))
                {
                    System.Diagnostics.Debug.WriteLine($"[ALIS] Python from candidate: {p}");
                    return p;
                }

            // 4) Search PATH
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathEnv.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate = System.IO.Path.Combine(dir.Trim(), "python.exe");
                if (File.Exists(candidate))
                {
                    System.Diagnostics.Debug.WriteLine($"[ALIS] Python from PATH: {candidate}");
                    return candidate;
                }
            }

            // Not found: return an empty string so the caller fails explicitly.
            System.Diagnostics.Debug.WriteLine(
                "[ALIS] WARNING: python.exe not found. ALIS_PYTHON Please set environment variable.");
            return "";
        }
        // ALIS session manager instance
        private readonly ALISSessionManager _session = new ALISSessionManager();
        // Current feedback/target (for Rn snapshots)
        private string _lastFeedbackPrompt = "";

        private List<string> _beforeLabels = null;
        private List<double> _beforeScores = null;
        private List<string> _afterLabels = null;
        private List<double> _afterScores = null;
        private string _targetEmotion = null;

        public MyWindow(ExternalEvent exEvent, MyEventHandler handler)
        {
            InitializeComponent();
            _exEvent = exEvent;
            _handler = handler;

            // Action buttons are always enabled.
            SetActionButtonsEnabled(true);
        }

        // ========================================================================
        // Image loading methods
        // ========================================================================
        public void LoadImagesFromTemp()
        {
            // Images are loaded in FullAnalysisButton_Click after rendering finishes
            // Only the chart is loaded here (called right after BIM analysis, before rendering)
            try
            {
                LoadBeforeChart(null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadImagesFromTemp] error: {ex.Message}");
            }
        }

        public void LoadAfterImagesOnly()
        {
            try
            {
                SetAfterImage(null);
                LoadAfterChart(null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadAfterImagesOnly] error: {ex.Message}");
            }
        }

        // Loads the Before image (specificPath if given, otherwise the latest file)
        private void SetBeforeImage(string specificPath)
        {
            try
            {
                string tempFolder = @"C:\Temp";
                string imagePath = specificPath;
                if (string.IsNullOrEmpty(imagePath))
                {
                    // Skip staged files; newest file first
                    var files = System.IO.Directory.Exists(tempFolder)
                        ? System.IO.Directory.GetFiles(tempFolder, "rendering_before_*.png")
                            .Where(f => !f.Contains("staged"))
                            .OrderByDescending(f => System.IO.File.GetLastWriteTime(f)).ToList()
                        : new System.Collections.Generic.List<string>();
                    if (files.Any()) imagePath = files.First();
                    // No fallback: only timestamped files are used
                }
                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                    LoadBeforeImage(imagePath);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SetBeforeImage] " + ex.Message); }
        }

        private void SetAfterImage(string specificPath)
        {
            try
            {
                string tempFolder = @"C:\Temp";
                string imagePath = specificPath;
                if (string.IsNullOrEmpty(imagePath))
                {
                    // Skip staged files; newest file first
                    var files = System.IO.Directory.Exists(tempFolder)
                        ? System.IO.Directory.GetFiles(tempFolder, "rendering_after_*.png")
                            .Where(f => !f.Contains("staged"))
                            .OrderByDescending(f => System.IO.File.GetLastWriteTime(f)).ToList()
                        : new System.Collections.Generic.List<string>();
                    if (files.Any()) imagePath = files.First();
                    // No fallback: nothing is shown if there is no After file
                }
                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                    LoadAfterImage(imagePath);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SetAfterImage] " + ex.Message); }
        }

        private void LoadBeforeImage(string imagePath)
        {
            try
            {
                if (File.Exists(imagePath))
                {
                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    BeforeImage.Source = bitmap;
                    BeforeImage.Visibility = Visibility.Visible;
                    BeforePlaceholder.Visibility = Visibility.Collapsed;
                    if (this.FindName("SelectBeforeButton") is Button selBefore) selBefore.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex) { MessageBox.Show($"Before image load failed: {ex.Message}\nPath: {imagePath}"); }
        }

        private void LoadAfterImage(string imagePath)
        {
            try
            {
                if (File.Exists(imagePath))
                {
                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    AfterImage.Source = bitmap;
                    AfterImage.Visibility = Visibility.Visible;
                    AfterPlaceholder.Visibility = Visibility.Collapsed;
                    if (this.FindName("SelectAfterButton") is Button selAfter) selAfter.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex) { MessageBox.Show($"After image load failed: {ex.Message}\nPath: {imagePath}"); }
        }

        // ========================================================================
        // Chart image loading
        // ========================================================================
        private void LoadBeforeChart(string _)
        {
            try
            {
                string jsonPath = @"C:\Temp\emotion_scores.json";
                if (!File.Exists(jsonPath)) return;
                ParseEmotionJson(jsonPath, out List<string> labels, out List<double> scores);
                if (labels == null || labels.Count == 0) return;
                Dispatcher.Invoke(() =>
                {
                    _beforeLabels = labels; _beforeScores = scores;
                    try { string tf = @"C:\Temp\target_emotion.txt"; if (File.Exists(tf)) _targetEmotion = File.ReadAllText(tf, System.Text.Encoding.UTF8).Trim(); } catch { }
                    DrawCombinedChart(BeforeChartCanvas, _beforeLabels, _beforeScores, _afterLabels, _afterScores, _targetEmotion);
                    BeforeChartScroll.Visibility = Visibility.Visible;
                    BeforeChartPlaceholder.Visibility = Visibility.Collapsed;
                });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[LoadBeforeChart] " + ex.Message); }
        }


        private void LoadAfterChart(string _)
        {
            try
            {
                string jsonPath = @"C:\Temp\emotion_scores_after.json";
                if (!File.Exists(jsonPath)) jsonPath = @"C:\Temp\emotion_scores.json";
                if (!File.Exists(jsonPath)) return;
                ParseEmotionJson(jsonPath, out List<string> labels, out List<double> scores);
                if (labels == null || labels.Count == 0) return;
                Dispatcher.Invoke(() =>
                {
                    _afterLabels = labels; _afterScores = scores;
                    try { string tf = @"C:\Temp\target_emotion.txt"; if (File.Exists(tf)) _targetEmotion = File.ReadAllText(tf, System.Text.Encoding.UTF8).Trim(); } catch { }
                    DrawCombinedChart(BeforeChartCanvas, _beforeLabels, _beforeScores, _afterLabels, _afterScores, _targetEmotion);
                    BeforeChartScroll.Visibility = Visibility.Visible;
                    BeforeChartPlaceholder.Visibility = Visibility.Collapsed;
                });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[LoadAfterChart] " + ex.Message); }
        }


        private void ParseEmotionJson(string jsonFilePath, out List<string> labels, out List<double> scores)
        {
            labels = new List<string>();
            scores = new List<double>();
            try
            {
                string json = File.ReadAllText(jsonFilePath, Encoding.UTF8);
                int labelsStart = json.IndexOf("\"labels\"") + 8;
                int labelsArrStart = json.IndexOf('[', labelsStart) + 1;
                int labelsArrEnd = json.IndexOf(']', labelsArrStart);
                string labelsStr = json.Substring(labelsArrStart, labelsArrEnd - labelsArrStart);
                foreach (var raw in labelsStr.Split(','))
                {
                    string lbl = raw.Trim().Trim('"');
                    if (!string.IsNullOrEmpty(lbl)) labels.Add(lbl);
                }
                int scoresStart = json.IndexOf("\"scores\"") + 8;
                int scoresArrStart = json.IndexOf('[', scoresStart) + 1;
                int scoresArrEnd = json.IndexOf(']', scoresArrStart);
                string scoresStr = json.Substring(scoresArrStart, scoresArrEnd - scoresArrStart);
                foreach (var raw in scoresStr.Split(','))
                {
                    if (double.TryParse(raw.Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double val))
                        scores.Add(val);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ParseEmotionJson] error: " + ex.Message);
                labels = null; scores = null;
            }
        }


        private void DrawCombinedChart(Canvas canvas,
            List<string> bLabels, List<double> bScores,
            List<string> aLabels, List<double> aScores,
            string targetEmotion = null)
        {
            canvas.Children.Clear();
            if ((bLabels == null || bLabels.Count == 0) && (aLabels != null && aLabels.Count > 0))
            {
                bLabels = aLabels; bScores = aScores; aLabels = null; aScores = null;
            }
            if (bLabels == null || bLabels.Count == 0) return;
            bool hasAfter = (aLabels != null && aScores != null && aLabels.Count > 0);

            var blue = new SolidColorBrush(Color.FromRgb(52, 152, 219));
            var green = new SolidColorBrush(Color.FromRgb(39, 174, 96));
            var orange = new SolidColorBrush(Color.FromRgb(230, 100, 20));
            var dark = new SolidColorBrush(Color.FromRgb(50, 50, 50));

            int n = bLabels.Count;
            double lblW = 90;
            double cW = canvas.Width > 0 ? canvas.Width : 420;
            double barMaxW = Math.Max(cW - lblW - 12, 180.0);
            double rowH = hasAfter ? 28 : 20;
            double barH = hasAfter ? 9 : 12;
            double gap = 2, topPad = hasAfter ? 18 : 8;
            canvas.Height = topPad + n * rowH + 8;

            // Axis range: Before and After each use their own scale
            double bAxisMax = bScores.Count > 0 ? bScores.Max() : 1.0;
            if (bAxisMax < 1e-6) bAxisMax = 1.0;
            double aAxisMax = (hasAfter && aScores != null && aScores.Count > 0) ? aScores.Max() : 1.0;
            if (aAxisMax < 1e-6) aAxisMax = 1.0;

            // Legend
            if (hasAfter)
            {
                var bBox = new Rectangle { Width = 10, Height = 8, Fill = blue, RadiusX = 1, RadiusY = 1 };
                Canvas.SetLeft(bBox, lblW); Canvas.SetTop(bBox, 3); canvas.Children.Add(bBox);
                var bTxt = new TextBlock { Text = "Before", FontSize = 8, Foreground = blue };
                Canvas.SetLeft(bTxt, lblW + 13); Canvas.SetTop(bTxt, 2); canvas.Children.Add(bTxt);
                var aBox = new Rectangle { Width = 10, Height = 8, Fill = green, RadiusX = 1, RadiusY = 1 };
                Canvas.SetLeft(aBox, lblW + 58); Canvas.SetTop(aBox, 3); canvas.Children.Add(aBox);
                var aTxt = new TextBlock { Text = "After", FontSize = 8, Foreground = green };
                Canvas.SetLeft(aTxt, lblW + 71); Canvas.SetTop(aTxt, 2); canvas.Children.Add(aTxt);
            }

            for (int i = 0; i < n; i++)
            {
                double y = topPad + i * rowH;
                bool isTgt = !string.IsNullOrEmpty(targetEmotion) &&
                    string.Equals(bLabels[i].Trim(), targetEmotion.Trim(), StringComparison.OrdinalIgnoreCase);

                // Target highlight
                if (isTgt)
                {
                    var hl = new Rectangle
                    {
                        Width = lblW + barMaxW + 8,
                        Height = rowH - 2,
                        Fill = new SolidColorBrush(Color.FromArgb(45, 255, 200, 0)),
                        RadiusX = 3,
                        RadiusY = 3
                    };
                    Canvas.SetLeft(hl, 0); Canvas.SetTop(hl, y + 1); canvas.Children.Add(hl);
                }

                // Labels
                var lbl = new TextBlock
                {
                    Text = bLabels[i],
                    FontSize = isTgt ? 11 : 9,
                    FontWeight = isTgt ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = isTgt ? new SolidColorBrush(Color.FromRgb(180, 60, 0)) : dark,
                    Width = lblW - 4,
                    TextAlignment = TextAlignment.Right
                };
                Canvas.SetLeft(lbl, 0); Canvas.SetTop(lbl, y + (rowH - 11) / 2.0); canvas.Children.Add(lbl);

                // Track background
                double totalH = hasAfter ? barH * 2 + gap : barH;
                double trackTop = y + (rowH - totalH) / 2.0;
                var track = new Rectangle
                {
                    Width = barMaxW,
                    Height = totalH,
                    Fill = new SolidColorBrush(Color.FromArgb(25, 180, 180, 180)),
                    RadiusX = 2,
                    RadiusY = 2
                };
                Canvas.SetLeft(track, lblW); Canvas.SetTop(track, trackTop); canvas.Children.Add(track);

                // Before bar: Before scale
                double bW = Math.Max(2, barMaxW * (bScores[i] / bAxisMax));
                var bBar = new Rectangle { Width = bW, Height = barH, Fill = blue, RadiusX = 2, RadiusY = 2 };
                Canvas.SetLeft(bBar, lblW); Canvas.SetTop(bBar, trackTop); canvas.Children.Add(bBar);

                // After bar: After scale (independent)
                if (hasAfter)
                {
                    double aW = Math.Max(2, barMaxW * (aScores[i] / aAxisMax));
                    double ah = isTgt ? barH + 2 : barH;
                    var aFill = isTgt ? orange : green;
                    var aBar = new Rectangle { Width = aW, Height = ah, Fill = aFill, RadiusX = 2, RadiusY = 2 };
                    Canvas.SetLeft(aBar, lblW); Canvas.SetTop(aBar, trackTop + barH + gap - (isTgt ? 1 : 0)); canvas.Children.Add(aBar);
                }

                // Target star
                if (isTgt)
                {
                    var star = new TextBlock
                    {
                        Text = "★",
                        FontSize = 11,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(200, 80, 0))
                    };
                    Canvas.SetLeft(star, lblW + barMaxW + 2); Canvas.SetTop(star, y + (rowH - 13) / 2.0); canvas.Children.Add(star);
                }
            }
        }






        /// <summary>
        /// Creates the empty and staged renderings in sequence in one Python process.
        /// The await ends when the "empty" field of `rendering_status_{mode}.json` is filled.
        /// The same process keeps creating the staged image in the background; the UI does not wait.
        /// </summary>
        private async Task RunGenerateRenderingAsync(string mode, Action<string> onProgress = null)
        {
            if (!File.Exists(RENDER_SCRIPT_PATH))
            {
                System.Diagnostics.Debug.WriteLine($"[Rendering] script missing: {RENDER_SCRIPT_PATH}");
                Dispatcher.Invoke(() => MessageBox.Show(
                    $"Rendering script not found:\n{RENDER_SCRIPT_PATH}\n\n" +
                    "ProjectRoot is incorrect or an outdated dll is loaded.\n" +
                    "Check C:\\Temp\\path_debug.txt",
                    "Rendering Stage Failed", MessageBoxButton.OK, MessageBoxImage.Warning));
                return;
            }
            if (!File.Exists(PYTHON_PATH))
            {
                Dispatcher.Invoke(() => MessageBox.Show(
                    $"Python not found:\n{PYTHON_PATH}\nSet ALIS_PYTHON environment variable.",
                    "Rendering Stage Failed", MessageBoxButton.OK, MessageBoxImage.Warning));
                return;
            }

            // One status file per mode, so before and after runs never share a file.
            // (If a before/staged process is still running when after starts,
            //  both would write the same status.json and polling could match the wrong run.)
            string statusPath = $@"C:\Temp\rendering_status_{mode}.json";
            try { if (File.Exists(statusPath)) File.Delete(statusPath); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Rendering] previous status delete failed: {ex.Message}"); }

            // Prefix of the empty PNG; only paths with this prefix are valid (checks the mode).
            string expectedPrefix = $"rendering_{mode}_";

            var psi = new ProcessStartInfo
            {
                FileName = PYTHON_PATH,
                Arguments = $"\"{RENDER_SCRIPT_PATH}\" --mode {mode}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            Process proc;
            try
            {
                proc = Process.Start(psi);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Rendering] process start failed: {ex.Message}");
                return;
            }

            // Read stdout/stderr asynchronously (avoids a deadlock when the OS buffer fills)
            _ = Task.Run(() => { try { proc.StandardOutput.ReadToEnd(); } catch { } });
            _ = Task.Run(() =>
            {
                try
                {
                    string err = proc.StandardError.ReadToEnd();
                    if (!string.IsNullOrEmpty(err))
                        System.Diagnostics.Debug.WriteLine($"[Rendering:{mode}] stderr: {err}");
                }
                catch { }
            });

            // Poll status.json until the "empty" field is filled (up to 10 minutes).
            // The first Gemini API call after a cold start can take 4-5 minutes, so the limit is generous.
            // Warm calls finish in 30 s to 1 min. Override with environment variable ALIS_RENDER_EMPTY_TIMEOUT_S.
            int emptyTimeoutS = 600;
            try {
                string ev = Environment.GetEnvironmentVariable("ALIS_RENDER_EMPTY_TIMEOUT_S");
                if (!string.IsNullOrEmpty(ev) && int.TryParse(ev, out int parsed) && parsed > 0)
                    emptyTimeoutS = parsed;
            } catch { }

            DateTime deadline = DateTime.Now.AddSeconds(emptyTimeoutS);
            bool emptyDetected = false;
            int processExitCode = -1;
            string lastDisplayedStatus = null;
            while (DateTime.Now < deadline)
            {
                await Task.Delay(250);

                if (proc.HasExited)
                {
                    processExitCode = proc.ExitCode;
                    System.Diagnostics.Debug.WriteLine(
                        $"[Rendering:{mode}] process exited early: exit={processExitCode}");
                    break;
                }

                if (File.Exists(statusPath))
                {
                    try
                    {
                        string json = File.ReadAllText(statusPath, Encoding.UTF8);

                        // ── Show progress: lock_waiting / rendering_progress ──
                        if (onProgress != null)
                        {
                            string newDisplay = null;
                            var lockMatch = System.Text.RegularExpressions.Regex.Match(
                                json, "\"lock_waiting\"\\s*:\\s*\\{\\s*\"other_pid\"\\s*:\\s*(\\d+)");
                            if (lockMatch.Success)
                            {
                                newDisplay = $"Another ALIS Python (PID {lockMatch.Groups[1].Value}) running — waiting...";
                            }
                            else
                            {
                                var attMatch = System.Text.RegularExpressions.Regex.Match(
                                    json,
                                    "\"rendering_progress\"\\s*:\\s*\\{[^}]*\"stage\"\\s*:\\s*\"([^\"]+)\"[^}]*\"attempt\"\\s*:\\s*(\\d+)[^}]*\"max_retries\"\\s*:\\s*(\\d+)");
                                if (attMatch.Success)
                                {
                                    string stage = attMatch.Groups[1].Value;
                                    string att = attMatch.Groups[2].Value;
                                    string max = attMatch.Groups[3].Value;
                                    var waitMatch = System.Text.RegularExpressions.Regex.Match(
                                        json, "\"next_wait_s\"\\s*:\\s*(\\d+)");
                                    string suffix = waitMatch.Success ? $" — {waitMatch.Groups[1].Value}s wait before retry" : "";
                                    newDisplay = $"Generating {mode} rendering... ({stage}, attempt {att}/{max}){suffix}";
                                }
                            }
                            if (newDisplay != null && newDisplay != lastDisplayedStatus)
                            {
                                lastDisplayedStatus = newDisplay;
                                try { onProgress(newDisplay); } catch { }
                            }
                        }

                        // Extract "empty": "<path>".
                        var em = System.Text.RegularExpressions.Regex.Match(
                            json, "\"empty\"\\s*:\\s*\"([^\"]+)\"");
                        if (!em.Success) continue;

                        string emptyFile = em.Groups[1].Value.Replace("\\\\", "\\");
                        string baseName = System.IO.Path.GetFileName(emptyFile);
                        if (!baseName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[Rendering:{mode}] empty path in status does not match expected prefix({expectedPrefix})  mismatch: {baseName} — ignored");
                            continue;
                        }

                        if (!File.Exists(emptyFile))
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[Rendering:{mode}] empty path in status but file missing: {emptyFile} — recheck next tick");
                            continue;
                        }

                        emptyDetected = true;
                        System.Diagnostics.Debug.WriteLine(
                            $"[Rendering:{mode}] empty detected: {baseName} → closing UI progress window");
                        break;
                    }
                    catch { /* The file may still be being written; retry on the next tick */ }
                }
            }

            if (!emptyDetected)
            {
                if (proc.HasExited && processExitCode != 0)
                {
                    Dispatcher.Invoke(() => MessageBox.Show(
                        $"Rendering process terminated before empty completed.\n" +
                        $"exit code: {processExitCode}\n\n" +
                        "Check Visual Studio Output window for [Rendering:* stderr] messages.\n" +
                        "Verify GEMINI_API_KEY environment variable is set.",
                        $"Rendering ({mode}) Failed", MessageBoxButton.OK, MessageBoxImage.Warning));
                }
                else if (!proc.HasExited)
                {
                    Dispatcher.Invoke(() => MessageBox.Show(
                        $"Rendering polling did not receive empty within {emptyTimeoutS}s.\n" +
                        "It may still be progressing in the background; PNG may appear in C:\\Temp soon.\n\n" +
                        "Cold start can take 4+ minutes. Environment variables:\n" +
                        "  ALIS_RENDER_EMPTY_TIMEOUT_S=900 (15 min)\n" +
                        "  ALIS_RENDER_WARMUP=1 (enable warm-up)\n" +
                        "Try these.",
                        $"Rendering ({mode}) Polling Timeout", MessageBoxButton.OK, MessageBoxImage.Information));
                }
            }

            // The same process continues with staged rendering (fire-and-forget).
            // Wait for exit in the background and only log the status (no UI effect).
            _ = Task.Run(() =>
            {
                try
                {
                    proc.WaitForExit();
                    System.Diagnostics.Debug.WriteLine(
                        $"[Rendering:{mode}] process final exit: exit={proc.ExitCode}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Rendering:{mode}] WaitForExit error: {ex.Message}");
                }
                finally
                {
                    try { proc.Dispose(); } catch { }
                }
            });
        }

        // ========================================================================
        // 1. Analysis button (FullAnalysis)
        // ========================================================================
        private async void FullAnalysisButton_Click(object sender, RoutedEventArgs e)
        {
            var progress = new ProgressWindow("Analyzing...", "Analyzing BIM data...");
            progress.Owner = this;
            IsEnabled = false;
            progress.Show();
            try
            {
                string currentAnalysis = @"C:\Temp\current_analysis.txt";
                if (File.Exists(currentAnalysis)) File.Delete(currentAnalysis);
                _handler.RequestId = MyEventHandler.EventID.FullAnalysis;
                _handler.AppWindow = this;
                // Start session (creates Results/ALIS_<user>_<timestamp>/R0)
                _session.StartSession();

                _exEvent.Raise();
                // Wait for BIM analysis (file check, up to 30 s)
                progress.UpdateStatus("Analyzing BIM data...");
                for (int i = 0; i < 60; i++) { await Task.Delay(500); if (File.Exists(currentAnalysis)) break; }
                if (File.Exists(currentAnalysis)) UpdateBeforeOptions(currentAnalysis);
                // Affective analysis -> creates emotion_scores.json
                progress.UpdateStatus("Affective inference... (Transformer)");

                // [DEBUG] Path diagnostics; see C:\Temp\path_debug.txt if something fails
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    string debugLog = $"=== ALIS Path Debug ({DateTime.Now:HH:mm:ss}) ===\r\n" +
                        $"Assembly.Location: {asm.Location}\r\n" +
                        $"CallerFilePath dir: {GetSourceFileDir()}\r\n" +
                        $"ProjectRoot: {ProjectRoot}\r\n" +
                        $"py/ exists: {Directory.Exists(System.IO.Path.Combine(ProjectRoot, "py"))}\r\n\r\n" +
                        $"PYTHON: {PYTHON_PATH}  exists: {File.Exists(PYTHON_PATH)}\r\n" +
                        $"AFFECTIVE: {AFFECTIVE_SCRIPT_PATH}  exists: {File.Exists(AFFECTIVE_SCRIPT_PATH)}\r\n" +
                        $"OPTIMIZATION: {OPTIMIZATION_SCRIPT_PATH}  exists: {File.Exists(OPTIMIZATION_SCRIPT_PATH)}\r\n" +
                        $"RENDER: {RENDER_SCRIPT_PATH}  exists: {File.Exists(RENDER_SCRIPT_PATH)}\r\n" +
                        $"BERT: {BERT_SCRIPT_PATH}  exists: {File.Exists(BERT_SCRIPT_PATH)}\r\n";
                    File.WriteAllText(@"C:\Temp\path_debug.txt", debugLog);
                }
                catch { }

                string affectiveError = null;
                await Task.Run(() => {
                    try
                    {
                        if (!File.Exists(AFFECTIVE_SCRIPT_PATH))
                        {
                            affectiveError = $"Inference script not found:\n{AFFECTIVE_SCRIPT_PATH}\n\n" +
                                             "ProjectRoot is incorrect or an outdated dll is loaded.\n" +
                                             "Check C:\\Temp\\path_debug.txt";
                            return;
                        }
                        if (!File.Exists(PYTHON_PATH))
                        {
                            affectiveError = $"Python not found:\n{PYTHON_PATH}\nSet ALIS_PYTHON environment variable.";
                            return;
                        }
                        var psi = new ProcessStartInfo
                        {
                            FileName = PYTHON_PATH,
                            Arguments = "\"" + AFFECTIVE_SCRIPT_PATH + "\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                            StandardOutputEncoding = Encoding.UTF8,
                            StandardErrorEncoding = Encoding.UTF8
                        };
                        using (var proc = Process.Start(psi))
                        {
                            string stdout = proc.StandardOutput.ReadToEnd();
                            string stderr = proc.StandardError.ReadToEnd();
                            proc.WaitForExit();
                            System.Diagnostics.Debug.WriteLine($"[Affective] exit={proc.ExitCode}");
                            if (!string.IsNullOrEmpty(stderr)) System.Diagnostics.Debug.WriteLine("[Affective stderr] " + stderr);
                            if (proc.ExitCode != 0)
                                affectiveError = $"Inference exit code {proc.ExitCode}\n\nstderr:\n{stderr}\n\nstdout tail:\n{(stdout.Length > 500 ? stdout.Substring(stdout.Length - 500) : stdout)}";
                        }
                    }
                    catch (Exception ex)
                    {
                        affectiveError = $"Inference call failed: {ex.Message}";
                        System.Diagnostics.Debug.WriteLine("[Affective] error: " + ex.Message);
                    }
                });
                if (affectiveError != null)
                {
                    Dispatcher.Invoke(() => MessageBox.Show(affectiveError, "Inference Stage Failed", MessageBoxButton.OK, MessageBoxImage.Warning));
                }
                // Before rendering (wait for non-staged only; staged runs in the background)
                progress.UpdateStatus("Generating before rendering... (typical 30s~1min, first call may take 4~5min cold-start)");
                await RunGenerateRenderingAsync("before",
                    msg => Dispatcher.Invoke(() => progress.UpdateStatus(msg)));
                // Chart + image loading
                progress.UpdateStatus("Loading charts and images...");
                Dispatcher.Invoke(() => { LoadBeforeChart(null); SetBeforeImage(null); });
                // Save R0 snapshot (Results folder)
                _session.SaveR0Snapshot();

            }
            catch (Exception ex) { MessageBox.Show("Full analysis error: " + ex.Message, "Error"); }
            finally { progress.Close(); IsEnabled = true; this.Show(); }
        }


        private void UpdateBeforeOptions(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return;
                var data = new Dictionary<string, string>();
                foreach (string line in File.ReadAllLines(filePath))
                {
                    if (line.Contains(":"))
                    {
                        string[] parts = line.Split(':');
                        if (parts.Length == 2) data[parts[0].Trim()] = parts[1].Trim();
                    }
                }
                Dispatcher.Invoke(() =>
                {
                    if (data.ContainsKey("Ceiling Height")) BeforeOption1Button.Content = $"{data["Ceiling Height"]}m";
                    if (data.ContainsKey("WWR")) BeforeOption2Button.Content = $"{data["WWR"]}%";
                    if (data.ContainsKey("CCT")) BeforeOption3Button.Content = $"{data["CCT"]}K";
                    if (data.ContainsKey("Room Color")) BeforeOption4Button.Content = data["Room Color"];
                    if (data.ContainsKey("Floor Material")) BeforeOption5Button.Content = data["Floor Material"];
                });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Before Options update error: {ex.Message}"); }
        }

        // ========================================================================
        // 2. Emotion analysis button
        // ========================================================================
        private async void EmotionAnalysisButton_Click(object sender, RoutedEventArgs e)
        {
            var progress = new ProgressWindow("Emotion analysis...", "Running BERT classification...");
            progress.Owner = this;
            IsEnabled = false;
            progress.Show();
            try
            {
                if (!File.Exists(@"C:\Temp\current_analysis.txt"))
                {
                    MessageBox.Show("Analysis file not found.\nPlease click [Analyze] first.", "Notice");
                    return;
                }
                var inputBox = this.FindName("InputTextBox") as System.Windows.Controls.TextBox;
                if (inputBox == null) { MessageBox.Show("Text input not found.", "Error"); return; }
                string userInput = inputBox.Text?.Trim();
                if (string.IsNullOrEmpty(userInput) || userInput == "...")
                {
                    MessageBox.Show("Please enter the desired emotion first.", "Notice");
                    return;
                }
                _lastFeedbackPrompt = userInput; // Store feedback
                string bertResult = await Task.Run(() => RunBERTInference(userInput));
                string targetEmotion = bertResult.Trim();
                if (targetEmotion.Contains(":")) targetEmotion = targetEmotion.Split(':')[1].Trim();
                // Save target_emotion.txt -> used to highlight the chart
                try { File.WriteAllText(@"C:\Temp\target_emotion.txt", targetEmotion, Encoding.UTF8); } catch { }
                Dispatcher.Invoke(() =>
                {
                    _targetEmotion = targetEmotion;  // Update field immediately
                    if (this.FindName("TargetEmotionText") is TextBlock txt) txt.Text = targetEmotion;
                    if (this.FindName("ImprovementSection") is Grid sec) sec.Visibility = Visibility.Visible;
                    // Refresh chart now (shows target highlight)
                    if (_beforeLabels != null)
                        DrawCombinedChart(BeforeChartCanvas, _beforeLabels, _beforeScores, _afterLabels, _afterScores, _targetEmotion);
                });
            }
            catch (Exception ex) { MessageBox.Show($"Emotion analysis error:\n{ex.Message}", "Error"); }
            finally { progress.Close(); IsEnabled = true; }
        }

        // ========================================================================
        // 3. Design revision button
        // ========================================================================
        private async void ModifyDesignButton_Click(object sender, RoutedEventArgs e)
        {
            var progress = new ProgressWindow("Design optimization...", "Computing optimal design...");
            progress.Owner = this;
            IsEnabled = false;
            progress.Show();
            try
            {
                if (!File.Exists(@"C:\Temp\target_emotion.txt"))
                { MessageBox.Show("Target emotion file missing.\nSteps: [Analyze] -> enter emotion -> [Emotion Analysis] -> [Modify Design]", "Notice"); return; }
                // Create Rn folder (Results/ALIS_.../Rn)
                _session.AdvanceIteration();
                // Run optimization
                progress.UpdateStatus("Searching optimal design combination...");
                await Task.Run(() => RunOptimizationScript());
                // Optimizer.py saves emotion_scores_after.json itself (all_scores)
                // Load After chart (refresh combined chart)
                progress.UpdateStatus("Loading after chart...");
                Dispatcher.Invoke(() => LoadAfterChart(null));
                string revisedFile = @"C:\Temp\revised_analysis.txt";
                if (File.Exists(revisedFile)) UpdateAfterOptions(revisedFile);
                // Generate After rendering
                progress.UpdateStatus("Generating after rendering... (typical 30s~1min, first call may take 4~5min cold-start)");
                await RunGenerateRenderingAsync("after",
                    msg => Dispatcher.Invoke(() => progress.UpdateStatus(msg)));
                // Load After images
                progress.UpdateStatus("Loading images...");
                Dispatcher.Invoke(() => SetAfterImage(null));
                // Save Rn snapshot (Results folder)
                _session.SaveRnSnapshot(_lastFeedbackPrompt, _targetEmotion ?? "");

            }
            catch (Exception ex) { MessageBox.Show("Modify Design error: " + ex.Message, "Error"); }
            finally { progress.Close(); IsEnabled = true; }
        }


        private void UpdateAfterOptions(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return;
                var data = new Dictionary<string, string>();
                foreach (string line in File.ReadAllLines(filePath))
                {
                    if (line.Contains(":"))
                    {
                        string[] parts = line.Split(':');
                        if (parts.Length == 2) data[parts[0].Trim()] = parts[1].Trim();
                    }
                }
                Dispatcher.Invoke(() =>
                {
                    if (data.ContainsKey("Ceiling Height")) AfterOption1Button.Content = $"{data["Ceiling Height"]}m";
                    if (data.ContainsKey("WWR")) AfterOption2Button.Content = $"{data["WWR"]}%";
                    if (data.ContainsKey("CCT")) AfterOption3Button.Content = $"{data["CCT"]}K";
                    if (data.ContainsKey("Room Color")) AfterOption4Button.Content = data["Room Color"];
                    if (data.ContainsKey("Floor Material")) AfterOption5Button.Content = data["Floor Material"];
                });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"After Options update error: {ex.Message}"); }
        }

        private void UpdateImprovementSection(string affectiveResult)
        {
            var improvementSection = this.FindName("ImprovementSection") as Grid;
            if (improvementSection == null) return;
            improvementSection.Visibility = Visibility.Visible;
            string[] lines = affectiveResult.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var recommendedValues = new Dictionary<string, string>();
            foreach (string line in lines)
            {
                if (line.StartsWith("Ceiling Height:")) recommendedValues["Ceiling Height"] = line.Replace("Ceiling Height:", "").Trim();
                else if (line.StartsWith("CCT:")) recommendedValues["CCT"] = line.Replace("CCT:", "").Trim();
                else if (line.StartsWith("Room Color:")) recommendedValues["Room Color"] = line.Replace("Room Color:", "").Trim();
            }
            UpdateOptionButtons("Before", recommendedValues);
            AfterOption1Button.Content = "Option 1"; AfterOption2Button.Content = "Option 2";
            AfterOption3Button.Content = "Option 3"; AfterOption4Button.Content = "Option 4"; AfterOption5Button.Content = "Option 5";
        }

        private void UpdateOptionButtons(string prefix, Dictionary<string, string> values)
        {
            int index = 1;
            foreach (var kvp in values)
            {
                if (this.FindName($"{prefix}Option{index}Button") is Button button) button.Content = $"{kvp.Key}: {kvp.Value}";
                index++;
            }
        }

        // ========================================================================
        // Python script runner methods
        // ========================================================================
        private string RunBERTInference(string userInput)
        {
            if (!File.Exists(PYTHON_PATH)) throw new Exception($"Python executable not found.\n\nPath: {PYTHON_PATH}");
            if (!File.Exists(BERT_SCRIPT_PATH)) throw new Exception($"BERT script not found.\n\nPath: {BERT_SCRIPT_PATH}");
            try
            {
                string scriptDirectory = System.IO.Path.GetDirectoryName(BERT_SCRIPT_PATH);
                var startInfo = new ProcessStartInfo
                {
                    FileName = PYTHON_PATH,
                    Arguments = $"\"{BERT_SCRIPT_PATH}\" \"{userInput}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = scriptDirectory,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                startInfo.EnvironmentVariables["TF_CPP_MIN_LOG_LEVEL"] = "2";
                startInfo.EnvironmentVariables["TF_ENABLE_ONEDNN_OPTS"] = "0";
                using (Process process = Process.Start(startInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                        throw new Exception($"BERT script execution failed\nExit code: {process.ExitCode}\n{(string.IsNullOrEmpty(error) ? output : error)}");
                    return output.Trim();
                }
            }
            catch (Exception ex) { throw new Exception($"Python execution error:\n\n{ex.Message}"); }
        }

        private void RunOptimizationScript()
        {
            if (!File.Exists(PYTHON_PATH)) throw new Exception($"Python executable not found.\n\nPath: {PYTHON_PATH}");
            if (!File.Exists(OPTIMIZATION_SCRIPT_PATH)) throw new Exception($"Optimizer script not found.\n\nPath: {OPTIMIZATION_SCRIPT_PATH}");
            try
            {
                string scriptDirectory = System.IO.Path.GetDirectoryName(OPTIMIZATION_SCRIPT_PATH);
                var startInfo = new ProcessStartInfo
                {
                    FileName = PYTHON_PATH,
                    Arguments = $"\"{OPTIMIZATION_SCRIPT_PATH}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = scriptDirectory,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                startInfo.EnvironmentVariables["TF_CPP_MIN_LOG_LEVEL"] = "2";
                startInfo.EnvironmentVariables["TF_ENABLE_ONEDNN_OPTS"] = "0";
                using (Process process = Process.Start(startInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                        throw new Exception($"Optimizer script execution failed\nExit code: {process.ExitCode}\n{(string.IsNullOrEmpty(error) ? output : error)}");
                }
            }
            catch (Exception ex) { throw new Exception($"Python execution error:\n\n{ex.Message}"); }
        }

        private string RunAffectiveInference()
        {
            if (!File.Exists(PYTHON_PATH)) throw new Exception($"Python executable not found.\n\nPath: {PYTHON_PATH}");
            if (!File.Exists(AFFECTIVE_SCRIPT_PATH)) throw new Exception($"Affective script not found.\n\nPath: {AFFECTIVE_SCRIPT_PATH}");
            try
            {
                string scriptDirectory = System.IO.Path.GetDirectoryName(AFFECTIVE_SCRIPT_PATH);
                var startInfo = new ProcessStartInfo
                {
                    FileName = PYTHON_PATH,
                    Arguments = $"\"{AFFECTIVE_SCRIPT_PATH}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = scriptDirectory,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                startInfo.EnvironmentVariables["TF_CPP_MIN_LOG_LEVEL"] = "2";
                startInfo.EnvironmentVariables["TF_ENABLE_ONEDNN_OPTS"] = "0";
                using (Process process = Process.Start(startInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                        throw new Exception($"Affective script execution failed\nExit code: {process.ExitCode}\n{(string.IsNullOrEmpty(error) ? output : error)}");
                    return output;
                }
            }
            catch (Exception ex) { throw new Exception($"Python execution error:\n\n{ex.Message}"); }
        }

        // ========================================================================
        // 4. Apply button
        // ========================================================================
        private void ApplyDesignButton_Click(object sender, RoutedEventArgs e)
        {
            IsEnabled = false;
            try
            {
                string revisedFile = @"C:\Temp\current_analysis.txt";
                if (!File.Exists(revisedFile))
                {
                    MessageBox.Show("Analysis file not found.\nPlease click [Analyze] first.", "Notice");
                    return;
                }
                _handler.RequestId = MyEventHandler.EventID.ApplyDesign;
                _handler.AppWindow = this;

                _exEvent.Raise();
            }
            catch (Exception ex) { MessageBox.Show($"Apply Design error: {ex.Message}", "Error"); }
            finally { IsEnabled = true; }
        }

        // ========================================================================
        // Simple JSON parse helper (extracts key:value without external libraries)
        // ========================================================================
        private static double ParseJsonDouble(string json, string key, double defaultVal)
        {
            try
            {
                string pattern = $"\"{key}\"\\s*:\\s*([\\d\\.\\-]+)";
                var m = System.Text.RegularExpressions.Regex.Match(json, pattern);
                return m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : defaultVal;
            }
            catch { return defaultVal; }
        }

        private static string ParseJsonString(string json, string key, string defaultVal)
        {
            try
            {
                string pattern = $"\"{key}\"\\s*:\\s*\"([^\"]+)\"";
                var m = System.Text.RegularExpressions.Regex.Match(json, pattern);
                return m.Success ? m.Groups[1].Value : defaultVal;
            }
            catch { return defaultVal; }
        }

        // ========================================================================
        // Select buttons (choose Before / After, keep the choice as Before, then iterate)
        // ========================================================================
        private void SelectBeforeButton_Click(object sender, RoutedEventArgs e) { ApplySelection(false); }
        private void SelectAfterButton_Click(object sender, RoutedEventArgs e) { ApplySelection(true); }

        private void ApplySelection(bool selectAfter)
        {
            try
            {
                string tmp = @"C:\Temp";
                if (selectAfter)
                {
                    // emotion_scores_after.json → emotion_scores.json
                    string aj = System.IO.Path.Combine(tmp, "emotion_scores_after.json");
                    string bj = System.IO.Path.Combine(tmp, "emotion_scores.json");
                    if (File.Exists(aj)) File.Copy(aj, bj, true);
                    // after_variables.json → current_analysis.txt
                    string av = System.IO.Path.Combine(tmp, "after_variables.json");
                    if (File.Exists(av))
                    {
                        string _j = File.ReadAllText(av);
                        double ch = ParseJsonDouble(_j, "ceiling_height_mm", 2700);
                        double wwr = ParseJsonDouble(_j, "wwr_ratio", 0.4);
                        double cct = ParseJsonDouble(_j, "cct_k", 4000);
                        string col = ParseJsonString(_j, "room_color", "White");
                        string flr = ParseJsonString(_j, "floor_material", "Wood");
                        File.WriteAllText(System.IO.Path.Combine(tmp, "current_analysis.txt"),
                            $"Ceiling Height: {ch / 1000:F2}\nWWR: {wwr * 100:F1}\nCCT: {cct:F0}\nRoom Color: {col}\nFloor Material: {flr}\n",
                            System.Text.Encoding.UTF8);
                    }
                    // Copy After rendering to Before
                    var aImgs = System.IO.Directory.GetFiles(tmp, "rendering_after_*.png")
                        .Where(f => !f.Contains("staged")).OrderByDescending(f => File.GetLastWriteTime(f)).ToList();
                    if (aImgs.Any())
                        File.Copy(aImgs.First(), System.IO.Path.Combine(tmp, $"rendering_before_{DateTime.Now:yyyyMMdd_HHmmss}.png"), true);
                }
                // Record the selection in CSV/meta.json
                _session.UpdateSelection(selectAfter ? "After" : "Before");
                // Delete After files
                foreach (var fn in new[] { "emotion_scores_after.json", "after_variables.json", "revised_analysis.txt" })
                { string fp = System.IO.Path.Combine(tmp, fn); if (File.Exists(fp)) File.Delete(fp); }
                // Refresh UI
                Dispatcher.Invoke(() =>
                {
                    LoadBeforeChart(null); SetBeforeImage(null);
                    // Refresh Before design variable cards (selected space data)
                    string curAnalysis = System.IO.Path.Combine(@"C:\Temp", "current_analysis.txt");
                    if (File.Exists(curAnalysis)) UpdateBeforeOptions(curAnalysis);
                    // Reset After chart (safe access via FindName)
                    if (this.FindName("AfterChartCanvas") is System.Windows.Controls.Canvas afterCanvas)
                        afterCanvas.Children.Clear();
                    if (this.FindName("AfterChartScroll") is System.Windows.Controls.ScrollViewer afterScroll)
                        afterScroll.Visibility = Visibility.Collapsed;
                    if (this.FindName("AfterChartPlaceholder") is TextBlock afterPlaceholder)
                        afterPlaceholder.Visibility = Visibility.Visible;
                    AfterImage.Source = null;
                    AfterPlaceholder.Visibility = Visibility.Visible;
                    AfterImage.Visibility = Visibility.Collapsed;
                    if (this.FindName("SelectAfterButton") is Button sa) sa.Visibility = Visibility.Collapsed;
                    AfterOption1Button.Content = "Option 1"; AfterOption2Button.Content = "Option 2";
                    AfterOption3Button.Content = "Option 3"; AfterOption4Button.Content = "Option 4"; AfterOption5Button.Content = "Option 5";
                    if (this.FindName("InputTextBox") is System.Windows.Controls.TextBox tb) tb.Text = string.Empty;
                    if (this.FindName("TargetEmotionText") is TextBlock tt) tt.Text = "Comfortable";
                    string tgt = System.IO.Path.Combine(@"C:\Temp", "target_emotion.txt");
                    if (File.Exists(tgt)) File.Delete(tgt);
                });
            }
            catch (Exception ex) { MessageBox.Show($"Selection error: {ex.Message}", "Error"); }
        }

        // ========================================================================
        // 5. Reset button
        // ========================================================================
        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. Reset text input
                if (this.FindName("InputTextBox") is System.Windows.Controls.TextBox inputBox) inputBox.Text = "...";
                if (this.FindName("ImprovementSection") is Grid improvementSection) improvementSection.Visibility = Visibility.Collapsed;
                if (this.FindName("TargetEmotionText") is TextBlock targetEmotionText) targetEmotionText.Text = "Comfortable";

                // 2. Reset design variables
                BeforeOption1Button.Content = "-"; BeforeOption2Button.Content = "-";
                BeforeOption3Button.Content = "-"; BeforeOption4Button.Content = "-"; BeforeOption5Button.Content = "-";
                AfterOption1Button.Content = "-"; AfterOption2Button.Content = "-";
                AfterOption3Button.Content = "-"; AfterOption4Button.Content = "-"; AfterOption5Button.Content = "-";

                // 3. Reset chart
                BeforeChartCanvas.Children.Clear();
                BeforeChartScroll.Visibility = Visibility.Collapsed;
                BeforeChartPlaceholder.Visibility = Visibility.Visible;

                // 4. Reset internal state
                _beforeLabels = null; _beforeScores = null;
                _afterLabels = null; _afterScores = null;
                _targetEmotion = null;
                _lastFeedbackPrompt = "";

                // 5. Clean C:\Temp (before loading images!)
                CleanTempFolder();

                // 6. Reset images
                BeforeImage.Source = null; AfterImage.Source = null;
                BeforePlaceholder.Visibility = Visibility.Visible; AfterPlaceholder.Visibility = Visibility.Visible;
                BeforeImage.Visibility = Visibility.Collapsed; AfterImage.Visibility = Visibility.Collapsed;
                if (this.FindName("SelectBeforeButton") is Button rsb) rsb.Visibility = Visibility.Collapsed;
                if (this.FindName("SelectAfterButton") is Button rsa) rsa.Visibility = Visibility.Collapsed;

                // 7. Keep action buttons enabled
                SetActionButtonsEnabled(true);

                // 8. Check images are null again
                BeforeImage.Source = null; AfterImage.Source = null;
                BeforePlaceholder.Visibility = Visibility.Visible; AfterPlaceholder.Visibility = Visibility.Visible;
                BeforeImage.Visibility = Visibility.Collapsed; AfterImage.Visibility = Visibility.Collapsed;

                // 9. Reset Revit
                _handler.RequestId = MyEventHandler.EventID.ResetToInitial;
                _handler.AppWindow = this;
                _exEvent.Raise();
            }
            catch (Exception ex) { MessageBox.Show($"Error during Reset: {ex.Message}", "Error"); }
        }

        /// <summary>
        /// Deletes only the files ALIS created in C:\Temp.
        /// </summary>
        private static void CleanTempFolder()
        {
            string tmp = @"C:\Temp";
            if (!Directory.Exists(tmp)) return;
            string[] patterns = {
                "rendering_*.png", "rendering_empty.png", "rendering_staged.png",
                "rendering_reference_camera.png", "rendering_status.json", "rendering_status_*.json",
                "current_analysis.txt", "revised_analysis.txt", "target_emotion.txt",
                "emotion_scores.json", "emotion_scores_after.json", "after_variables.json",
                "floor_analysis_debug.txt", "path_debug.txt", "staged_debug.txt",
                "render_process_log.txt", "floor_debug.txt",
            };
            foreach (var pattern in patterns)
            {
                try { foreach (var f in Directory.GetFiles(tmp, pattern)) File.Delete(f); }
                catch { }
            }
            System.Diagnostics.Debug.WriteLine("[ALIS] C:\\Temp cleaned");
        }

        private void AfterOption1Button_Click(object sender, RoutedEventArgs e) { }
        private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e) { }
        private void AnalyzeButton_Click(object sender, RoutedEventArgs e) { EmotionAnalysisButton_Click(sender, e); }
        private void DesignModifyButton_Click(object sender, RoutedEventArgs e) { ModifyDesignButton_Click(sender, e); }

        /// <summary>
        /// Handler for the Empty / Staged toggle buttons (top right of the Before / After cards).
        /// Button Tag = "before_empty" | "before_staged" | "after_empty" | "after_staged"
        /// On click, loads the latest PNG for that condition from C:\Temp into the card.
        /// </summary>
        private void ConditionToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn) || !(btn.Tag is string condition)) return;

            string tmp = @"C:\Temp";
            string prefix;
            bool excludeStaged = false;
            switch (condition)
            {
                case "before_empty":  prefix = "rendering_before_";        excludeStaged = true; break;
                case "before_staged": prefix = "rendering_before_staged_"; break;
                case "after_empty":   prefix = "rendering_after_";         excludeStaged = true; break;
                case "after_staged":  prefix = "rendering_after_staged_";  break;
                default: return;
            }

            string img = FindLatestImage(tmp, prefix, excludeStaged);
            if (img == null)
            {
                System.Diagnostics.Debug.WriteLine($"[ConditionToggle] {condition} → no image (prefix={prefix})");
                return;
            }

            bool isBefore = condition.StartsWith("before");
            if (isBefore) LoadBeforeImage(img);
            else          LoadAfterImage(img);

            // Show the active state: selected side in a strong color, the other in gray.
            UpdateConditionToggleStyle(condition);
        }

        /// <summary>
        /// Updates the toggle buttons: active side in color, inactive side in gray.
        /// </summary>
        private void UpdateConditionToggleStyle(string activeCondition)
        {
            // (name, condition, activeColor) mapping
            var defs = new[] {
                new { Name = "BeforeEmptyBtn",  Cond = "before_empty",  Active = "#5DADE2" },
                new { Name = "BeforeStagedBtn", Cond = "before_staged", Active = "#5DADE2" },
                new { Name = "AfterEmptyBtn",   Cond = "after_empty",   Active = "#2ECC71" },
                new { Name = "AfterStagedBtn",  Cond = "after_staged",  Active = "#2ECC71" },
            };
            foreach (var d in defs)
            {
                if (this.FindName(d.Name) is Button b)
                {
                    bool isActive = (d.Cond == activeCondition);
                    b.Background = (System.Windows.Media.Brush)
                        new System.Windows.Media.BrushConverter().ConvertFrom(isActive ? d.Active : "#7F8C8D");
                }
            }
        }

        /// <summary>
        /// Latest PNG in C:\Temp whose name starts with prefix.
        /// If excludeStaged=true, files with "staged" in the name are skipped.
        /// </summary>
        private static string FindLatestImage(string folder, string prefix, bool excludeStaged)
        {
            if (!Directory.Exists(folder)) return null;
            var files = Directory.GetFiles(folder, prefix + "*.png");
            if (excludeStaged)
                files = files.Where(f => !System.IO.Path.GetFileName(f).Contains("staged")).ToArray();
            return files.OrderByDescending(f => File.GetLastWriteTime(f)).FirstOrDefault();
        }

        /// <summary>
        /// Enables or disables the action buttons (always called with enabled = true).
        /// </summary>
        private void SetActionButtonsEnabled(bool enabled)
        {
            var gatedButtons = new[] { "SelectBeforeButton", "SelectAfterButton" };
            foreach (var name in gatedButtons)
            {
                if (this.FindName(name) is Button btn)
                {
                    btn.IsEnabled = enabled;
                    btn.Opacity = enabled ? 1.0 : 0.4;
                }
            }
        }
    }

    // ========================================================================
    // ALIS session manager
    // ========================================================================
    public class ALISSessionManager
    {
        public static readonly string ResultsRoot = System.IO.Path.Combine(MyWindow.ProjectRoot, "Results");
        public string SessionFolder { get; private set; }
        public string CurrentRn { get; private set; }
        public int CurrentIter { get; private set; }
        // User label included in the session folder name
        public string UserId { get; set; } = "U000";
        private string CsvPath { get { return System.IO.Path.Combine(SessionFolder, "session_log.csv"); } }
        public void StartSession() { string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss"); SessionFolder = System.IO.Path.Combine(ResultsRoot, "ALIS_" + UserId + "_" + ts); System.IO.Directory.CreateDirectory(SessionFolder); CurrentIter = 0; CurrentRn = CreateRn(0); WriteCsvHeader(); }
        private string CreateRn(int n) { string p = System.IO.Path.Combine(SessionFolder, "R" + n.ToString()); System.IO.Directory.CreateDirectory(System.IO.Path.Combine(p, "Image")); System.IO.Directory.CreateDirectory(System.IO.Path.Combine(p, "Json")); System.IO.Directory.CreateDirectory(System.IO.Path.Combine(p, "Txt")); return p; }
        public void AdvanceIteration() { CurrentIter++; CurrentRn = CreateRn(CurrentIter); }
        public void CopyToCurrentRn(string src, string fn) { if (string.IsNullOrEmpty(CurrentRn) || !System.IO.File.Exists(src)) return; if (fn == null) fn = System.IO.Path.GetFileName(src); string ext = System.IO.Path.GetExtension(fn).ToLower(); string sub = (ext == ".png" || ext == ".jpg" || ext == ".bmp") ? "Image" : ext == ".json" ? "Json" : "Txt"; string dst = System.IO.Path.Combine(CurrentRn, sub, fn); try { System.IO.File.Copy(src, dst, false); } catch (System.IO.IOException) { } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ALISSession] " + ex.Message); } }
        public void CopyToCurrentRn(string src) { CopyToCurrentRn(src, null); }
        public void SaveR0Snapshot() { string tmp = @"C:\Temp"; CopyToCurrentRn(System.IO.Path.Combine(tmp, "current_analysis.txt")); CopyToCurrentRn(System.IO.Path.Combine(tmp, "emotion_scores.json")); CopyToCurrentRn(System.IO.Path.Combine(tmp, "rendering_status_before.json")); CopyToCurrentRn(System.IO.Path.Combine(tmp, "rendering_reference_camera.png")); CopyLatestR(tmp, "rendering_before_", false); CopyLatestR(tmp, "rendering_before_staged_", true); AppendCsvRow("", "", "-", true); }
        public void SaveRnSnapshot(string fb, string te) { string tmp = @"C:\Temp"; CopyToCurrentRn(System.IO.Path.Combine(tmp, "target_emotion.txt")); CopyToCurrentRn(System.IO.Path.Combine(tmp, "revised_analysis.txt")); CopyToCurrentRn(System.IO.Path.Combine(tmp, "emotion_scores_after.json")); CopyToCurrentRn(System.IO.Path.Combine(tmp, "after_variables.json")); CopyToCurrentRn(System.IO.Path.Combine(tmp, "rendering_status_after.json")); CopyLatestR(tmp, "rendering_after_", false); CopyLatestR(tmp, "rendering_after_staged_", true); WriteMeta(fb, te, "-"); AppendCsvRow(fb, te, "-", false); }
        public void UpdateSelection(string sel) { if (!System.IO.File.Exists(CsvPath)) return; var ls = new System.Collections.Generic.List<string>(System.IO.File.ReadAllLines(CsvPath, Encoding.UTF8)); if (ls.Count < 2) return; int li = ls.Count - 1; var cols = new System.Collections.Generic.List<string>(ls[li].Split(',')); if (cols.Count >= 2) { cols[cols.Count - 2] = sel; cols[cols.Count - 1] = sel == "After" ? "success" : "failure"; ls[li] = string.Join(",", cols.ToArray()); System.IO.File.WriteAllLines(CsvPath, ls.ToArray(), Encoding.UTF8); } string mp = System.IO.Path.Combine(CurrentRn, "meta.json"); if (System.IO.File.Exists(mp)) { string mt = System.IO.File.ReadAllText(mp, Encoding.UTF8); mt = System.Text.RegularExpressions.Regex.Replace(mt, "\"selection\"\\s*:\\s*\"-\"", "\"selection\": \"" + sel + "\""); System.IO.File.WriteAllText(mp, mt, Encoding.UTF8); } }
        private void CopyLatestR(string tmp, string prefix, bool staged) { string[] fs = System.IO.Directory.GetFiles(tmp, prefix + "*.png"); string best = null; DateTime bestT = DateTime.MinValue; foreach (string f in fs) { bool hs = f.Contains("staged"); if (staged != hs) continue; DateTime wt = System.IO.File.GetLastWriteTime(f); if (wt > bestT) { bestT = wt; best = f; } } if (best != null) CopyToCurrentRn(best); }
        private void WriteMeta(string fb, string te, string sel) { if (string.IsNullOrEmpty(CurrentRn)) return; string mp = System.IO.Path.Combine(CurrentRn, "meta.json"); string j = "{\r\n  \"iteration\": " + CurrentIter.ToString() + ",\r\n  \"feedback_prompt\": \"" + EJ(fb) + "\",\r\n  \"target_emotion\": \"" + EJ(te) + "\",\r\n  \"timestamp\": \"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\",\r\n  \"selection\": \"" + sel + "\"\r\n}"; System.IO.File.WriteAllText(mp, j, Encoding.UTF8); }
        private void WriteCsvHeader() { string h = "Iteration,Type,Ceiling_height,WWR,CCT,Color,Material,pleasant,excitement,cozy,spacious,attractive,simple,bright,open,calm,comfortable,Feedback_prompt,Target_emotion,Selection,label,Timestamp"; System.IO.File.WriteAllText(CsvPath, h + Environment.NewLine, Encoding.UTF8); }
        public void AppendCsvRow(string fb, string te, string sel, bool isBase) { string tmp = @"C:\Temp"; string[] d = RD(tmp); string[] s = RS(tmp, isBase); string lbl = sel == "After" ? "success" : sel == "Before" ? "failure" : "-"; string row = string.Join(",", new string[] { CurrentIter.ToString(), isBase ? "baseline" : "proposed", d[0], d[1], d[2], d[3], d[4], s[0], s[1], s[2], s[3], s[4], s[5], s[6], s[7], s[8], s[9], EC(fb), EC(te), sel, lbl, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }); System.IO.File.AppendAllText(CsvPath, row + Environment.NewLine, Encoding.UTF8); }
        private string[] RD(string tmp) { string f = System.IO.Path.Combine(tmp, "current_analysis.txt"); if (!System.IO.File.Exists(f)) return new[] { "-", "-", "-", "-", "-" }; var d = new System.Collections.Generic.Dictionary<string, string>(); foreach (string line in System.IO.File.ReadAllLines(f, Encoding.UTF8)) if (line.Contains(":")) { string[] p = line.Split(':'); if (p.Length == 2) d[p[0].Trim()] = p[1].Trim(); } return new[] { d.TryGetValue("Ceiling Height", out string v) ? v : "-", d.TryGetValue("WWR", out v) ? v : "-", d.TryGetValue("CCT", out v) ? v : "-", d.TryGetValue("Room Color", out v) ? v : "-", d.TryGetValue("Floor Material", out v) ? v : "-" }; }
        private string[] RS(string tmp, bool isBefore) { string f = System.IO.Path.Combine(tmp, isBefore ? "emotion_scores.json" : "emotion_scores_after.json"); if (!System.IO.File.Exists(f)) return new[] { "-", "-", "-", "-", "-", "-", "-", "-", "-", "-" }; string js = System.IO.File.ReadAllText(f, Encoding.UTF8); var m = System.Text.RegularExpressions.Regex.Match(js, "\"scores\"\\s*:\\s*\\[([\\d\\s.,]+)\\]"); if (!m.Success) return new[] { "-", "-", "-", "-", "-", "-", "-", "-", "-", "-" }; string[] v = m.Groups[1].Value.Split(','); for (int i = 0; i < v.Length; i++) v[i] = v[i].Trim(); string[] r = new string[10]; for (int i = 0; i < 10; i++) r[i] = i < v.Length ? v[i] : "-"; return r; }
        private static string EJ(string s) { if (s == null) s = ""; return s.Replace("\\", "\\\\").Replace("\"", "\\\""); }
        private static string EC(string s) { if (s == null) s = ""; return (s.Contains(",") || s.Contains("\"") || s.Contains("\n")) ? "\"" + s.Replace("\"", "\"\"") + "\"" : s; }
    }

    // ========================================================================
    // Progress window
    // ========================================================================
    public class ProgressWindow : Window
    {
        private readonly System.Windows.Controls.ProgressBar _bar;
        private readonly TextBlock _txt;

        public ProgressWindow(string title, string message)
        {
            Title = title; Width = 420; Height = 145;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.ToolWindow;
            Topmost = true;
            var sp = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            _txt = new TextBlock
            {
                Text = message,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 14),
                TextWrapping = TextWrapping.Wrap
            };
            _bar = new System.Windows.Controls.ProgressBar
            {
                IsIndeterminate = true,
                Height = 20,
                Minimum = 0,
                Maximum = 100
            };
            sp.Children.Add(_txt);
            sp.Children.Add(_bar);
            Content = sp;
        }

        public void UpdateStatus(string msg) => Dispatcher.Invoke(() => _txt.Text = msg);
    }
}