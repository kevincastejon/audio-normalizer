using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

#if WINDOWS
using Windows.Media.Core;
using Windows.Media.Playback;
#endif

namespace AudioNormalizer;

public partial class MainPage : ContentPage, INotifyPropertyChanged
{
    const double OutputAttenuationMinDb = -24.0;
    const double OutputAttenuationMaxDb = 0.0;
    const double OutputAttenuationStepDb = 0.5;
    const int OutputAttenuationMaxDecimals = 3;

    bool _isUpdatingOutputAttenuationText;
    string _outputAttenuationText = "0";

    CancellationTokenSource? _normalizeCts;
    Process? _currentProcess;

    CancellationTokenSource? _previewCts;
    Process? _previewProcess;
    AudioItem? _previewItem;

    AudioItem? _playbackItem;
    string? _currentPlaybackPath;
    bool _currentPlaybackIsPreview;

#if WINDOWS
    readonly MediaPlayer _mediaPlayer;
#endif

    bool _isNormalizing;

    public bool IsNormalizing
    {
        get => _isNormalizing;
        set
        {
            if (_isNormalizing == value) return;
            _isNormalizing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowSelectedCount));
            OnPropertyChanged(nameof(IsNotNormalizing));
        }
    }

    public bool ShowSelectedCount => HasItems && IsNotNormalizing;

    public bool IsNotNormalizing => !IsNormalizing;

    public string SelectedFilesText => $"{AudioFiles.Count(x => x.IsChecked)} file(s) selected";

    string _outputPostfix = "_Normalized";

    public string OutputPostfix
    {
        get => _outputPostfix;
        set
        {
            if (_outputPostfix == value) return;
            _outputPostfix = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<AudioItem> AudioFiles { get; } = new();

    string? pickedDirectory;

    public string? PickedDirectory
    {
        get => pickedDirectory;
        private set
        {
            pickedDirectory = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NoFilesMessage));
            OnPropertyChanged(nameof(ShowNoFilesMessage));
        }
    }

    public bool HasItems => AudioFiles.Count > 0;

    public bool ShowNoFilesMessage => !HasItems && !string.IsNullOrWhiteSpace(PickedDirectory);

    public string NoFilesMessage => string.IsNullOrWhiteSpace(PickedDirectory)
        ? string.Empty
        : $"No audio file found in directory {PickedDirectory}";

    public string OutputAttenuationText
    {
        get => _outputAttenuationText;
        set
        {
            if (_outputAttenuationText == value) return;
            _outputAttenuationText = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public MainPage()
    {
        InitializeComponent();
        BindingContext = this;

        CleanupNormalizeTempOnStartup();
        CleanupPreviewTempOnStartup();

        AudioFiles.CollectionChanged += OnAudioFilesCollectionChanged;

#if WINDOWS
        _mediaPlayer = new MediaPlayer();
        _mediaPlayer.MediaEnded += (_, _) => MainThread.BeginInvokeOnMainThread(StopPlayback);
        _mediaPlayer.MediaFailed += (_, _) => MainThread.BeginInvokeOnMainThread(StopPlayback);
#endif
    }

    void OnAudioFilesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var obj in e.OldItems)
                if (obj is AudioItem item)
                    item.PropertyChanged -= OnAudioItemPropertyChanged;

        if (e.NewItems is not null)
            foreach (var obj in e.NewItems)
                if (obj is AudioItem item)
                    item.PropertyChanged += OnAudioItemPropertyChanged;

        OnPropertyChanged(nameof(SelectedFilesText));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowSelectedCount));
        OnPropertyChanged(nameof(ShowNoFilesMessage));
    }

    void OnAudioItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioItem.IsChecked))
            OnPropertyChanged(nameof(SelectedFilesText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    async void OnPickFolderClicked(object sender, EventArgs e)
    {
        var directory = await PickFolderAsync();
        if (string.IsNullOrWhiteSpace(directory))
            return;

        PickedDirectory = directory;
        LoadAudioFiles(directory);
    }

    void OnSelectAllClicked(object sender, EventArgs e)
    {
        foreach (var item in AudioFiles)
            item.IsChecked = true;
    }

    void OnClearAllClicked(object sender, EventArgs e)
    {
        foreach (var item in AudioFiles)
            item.IsChecked = false;
    }

    void OnOutputAttenuationMinusClicked(object sender, EventArgs e)
    {
        var v = GetOutputAttenuationDb();
        v = ClampDb(v - OutputAttenuationStepDb);
        v = SnapToStep(v, OutputAttenuationStepDb);
        SetOutputAttenuationTextFromValue(v);
    }

    void OnOutputAttenuationPlusClicked(object sender, EventArgs e)
    {
        var v = GetOutputAttenuationDb();
        v = ClampDb(v + OutputAttenuationStepDb);
        v = SnapToStep(v, OutputAttenuationStepDb);
        SetOutputAttenuationTextFromValue(v);
    }

    void OnOutputAttenuationTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingOutputAttenuationText) return;

        var sanitized = SanitizeDbText(e.NewTextValue);
        if (sanitized == e.NewTextValue)
            return;

        _isUpdatingOutputAttenuationText = true;
        OutputAttenuationText = sanitized;
        _isUpdatingOutputAttenuationText = false;
    }

    void OnOutputAttenuationUnfocused(object sender, FocusEventArgs e)
    {
        NormalizeOutputAttenuationText();
    }

    void OnPlaySourceTapped(object sender, TappedEventArgs e)
    {
        if (IsNormalizing) return;
        if (sender is not BindableObject bo) return;
        if (bo.BindingContext is not AudioItem item) return;

        if (_playbackItem == item && !_currentPlaybackIsPreview)
        {
            CancelPreviewGenerationAndPlayback();
            return;
        }

        CancelPreviewGenerationAndPlayback();
        StartPlayback(item, item.FullPath, false);
    }

    async void OnPreviewTapped(object sender, TappedEventArgs e)
    {
        if (IsNormalizing) return;
        if (sender is not BindableObject bo) return;
        if (bo.BindingContext is not AudioItem item) return;

        if (_playbackItem == item && _currentPlaybackIsPreview && !item.IsPreviewGenerating)
        {
            CancelPreviewGenerationAndPlayback();
            return;
        }

        CancelPreviewGenerationAndPlayback();

        var ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        var ffprobePath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");

        if (!File.Exists(ffmpegPath) || !File.Exists(ffprobePath))
            return;

        item.IsPreviewGenerating = true;
        _previewItem = item;
        _previewCts = new CancellationTokenSource();

        string? previewFile = null;

        try
        {
            previewFile = await GeneratePreviewFileAsync(ffmpegPath, ffprobePath, item.FullPath, _previewCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(previewFile))
                TryDeleteFile(previewFile);
            return;
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(previewFile))
                TryDeleteFile(previewFile);
            return;
        }
        finally
        {
            if (_previewItem is not null)
                _previewItem.IsPreviewGenerating = false;

            _previewItem = null;

            _previewProcess = null;
            _previewCts?.Dispose();
            _previewCts = null;
        }

        if (!string.IsNullOrWhiteSpace(previewFile) && File.Exists(previewFile))
            StartPlayback(item, previewFile, true);
    }

    void CancelPreviewGenerationAndPlayback()
    {
        StopPlayback();

        _previewCts?.Cancel();

        try
        {
            if (_previewProcess is { HasExited: false })
                _previewProcess.Kill(true);
        }
        catch
        {
        }

        if (_previewItem is not null)
            _previewItem.IsPreviewGenerating = false;

        _previewItem = null;

        _previewProcess = null;
        _previewCts?.Dispose();
        _previewCts = null;
    }

    void StopPlayback()
    {
#if WINDOWS
        try
        {
            _mediaPlayer.Pause();
            _mediaPlayer.Source = null;
        }
        catch
        {
        }
#endif

        if (_playbackItem is not null)
        {
            _playbackItem.IsPlayingSource = false;
            _playbackItem.IsPlayingPreview = false;
            _playbackItem = null;
        }

        if (_currentPlaybackIsPreview && !string.IsNullOrWhiteSpace(_currentPlaybackPath))
            TryDeleteFile(_currentPlaybackPath);

        _currentPlaybackPath = null;
        _currentPlaybackIsPreview = false;
    }

    void StartPlayback(AudioItem item, string path, bool isPreview)
    {
        StopPlayback();

        _playbackItem = item;
        _currentPlaybackPath = path;
        _currentPlaybackIsPreview = isPreview;

        item.IsPlayingSource = !isPreview;
        item.IsPlayingPreview = isPreview;

#if WINDOWS
        try
        {
            var uri = new Uri(path);
            _mediaPlayer.Source = MediaSource.CreateFromUri(uri);
            _mediaPlayer.Play();
        }
        catch
        {
            StopPlayback();
        }
#endif
    }

    async void OnNormalizeClicked(object sender, EventArgs e)
    {
        if (IsNormalizing) return;

        CancelPreviewGenerationAndPlayback();

        var ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        var ffprobePath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");

        if (!File.Exists(ffmpegPath))
        {
            await DisplayAlertAsync("FFmpeg", "ffmpeg.exe not found.", "OK");
            return;
        }

        if (!File.Exists(ffprobePath))
        {
            await DisplayAlertAsync("FFmpeg", "ffprobe.exe not found.", "OK");
            return;
        }

        var selectedItems = AudioFiles.Where(f => f.IsChecked).ToList();
        var selectedFiles = selectedItems.Select(f => f.FullPath).ToList();

        if (selectedFiles.Count == 0)
        {
            await DisplayAlertAsync("Normalize", "No file selected.", "OK");
            return;
        }

        var postfix = (OutputPostfix ?? string.Empty).Trim();
        var overwrite = string.IsNullOrWhiteSpace(postfix);

        if (overwrite)
        {
            var proceed = await DisplayAlertAsync(
                "Overwrite files?",
                $"Postfix is empty. This will overwrite {selectedFiles.Count} file(s). Continue?",
                "Overwrite",
                "Cancel");

            if (!proceed)
                return;
        }

        foreach (var item in selectedItems)
            item.Status = AudioStatus.None;

        _normalizeCts = new CancellationTokenSource();
        IsNormalizing = true;

        var tempRoot = GetNormalizeTempRoot();
        List<(string, string)> errors = new();

        try
        {
            foreach (var item in selectedItems)
            {
                var file = item.FullPath;

                _normalizeCts.Token.ThrowIfCancellationRequested();

                item.Status = AudioStatus.Processing;

                var dir = Path.GetDirectoryName(file)!;
                var name = Path.GetFileNameWithoutExtension(file);
                var ext = Path.GetExtension(file);

                var outputPath = overwrite
                    ? file
                    : Path.Combine(dir, $"{name}{postfix}{ext}");

                var tempFile = Path.Combine(tempRoot, $"{Guid.NewGuid()}{ext}");

                var (ok, errorText) = await ProcessSingleFileToOutputAsync(ffmpegPath, ffprobePath, file, tempFile, _normalizeCts.Token);
                if (!ok)
                {
                    TryDeleteFile(tempFile);
                    errors.Add((file, errorText));
                    item.Status = AudioStatus.Failed;
                    continue;
                }

                try
                {
                    if (File.Exists(outputPath))
                        File.Delete(outputPath);
                }
                catch
                {
                }

                if (overwrite)
                    TryDeleteFile(file);

                File.Move(tempFile, outputPath);
                item.Status = AudioStatus.Succeeded;
            }

            await DisplayAlertAsync(
                "Normalize",
                $"{selectedFiles.Count - errors.Count} audio file(s) normalized.{(errors.Count == 0 ? "" : $"\nNormalization failed for {errors.Count} file(s).")}",
                "OK");
        }
        catch (OperationCanceledException)
        {
            await DisplayAlertAsync("Normalize", "Canceled.", "OK");
        }
        finally
        {
            _currentProcess = null;
            _normalizeCts?.Dispose();
            _normalizeCts = null;
            IsNormalizing = false;
            CleanNormalizeTempFiles();
        }
    }

    async Task<(bool ok, string errorText)> ProcessSingleFileToOutputAsync(string ffmpegPath, string ffprobePath, string inputFile, string outputFile, CancellationToken token)
    {
        var ext = Path.GetExtension(inputFile);
        var encodingArgs = await BuildEncodingArgsFromFfprobeAsync(ffprobePath, inputFile, ext, token);
        var meas = await GetLoudnormMeasurementsAsync(ffmpegPath, inputFile, token);

        var filter =
            $"loudnorm=I=-16:TP=-1.5:LRA=11" +
            $":measured_I={meas.InputI.ToString(CultureInfo.InvariantCulture)}" +
            $":measured_TP={meas.InputTP.ToString(CultureInfo.InvariantCulture)}" +
            $":measured_LRA={meas.InputLRA.ToString(CultureInfo.InvariantCulture)}" +
            $":measured_thresh={meas.InputThresh.ToString(CultureInfo.InvariantCulture)}" +
            $":offset={meas.TargetOffset.ToString(CultureInfo.InvariantCulture)}" +
            $":linear=true:print_format=summary";

        var attenuationDb = GetOutputAttenuationDb();
        if (attenuationDb < 0)
            filter += $",volume={attenuationDb.ToString(CultureInfo.InvariantCulture)}dB";

        var arguments =
            $"-y -i \"{inputFile}\" -map 0:a:0 -vn -map_metadata 0 -af \"{filter}\" {encodingArgs} \"{outputFile}\"";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        _currentProcess = process;

        process.Start();

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch
            {
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            var error = (await process.StandardError.ReadToEndAsync()).Trim();
            if (string.IsNullOrWhiteSpace(error))
                error = $"Exit code: {process.ExitCode}";

            return (false, error);
        }

        return (true, string.Empty);
    }

    async Task<string> GeneratePreviewFileAsync(string ffmpegPath, string ffprobePath, string inputFile, CancellationToken token)
    {
        var ext = Path.GetExtension(inputFile);
        var previewRoot = GetPreviewTempRoot();
        var previewFile = Path.Combine(previewRoot, $"{Guid.NewGuid()}{ext}");

        var encodingArgs = await BuildEncodingArgsFromFfprobeAsync(ffprobePath, inputFile, ext, token);
        var meas = await GetLoudnormMeasurementsAsync(ffmpegPath, inputFile, token);

        var filter =
            $"loudnorm=I=-16:TP=-1.5:LRA=11" +
            $":measured_I={meas.InputI.ToString(CultureInfo.InvariantCulture)}" +
            $":measured_TP={meas.InputTP.ToString(CultureInfo.InvariantCulture)}" +
            $":measured_LRA={meas.InputLRA.ToString(CultureInfo.InvariantCulture)}" +
            $":measured_thresh={meas.InputThresh.ToString(CultureInfo.InvariantCulture)}" +
            $":offset={meas.TargetOffset.ToString(CultureInfo.InvariantCulture)}" +
            $":linear=true:print_format=summary";

        var attenuationDb = GetOutputAttenuationDb();
        if (attenuationDb < 0)
            filter += $",volume={attenuationDb.ToString(CultureInfo.InvariantCulture)}dB";

        var arguments =
            $"-y -i \"{inputFile}\" -map 0:a:0 -vn -map_metadata 0 -af \"{filter}\" {encodingArgs} \"{previewFile}\"";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        _previewProcess = process;

        process.Start();

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch
            {
            }

            TryDeleteFile(previewFile);
            throw;
        }

        if (process.ExitCode != 0)
        {
            TryDeleteFile(previewFile);
            throw new Exception("ffmpeg preview failed.");
        }

        return previewFile;
    }

    void LoadAudioFiles(string rootDirectory)
    {
        AudioFiles.Clear();

        foreach (var file in EnumerateAudioFilesSafe(rootDirectory))
            AudioFiles.Add(new AudioItem(file));
    }

    static IEnumerable<string> EnumerateAudioFilesSafe(string rootDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(rootDirectory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }

            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var dir in dirs)
                pending.Push(dir);
        }
    }

    static async Task<string?> PickFolderAsync()
    {
#if WINDOWS
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");

            var window = Application.Current?.Windows.FirstOrDefault();
            var platformWindow = window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (platformWindow is null)
                return null;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(platformWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch
        {
            return null;
        }
#else
        await Task.CompletedTask;
        return null;
#endif
    }

    void OnCancelClicked(object sender, EventArgs e)
    {
        _normalizeCts?.Cancel();

        try
        {
            if (_currentProcess is { HasExited: false })
                _currentProcess.Kill(true);
        }
        catch
        {
        }
    }

    static string GetNormalizeTempRoot()
    {
        var tempRoot = Path.Combine(FileSystem.CacheDirectory, "NormalizedTempFiles");
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    static string GetPreviewTempRoot()
    {
        var tempRoot = Path.Combine(FileSystem.CacheDirectory, "PreviewTempFiles");
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    static void CleanNormalizeTempFiles()
    {
        var tempRoot = GetNormalizeTempRoot();

        try
        {
            foreach (var file in Directory.EnumerateFiles(tempRoot))
                TryDeleteFile(file);
        }
        catch
        {
        }
    }

    static void CleanPreviewTempFiles()
    {
        var tempRoot = GetPreviewTempRoot();

        try
        {
            foreach (var file in Directory.EnumerateFiles(tempRoot))
                TryDeleteFile(file);
        }
        catch
        {
        }
    }

    void CleanupNormalizeTempOnStartup()
    {
        CleanNormalizeTempFiles();
    }

    void CleanupPreviewTempOnStartup()
    {
        CleanPreviewTempFiles();
    }

    static double ClampDb(double v)
    {
        if (v < OutputAttenuationMinDb) return OutputAttenuationMinDb;
        if (v > OutputAttenuationMaxDb) return OutputAttenuationMaxDb;
        return v;
    }

    double GetOutputAttenuationDb()
    {
        if (!TryParseDbInvariant(OutputAttenuationText, out var v))
            v = 0;

        v = ClampDb(v);
        if (v > 0) v = 0;
        return v;
    }

    void NormalizeOutputAttenuationText()
    {
        if (!TryParseDbInvariant(OutputAttenuationText, out var v))
        {
            SetOutputAttenuationTextFromValue(0);
            return;
        }

        v = ClampDb(v);
        if (v > 0) v = 0;
        SetOutputAttenuationTextFromValue(v);
    }

    void SetOutputAttenuationTextFromValue(double v)
    {
        var s = FormatDbForUi(v);
        if (OutputAttenuationText == s)
            return;

        _isUpdatingOutputAttenuationText = true;
        OutputAttenuationText = s;
        _isUpdatingOutputAttenuationText = false;
    }

    static double SnapToStep(double v, double step)
    {
        if (step <= 0) return v;
        return Math.Round(v / step, MidpointRounding.AwayFromZero) * step;
    }

    static string SanitizeDbText(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var s = input.Trim();

        var sb = new System.Text.StringBuilder(s.Length);
        var hasMinus = false;
        var hasSep = false;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];

            if (c == '-')
            {
                if (sb.Length == 0 && !hasMinus)
                {
                    sb.Append('-');
                    hasMinus = true;
                }
                continue;
            }

            if (c == '.' || c == ',')
            {
                if (!hasSep)
                {
                    sb.Append(c);
                    hasSep = true;
                }
                continue;
            }

            if (c >= '0' && c <= '9')
            {
                sb.Append(c);
                continue;
            }
        }

        var result = sb.ToString();

        if (result.Length > 1 && result[0] == '-' && (result[1] == '.' || result[1] == ','))
            result = "-0" + result.Substring(1);

        if (result.Length == 1 && (result[0] == '.' || result[0] == ','))
            result = "0" + result;

        return result;
    }

    static bool TryParseDbInvariant(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.Trim();
        if (s == "-" || s == "." || s == "," || s == "-." || s == "-,")
            return false;

        s = s.Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    static string FormatDbForUi(double v)
    {
        v = ClampDb(v);
        var culture = CultureInfo.CurrentCulture;

        var format = "0." + new string('#', OutputAttenuationMaxDecimals);
        var s = v.ToString(format, culture);

        var sep = culture.NumberFormat.NumberDecimalSeparator;
        if (s.Contains(sep, StringComparison.Ordinal))
            s = s.TrimEnd('0').TrimEnd(sep[0]);

        if (s == "-0")
            s = "0";

        return s;
    }

    sealed class FfprobeRoot
    {
        public FfprobeStream[]? streams { get; set; }
        public FfprobeFormat? format { get; set; }
    }

    sealed class FfprobeStream
    {
        public string? codec_type { get; set; }
        public string? codec_name { get; set; }
        public string? sample_fmt { get; set; }
        public string? sample_rate { get; set; }
        public int? channels { get; set; }
        public string? channel_layout { get; set; }
        public string? bit_rate { get; set; }
        public string? profile { get; set; }
    }

    sealed class FfprobeFormat
    {
        public string? bit_rate { get; set; }
        public string? format_name { get; set; }
    }

    static long? ParseLongInvariant(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return v;
        return null;
    }

    static string BitrateToFfmpegArg(long bitsPerSecond)
    {
        var kbps = (long)Math.Round(bitsPerSecond / 1000.0);
        if (kbps <= 0) kbps = 1;
        return $"{kbps}k";
    }

    static string? MapWavSampleFmtToPcmCodec(string? sampleFmt)
    {
        if (string.IsNullOrWhiteSpace(sampleFmt))
            return null;

        var sf = sampleFmt.Trim().ToLowerInvariant();
        if (sf.StartsWith("s16")) return "pcm_s16le";
        if (sf.StartsWith("s24")) return "pcm_s24le";
        if (sf.StartsWith("s32")) return "pcm_s32le";
        if (sf.StartsWith("s64")) return "pcm_s64le";
        if (sf.StartsWith("flt")) return "pcm_f32le";
        if (sf.StartsWith("dbl")) return "pcm_f64le";

        return null;
    }

    async Task<string> BuildEncodingArgsFromFfprobeAsync(string ffprobePath, string inputFile, string ext, CancellationToken token)
    {
        var args =
            $"-v error -select_streams a:0 -show_entries stream=codec_type,codec_name,bit_rate,sample_rate,channels,channel_layout,sample_fmt,profile -show_entries format=bit_rate,format_name -of json \"{inputFile}\"";

        var (exitCode, stdout, _) = await RunProcessCaptureAsync(ffprobePath, args, token);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            return string.Empty;

        FfprobeRoot? root;
        try
        {
            root = JsonSerializer.Deserialize<FfprobeRoot>(stdout);
        }
        catch
        {
            return string.Empty;
        }

        var stream = root?.streams?.FirstOrDefault(s => string.Equals(s.codec_type, "audio", StringComparison.OrdinalIgnoreCase));
        if (stream is null)
            return string.Empty;

        var br = ParseLongInvariant(stream.bit_rate) ?? ParseLongInvariant(root?.format?.bit_rate);
        var sr = ParseLongInvariant(stream.sample_rate);
        var ch = stream.channels;

        var extension = (ext ?? string.Empty).Trim().ToLowerInvariant();

        var parts = new List<string>();

        if (extension == ".mp3")
        {
            parts.Add("-c:a libmp3lame");
            if (br.HasValue && br.Value > 0)
                parts.Add($"-b:a {BitrateToFfmpegArg(br.Value)}");

            if (sr.HasValue && sr.Value > 0)
                parts.Add($"-ar {sr.Value.ToString(CultureInfo.InvariantCulture)}");

            if (ch.HasValue && ch.Value > 0)
                parts.Add($"-ac {ch.Value.ToString(CultureInfo.InvariantCulture)}");

            parts.Add("-id3v2_version 3");
        }
        else if (extension == ".wav")
        {
            var pcmCodec = MapWavSampleFmtToPcmCodec(stream.sample_fmt) ?? "pcm_s16le";
            parts.Add($"-c:a {pcmCodec}");

            if (sr.HasValue && sr.Value > 0)
                parts.Add($"-ar {sr.Value.ToString(CultureInfo.InvariantCulture)}");

            if (ch.HasValue && ch.Value > 0)
                parts.Add($"-ac {ch.Value.ToString(CultureInfo.InvariantCulture)}");
        }
        else
        {
            if (sr.HasValue && sr.Value > 0)
                parts.Add($"-ar {sr.Value.ToString(CultureInfo.InvariantCulture)}");

            if (ch.HasValue && ch.Value > 0)
                parts.Add($"-ac {ch.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        return string.Join(' ', parts);
    }

    async Task<(int exitCode, string stdout, string stderr)> RunProcessCaptureAsync(string exePath, string arguments, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };

        process.Start();

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch
            {
            }

            throw;
        }

        var stdout = string.Empty;
        var stderr = string.Empty;
        try { stdout = await process.StandardOutput.ReadToEndAsync(); } catch { }
        try { stderr = await process.StandardError.ReadToEndAsync(); } catch { }

        return (process.ExitCode, stdout, stderr);
    }

    sealed class LoudnormMeasurements
    {
        public double InputI { get; init; }
        public double InputTP { get; init; }
        public double InputLRA { get; init; }
        public double InputThresh { get; init; }
        public double TargetOffset { get; init; }
    }

    static readonly Regex LoudnormJsonRegex =
        new(@"\{[\s\S]*?""input_i""[\s\S]*?\}", RegexOptions.Compiled);

    async Task<LoudnormMeasurements> GetLoudnormMeasurementsAsync(string ffmpegPath, string inputFile, CancellationToken token)
    {
        var nullSink = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        var args =
            $"-hide_banner -nostats -i \"{inputFile}\" " +
            "-af loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json " +
            $"-f null {nullSink}";

        var (exitCode, _, stderr) = await RunProcessCaptureAsync(ffmpegPath, args, token);

        if (exitCode != 0)
        {
            var msg = (stderr ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(msg)) msg = $"Exit code: {exitCode}";
            throw new Exception($"ffmpeg loudnorm analysis failed: {msg}");
        }

        var json = ExtractLoudnormJson(stderr);
        return ParseLoudnormMeasurements(json);
    }

    static string ExtractLoudnormJson(string? ffmpegStderr)
    {
        if (string.IsNullOrWhiteSpace(ffmpegStderr))
            throw new Exception("ffmpeg analysis produced no stderr output.");

        var m = LoudnormJsonRegex.Match(ffmpegStderr);
        if (!m.Success)
            throw new Exception("Could not find loudnorm JSON in ffmpeg output.");

        return m.Value;
    }

    static LoudnormMeasurements ParseLoudnormMeasurements(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        static double GetDouble(JsonElement r, string name)
        {
            if (!r.TryGetProperty(name, out var p))
                throw new Exception($"Missing '{name}' in loudnorm JSON.");

            var s = p.GetString();
            if (string.IsNullOrWhiteSpace(s) ||
                !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                throw new Exception($"Invalid '{name}' value in loudnorm JSON: {s}");

            return v;
        }

        return new LoudnormMeasurements
        {
            InputI = GetDouble(root, "input_i"),
            InputTP = GetDouble(root, "input_tp"),
            InputLRA = GetDouble(root, "input_lra"),
            InputThresh = GetDouble(root, "input_thresh"),
            TargetOffset = GetDouble(root, "target_offset"),
        };
    }

    public enum AudioStatus
    {
        None = 0,
        Processing = 1,
        Succeeded = 2,
        Failed = 3
    }

    public sealed class AudioItem : INotifyPropertyChanged
    {
        readonly string fullPath;

        bool isChecked = true;
        public bool IsChecked
        {
            get => isChecked;
            set
            {
                if (isChecked == value) return;
                isChecked = value;
                OnPropertyChanged();
            }
        }

        AudioStatus status = AudioStatus.None;
        public AudioStatus Status
        {
            get => status;
            set
            {
                if (status == value) return;
                status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowStatus));
                OnPropertyChanged(nameof(IsProcessing));
                OnPropertyChanged(nameof(IsSucceeded));
                OnPropertyChanged(nameof(IsFailed));
            }
        }

        bool isPreviewGenerating;
        public bool IsPreviewGenerating
        {
            get => isPreviewGenerating;
            set
            {
                if (isPreviewGenerating == value) return;
                isPreviewGenerating = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PreviewButtonOpacity));
                OnPropertyChanged(nameof(IsPreviewEyeVisible));
                OnPropertyChanged(nameof(IsPreviewStopVisible));
            }
        }

        bool isPlayingSource;
        public bool IsPlayingSource
        {
            get => isPlayingSource;
            set
            {
                if (isPlayingSource == value) return;
                isPlayingSource = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNotPlayingSource));
            }
        }

        bool isPlayingPreview;
        public bool IsPlayingPreview
        {
            get => isPlayingPreview;
            set
            {
                if (isPlayingPreview == value) return;
                isPlayingPreview = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPreviewEyeVisible));
                OnPropertyChanged(nameof(IsPreviewStopVisible));
            }
        }

        public bool IsNotPlayingSource => !IsPlayingSource;

        public bool IsPreviewEyeVisible => !IsPreviewGenerating && !IsPlayingPreview;

        public bool IsPreviewStopVisible => !IsPreviewGenerating && IsPlayingPreview;

        public double PreviewButtonOpacity => IsPreviewGenerating ? 0.5 : 1.0;

        public bool ShowStatus => Status != AudioStatus.None;
        public bool IsProcessing => Status == AudioStatus.Processing;
        public bool IsSucceeded => Status == AudioStatus.Succeeded;
        public bool IsFailed => Status == AudioStatus.Failed;

        public string FileName { get; }
        public string FullPath => fullPath;

        public AudioItem(string path)
        {
            fullPath = path;
            FileName = Path.GetFileName(path);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}