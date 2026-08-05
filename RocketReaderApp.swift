import SwiftUI
import PDFKit
import AVFoundation
import UniformTypeIdentifiers

// MARK: - ORP
struct ORPCalculator {
    static func index(for word: String) -> Int {
        let len = word.filter { $0.isLetter }.count
        switch len {
        case 0, 1: return 0
        case 2...5: return 1
        case 6...9: return 2
        case 10...13: return 3
        default: return 4
        }
    }
    
    static func parts(for word: String) -> (String, String, String) {
        let chars = Array(word)
        guard !chars.isEmpty else { return ("", "", "") }
        let idx = min(index(for: word), chars.count - 1)
        let prefix = String(chars[0..<idx])
        let orp = String(chars[idx])
        let suffix = idx + 1 < chars.count ? String(chars[(idx+1)...]) : ""
        return (prefix, orp, suffix)
    }
}

// MARK: - Text Processing (Brian-style pauses)
struct TextProcessor {
    static func words(from text: String) -> [String] {
        text.components(separatedBy: .whitespacesAndNewlines)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
    }
    
    static func pauseMultiplier(for word: String) -> Double {
        let w = word.lowercased()
        if w.hasSuffix(".") || w.hasSuffix("!") || w.hasSuffix("?") { return 2.4 }
        if w.hasSuffix(",") || w.hasSuffix(";") || w.hasSuffix(":") { return 1.7 }
        if word.count >= 9 { return 1.35 }
        if word.count >= 6 { return 1.15 }
        return 1.0
    }
}

// MARK: - Metronome
@MainActor
class MetronomePlayer: ObservableObject {
    @Published var isEnabled = false
    private var player: AVAudioPlayer?
    
    func prepare() {
        // Uses system sound as fallback so no asset is required
        // For a real click, add "click.wav" to the project and load it here
    }
    
    func tick() {
        guard isEnabled else { return }
        AudioServicesPlaySystemSound(1104) // short click
    }
}

// MARK: - RSVP Engine with Warm-up
@MainActor
final class RSVPEngine: ObservableObject {
    @Published var currentIndex = 0
    @Published var isPlaying = false
    @Published var targetWPM: Double = 300
    @Published var currentWPM: Double = 300
    @Published var progress: Double = 0
    @Published var useWarmup = true
    
    private(set) var words: [String] = []
    private var timer: Timer?
    private let warmupWords = 40          // ramp over first 40 words
    
    var currentWord: String {
        currentIndex < words.count ? words[currentIndex] : ""
    }
    
    var hasText: Bool { !words.isEmpty }
    
    func load(_ text: String) {
        stop()
        words = TextProcessor.words(from: text)
        currentIndex = 0
        currentWPM = useWarmup ? max(180, targetWPM * 0.55) : targetWPM
        updateProgress()
    }
    
    func play() {
        guard hasText, currentIndex < words.count else { return }
        isPlaying = true
        scheduleNext()
    }
    
    func pause() {
        isPlaying = false
        timer?.invalidate()
        timer = nil
    }
    
    func stop() {
        pause()
        currentIndex = 0
        currentWPM = useWarmup ? max(180, targetWPM * 0.55) : targetWPM
        updateProgress()
    }
    
    func jump(to index: Int) {
        pause()
        currentIndex = max(0, min(index, words.count - 1))
        updateProgress()
    }
    
    func skip(by n: Int) { jump(to: currentIndex + n) }
    
    private func scheduleNext() {
        timer?.invalidate()
        guard isPlaying, currentIndex < words.count else {
            isPlaying = false
            return
        }
        
        // Warm-up ramp
        if useWarmup && currentIndex < warmupWords {
            let t = Double(currentIndex) / Double(warmupWords)
            currentWPM = max(180, targetWPM * 0.55) + (targetWPM - max(180, targetWPM * 0.55)) * t
        } else {
            currentWPM = targetWPM
        }
        
        let word = words[currentIndex]
        let baseMs = 60_000.0 / currentWPM
        let duration = baseMs * TextProcessor.pauseMultiplier(for: word)
        
        timer = Timer.scheduledTimer(withTimeInterval: duration / 1000.0, repeats: false) { [weak self] _ in
            Task { @MainActor in
                guard let self, self.isPlaying else { return }
                self.currentIndex += 1
                self.updateProgress()
                if self.currentIndex >= self.words.count {
                    self.isPlaying = false
                } else {
                    self.scheduleNext()
                }
            }
        }
    }
    
