// App state behind the window: owns the frame source, the output engine and the virtual camera
// output, and publishes what the SwiftUI views show.
import AppKit
import SwiftUI

// Where the camera connection stands, for the status badge.
enum LivePhase: Equatable {
    case stopped, connecting, waiting, live, problem

    var title: String {
        switch self {
        case .stopped: return "Stopped"
        case .connecting: return "Connecting"
        case .waiting: return "Waiting"
        case .live: return "Live"
        case .problem: return "Retrying"
        }
    }

    var color: Color {
        switch self {
        case .stopped: return .secondary
        case .connecting, .waiting: return .orange
        case .live: return .red
        case .problem: return .yellow
        }
    }
}

// One-line output statistics shown over the preview.
struct OutputStats: Equatable {
    var outFps = 0.0, cameraFps = 0.0
    var repeated = 0, dropped = 0
}

final class AppModel: ObservableObject {
    @Published var settings: StreamSettings { didSet { settingsChanged(oldValue) } }
    @Published var useTestPattern = false { didSet { if running { stop(); start() } } }
    @Published var running = false
    @Published var diagnosing = false

    @Published private(set) var phase = LivePhase.stopped
    @Published private(set) var model = ""
    @Published private(set) var detail = "Press Start to connect to your camera."
    @Published private(set) var exposure: Exposure?
    @Published private(set) var stats = OutputStats()
    @Published private(set) var hasPicture = false
    @Published private(set) var vcamConnected = false
    @Published private(set) var canonServiceRunning = false
    // A short message about something the user just did (snapshot saved, camera installed...).
    @Published private(set) var notice: String?

    @Published var isoCode: UInt32?
    @Published var shutterCode: UInt32?
    @Published private(set) var isoEnabled = false
    @Published private(set) var shutterEnabled = false

    let preview = PreviewNSView()
    let vcam = VirtualCameraOutput()
    private let installer = VirtualCameraInstaller()
    private var source: LiveSource?
    private var engine: OutputEngine?
    private var timer: Timer?
    private var redrawPending = false
    private let redrawLock = NSLock()
    private var syncing = false
    private var pausedCanonService = false
    private var lastCanonCheck = -Double.infinity
    private var noticeUntil = 0.0

    var isCamera: Bool { !useTestPattern }

