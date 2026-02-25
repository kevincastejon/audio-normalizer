using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AudioNormalizer;

public partial class MainPage : ContentPage, INotifyPropertyChanged
{
    private CancellationTokenSource? _normalizeCts;
    private Process? _currentProcess;

    private bool _isNormalizing;
    public bool IsNormalizing
    {
        get => _isNormalizing;
        set
        {
            if (_isNormalizing == value) return;
            _isNormalizing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotNormalizing));
        }
    }

    public bool IsNotNormalizing => !IsNormalizing;

    private string _outputPostfix = "_Normalized";
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

    public MainPage()
    {
        InitializeComponent();
        BindingContext = this;

        CleanupNormalizeTempOnStartup();

        AudioFiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(ShowNoFilesMessage));
        };
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

    async void OnNormalizeClicked(object sender, EventArgs e)
    {
        if (IsNormalizing) return;

        var ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");

        if (!File.Exists(ffmpegPath))
        {
            await DisplayAlertAsync("FFmpeg", "ffmpeg.exe not found.", "OK");
            return;
        }

        var selectedFiles = AudioFiles
            .Where(f => f.IsChecked)
            .Select(f => f.FullPath)
            .ToList();

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

        _normalizeCts = new CancellationTokenSource();
        IsNormalizing = true;

        var tempRoot = GetNormalizeTempRoot();
        List<(string, string)> errors = new();
        try
        {
            foreach (var file in selectedFiles)
            {
                _normalizeCts.Token.ThrowIfCancellationRequested();

                var dir = Path.GetDirectoryName(file)!;
                var name = Path.GetFileNameWithoutExtension(file);
                var ext = Path.GetExtension(file);

                var outputPath = overwrite
                    ? file
                    : Path.Combine(dir, $"{name}{postfix}{ext}");

                var tempFile = Path.Combine(tempRoot, $"{Guid.NewGuid()}{ext}");

                var arguments = $"-y -i \"{file}\" -af loudnorm=I=-16:TP=-1.5:LRA=11 \"{tempFile}\"";

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
                    await process.WaitForExitAsync(_normalizeCts.Token);
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

                    TryDeleteFile(tempFile);
                    throw;
                }

                if (process.ExitCode != 0)
                {
                    var error = (await process.StandardError.ReadToEndAsync()).Trim();
                    if (string.IsNullOrWhiteSpace(error))
                        error = $"Exit code: {process.ExitCode}";

                    TryDeleteFile(tempFile);
                    errors.Add((file, error));
                    continue;
                }

                if (overwrite)
                {
                    File.Delete(file);
                    File.Move(tempFile, file);
                }
                else
                {
                    try
                    {
                        if (File.Exists(outputPath))
                            File.Delete(outputPath);
                    }
                    catch
                    {
                    }

                    File.Move(tempFile, outputPath);
                }
            }

            await DisplayAlertAsync("Normalize", $"{selectedFiles.Count - errors.Count} audio file(s) normalized.{(errors.Count == 0 ? "" : $"\nNormalization failed for {errors.Count} file(s).")}", "OK");
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

    void CleanupNormalizeTempOnStartup()
    {
        CleanNormalizeTempFiles();
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

        public string DisplayText { get; }

        public string FullPath => fullPath;

        public AudioItem(string path)
        {
            fullPath = path;
            var name = Path.GetFileName(path);
            DisplayText = $"{name} ({fullPath})";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}