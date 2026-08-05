using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using UglyToad.PdfPig;

namespace RocketReader.Windows;

public partial class MainWindow : Window
{
    private readonly ReaderViewModel viewModel = new();
    private bool isFullscreen;
    private WindowState restoreWindowState;
    private WindowStyle restoreWindowStyle;
    private ResizeMode restoreResizeMode;
    private bool restoreTopmost;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && isFullscreen)
        {
            ExitFullscreen();
            e.Handled = true;
        }
    }

    private void ToggleFullscreen()
    {
        if (isFullscreen)
        {
            ExitFullscreen();
            return;
        }

        restoreWindowState = WindowState;
        restoreWindowStyle = WindowStyle;
        restoreResizeMode = ResizeMode;
        restoreTopmost = Topmost;

        isFullscreen = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        WindowState = WindowState.Maximized;
    }

    private void ExitFullscreen()
    {
        if (!isFullscreen)
        {
            return;
        }

        isFullscreen = false;
        Topmost = restoreTopmost;
        WindowStyle = restoreWindowStyle;
        ResizeMode = restoreResizeMode;
        WindowState = restoreWindowState;
    }

    private void StartReader_Click(object sender, RoutedEventArgs e)
    {
        viewModel.StartReader();
    }

    private void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Supported Files|*.txt;*.pdf|Text Files|*.txt|PDF Files|*.pdf",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            viewModel.LoadFromFile(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RecentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: RecentEntry selected } listBox)
        {
            return;
        }

        viewModel.LoadRecent(selected);
        listBox.SelectedItem = null;
    }

    private void ExitReader_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ExitReader();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        viewModel.TogglePlayback();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        viewModel.Skip(-8);
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        viewModel.Skip(8);
    }
}

public sealed class ReaderViewModel : INotifyPropertyChanged, IDisposable
{
    private const int WarmupWords = 40;
    private readonly DispatcherTimer timer;
    private readonly string storagePath;
    private string currentOrp = string.Empty;
    private string currentPrefix = string.Empty;
    private string currentSuffix = string.Empty;
    private double targetWpm = 300;
    private double currentWpm = 300;
    private int currentIndex;
    private string inputText = string.Empty;
    private bool isReaderVisible;
    private bool isPlaying;
    private bool isMetronomeEnabled;
    private double progressPercent;
    private bool useWarmup = true;
    private string[] words = Array.Empty<string>();

    public ReaderViewModel()
    {
        timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            IsEnabled = false
        };
        timer.Tick += OnTick;

        storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RocketReader",
            "reader-state.json");

        RecentItems = new ObservableCollection<RecentEntry>();
        LoadState();
        ResetCurrentWpm();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RecentEntry> RecentItems { get; }

    public string InputText
    {
        get => inputText;
        set
        {
            if (SetField(ref inputText, value))
            {
                OnPropertyChanged(nameof(WordCount));
            }
        }
    }

    public bool IsReaderVisible
    {
        get => isReaderVisible;
        private set
        {
            if (SetField(ref isReaderVisible, value))
            {
                OnPropertyChanged(nameof(IsEditorVisible));
            }
        }
    }

    public bool IsEditorVisible => !IsReaderVisible;

    public bool IsPlaying
    {
        get => isPlaying;
        private set
        {
            if (SetField(ref isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseLabel));
            }
        }
    }

    public bool IsMetronomeEnabled
    {
        get => isMetronomeEnabled;
        set => SetField(ref isMetronomeEnabled, value);
    }

    public bool UseWarmup
    {
        get => useWarmup;
        set
        {
            if (SetField(ref useWarmup, value))
            {
                ResetCurrentWpm();
                OnPropertyChanged(nameof(WarmupStatus));
            }
        }
    }

    public double TargetWpm
    {
        get => targetWpm;
        set
        {
            if (SetField(ref targetWpm, value))
            {
                if (!UseWarmup || currentIndex >= WarmupWords)
                {
                    CurrentWpm = targetWpm;
                }

                OnPropertyChanged(nameof(TargetWpmLabel));
                SaveState();
            }
        }
    }

    public double ProgressPercent
    {
        get => progressPercent;
        set => SetField(ref progressPercent, value);
    }

    public string CurrentWpmLabel => $"{Math.Round(CurrentWpm)} WPM";

    public string TargetWpmLabel => $"{Math.Round(TargetWpm)} WPM";

    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    public string CurrentPrefix
    {
        get => currentPrefix;
        set => SetField(ref currentPrefix, value);
    }

    public string CurrentOrp
    {
        get => currentOrp;
        set => SetField(ref currentOrp, value);
    }

    public string CurrentSuffix
    {
        get => currentSuffix;
        set => SetField(ref currentSuffix, value);
    }

    public int WordCount => TextProcessor.WordsFrom(InputText).Length;

    public string WarmupStatus => UseWarmup ? "On" : "Off";

    private double CurrentWpm
    {
        get => currentWpm;
        set
        {
            if (SetField(ref currentWpm, value))
            {
                OnPropertyChanged(nameof(CurrentWpmLabel));
            }
        }
    }

    private string CurrentWord => currentIndex >= 0 && currentIndex < words.Length ? words[currentIndex] : string.Empty;

    public void StartReader()
    {
        if (string.IsNullOrWhiteSpace(InputText))
        {
            return;
        }

        LoadText(InputText, addToRecents: true, title: "Pasted Text");
        IsReaderVisible = true;
    }

    public void ExitReader()
    {
        Pause();
        IsReaderVisible = false;
    }

    public void TogglePlayback()
    {
        if (IsPlaying)
        {
            Pause();
            return;
        }

        Play();
        TickMetronome();
    }

    public void Skip(int amount)
    {
        if (words.Length == 0)
        {
            return;
        }

        Pause();
        currentIndex = Math.Clamp(currentIndex + amount, 0, words.Length - 1);
        NotifyReaderState();
    }

    public void LoadRecent(RecentEntry entry)
    {
        InputText = entry.Body;
        LoadText(entry.Body, addToRecents: false, title: entry.Title);
        IsReaderVisible = true;
    }

    public void LoadFromFile(string path)
    {
        string text = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? ExtractPdfText(path)
            : File.ReadAllText(path);

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("No readable text was found in that file.");
        }

        InputText = text;
        LoadText(text, addToRecents: true, title: Path.GetFileName(path));
        IsReaderVisible = true;
    }

    public void Dispose()
    {
        timer.Stop();
        timer.Tick -= OnTick;
    }

    private void LoadText(string text, bool addToRecents, string title)
    {
        Pause();
        words = TextProcessor.WordsFrom(text);
        currentIndex = 0;
        ResetCurrentWpm();

        if (addToRecents)
        {
            AddRecent(title, text);
        }

        NotifyReaderState();
        SaveState();
    }

    private void Play()
    {
        if (words.Length == 0 || currentIndex >= words.Length)
        {
            return;
        }

        IsPlaying = true;
        ScheduleNext();
    }

    private void Pause()
    {
        IsPlaying = false;
        timer.Stop();
    }

    private void ScheduleNext()
    {
        timer.Stop();

        if (!IsPlaying || currentIndex >= words.Length)
        {
            IsPlaying = false;
            return;
        }

        if (UseWarmup && currentIndex < WarmupWords)
        {
            double startWpm = Math.Max(180, TargetWpm * 0.55);
            double progress = (double)currentIndex / WarmupWords;
            CurrentWpm = startWpm + ((TargetWpm - startWpm) * progress);
        }
        else
        {
            CurrentWpm = TargetWpm;
        }

        double baseMilliseconds = 60000.0 / CurrentWpm;
        double interval = baseMilliseconds * TextProcessor.PauseMultiplierFor(CurrentWord);
        timer.Interval = TimeSpan.FromMilliseconds(interval);
        timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        timer.Stop();

        if (!IsPlaying)
        {
            return;
        }

        currentIndex++;
        NotifyReaderState();

        if (currentIndex >= words.Length)
        {
            IsPlaying = false;
            return;
        }

        TickMetronome();
        ScheduleNext();
    }

    private void ResetCurrentWpm()
    {
        CurrentWpm = UseWarmup ? Math.Max(180, TargetWpm * 0.55) : TargetWpm;
    }

    private void TickMetronome()
    {
        if (IsMetronomeEnabled)
        {
            SystemSounds.Asterisk.Play();
        }
    }

    private void NotifyReaderState()
    {
        var parts = OrpCalculator.PartsFor(CurrentWord);
        CurrentPrefix = parts.prefix;
        CurrentOrp = parts.orp;
        CurrentSuffix = parts.suffix;
        ProgressPercent = words.Length == 0 ? 0 : 100.0 * currentIndex / words.Length;
    }

    private void AddRecent(string title, string body)
    {
        for (int index = RecentItems.Count - 1; index >= 0; index--)
        {
            if (RecentItems[index].Body == body)
            {
                RecentItems.RemoveAt(index);
            }
        }

        RecentItems.Insert(0, new RecentEntry(title, body, DateTime.UtcNow));

        while (RecentItems.Count > 12)
        {
            RecentItems.RemoveAt(RecentItems.Count - 1);
        }
    }

    private string ExtractPdfText(string path)
    {
        using PdfDocument document = PdfDocument.Open(path);
        return string.Join(Environment.NewLine, document.GetPages().Select(page => page.Text));
    }

    private void LoadState()
    {
        if (!File.Exists(storagePath))
        {
            return;
        }

        try
        {
            PersistedState? state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(storagePath));
            if (state is null)
            {
                return;
            }

            targetWpm = state.TargetWpm <= 0 ? 300 : state.TargetWpm;
            useWarmup = state.UseWarmup;
            isMetronomeEnabled = state.IsMetronomeEnabled;
            inputText = state.LastText ?? string.Empty;

            RecentItems.Clear();
            foreach (RecentEntry item in state.Recents ?? Enumerable.Empty<RecentEntry>())
            {
                RecentItems.Add(item);
            }
        }
        catch
        {
        }

        OnPropertyChanged(nameof(InputText));
        OnPropertyChanged(nameof(TargetWpm));
        OnPropertyChanged(nameof(TargetWpmLabel));
        OnPropertyChanged(nameof(WordCount));
        OnPropertyChanged(nameof(WarmupStatus));
        OnPropertyChanged(nameof(IsMetronomeEnabled));
    }

    private void SaveState()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
        PersistedState state = new()
        {
            TargetWpm = TargetWpm,
            UseWarmup = UseWarmup,
            IsMetronomeEnabled = IsMetronomeEnabled,
            LastText = InputText,
            Recents = RecentItems.ToList()
        };

        File.WriteAllText(storagePath, JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class PersistedState
    {
        public double TargetWpm { get; set; }

        public bool UseWarmup { get; set; } = true;

        public bool IsMetronomeEnabled { get; set; }

        public string? LastText { get; set; }

        public List<RecentEntry>? Recents { get; set; }
    }
}

