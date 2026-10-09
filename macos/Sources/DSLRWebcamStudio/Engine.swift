// Settings, pacing, frame sources and the fixed-rate output engine (same design as the Windows app).
import Foundation

// ---- settings: [Global] in config.ini, same keys as the Windows/Linux apps ----

struct StreamSettings: Equatable {
    static let resolutions = [360, 720, 1080]
    // Canon DSLR live view tops out around 17-30 fps over USB 2.0, so 60 would only repeat frames.
    static let fpsChoices = [15, 24, 25, 30]
    static let levels = ["Off", "Low", "Medium", "High"]

    var width = 1280, height = 720, fps = 30
    var fill = false
    var flipH = false, flipV = false
    var noise = 2, sharp = 1

    var resolution: Int { StreamSettings.sizeToResolution(width, height) }

    static func resolutionToSize(_ r: Int) -> (Int, Int) {
        switch r {
        case 360: return (640, 360)
        case 720: return (1280, 720)
        default: return (1920, 1080) // Canon's default branch
        }
    }

    static func sizeToResolution(_ w: Int, _ h: Int) -> Int {
        if w == 640 && h == 360 { return 360 }
        if w == 1280 && h == 720 { return 720 }
        return 1080
    }

    func sameFormat(_ o: StreamSettings) -> Bool { width == o.width && height == o.height && fps == o.fps && fill == o.fill }

    static var defaultPath: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("DSLR Webcam Studio").appendingPathComponent("config.ini")
    }

    static func load(_ url: URL) -> StreamSettings {
        var s = StreamSettings()
        let g = Ini.read(url, section: "Global")
        let w = Int(g["StreamWidth"] ?? "") ?? 0, h = Int(g["StreamHeight"] ?? "") ?? 0
        if resolutions.map(resolutionToSize).contains(where: { $0 == (w, h) }) { s.width = w; s.height = h }
        if let f = Int(g["StreamFps"] ?? ""), fpsChoices.contains(f) { s.fps = f }
        s.fill = (g["FitMode"] ?? "").lowercased() == "fill"
        s.flipH = g["FlipHorizontal"] == "1"
        s.flipV = g["FlipVertical"] == "1"
        if let n = Int(g["NoiseReduction"] ?? "") { s.noise = min(3, max(0, n)) }
        if let n = Int(g["Sharpness"] ?? "") { s.sharp = min(3, max(0, n)) }
        return s
    }

    func save(_ url: URL) {
        Ini.write(url, section: "Global", values: [
            ("StreamWidth", "\(width)"), ("StreamHeight", "\(height)"), ("StreamFps", "\(fps)"),
            ("FitMode", fill ? "fill" : "fit"), ("FlipHorizontal", flipH ? "1" : "0"), ("FlipVertical", flipV ? "1" : "0"),
            ("NoiseReduction", "\(noise)"), ("Sharpness", "\(sharp)"),
        ])
    }
}

// Minimal ini reader/writer that keeps unrelated sections and keys intact.
enum Ini {
    static func read(_ url: URL, section: String) -> [String: String] {
        guard let text = try? String(contentsOf: url, encoding: .utf8) else { return [:] }
        var out: [String: String] = [:]
        var inSection = false
        for raw in text.components(separatedBy: .newlines) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("[") && line.hasSuffix("]") {
                inSection = line.dropFirst().dropLast().trimmingCharacters(in: .whitespaces).caseInsensitiveCompare(section) == .orderedSame
                continue
            }
            if inSection, let eq = line.firstIndex(of: "="), !line.hasPrefix(";"), !line.hasPrefix("#") {
                out[line[..<eq].trimmingCharacters(in: .whitespaces)] = line[line.index(after: eq)...].trimmingCharacters(in: .whitespaces)
            }
        }
        return out
    }

    static func write(_ url: URL, section: String, values: [(String, String)]) {
        var lines = ((try? String(contentsOf: url, encoding: .utf8)) ?? "").components(separatedBy: .newlines)
        if lines.last == "" { lines.removeLast() }
        var pending = values
        var inSection = false, sectionEnd = -1
        for i in lines.indices {
            let line = lines[i].trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("[") && line.hasSuffix("]") {
                inSection = line.dropFirst().dropLast().trimmingCharacters(in: .whitespaces).caseInsensitiveCompare(section) == .orderedSame
                if inSection { sectionEnd = i + 1 }
                continue
            }
            if inSection, let eq = line.firstIndex(of: "=") {
                let key = line[..<eq].trimmingCharacters(in: .whitespaces)
                if let k = pending.firstIndex(where: { $0.0.caseInsensitiveCompare(key) == .orderedSame }) {
                    lines[i] = "\(key)=\(pending[k].1)"
                    pending.remove(at: k)
                }
                sectionEnd = i + 1
            }
        }
        if sectionEnd < 0 { lines.append("[\(section)]"); sectionEnd = lines.count }
        for (k, v) in pending { lines.insert("\(k)=\(v)", at: sectionEnd); sectionEnd += 1 }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? (lines.joined(separator: "\n") + "\n").write(to: url, atomically: true, encoding: .utf8)
    }
}

