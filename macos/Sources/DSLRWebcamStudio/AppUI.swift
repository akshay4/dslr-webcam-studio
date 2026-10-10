// SwiftUI window: live preview, output settings, image cleanup and camera exposure controls.
import AppKit
import SwiftUI

final class AppModel: ObservableObject {
    @Published var settings: StreamSettings { didSet { settingsChanged(oldValue) } }
    @Published var useTestPattern = false { didSet { if running { stop(); start() } } }
    @Published var running = false
    @Published var diagnosing = false
    @Published var stateText = "Stopped"
    @Published var exposureText = ""
    @Published var statsText = ""
    @Published var isoCode: UInt32? = nil
    @Published var shutterCode: UInt32? = nil
    @Published var isoEnabled = false
    @Published var shutterEnabled = false

    let preview = PreviewNSView()
    let vcam = VirtualCameraOutput()
    private let installer = VirtualCameraInstaller()
    private var source: LiveSource?
    private var engine: OutputEngine?
    private var timer: Timer?
    private var redrawPending = false
    private let redrawLock = NSLock()
    private var syncing = false

    init() {
        settings = StreamSettings.load(StreamSettings.defaultPath)
        timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in self?.tick() }
    }

    func start() {
        if running || diagnosing { return }
        let src: LiveSource = useTestPattern ? TestPatternSource() : CameraSource()
        source = src
        src.start()
        startEngine()
        running = true
    }

    func stop() {
        engine?.stop(); engine = nil
        source?.stop(); source = nil
        vcam.stop()
        running = false
        preview.show(nil, message: "Stopped. Press Start.")
    }

    private func startEngine() {
        guard let src = source else { return }
        let e = OutputEngine(source: src, settings: settings)
        e.onFrame = { [weak self] in self?.frameReady() }
        e.onSend = { [vcam] f in vcam.send(f) }
        engine = e
        e.start()
    }

    private func settingsChanged(_ old: StreamSettings) {
        if old == settings { return }
        settings.save(StreamSettings.defaultPath)
        guard let e = engine else { return }
        if settings.sameFormat(old) { e.setLive(settings) } // flip / cleanup change in place
        else { e.stop(); startEngine() }
    }

    private func frameReady() { // output thread
        redrawLock.lock()
        let schedule = !redrawPending
        redrawPending = true
        redrawLock.unlock()
        if !schedule { return }
        DispatchQueue.main.async { [weak self] in
            guard let self = self else { return }
            self.redrawLock.lock(); self.redrawPending = false; self.redrawLock.unlock()
            if let f = self.engine?.currentFrame() { self.preview.show(f.cgImage(), message: nil) }
        }
    }

    func setISO(_ v: UInt32?) {
        isoCode = v
        if !syncing, let v = v, let cam = source as? CameraSource { cam.request(PTP.dpcISOSpeed, v) }
    }

    func setShutter(_ v: UInt32?) {
        shutterCode = v
        if !syncing, let v = v, let cam = source as? CameraSource { cam.request(PTP.dpcShutterSpeed, v) }
    }

    func snapshot() {
        guard let f = engine?.currentFrame(), let img = f.cgImage() else { return }
        let rep = NSBitmapImageRep(cgImage: img)
        guard let png = rep.representation(using: .png, properties: [:]) else { return }
        let fmt = DateFormatter()
        fmt.dateFormat = "yyyyMMdd_HHmmss"
        let dir = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask)[0]
        let url = dir.appendingPathComponent("DSLRWebcam_\(fmt.string(from: Date())).png")
        if (try? png.write(to: url)) != nil { stateText = "Saved \(url.path)" }
    }

    func installVirtualCamera() {
        if let why = VirtualCameraInstaller.unavailableReason {
            let alert = NSAlert()
            alert.messageText = "Virtual camera"
            alert.informativeText = why
            alert.runModal()
            return
        }
        stateText = "Installing the virtual camera..."
        installer.install { [weak self] msg in self?.stateText = msg }
    }

    func diagnostics() {
        stop()
        diagnosing = true
        stateText = "Running diagnostics (about 10 seconds)..."
        DispatchQueue.global().async {
            let report = Diagnostics.run()
            let url = Diagnostics.save(report)
            DispatchQueue.main.async {
                self.diagnosing = false
                self.stateText = "Diagnostics saved to \(url.path)"
                let alert = NSAlert()
                alert.messageText = "Diagnostics"
                alert.informativeText = report
                alert.addButton(withTitle: "Copy to clipboard")
                alert.addButton(withTitle: "Close")
                if alert.runModal() == .alertFirstButtonReturn {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(report, forType: .string)
                }
            }
        }
    }

    private func tick() {
        guard let src = source, let e = engine else {
            stateText = ["Saved", "Diagnostics", "Running", "Virtual", "Could not", "Approve", "Installing", "Move"].contains { stateText.hasPrefix($0) }
            ? stateText : "Stopped"
            statsText = "Output \(settings.width)x\(settings.height) @ \(settings.fps) fps"
            exposureText = ""
            isoEnabled = false; shutterEnabled = false
            return
        }
        let ex = (src as? CameraSource)?.exposure
        let isoOK = ex.map { CameraValues.isoSettable($0.mode) } ?? false
        var state = (src.model.isEmpty ? "" : src.model + ": ") + src.state
        if ex != nil && !isoOK { state += "  (turn the mode dial to M to set ISO/shutter)" }
        if !["Saved", "Virtual", "Could not", "Approve", "Installing"].contains(where: { stateText.hasPrefix($0) }) || src.state != "Live" {
            stateText = state
        }
        if let ex = ex {
            exposureText = "\(CameraValues.name(CameraValues.modes, ex.mode))  |  ISO \(CameraValues.name(CameraValues.iso, ex.iso))  |  "
                + "\(CameraValues.name(CameraValues.shutter, ex.shutter))  |  \(CameraValues.name(CameraValues.aperture, ex.aperture))"
            syncing = true
            isoCode = ex.iso; shutterCode = ex.shutter
            syncing = false
        } else {
            exposureText = ""
        }
        isoEnabled = isoOK
        shutterEnabled = ex.map { CameraValues.shutterSettable($0.mode) } ?? false
        statsText = String(format: "Output %dx%d @ %d fps target, %.1f actual  |  camera %.1f fps  |  repeated %d  dropped %d",
                           settings.width, settings.height, settings.fps, e.meter.current(), src.meter.current(), e.repeated, e.dropped)
            + (vcam.connected ? "  |  virtual camera on" : "")
        if preview.isEmpty { preview.show(nil, message: src.state) }
    }
}

