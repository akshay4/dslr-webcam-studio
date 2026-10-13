// SwiftUI window: the live preview on the left, an inspector with camera, output, image and
// virtual camera settings on the right, and Start / Snapshot / Diagnostics in the toolbar.
import AppKit
import SwiftUI

// ---- preview ----------------------------------------------------------------------------------

// Layer-backed view showing the output frame scaled to fit (the engine draws CGImages into it
// directly, without going through SwiftUI for every frame).
final class PreviewNSView: NSView {
    private(set) var isEmpty = true

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor
        layer?.contentsGravity = .resizeAspect
        layer?.cornerRadius = 10
        layer?.masksToBounds = true
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    func show(_ image: CGImage?) {
        layer?.contents = image
        isEmpty = image == nil
    }
}

struct PreviewView: NSViewRepresentable {
    let view: PreviewNSView
    func makeNSView(context: Context) -> PreviewNSView { view }
    func updateNSView(_ nsView: PreviewNSView, context: Context) {}
}

struct PreviewPane: View {
    @ObservedObject var model: AppModel

    var body: some View {
        ZStack {
            PreviewView(view: model.preview)
            if !model.hasPicture { placeholder }
        }
        .overlay(alignment: .topLeading) { StatusBadge(model: model).padding(12) }
        .overlay(alignment: .topTrailing) {
            if model.running {
                Chip(text: "\(model.settings.width)×\(model.settings.height) · \(model.settings.fps) fps").padding(12)
            }
        }
        .overlay(alignment: .bottom) {
            VStack(spacing: 8) {
                if let n = model.notice { NoticeBanner(text: n) }
                if model.running && model.hasPicture { StatsBar(stats: model.stats, vcam: model.vcamConnected) }
            }
            .padding(12)
            .animation(.easeOut(duration: 0.2), value: model.notice)
        }
        .padding(12)
    }

    private var placeholder: some View {
        VStack(spacing: 14) {
            Image(systemName: model.running ? "camera.aperture" : "video.slash")
                .font(.system(size: 44, weight: .light))
                .foregroundColor(.white.opacity(0.45))
            Text(model.running ? model.phase.title + "…" : "Camera stopped")
                .font(.title3.weight(.semibold))
                .foregroundColor(.white.opacity(0.85))
            if !model.detail.isEmpty {
                Text(model.detail)
                    .font(.callout)
                    .foregroundColor(.white.opacity(0.6))
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 420)
            }
            if !model.running {
                Button(action: model.start) { Label("Start", systemImage: "play.fill").frame(minWidth: 90) }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .disabled(model.diagnosing)
            } else if model.isCamera && model.canonServiceRunning && model.phase != .live {
                Button("Pause Canon Webcam Service", action: model.pauseCanonService)
                    .controlSize(.large)
            }
        }
        .padding(24)
    }
}

struct StatusBadge: View {
    @ObservedObject var model: AppModel
    @State private var pulse = false

    var body: some View {
        HStack(spacing: 7) {
            Circle()
                .fill(model.phase.color)
                .frame(width: 8, height: 8)
                .opacity(model.phase == .live && pulse ? 0.35 : 1)
                .animation(model.phase == .live ? .easeInOut(duration: 0.9).repeatForever() : .default, value: pulse)
            Text(model.phase.title.uppercased())
                .font(.caption.weight(.bold))
                .tracking(0.6)
            if !model.model.isEmpty {
                Text(model.model).font(.caption).foregroundColor(.secondary)
            }
        }
        .padding(.horizontal, 10).padding(.vertical, 5)
        .background(.ultraThinMaterial, in: Capsule())
        .environment(\.colorScheme, .dark)
        .onAppear { pulse = true }
    }
}

struct Chip: View {
    let text: String
    var body: some View {
        Text(text)
            .font(.caption.monospacedDigit())
            .padding(.horizontal, 10).padding(.vertical, 5)
            .background(.ultraThinMaterial, in: Capsule())
            .environment(\.colorScheme, .dark)
    }
}