// ---- pacing --------------------------------------------------------------------------

func monoNow() -> Double { Double(DispatchTime.now().uptimeNanoseconds) / 1e9 }

// Fixed timeline; a faster source drops frames, a slower one repeats them; late ticks skip a slot instead of bursting.
struct Pacer {
    let interval: Double
    private var next = -1.0
    private(set) var late = 0
    init(fps: Int) { interval = 1.0 / Double(fps) }

    // Returns how long to sleep before the next tick.
    mutating func step(now: Double) -> Double {
        if next < 0 { next = now + interval; return 0 }
        if now >= next {
            late += 1
            next += (((now - next) / interval).rounded(.down) + 1) * interval
        }
        let delay = next - now
        next += interval
        return delay
    }
}

final class RateMeter {
    private var stamps: [Double] = []
    private let lock = NSLock()
    func tick(_ now: Double = monoNow()) { lock.lock(); stamps.append(now); trim(now); lock.unlock() }
    func current(_ now: Double = monoNow()) -> Double {
        lock.lock(); defer { lock.unlock() }
        trim(now)
        guard stamps.count >= 2, let first = stamps.first, let last = stamps.last, last > first else { return 0 }
        return Double(stamps.count - 1) / (last - first)
    }
    private func trim(_ now: Double) { while let f = stamps.first, now - f > 1.0 { stamps.removeFirst() } }
}

// ---- sources ---------------------------------------------------------------------------

struct Exposure { var mode, iso, shutter, aperture: UInt32? }

class LiveSource {
    private let lock = NSLock()
    private var latest: Frame?
    private(set) var seqValue = 0
    private var thread: Thread?
    fileprivate var stopping = false
    let meter = RateMeter()
    private var _model = "", _state = "Stopped"

    var isCamera: Bool { false }
    var seq: Int { lock.lock(); defer { lock.unlock() }; return seqValue }
    var model: String { lock.lock(); defer { lock.unlock() }; return _model }
    var state: String { lock.lock(); defer { lock.unlock() }; return _state }
    func setStatus(model: String? = nil, state: String? = nil) {
        lock.lock(); if let m = model { _model = m }; if let s = state { _state = s }; lock.unlock()
    }

    func start() {
        stopping = false
        let t = Thread { [weak self] in self?.run() }
        t.qualityOfService = .userInteractive
        thread = t
        t.start()
    }

    func stop() {
        stopping = true
        let deadline = Date().addingTimeInterval(4)
        while let t = thread, !t.isFinished, Date() < deadline { Thread.sleep(forTimeInterval: 0.02) }
        thread = nil
        lock.lock(); latest = nil; _state = "Stopped"; lock.unlock()
    }

    // Returns the newest frame and its sequence number (0 = none yet).
    func newest() -> (Frame?, Int) { lock.lock(); defer { lock.unlock() }; return (latest, latest == nil ? 0 : seqValue) }

    func publish(_ f: Frame) {
        lock.lock(); latest = f; seqValue += 1; lock.unlock()
        meter.tick()
    }

    func run() {}

    func nap(_ seconds: Double) {
        let end = Date().addingTimeInterval(seconds)
        while !stopping && Date() < end { Thread.sleep(forTimeInterval: 0.05) }
    }
}

final class CameraSource: LiveSource {
    override var isCamera: Bool { true }
    private let reqLock = NSLock()
    private var requests: [(UInt32, UInt32)] = []
    private var _exposure: Exposure?
    var exposure: Exposure? { reqLock.lock(); defer { reqLock.unlock() }; return _exposure }

    // Queues a property change; PTP calls happen on the camera thread.
    func request(_ prop: UInt32, _ value: UInt32) { reqLock.lock(); requests.append((prop, value)); reqLock.unlock() }

    private func updateExposure(_ c: CanonCamera) {
        let e = Exposure(mode: c.props[PTP.dpcAutoExposureMode], iso: c.props[PTP.dpcISOSpeed],
                         shutter: c.props[PTP.dpcShutterSpeed], aperture: c.props[PTP.dpcAperture])
        reqLock.lock(); _exposure = e; reqLock.unlock()
    }

    override func run() {
        while !stopping {
            setStatus(state: "Connecting to camera...")
            do {
                let cam = try CanonCamera.open()
                defer { cam.close(); reqLock.lock(); _exposure = nil; reqLock.unlock() }
                setStatus(model: cam.model, state: "Live")
                updateExposure(cam)
                var lastExposure = monoNow()
                while !stopping {
                    reqLock.lock(); let pending = requests; requests = []; reqLock.unlock()
                    for (p, v) in pending {
                        let rc = cam.setProperty(p, v)
                        setStatus(state: rc == PTP.rcOK ? "Live" : "Camera refused the change (\(PTP.rcName(rc))). Set the mode dial to M.")
                    }
                    if !pending.isEmpty || monoNow() - lastExposure > 0.5 { updateExposure(cam); lastExposure = monoNow() }
                    guard let jpeg = try cam.readJPEG() else { Thread.sleep(forTimeInterval: 0.005); continue }
                    if let f = Frame.decodeJPEG(jpeg) { publish(f) }
                }
            } catch {
                setStatus(state: "Camera: \(error.localizedDescription) Retrying...")
            }
            nap(2)
        }
    }
}