// Layer-backed preview: the output frame scaled to fit, black around it.
final class PreviewNSView: NSView {
    private let label = NSTextField(labelWithString: "")
    private(set) var isEmpty = true

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor
        layer?.contentsGravity = .resizeAspect
        label.textColor = .gray
        label.alignment = .center
        label.translatesAutoresizingMaskIntoConstraints = false
        addSubview(label)
        NSLayoutConstraint.activate([label.centerXAnchor.constraint(equalTo: centerXAnchor),
                                     label.centerYAnchor.constraint(equalTo: centerYAnchor),
                                     label.widthAnchor.constraint(lessThanOrEqualTo: widthAnchor, constant: -40)])
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    func show(_ image: CGImage?, message: String?) {
        layer?.contents = image
        isEmpty = image == nil
        label.stringValue = image == nil ? (message ?? "") : ""
    }
}

struct PreviewView: NSViewRepresentable {
    let view: PreviewNSView
    func makeNSView(context: Context) -> PreviewNSView { view }
    func updateNSView(_ nsView: PreviewNSView, context: Context) {}
}

struct ContentView: View {
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
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 12) {
                    Button(model.running ? "Stop" : "Start") { model.running ? model.stop() : model.start() }
                        .keyboardShortcut(.space, modifiers: [])
                        .disabled(model.diagnosing)
                    Button("Snapshot") { model.snapshot() }.disabled(!model.running)
                    Button("Diagnostics") { model.diagnostics() }.disabled(model.running || model.diagnosing)
                    Button("Install virtual camera") { model.installVirtualCamera() }
                    if AppInfo.donateConfigured, let url = URL(string: AppInfo.donateURL) {
                        Link("\u{2615} Buy me a coffee", destination: url)
                    }
                    Spacer()
                    Picker("Source", selection: $model.useTestPattern) {
                        Text("Canon EOS (USB)").tag(false)
                        Text("Test pattern").tag(true)
                    }.frame(width: 230)
                }
                HStack(spacing: 12) {
                    Picker("Streaming Video Output Resolution", selection: resolution) {
                        ForEach(StreamSettings.resolutions, id: \.self) { r in
                            let (w, h) = StreamSettings.resolutionToSize(r)
                            Text("\(w) x \(h)  (\(r)p)").tag(r)
                        }
                    }.frame(width: 390)
                    Picker("Target Streaming Framerate", selection: binding(\.fps)) {
                        ForEach(StreamSettings.fpsChoices, id: \.self) { Text("\($0) fps").tag($0) }
                    }.frame(width: 280)
                    Picker("Aspect", selection: binding(\.fill)) {
                        Text("Fit (black side bars)").tag(false)
                        Text("Fill (crop to 16:9)").tag(true)
                    }.frame(width: 240)
                }
                HStack(spacing: 12) {
                    Toggle("Mirror left/right", isOn: binding(\.flipH))
                    Toggle("Flip upside-down", isOn: binding(\.flipV))
                    Picker("Noise reduction", selection: binding(\.noise)) {
                        ForEach(0..<4, id: \.self) { Text(StreamSettings.levels[$0]).tag($0) }
                    }.frame(width: 210)
                    Picker("Sharpness", selection: binding(\.sharp)) {
                        ForEach(0..<4, id: \.self) { Text(StreamSettings.levels[$0]).tag($0) }
                    }.frame(width: 180)
                    Picker("Camera ISO", selection: Binding(get: { model.isoCode }, set: { model.setISO($0) })) {
                        Text("-").tag(UInt32?.none)
                        ForEach(CameraValues.choices(CameraValues.isoChoices, CameraValues.iso, model.isoCode), id: \.0) { c in
                            Text(c.1).tag(UInt32?.some(c.0))
                        }
                    }.frame(width: 170).disabled(!model.isoEnabled)
                    Picker("Shutter", selection: Binding(get: { model.shutterCode }, set: { model.setShutter($0) })) {
                        Text("-").tag(UInt32?.none)
                        ForEach(CameraValues.choices(CameraValues.shutterChoices, CameraValues.shutter, model.shutterCode), id: \.0) { c in
                            Text(c.1).tag(UInt32?.some(c.0))
                        }
                    }.frame(width: 150).disabled(!model.shutterEnabled)
                }
            }
            .padding(10)

            PreviewView(view: model.preview).frame(minWidth: 640, minHeight: 360)

            HStack {
                Text(model.stateText).lineLimit(1).truncationMode(.tail)
                Spacer()
                Text(model.exposureText)
                Text(model.statsText)
            }
            .font(.system(size: 11))
            .padding(.horizontal, 10).padding(.vertical, 5)
        }
        .frame(minWidth: 1100, minHeight: 640)
        .onAppear { model.start() }
        .onDisappear { model.stop() }
    }
}

struct DSLRWebcamStudioApp: App {
    @StateObject private var model = AppModel()
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate

    var body: some Scene {
        WindowGroup("DSLR Webcam Studio \(AppInfo.version)") {
            ContentView(model: model)
                .onReceive(NotificationCenter.default.publisher(for: NSApplication.willTerminateNotification)) { _ in
                    model.stop() // returns the camera's live view to normal
                }
        }
        // One window only: the preview view and the camera can't be shared between windows.
        .commands { CommandGroup(replacing: .newItem) {} }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}
