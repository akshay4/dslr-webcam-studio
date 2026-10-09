// Built-in checks for the platform-independent logic (run in CI: --selftest), mirroring the
// Windows and Linux self-tests, plus the camera diagnostics report.
import Foundation

enum SelfTest {
    private static var failures = 0

    private static func check(_ ok: Bool, _ what: String) {
        print((ok ? "PASS " : "FAIL ") + what)
        if !ok { failures += 1 }
    }

    static func run() -> Bool {
        failures = 0
        check(StreamSettings.resolutionToSize(360) == (640, 360), "360 -> 640x360")
        check(StreamSettings.resolutionToSize(720) == (1280, 720), "720 -> 1280x720")
        check(StreamSettings.resolutionToSize(999) == (1920, 1080), "unknown -> 1080p (Canon default)")

        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("dws-\(UUID().uuidString)")
        let ini = dir.appendingPathComponent("config.ini")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try? "[Other]\nx=1\n[Global]\nLogLevel=3\nStreamFps=59\n".write(to: ini, atomically: true, encoding: .utf8)
        check(StreamSettings.load(ini) == StreamSettings(), "invalid ini values fall back to defaults")
        var s = StreamSettings()
        s.width = 1920; s.height = 1080; s.fps = 25; s.fill = true; s.flipH = true; s.noise = 3; s.sharp = 0
        s.save(ini)
        check(StreamSettings.load(ini) == s, "ini round trip")
        let text = (try? String(contentsOf: ini, encoding: .utf8)) ?? ""
        check(text.contains("StreamWidth=1920") && text.contains("StreamFps=25") && text.contains("FitMode=fill"), "Canon-style keys written")
        check(text.contains("LogLevel=3") && text.contains("[Other]"), "other ini keys preserved")
        try? FileManager.default.removeItem(at: dir)

        check(Geometry.fit(960, 640, 1280, 720) == IRect(x: 100, y: 0, w: 1080, h: 720), "fit 960x640 into 720p")
        check(Geometry.fit(960, 640, 1920, 1080) == IRect(x: 150, y: 0, w: 1620, h: 1080), "fit into 1080p")
        check(Geometry.fit(960, 640, 640, 360) == IRect(x: 50, y: 0, w: 540, h: 360), "fit into 360p")
        check(Geometry.crop(960, 640, 1280, 720) == IRect(x: 0, y: 50, w: 960, h: 540), "fill crop 960x640 to 16:9")
        check(Geometry.crop(2000, 720, 1280, 720) == IRect(x: 360, y: 0, w: 1280, h: 720), "fill crop wide source")

        var p = Pacer(fps: 30)
        var t = 0.0
        t += p.step(now: t)
        let t0 = t
        for i in 0..<300 { t += i % 2 == 0 ? 0.013 : 0.001; t += p.step(now: t) }
        check(abs(t - t0 - 10) < 1e-6 && p.late == 0, "pacer: 300 ticks at 30 fps = 10 s, no drift")
        t += 0.5
        t += p.step(now: t)
        let before = t
        t += p.step(now: t)
        check(p.late == 1 && abs(t - before - 1.0 / 30) < 1e-9, "pacer: resync after stall without burst")

        let meter = RateMeter()
        var rt = 0.0
        for _ in 0..<75 { meter.tick(rt); rt += 1.0 / 25 }
        rt -= 1.0 / 25
        check(abs(meter.current(rt) - 25) < 0.01, "rate meter: 25 fps")
        check(meter.current(rt + 2) == 0, "rate meter: 0 after ticks stop")

        let sc = Scaler()
        let src = Frame(w: 4, h: 4, fill: 0xFFC8_6432)
        let dst = Frame(w: 16, h: 9)
        let r = Geometry.fit(4, 4, 16, 9)
        sc.draw(src, IRect(x: 0, y: 0, w: 4, h: 4), dst, r)
        check(dst.px[(r.y + r.h / 2) * 16 + r.x + r.w / 2] == 0xFFC8_6432, "scaler keeps color")
        check(dst.px[4 * 16] == 0xFF00_0000, "scaler paints side bars black")

        let q = Frame(w: 8, h: 8), out = Frame(w: 8, h: 8)
        for y in 0..<8 { for x in 0..<8 { q.px[y * 8 + x] = 0xFF00_0000 | (x < 4 ? 0xFF0000 : 0x0000FF) | (y < 4 ? 0x00FF00 : 0) } }
        let full = IRect(x: 0, y: 0, w: 8, h: 8)
        sc.draw(q, full, out, full, flipH: true)
        check(out.px[0] & 0xFF == 0xFF && (out.px[7] >> 16) & 0xFF == 0xFF, "mirror swaps left and right")
        sc.draw(q, full, out, full, flipV: true)
        check((out.px[0] >> 8) & 0xFF == 0 && (out.px[56] >> 8) & 0xFF == 0xFF, "flip swaps top and bottom")
        sc.draw(q, full, out, full)
        check(out.px[0] == 0xFFFF_FF00 && out.px[63] & 0xFF == 0xFF, "no flip keeps orientation")

        var gen = SystemRandomNumberGenerator()
        let en = Enhancer()
        var last = Frame(w: 64, h: 64)
        for _ in 0..<20 {
            let f = Frame(w: 64, h: 64)
            for i in 0..<(64 * 64) {
                let v = UInt32(128 + Int(Double.random(in: 0..<1, using: &gen) * 30) - 15)
                f.px[i] = 0xFF00_0000 | v << 16 | v << 8 | v
            }
            last = en.process(f, noise: 3, sharp: 0)
        }
        let inStd = 30 / 12.0.squareRoot(), outStd = stddevGreen(last)
        check(outStd < inStd * 0.6, String(format: "noise reduction: static noise std %.1f -> %.1f", inStd, outStd))
        let moved = en.process(Frame(w: 64, h: 64, fill: 0xFFFF_FFFF), noise: 3, sharp: 0)
        check((moved.px[32 * 64 + 32] >> 8) & 255 > 245, "noise reduction: motion passes through without smearing")

        var ev: [UInt8] = []
        ev += PTP.le32(16) + PTP.le32(0xC189) + PTP.le32(PTP.dpcISOSpeed) + PTP.le32(0x60)
        ev += PTP.le32(12) + PTP.le32(0xC18A) + PTP.le32(PTP.dpcISOSpeed)
        ev += PTP.le32(8) + PTP.le32(0)
        var props: [UInt32: UInt32] = [:]
        PTP.parseEvents(ev, into: &props)
        check(props.count == 1 && props[PTP.dpcISOSpeed] == 0x60 && CameraValues.name(CameraValues.iso, 0x60) == "800", "event parse: ISO 800")

        let jpeg: [UInt8] = [0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9]
        let vf = PTP.le32(12) + PTP.le32(5) + PTP.le32(0) + PTP.le32(UInt32(8 + jpeg.count)) + PTP.le32(1) + jpeg
        check(PTP.extractJPEG(vf).map(Array.init) == jpeg, "viewfinder block parse (type 1 = JPEG)")
        check(PTP.extractJPEG(jpeg).map(Array.init) == jpeg, "viewfinder bare JPEG fallback")

        print(failures == 0 ? "ALL PASSED" : "\(failures) FAILED")
        return failures == 0
    }