    private func updateProgress() {
        progress = words.isEmpty ? 0 : Double(currentIndex) / Double(words.count)
    }
}

// MARK: - Recent Texts Store
struct RecentText: Identifiable, Codable, Equatable {
    let id: UUID
    let title: String
    let body: String
    let date: Date
    
    init(title: String, body: String) {
        self.id = UUID()
        self.title = title
        self.body = body
        self.date = Date()
    }
}

@MainActor
class RecentStore: ObservableObject {
    @Published var items: [RecentText] = []
    private let key = "rocket.recent"
    
    init() { load() }
    
    func add(_ text: String, title: String = "Pasted Text") {
        let item = RecentText(title: title, body: text)
        items.removeAll { $0.body == text }
        items.insert(item, at: 0)
        if items.count > 12 { items = Array(items.prefix(12)) }
        save()
    }
    
    private func save() {
        if let data = try? JSONEncoder().encode(items) {
            UserDefaults.standard.set(data, forKey: key)
        }
    }
    
    private func load() {
        if let data = UserDefaults.standard.data(forKey: key),
           let decoded = try? JSONDecoder().decode([RecentText].self, from: data) {
            items = decoded
        }
    }
}

// MARK: - Reader UI
struct ReaderView: View {
    @ObservedObject var engine: RSVPEngine
    @ObservedObject var metronome: MetronomePlayer
    @Environment(\.dismiss) private var dismiss
    
    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            
            VStack(spacing: 0) {
                // Top bar
                HStack {
                    Button { dismiss() } label: {
                        Image(systemName: "xmark.circle.fill")
                            .font(.title2)
                            .foregroundStyle(.white.opacity(0.7))
                    }
                    Spacer()
                    Text("\(Int(engine.currentWPM)) WPM")
                        .font(.subheadline.monospacedDigit())
                        .foregroundStyle(.white.opacity(0.8))
                }
                .padding(.horizontal)
                .padding(.top, 12)
                
                ProgressView(value: engine.progress)
                    .tint(.red.opacity(0.85))
                    .padding(.horizontal)
                    .padding(.top, 8)
                
                Spacer()
                
                // Fixed focus + ORP
                ZStack {
                    HStack {
                        Rectangle().fill(.white.opacity(0.12)).frame(width: 1.5, height: 72)
                        Spacer()
                        Rectangle().fill(.white.opacity(0.12)).frame(width: 1.5, height: 72)
                    }
                    .frame(width: 170)
                    
                    let parts = ORPCalculator.parts(for: engine.currentWord)
                    HStack(spacing: 0) {
                        Text(parts.0).foregroundStyle(.white.opacity(0.85))
                        Text(parts.1).foregroundStyle(.red).fontWeight(.bold)
                        Text(parts.2).foregroundStyle(.white.opacity(0.85))
                    }
                    .font(.system(size: 44, weight: .medium, design: .rounded))
                    .lineLimit(1)
                    .minimumScaleFactor(0.45)
                }
                .frame(height: 120)
                
                Spacer()
                
                // Controls
                VStack(spacing: 18) {
                    Slider(value: $engine.targetWPM, in: 150...1200, step: 25)
                        .tint(.red)
                        .padding(.horizontal, 36)
                        .onChange(of: engine.targetWPM) { _, new in
                            if !engine.useWarmup || engine.currentIndex >= 40 {
                                engine.currentWPM = new
                            }
                        }
                    
                    HStack(spacing: 40) {
                        Button { engine.skip(by: -8) } label: {
                            Image(systemName: "backward.fill").font(.title2)
                        }
                        
                        Button {
                            if engine.isPlaying {
                                engine.pause()
                            } else {
                                engine.play()
                            }
                            metronome.tick()
                        } label: {
                            Image(systemName: engine.isPlaying ? "pause.circle.fill" : "play.circle.fill")
                                .font(.system(size: 68))
                                .foregroundStyle(.red)
                        }
                        
                        Button { engine.skip(by: 8) } label: {
                            Image(systemName: "forward.fill").font(.title2)
                        }
                    }
                    .foregroundStyle(.white)
                    
                    Toggle("Metronome", isOn: $metronome.isEnabled)
                        .tint(.red)
                        .foregroundStyle(.white.opacity(0.85))
                        .padding(.horizontal, 70)
                    
                    Toggle("Warm-up", isOn: $engine.useWarmup)
                        .tint(.red)
                        .foregroundStyle(.white.opacity(0.85))
                        .padding(.horizontal, 70)
                }
                .padding(.bottom, 36)
            }
        }
        .preferredColorScheme(.dark)
        .statusBarHidden(true)
        .onAppear { metronome.prepare() }
    }
}