struct StatsBar: View {
    let stats: OutputStats
    let vcam: Bool

    var body: some View {
        HStack(spacing: 14) {
            stat("Output", String(format: "%.1f fps", stats.outFps))
            stat("Camera", String(format: "%.1f fps", stats.cameraFps))
            stat("Repeated", "\(stats.repeated)")
            stat("Dropped", "\(stats.dropped)")
            if vcam { Label("Virtual camera", systemImage: "video.fill").font(.caption).foregroundColor(.green) }
        }
        .padding(.horizontal, 12).padding(.vertical, 6)
        .background(.ultraThinMaterial, in: Capsule())
        .environment(\.colorScheme, .dark)
    }

    private func stat(_ name: String, _ value: String) -> some View {
        HStack(spacing: 4) {
            Text(name).foregroundColor(.secondary)
            Text(value).monospacedDigit()
        }
        .font(.caption)
    }
}

struct NoticeBanner: View {
    let text: String
    var body: some View {
        Label(text, systemImage: "info.circle.fill")
            .font(.callout)
            .lineLimit(2)
            .padding(.horizontal, 14).padding(.vertical, 8)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            .transition(.move(edge: .bottom).combined(with: .opacity))
    }
}

// ---- inspector --------------------------------------------------------------------------------

struct InspectorSection<Content: View>: View {
    let title: String
    let symbol: String
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Label(title, systemImage: symbol)
                .font(.subheadline.weight(.semibold))
                .foregroundColor(.secondary)
            VStack(alignment: .leading, spacing: 12) { content }
                .padding(12)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Color.primary.opacity(0.08)))
        }
    }
}

// A labeled control row: caption on top, control below (fits the narrow inspector).
struct Field<Content: View>: View {
    let title: String
    @ViewBuilder var content: Content
    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title).font(.caption).foregroundColor(.secondary)
            content
        }
    }
}