    private static func stddevGreen(_ f: Frame) -> Double {
        var s = 0.0, s2 = 0.0
        for p in f.px { let v = Double((p >> 8) & 255); s += v; s2 += v * v }
        let n = Double(f.px.count), m = s / n
        return max(0, s2 / n - m * m).squareRoot()
    }
}

enum Diagnostics {
    static func run() -> String {
        var out = "DSLR Webcam Studio diagnostics  \(Date())\nApp \(AppInfo.version) (macOS \(ProcessInfo.processInfo.operatingSystemVersionString))\n\nLive view test:\n"
        do {
            let cam = try CanonCamera.open()
            defer { cam.close() }
            out += "  Model: \(cam.info.model)  firmware \(cam.info.version)  (\(cam.info.manufacturer))\n"
            out += "  Canon ops: " + cam.info.operations.filter { $0 >= 0x9100 }.map { String(format: "%04X", $0) }.joined(separator: " ") + "\n"
            let start = monoNow()
            var first = -1.0, frames = 0, notReady = 0, bytes = 0
            while monoNow() - start < 6 {
                guard let j = try cam.readJPEG() else { notReady += 1; Thread.sleep(forTimeInterval: 0.005); continue }
                if first < 0 { first = monoNow() - start }
                frames += 1
                bytes += j.count
            }
            let active = monoNow() - start - max(0, first)
            out += String(format: "  first frame after %.0f ms\n  %d frames, %.1f fps, %d not-ready polls, avg JPEG %d KB\n",
                          first * 1000, frames, Double(frames) / max(active, 0.001), notReady, frames > 0 ? bytes / frames / 1024 : 0)
            out += "  mode \(CameraValues.name(CameraValues.modes, cam.props[PTP.dpcAutoExposureMode]))  ISO \(CameraValues.name(CameraValues.iso, cam.props[PTP.dpcISOSpeed]))"
            out += "  \(CameraValues.name(CameraValues.shutter, cam.props[PTP.dpcShutterSpeed]))  \(CameraValues.name(CameraValues.aperture, cam.props[PTP.dpcAperture]))\n"
            out += frames > 0 ? "  result: OK\n" : "  result: FAILED - no frames\n"
        } catch {
            out += "  result: FAILED - \(error.localizedDescription)\n"
        }
        return out
    }

    static func save(_ report: String) -> URL {
        let fmt = DateFormatter()
        fmt.dateFormat = "yyyyMMdd_HHmmss"
        let url = StreamSettings.defaultPath.deletingLastPathComponent().appendingPathComponent("diagnostics_\(fmt.string(from: Date())).txt")
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? report.write(to: url, atomically: true, encoding: .utf8)
        return url
    }
}