// MARK: - Main App
@main
struct RocketReaderApp: App {
    var body: some Scene {
        WindowGroup {
            ContentView()
        }
    }
}

struct ContentView: View {
    @StateObject private var engine = RSVPEngine()
    @StateObject private var metronome = MetronomePlayer()
    @StateObject private var recent = RecentStore()
    
    @AppStorage("rocket.lastWPM") private var savedWPM: Double = 300
    
    @State private var inputText = ""
    @State private var showingReader = false
    @State private var showingImporter = false
    
    var body: some View {
        NavigationStack {
            VStack(spacing: 16) {
                TextEditor(text: $inputText)
                    .frame(minHeight: 180)
                    .padding(10)
                    .background(Color(.systemGray6))
                    .cornerRadius(12)
                    .padding(.horizontal)
                
                HStack(spacing: 12) {
                    Button {
                        engine.targetWPM = savedWPM
                        engine.load(inputText)
                        recent.add(inputText)
                        showingReader = true
                    } label: {
                        Label("Start Rocket Reader", systemImage: "rocket.fill")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(.red)
                    .disabled(inputText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    
                    Button {
                        showingImporter = true
                    } label: {
                        Image(systemName: "doc.badge.plus")
                    }
                    .buttonStyle(.bordered)
                }
                .padding(.horizontal)
                
                if !recent.items.isEmpty {
                    List {
                        Section("Recent") {
                            ForEach(recent.items) { item in
                                Button {
                                    inputText = item.body
                                    engine.targetWPM = savedWPM
                                    engine.load(item.body)
                                    showingReader = true
                                } label: {
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(item.title)
                                            .font(.subheadline.weight(.medium))
                                        Text(item.body.prefix(80) + "…")
                                            .font(.caption)
                                            .foregroundStyle(.secondary)
                                            .lineLimit(2)
                                    }
                                }
                            }
                            .onDelete { indexSet in
                                recent.items.remove(atOffsets: indexSet)
                            }
                        }
                    }
                    .listStyle(.plain)
                }
                
                Spacer()
            }
            .navigationTitle("Rocket Reader")
            .onAppear {
                engine.targetWPM = savedWPM
            }
            .onChange(of: engine.targetWPM) { _, new in
                savedWPM = new
            }
            .fullScreenCover(isPresented: $showingReader) {
                ReaderView(engine: engine, metronome: metronome)
            }
            .fileImporter(
                isPresented: $showingImporter,
                allowedContentTypes: [.pdf, .plainText],
                allowsMultipleSelection: false
            ) { result in
                switch result {
                case .success(let urls):
                    guard let url = urls.first else { return }
                    let access = url.startAccessingSecurityScopedResource()
                    defer { if access { url.stopAccessingSecurityScopedResource() } }
                    
                    if url.pathExtension.lowercased() == "pdf" {
                        let text = extractPDFText(url)
                        inputText = text
                        recent.add(text, title: url.lastPathComponent)
                    } else if let text = try? String(contentsOf: url, encoding: .utf8) {
                        inputText = text
                        recent.add(text, title: url.lastPathComponent)
                    }
                case .failure:
                    break
                }
            }
            .onOpenURL { url in
                // Handle Share Extension / deep link
                if url.scheme == "rocketreader",
                   let components = URLComponents(url: url, resolvingAgainstBaseURL: false),
                   let text = components.queryItems?.first(where: { $0.name == "text" })?.value {
                    inputText = text
                    engine.targetWPM = savedWPM
                    engine.load(text)
                    recent.add(text, title: "Shared Text")
                    showingReader = true
                }
            }
        }
    }
    
    private func extractPDFText(_ url: URL) -> String {
        guard let doc = PDFDocument(url: url) else { return "" }
        var result = ""
        for i in 0..<doc.pageCount {
            if let page = doc.page(at: i), let s = page.string {
                result += s + "\n"
            }
        }
        return result
    }
}