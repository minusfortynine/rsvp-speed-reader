using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using UglyToad.PdfPig;
using System.Xml.Linq;

namespace RocketReader.Windows;

public partial class MainWindow : Window
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsDisplayRequired = 0x00000002;
    private const uint EsSystemRequired = 0x00000001;
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
        Closed += MainWindow_Closed;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        RestoreExecutionState();
        viewModel.Dispose();
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
        PreventSleepWhileFullscreen();
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
        RestoreExecutionState();
    }

    private static void PreventSleepWhileFullscreen()
    {
        SetThreadExecutionState(EsContinuous | EsDisplayRequired | EsSystemRequired);
    }

    private static void RestoreExecutionState()
    {
        SetThreadExecutionState(EsContinuous);
    }

    private void StartReader_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            viewModel.StartReader();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Start failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Supported Files|*.txt;*.pdf;*.epub|Text Files|*.txt|PDF Files|*.pdf|EPUB Files|*.epub",
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
    private readonly HttpClient httpClient = new();
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

        string sourceText = InputText;
        string title = "Pasted Text";

        if (TryGetWebsiteUri(InputText, out Uri websiteUri))
        {
            sourceText = LoadFromWebsite(websiteUri);
            title = $"Website: {websiteUri.Host}";
            InputText = sourceText;
        }

        LoadText(sourceText, addToRecents: true, title: title);
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
            : Path.GetExtension(path).Equals(".epub", StringComparison.OrdinalIgnoreCase)
                ? ExtractEpubText(path)
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
        httpClient.Dispose();
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

    private string LoadFromWebsite(Uri websiteUri)
    {
        using HttpResponseMessage response = httpClient.GetAsync(websiteUri).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        string responseText = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        string readableText = ExtractReadableText(responseText);

        if (string.IsNullOrWhiteSpace(readableText))
        {
            throw new InvalidOperationException("No readable text was found at that website.");
        }

        return readableText;
    }

    private static bool TryGetWebsiteUri(string input, out Uri websiteUri)
    {
        string candidate = input.Trim();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Any(char.IsWhiteSpace))
        {
            websiteUri = null!;
            return false;
        }

        string uriCandidate = candidate;
        if (!candidate.Contains("://", StringComparison.Ordinal) &&
            (candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || candidate.Contains('.')))
        {
            uriCandidate = "https://" + candidate;
        }

        if (Uri.TryCreate(uriCandidate, UriKind.Absolute, out Uri? parsedUri) &&
            (parsedUri.Scheme == Uri.UriSchemeHttp || parsedUri.Scheme == Uri.UriSchemeHttps))
        {
            websiteUri = parsedUri;
            return true;
        }

        websiteUri = null!;
        return false;
    }

    private static string ExtractReadableText(string content)
    {
        string text = Regex.Replace(content, "(?is)<(script|style|noscript|head)[^>]*>.*?</\\1>", " ");
        text = Regex.Replace(text, "(?i)<br\\s*/?>", "\n");
        text = Regex.Replace(text, "(?i)</(p|div|h[1-6]|li|tr|section|article|header|footer|blockquote|pre)>", "\n");
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    private string ExtractEpubText(string path)
    {
        using FileStream fileStream = File.OpenRead(path);
        using ZipArchive archive = new(fileStream, ZipArchiveMode.Read);

        ZipArchiveEntry? containerEntry = archive.GetEntry("META-INF/container.xml");
        if (containerEntry is null)
        {
            throw new InvalidOperationException("The EPUB container metadata could not be found.");
        }

        string packagePath;
        using (Stream containerStream = containerEntry.Open())
        {
            XDocument containerDocument = XDocument.Load(containerStream);
            XNamespace containerNamespace = "urn:oasis:names:tc:opendocument:xmlns:container";
            packagePath = containerDocument
                .Descendants(containerNamespace + "rootfile")
                .Select(rootfile => (string?)rootfile.Attribute("full-path"))
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
                ?? throw new InvalidOperationException("The EPUB package document could not be found.");
        }

        ZipArchiveEntry? packageEntry = archive.GetEntry(NormalizeZipPath(packagePath));
        if (packageEntry is null)
        {
            throw new InvalidOperationException("The EPUB package document could not be opened.");
        }

        using Stream packageStream = packageEntry.Open();
        XDocument packageDocument = XDocument.Load(packageStream);
        XNamespace opfNamespace = "http://www.idpf.org/2007/opf";

        XElement? manifest = packageDocument.Root?.Element(opfNamespace + "manifest");
        XElement? spine = packageDocument.Root?.Element(opfNamespace + "spine");
        if (manifest is null || spine is null)
        {
            throw new InvalidOperationException("The EPUB content structure is invalid.");
        }

        var manifestItems = manifest
            .Elements(opfNamespace + "item")
            .Select(item => new
            {
                Id = (string?)item.Attribute("id"),
                Href = (string?)item.Attribute("href")
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href))
            .ToDictionary(item => item.Id!, item => item.Href!, StringComparer.Ordinal);

        string packageDirectory = Path.GetDirectoryName(packagePath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var chapters = new List<string>();

        foreach (XElement itemRef in spine.Elements(opfNamespace + "itemref"))
        {
            string? idRef = (string?)itemRef.Attribute("idref");
            if (string.IsNullOrWhiteSpace(idRef) || !manifestItems.TryGetValue(idRef, out string? href))
            {
                continue;
            }

            string entryPath = ResolveZipEntryPath(packageDirectory, href);
            ZipArchiveEntry? chapterEntry = archive.GetEntry(entryPath);
            if (chapterEntry is null)
            {
                continue;
            }

            chapters.Add(ExtractXhtmlText(chapterEntry));
        }

        return string.Join(Environment.NewLine, chapters.Where(chapter => !string.IsNullOrWhiteSpace(chapter)));
    }

    private static string ExtractXhtmlText(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        XDocument document = XDocument.Load(stream);
        var builder = new StringBuilder();

        if (document.Root is not null)
        {
            AppendText(document.Root, builder);
        }

        return Regex.Replace(builder.ToString(), "\\s+", " ").Trim();
    }

    private static void AppendText(XNode node, StringBuilder builder)
    {
        if (node is XText text)
        {
            builder.Append(text.Value);
            return;
        }

        if (node is XCData cdata)
        {
            builder.Append(cdata.Value);
            return;
        }

        if (node is not XElement element)
        {
            return;
        }

        string localName = element.Name.LocalName;
        if (localName is "script" or "style")
        {
            return;
        }

        bool isBlockElement = localName is "article" or "aside" or "blockquote" or "br" or "div" or "footer" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "header" or "li" or "nav" or "p" or "pre" or "section" or "td" or "th" or "tr";
        if (isBlockElement && builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
        {
            builder.Append(' ');
        }

        foreach (XNode child in element.Nodes())
        {
            AppendText(child, builder);
        }

        if (isBlockElement && builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
        {
            builder.Append(' ');
        }
    }

    private static string ResolveZipEntryPath(string baseDirectory, string relativePath)
    {
        string normalizedBaseDirectory = baseDirectory.Replace('/', Path.DirectorySeparatorChar);
        string normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string combinedPath = string.IsNullOrWhiteSpace(normalizedBaseDirectory)
            ? normalizedRelativePath
            : Path.Combine(normalizedBaseDirectory, normalizedRelativePath);

        string fullPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "epub", combinedPath));
        string relativeToTemp = Path.GetRelativePath(Path.Combine(Path.GetTempPath(), "epub"), fullPath);
        return relativeToTemp.Replace(Path.DirectorySeparatorChar, '/').TrimStart('/');
    }

    private static string NormalizeZipPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
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