    init() {
        settings = StreamSettings.load(StreamSettings.defaultPath)
        timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in self?.tick() }
    }

    // ---- running ----

    func start() {
        if running || diagnosing { return }
        let src: LiveSource = useTestPattern ? TestPatternSource() : CameraSource()
        source = src
        src.start()
        startEngine()
        running = true
        tick()
    }

    func stop() {
        engine?.stop(); engine = nil
        source?.stop(); source = nil
        vcam.stop()
        running = false
        preview.show(nil)
        hasPicture = false
        tick()
    }

    func toggle() { running ? stop() : start() }

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
            if let f = self.engine?.currentFrame() {
                self.preview.show(f.cgImage())
                if !self.hasPicture { self.hasPicture = true }
            }
        }
    }

    // ---- camera exposure ----

    func setISO(_ v: UInt32?) {
        isoCode = v
        if !syncing, let v = v, let cam = source as? CameraSource { cam.request(PTP.dpcISOSpeed, v) }
    }

    func setShutter(_ v: UInt32?) {
        shutterCode = v
        if !syncing, let v = v, let cam = source as? CameraSource { cam.request(PTP.dpcShutterSpeed, v) }
    }

    // ---- actions ----

    func post(_ message: String, seconds: Double = 6) {
        notice = message
        noticeUntil = monoNow() + seconds
    }

    func snapshot() {
        guard let f = engine?.currentFrame(), let img = f.cgImage() else { return }
        let rep = NSBitmapImageRep(cgImage: img)
        guard let png = rep.representation(using: .png, properties: [:]) else { return }
        let fmt = DateFormatter()
        fmt.dateFormat = "yyyyMMdd_HHmmss"
        let dir = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask)[0]
        let url = dir.appendingPathComponent("DSLRWebcam_\(fmt.string(from: Date())).png")
        if (try? png.write(to: url)) != nil { post("Snapshot saved to Pictures: \(url.lastPathComponent)") }
        else { post("Could not save the snapshot.") }
    }

    func revealSnapshots() {
        NSWorkspace.shared.open(FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask)[0])
    }

    func installVirtualCamera() {
        if let why = VirtualCameraInstaller.unavailableReason {
            let alert = NSAlert()
            alert.messageText = "Virtual camera"
            alert.informativeText = why
            alert.runModal()
            return
        }
        post("Installing the virtual camera...", seconds: 60)
        installer.install { [weak self] msg in self?.post(msg, seconds: 12) }
    }

    // Canon's EOS Webcam Utility service grabs the camera; pause it while we run, restore it on quit.
    func pauseCanonService() {
        post("Pausing Canon's webcam service...", seconds: 15)
        DispatchQueue.global().async {
            let ok = CanonSoftware.pauseService()
            DispatchQueue.main.async {
                self.canonServiceRunning = CanonSoftware.serviceRunning
                guard ok else { self.post("Could not pause Canon's webcam service."); return }
                self.pausedCanonService = true
                self.post("Canon's webcam service is paused until you quit DSLR Webcam Studio.")
                if self.running && !self.useTestPattern { self.stop(); self.start() }
            }
        }
    }

    func resumeCanonService() {
        DispatchQueue.global().async {
            let ok = CanonSoftware.resumeService()
            DispatchQueue.main.async {
                self.pausedCanonService = false
                self.canonServiceRunning = CanonSoftware.serviceRunning
                if !ok { self.post("Could not restart Canon's webcam service; it starts again at your next login.") }
            }
        }
    }

    func quit() {
        stop() // returns the camera's live view to normal
        if pausedCanonService { CanonSoftware.resumeService(); pausedCanonService = false } // blocking: the app is exiting
    }

    func diagnostics() {
        stop()
        diagnosing = true
        post("Running diagnostics (about 10 seconds)...", seconds: 30)
        DispatchQueue.global().async {
            let report = Diagnostics.run()
            let url = Diagnostics.save(report)
            DispatchQueue.main.async {
                self.diagnosing = false
                self.post("Diagnostics saved to \(url.lastPathComponent)")
                let alert = NSAlert()
                alert.messageText = "Diagnostics"
                alert.informativeText = report
                alert.addButton(withTitle: "Copy to Clipboard")
                alert.addButton(withTitle: "Close")
                if alert.runModal() == .alertFirstButtonReturn {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(report, forType: .string)
                }
            }
        }
    }

    // ---- status, 4 times a second ----

    private func tick() {
        let now = monoNow()
        if notice != nil && now > noticeUntil { notice = nil }
        if now - lastCanonCheck > 2 {
            lastCanonCheck = now
            let r = CanonSoftware.serviceRunning
            if r != canonServiceRunning { canonServiceRunning = r }
        }
        if vcam.connected != vcamConnected { vcamConnected = vcam.connected }

        guard let src = source, let e = engine else {
            set(&phase, diagnosing ? .connecting : .stopped)
            set(&model, "")
            set(&detail, diagnosing ? "Running diagnostics..." : "Press Start to connect to your camera.")
            set(&exposure, nil)
            set(&stats, OutputStats())
            set(&isoEnabled, false); set(&shutterEnabled, false)
            return
        }
        let state = src.state
        let p: LivePhase = state == "Live" ? .live
            : state.hasPrefix("Waiting") ? .waiting
            : state.hasPrefix("Connecting") ? .connecting
            : state.hasPrefix("Camera") ? .problem : .stopped
        set(&phase, p)
        set(&model, src.model)
        var d = state == "Live" ? "" : state
        if src.isCamera && canonServiceRunning && p == .connecting {
            d = "Canon's EOS Webcam Utility service is running and may be holding the camera."
        }
        set(&detail, d)

        let ex = (src as? CameraSource)?.exposure
        set(&exposure, ex)
        if let ex = ex {
            syncing = true
            if isoCode != ex.iso { isoCode = ex.iso }
            if shutterCode != ex.shutter { shutterCode = ex.shutter }
            syncing = false
        }
        set(&isoEnabled, ex.map { CameraValues.isoSettable($0.mode) } ?? false)
        set(&shutterEnabled, ex.map { CameraValues.shutterSettable($0.mode) } ?? false)
        set(&stats, OutputStats(outFps: (e.meter.current() * 10).rounded() / 10, cameraFps: (src.meter.current() * 10).rounded() / 10,
                                repeated: e.repeated, dropped: e.dropped))
    }

    // Publishes only real changes, so SwiftUI doesn't redraw four times a second for nothing.
    private func set<T: Equatable>(_ field: inout T, _ value: T) { if field != value { field = value } }
}

extension Exposure: Equatable {
    static func == (a: Exposure, b: Exposure) -> Bool {
        a.mode == b.mode && a.iso == b.iso && a.shutter == b.shutter && a.aperture == b.aperture
    }
}