// 3:2 moving test pattern at ~29.97 fps, sized like Canon live view (960x640).
final class TestPatternSource: LiveSource {
    override func run() {
        setStatus(model: "Test pattern 960x640", state: "Live")
        let bars: [UInt32] = [0xFFC0C0C0, 0xFFC0C000, 0xFF00C0C0, 0xFF00C000, 0xFFC000C0, 0xFFC00000, 0xFF0000C0]
        var next = monoNow(), n = 0
        while !stopping {
            let f = Frame(w: 960, h: 640)
            let bw = 960 / 7, bx = (n * 8) % 960
            for y in 0..<640 { for x in 0..<960 { f.px[y * 960 + x] = (x >= bx && x < bx + 24) ? 0xFFFFFFFF : bars[min(6, x / bw)] } }
            publish(f)
            n += 1
            next += 1 / 29.97
            let wait = next - monoNow()
            if wait > 0 { Thread.sleep(forTimeInterval: wait) } else { next = monoNow() }
        }
    }
}

// ---- output engine -------------------------------------------------------------------------
// Each tick first sends the frame composed during the previous slot, then composes the newest
// camera frame for the next one, so send timing doesn't depend on processing time.

final class OutputEngine {
    let source: LiveSource
    let settings: StreamSettings
    private let lock = NSLock()
    private var front: Frame
    private var pending: Frame
    private var pendingReady = false
    private var live: StreamSettings
    private var stopping = false
    private var thread: Thread?
    private let scaler = Scaler(), enhancer = Enhancer()
    let meter = RateMeter()
    private(set) var repeated = 0, dropped = 0, frames = 0, late = 0
    private(set) var composeMs = 0.0
    var onFrame: (() -> Void)?

    init(source: LiveSource, settings: StreamSettings) {
        self.source = source
        self.settings = settings
        live = settings
        front = Frame(w: settings.width, h: settings.height)
        pending = Frame(w: settings.width, h: settings.height)
    }

    // Flip and cleanup levels change in place, without restarting the output.
    func setLive(_ s: StreamSettings) { lock.lock(); live = s; lock.unlock() }

    func start() {
        let t = Thread { [weak self] in self?.run() }
        t.qualityOfService = .userInteractive
        thread = t
        t.start()
    }

    func stop() {
        stopping = true
        let deadline = Date().addingTimeInterval(3)
        while let t = thread, !t.isFinished, Date() < deadline { Thread.sleep(forTimeInterval: 0.01) }
    }

    // A snapshot of the frame most recently sent, or nil before the first one. The pixel array is
    // copied under the lock; copy-on-write keeps it intact when the output thread reuses the buffer.
    func currentFrame() -> Frame? {
        lock.lock(); defer { lock.unlock() }
        return frames > 0 ? Frame(w: front.w, h: front.h, px: front.px) : nil
    }

    private func run() {
        var pacer = Pacer(fps: settings.fps)
        var lastSeq = 0, started = false
        lock.lock(); var lastLive = live; lock.unlock()
        while !stopping {
            let d = pacer.step(now: monoNow())
            if d > 0 { Thread.sleep(forTimeInterval: d) }
            if stopping { break }

            // 1. Send on the slot.
            lock.lock()
            if pendingReady { let t = front; front = pending; pending = t; pendingReady = false; started = true }
            else if started { repeated += 1 }
            if started { frames += 1; late = pacer.late }
            let opts = live
            lock.unlock()
            if started { meter.tick(); onFrame?() }

            // 2. Prepare the next slot from the newest camera frame (or right away after an option change).
            let (frame, seq) = source.newest()
            if let frame = frame, seq != lastSeq || opts != lastLive {
                lastLive = opts
                let t0 = monoNow()
                compose(frame, opts)
                let ms = (monoNow() - t0) * 1000
                lock.lock()
                if lastSeq != 0 { dropped += max(0, seq - lastSeq - 1) }
                composeMs = composeMs == 0 ? ms : 0.9 * composeMs + 0.1 * ms
                pendingReady = true
                lock.unlock()
                lastSeq = seq
            }
        }
    }

    private func compose(_ src: Frame, _ o: StreamSettings) {
        let img = enhancer.process(src, noise: o.noise, sharp: o.sharp)
        let W = settings.width, H = settings.height
        if settings.fill {
            scaler.draw(img, Geometry.crop(img.w, img.h, W, H), pending, IRect(x: 0, y: 0, w: W, h: H), flipH: o.flipH, flipV: o.flipV)
        } else {
            scaler.draw(img, IRect(x: 0, y: 0, w: img.w, h: img.h), pending, Geometry.fit(img.w, img.h, W, H), flipH: o.flipH, flipV: o.flipV)
        }
    }
}