struct ExposureTile: View {
    let name: String
    let value: String
    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(name.uppercased()).font(.system(size: 9, weight: .semibold)).tracking(0.5).foregroundColor(.secondary)
            Text(value).font(.system(.callout, design: .rounded).weight(.medium)).monospacedDigit().lineLimit(1).minimumScaleFactor(0.7)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct Inspector: View {
    @ObservedObject var model: AppModel

    private func binding<T>(_ kp: WritableKeyPath<StreamSettings, T>) -> Binding<T> {
        Binding(get: { model.settings[keyPath: kp] }, set: { model.settings[keyPath: kp] = $0 })
    }

    private var resolution: Binding<Int> {
        Binding(get: { model.settings.resolution }, set: { r in
            let (w, h) = StreamSettings.resolutionToSize(r)
            var s = model.settings; s.width = w; s.height = h; model.settings = s
        })
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                camera
                // Only when it matters: the camera isn't streaming and Canon's service may be the reason.
                if model.isCamera && model.canonServiceRunning && model.phase != .live { canonWarning }
                output
                image
                virtualCamera
            }
            .padding(16)
        }
        .frame(width: 300)
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private var camera: some View {
        InspectorSection(title: "Camera", symbol: "camera") {
            Picker("Source", selection: $model.useTestPattern) {
                Text("Canon EOS").tag(false)
                Text("Test Pattern").tag(true)
            }
            .pickerStyle(.segmented)
            .labelsHidden()

            if let ex = model.exposure {
                HStack(spacing: 8) {
                    ExposureTile(name: "Mode", value: CameraValues.shortName(CameraValues.modes, ex.mode))
                    ExposureTile(name: "ISO", value: CameraValues.name(CameraValues.iso, ex.iso))
                }
                HStack(spacing: 8) {
                    ExposureTile(name: "Shutter", value: CameraValues.name(CameraValues.shutter, ex.shutter))
                    ExposureTile(name: "Aperture", value: CameraValues.name(CameraValues.aperture, ex.aperture))
                }
                Divider()
            }

            HStack(spacing: 10) {
                Field(title: "ISO") {
                    Picker("ISO", selection: Binding(get: { model.isoCode }, set: { model.setISO($0) })) {
                        Text("–").tag(UInt32?.none)
                        ForEach(CameraValues.choices(CameraValues.isoChoices, CameraValues.iso, model.isoCode), id: \.0) { c in
                            Text(c.1).tag(UInt32?.some(c.0))
                        }
                    }
                    .labelsHidden()
                    .disabled(!model.isoEnabled)
                }
                Field(title: "Shutter") {
                    Picker("Shutter", selection: Binding(get: { model.shutterCode }, set: { model.setShutter($0) })) {
                        Text("–").tag(UInt32?.none)
                        ForEach(CameraValues.choices(CameraValues.shutterChoices, CameraValues.shutter, model.shutterCode), id: \.0) { c in
                            Text(c.1).tag(UInt32?.some(c.0))
                        }
                    }
                    .labelsHidden()
                    .disabled(!model.shutterEnabled)
                }
            }
            if model.exposure != nil && !model.shutterEnabled {
                Label("Turn the mode dial to M to set ISO and shutter here.", systemImage: "dial.medium")
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var canonWarning: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label("Canon's webcam service is running", systemImage: "exclamationmark.triangle.fill")
                .font(.callout.weight(.semibold))
                .foregroundColor(.orange)
            Text("EOS Webcam Utility's background service can take over the camera. Pause it while you use DSLR Webcam Studio; it comes back when you quit.")
                .font(.caption)
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Button("Pause Canon Service", action: model.pauseCanonService)
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }

    private var output: some View {
        InspectorSection(title: "Output", symbol: "rectangle.on.rectangle") {
            Field(title: "Resolution") {
                Picker("Resolution", selection: resolution) {
                    ForEach(StreamSettings.resolutions, id: \.self) { Text(verbatim: "\($0)p").tag($0) }
                }
                .pickerStyle(.segmented).labelsHidden()
            }
            Field(title: "Frame rate") {
                Picker("Frame rate", selection: binding(\.fps)) {
                    ForEach(StreamSettings.fpsChoices, id: \.self) { Text(verbatim: "\($0)").tag($0) }
                }
                .pickerStyle(.segmented).labelsHidden()
            }
            Field(title: "Aspect") {
                Picker("Aspect", selection: binding(\.fill)) {
                    Text("Fit").tag(false)
                    Text("Fill 16:9").tag(true)
                }
                .pickerStyle(.segmented).labelsHidden()
            }
            Text(model.settings.fill ? "Crops the camera's 3:2 picture to fill the frame." : "Shows the whole picture with black side bars.")
                .font(.caption).foregroundColor(.secondary)
        }
    }

    private var image: some View {
        InspectorSection(title: "Image", symbol: "wand.and.stars") {
            Toggle("Mirror left/right", isOn: binding(\.flipH))
            Toggle("Flip upside down", isOn: binding(\.flipV))
            Field(title: "Noise reduction") {
                Picker("Noise reduction", selection: binding(\.noise)) {
                    ForEach(0..<4, id: \.self) { Text(StreamSettings.levels[$0]).tag($0) }
                }
                .pickerStyle(.segmented).labelsHidden()
            }
            Field(title: "Sharpness") {
                Picker("Sharpness", selection: binding(\.sharp)) {
                    ForEach(0..<4, id: \.self) { Text(StreamSettings.levels[$0]).tag($0) }
                }
                .pickerStyle(.segmented).labelsHidden()
            }
        }
    }

    private var virtualCamera: some View {
        InspectorSection(title: "Virtual Camera", symbol: "video.badge.checkmark") {
            HStack(spacing: 8) {
                Circle().fill(model.vcamConnected ? Color.green : Color.secondary.opacity(0.5)).frame(width: 8, height: 8)
                Text(model.vcamConnected ? "Sending to \"\(VCamName.name)\"" : "Not connected")
                    .font(.callout)
            }
            Text("Video apps (Zoom, Teams, Meet, OBS, FaceTime) see this app's picture as the camera \"\(VCamName.name)\".")
                .font(.caption).foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Button("Install Virtual Camera…", action: model.installVirtualCamera)
        }
    }
}

// The virtual camera's name, without importing VCamShared into every view.
enum VCamName { static let name = "DSLR Webcam Studio" }

// ---- window -----------------------------------------------------------------------------------

struct ContentView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        HStack(spacing: 0) {
            PreviewPane(model: model)
                .frame(minWidth: 560, minHeight: 380)
                .background(Color(nsColor: .underPageBackgroundColor))
            Divider()
            Inspector(model: model)
        }
        .frame(minWidth: 880, minHeight: 560)
        .toolbar {
            ToolbarItemGroup(placement: .navigation) {
                Button(action: model.toggle) {
                    Label(model.running ? "Stop" : "Start", systemImage: model.running ? "stop.fill" : "play.fill")
                        .labelStyle(.titleAndIcon)
                }
                .help(model.running ? "Stop the camera (⌘.)" : "Start the camera (⌘R)")
                .disabled(model.diagnosing)
            }
            ToolbarItemGroup(placement: .primaryAction) {
                Button(action: model.snapshot) { Label("Snapshot", systemImage: "camera") }
                    .help("Save the current picture to Pictures (⌘S)")
                    .disabled(!model.hasPicture)
                Button(action: model.diagnostics) { Label("Diagnostics", systemImage: "stethoscope") }
                    .help("Test the camera connection and save a report")
                    .disabled(model.running || model.diagnosing)
                if AppInfo.donateConfigured, let url = URL(string: AppInfo.donateURL) {
                    Link(destination: url) { Label("Buy me a coffee", systemImage: "cup.and.saucer") }
                        .help("Buy me a coffee")
                }
            }
        }
        .onAppear { model.start() }
        .onDisappear { model.stop() }
    }
}