public sealed record RecentEntry(string Title, string Body, DateTime Date)
{
    public string Preview => Body.Length <= 120 ? Body : Body[..120] + "...";
}

public static class OrpCalculator
{
    public static int IndexFor(string word)
    {
        int letterCount = word.Count(char.IsLetter);
        return letterCount switch
        {
            <= 1 => 0,
            <= 5 => 1,
            <= 9 => 2,
            <= 13 => 3,
            _ => 4
        };
    }

    public static (string prefix, string orp, string suffix) PartsFor(string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        int index = Math.Min(IndexFor(word), word.Length - 1);
        return (
            word[..index],
            word[index].ToString(),
            index + 1 < word.Length ? word[(index + 1)..] : string.Empty);
    }
}

public static class TextProcessor
{
    public static string[] WordsFrom(string text)
    {
        return text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static double PauseMultiplierFor(string word)
    {
        string lowered = word.ToLowerInvariant();

        if (lowered.EndsWith('.') || lowered.EndsWith('!') || lowered.EndsWith('?'))
        {
            return 2.4;
        }

        if (lowered.EndsWith(',') || lowered.EndsWith(';') || lowered.EndsWith(':'))
        {
            return 1.7;
        }

        if (word.Length >= 9)
        {
            return 1.35;
        }

        if (word.Length >= 6)
        {
            return 1.15;
        }

        return 1.0;
    }
}