struct DSLRWebcamStudioApp: App {
    @StateObject private var model = AppModel()
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate

    var body: some Scene {
        WindowGroup("DSLR Webcam Studio") {
            ContentView(model: model)
                .onReceive(NotificationCenter.default.publisher(for: NSApplication.willTerminateNotification)) { _ in
                    model.quit()
                }
        }
        .windowToolbarStyle(.unified)
        .commands {
            // One window only: the preview view and the camera can't be shared between windows.
            CommandGroup(replacing: .newItem) {}
            CommandMenu("Camera") {
                Button(model.running ? "Stop" : "Start") { model.toggle() }
                    .keyboardShortcut(model.running ? "." : "r", modifiers: .command)
                    .disabled(model.diagnosing)
                Button("Take Snapshot") { model.snapshot() }
                    .keyboardShortcut("s", modifiers: .command)
                    .disabled(!model.hasPicture)
                Button("Show Snapshots in Finder") { model.revealSnapshots() }
                Divider()
                Toggle("Mirror Left/Right", isOn: Binding(get: { model.settings.flipH }, set: { model.settings.flipH = $0 }))
                    .keyboardShortcut("m", modifiers: [.command, .shift])
                Divider()
                Button("Run Diagnostics") { model.diagnostics() }.disabled(model.running || model.diagnosing)
                Button("Install Virtual Camera…") { model.installVirtualCamera() }
                Divider()
                Button("Pause Canon Webcam Service") { model.pauseCanonService() }.disabled(!model.canonServiceRunning)
                Button("Resume Canon Webcam Service") { model.resumeCanonService() }.disabled(model.canonServiceRunning)
            }
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}
