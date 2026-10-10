// The "DSLR Webcam Studio" camera that other apps see (Teams, Meet, Zoom, OBS, FaceTime...).
// A CoreMediaIO Camera Extension with two streams on one device:
//   sink   - the DSLR Webcam Studio app pushes its finished output frames in here;
//   source - what video apps read: the newest app frame, sent at a steady 30 fps and scaled to
//            the size the app asked for, or a "start DSLR Webcam Studio" picture when no frames come.
// Everything runs on one serial queue.
import Accelerate
import CoreMedia
import CoreMediaIO
import CoreText
import CoreVideo
import Foundation
import IOKit.audio
import VCamShared

@available(macOS 12.3, *)
final class ProviderSource: NSObject, CMIOExtensionProviderSource {
    private(set) var provider: CMIOExtensionProvider!
    private var deviceSource: DeviceSource!

    init(clientQueue: DispatchQueue?) {
        super.init()
        provider = CMIOExtensionProvider(source: self, clientQueue: clientQueue)
        deviceSource = DeviceSource()
        do { try provider.addDevice(deviceSource.device) } catch { fatalError("addDevice: \(error)") }
    }

    func connect(to client: CMIOExtensionClient) throws {}
    func disconnect(from client: CMIOExtensionClient) {}

    var availableProperties: Set<CMIOExtensionProperty> { [.providerManufacturer] }

    func providerProperties(forProperties properties: Set<CMIOExtensionProperty>) throws -> CMIOExtensionProviderProperties {
        let p = CMIOExtensionProviderProperties(dictionary: [:])
        if properties.contains(.providerManufacturer) { p.manufacturer = VCam.name }
        return p
    }

    func setProviderProperties(_ providerProperties: CMIOExtensionProviderProperties) throws {}
}

@available(macOS 12.3, *)
final class DeviceSource: NSObject, CMIOExtensionDeviceSource {
    private(set) var device: CMIOExtensionDevice!
    private var source: StreamSource!, sink: StreamSource!
    private var sourceStream: CMIOExtensionStream!, sinkStream: CMIOExtensionStream!
    private let queue = DispatchQueue(label: "camera", qos: .userInteractive)

    private var outputTimer: DispatchSourceTimer?, sinkTimer: DispatchSourceTimer?
    private var sinkClient: CMIOExtensionClient?
    private var consuming = false
    private var latest: CVPixelBuffer?
    private var latestAt = 0.0
    private var pools: [Int: CVPixelBufferPool] = [:]
    private var placeholders: [Int: CVPixelBuffer] = [:]

    override init() {
        super.init()
        device = CMIOExtensionDevice(localizedName: VCam.name, deviceID: UUID(uuidString: VCam.deviceUID)!,
                                     legacyDeviceID: nil, source: self)
        let formats = VCam.sizes.map { (w, h) -> CMIOExtensionStreamFormat in
            var fd: CMFormatDescription?
            CMVideoFormatDescriptionCreate(allocator: kCFAllocatorDefault, codecType: kCVPixelFormatType_32BGRA,
                                           width: Int32(w), height: Int32(h), extensions: nil, formatDescriptionOut: &fd)
            let d = CMTime(value: 1, timescale: CMTimeScale(VCam.fps))
            return CMIOExtensionStreamFormat(formatDescription: fd!, maxFrameDuration: d, minFrameDuration: d, validFrameDurations: nil)
        }
        source = StreamSource(device: self, formats: formats, isSink: false)
        sink = StreamSource(device: self, formats: formats, isSink: true)
        sourceStream = CMIOExtensionStream(localizedName: VCam.name, streamID: UUID(uuidString: VCam.sourceStreamUID)!,
                                           direction: .source, clockType: .hostTime, source: source)
        sinkStream = CMIOExtensionStream(localizedName: VCam.name + " input", streamID: UUID(uuidString: VCam.sinkStreamUID)!,
                                         direction: .sink, clockType: .hostTime, source: sink)
        do {
            try device.addStream(sourceStream)
            try device.addStream(sinkStream)
        } catch { fatalError("addStream: \(error)") }
    }

    var availableProperties: Set<CMIOExtensionProperty> { [.deviceTransportType, .deviceModel] }

    func deviceProperties(forProperties properties: Set<CMIOExtensionProperty>) throws -> CMIOExtensionDeviceProperties {
        let p = CMIOExtensionDeviceProperties(dictionary: [:])
        if properties.contains(.deviceTransportType) { p.transportType = kIOAudioDeviceTransportTypeVirtual }
        if properties.contains(.deviceModel) { p.model = VCam.name }
        return p
    }

    func setDeviceProperties(_ deviceProperties: CMIOExtensionDeviceProperties) throws {}

    // ---- source: video apps reading the camera ----

    func startSource() {
        queue.async {
            if self.outputTimer != nil { return }
            let t = DispatchSource.makeTimerSource(queue: self.queue)
            t.schedule(deadline: .now(), repeating: 1.0 / Double(VCam.fps), leeway: .milliseconds(2))
            t.setEventHandler { self.sendFrame() } // the device lives as long as the process
            self.outputTimer = t
            t.resume()
        }
    }

    func stopSource() { queue.async { self.outputTimer?.cancel(); self.outputTimer = nil } }

    private func sendFrame() {
        let (w, h) = VCam.sizes[min(max(source.activeFormatIndex, 0), VCam.sizes.count - 1)]
        let now = VCam.hostNow()
        var frame: CVPixelBuffer?
        if let l = latest, now.seconds - latestAt < VCam.staleSeconds { frame = fit(l, w, h) }
        guard let pb = frame ?? placeholder(w, h), let sb = VCam.sampleBuffer(pb, at: now) else { return }
        sourceStream.send(sb, discontinuity: [], hostTimeInNanoseconds: UInt64(now.seconds * 1e9))
    }

    // ---- sink: frames from the DSLR Webcam Studio app ----

    func authorizeSink(_ client: CMIOExtensionClient) { queue.async { self.sinkClient = client } }

    func startSink() {
        queue.async {
            if self.sinkTimer != nil { return }
            let t = DispatchSource.makeTimerSource(queue: self.queue)
            t.schedule(deadline: .now(), repeating: 1.0 / Double(VCam.fps * 2), leeway: .milliseconds(1))
            t.setEventHandler { self.consume() }
            self.sinkTimer = t
            t.resume()
        }
    }

    func stopSink() {
        queue.async {
            self.sinkTimer?.cancel(); self.sinkTimer = nil
            self.sinkClient = nil
            self.latest = nil
        }
    }

    private func consume() {
        guard !consuming, let client = sinkClient else { return }
        consuming = true
        sinkStream.consumeSampleBuffer(from: client) { [weak self] sb, seq, _, _, _ in
            guard let self = self else { return }
            self.queue.async {
                self.consuming = false
                guard let sb = sb, let pb = CMSampleBufferGetImageBuffer(sb) else { return }
                let now = VCam.hostNow()
                self.latest = pb
                self.latestAt = now.seconds
                self.sinkStream.notifyScheduledOutputChanged(
                    CMIOExtensionScheduledOutput(sequenceNumber: seq, hostTimeInNanoseconds: UInt64(now.seconds * 1e9)))
            }
        }
    }

    // ---- pixels ----

    private func pool(_ w: Int, _ h: Int) -> CVPixelBufferPool? {
        if let p = pools[w] { return p }
        let p = VCam.makePool(w, h)
        pools[w] = p
        return p
    }

    // The app's frame at the size the video app asked for (the app may be set to another resolution).
    private func fit(_ src: CVPixelBuffer, _ w: Int, _ h: Int) -> CVPixelBuffer? {
        if CVPixelBufferGetWidth(src) == w && CVPixelBufferGetHeight(src) == h { return src }
        guard CVPixelBufferGetPixelFormatType(src) == kCVPixelFormatType_32BGRA,
              let p = pool(w, h), let dst = VCam.makeBuffer(p) else { return nil }
        CVPixelBufferLockBaseAddress(src, .readOnly); defer { CVPixelBufferUnlockBaseAddress(src, .readOnly) }
        CVPixelBufferLockBaseAddress(dst, []); defer { CVPixelBufferUnlockBaseAddress(dst, []) }
        var s = vImage_Buffer(data: CVPixelBufferGetBaseAddress(src), height: vImagePixelCount(CVPixelBufferGetHeight(src)),
                              width: vImagePixelCount(CVPixelBufferGetWidth(src)), rowBytes: CVPixelBufferGetBytesPerRow(src))
        var d = vImage_Buffer(data: CVPixelBufferGetBaseAddress(dst), height: vImagePixelCount(h),
                              width: vImagePixelCount(w), rowBytes: CVPixelBufferGetBytesPerRow(dst))
        // Channel order doesn't matter to the scaler, so the ARGB routine handles BGRA as is.
        return vImageScale_ARGB8888(&s, &d, nil, vImage_Flags(kvImageHighQualityResampling)) == kvImageNoError ? dst : nil
    }

    private func placeholder(_ w: Int, _ h: Int) -> CVPixelBuffer? {
        if let p = placeholders[w] { return p }
        guard let p = pool(w, h), let pb = VCam.makeBuffer(p) else { return nil }
        CVPixelBufferLockBaseAddress(pb, []); defer { CVPixelBufferUnlockBaseAddress(pb, []) }
        guard let ctx = CGContext(data: CVPixelBufferGetBaseAddress(pb), width: w, height: h, bitsPerComponent: 8,
                                  bytesPerRow: CVPixelBufferGetBytesPerRow(pb), space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGBitmapInfo.byteOrder32Little.rawValue | CGImageAlphaInfo.premultipliedFirst.rawValue)
        else { return nil }
        ctx.setFillColor(CGColor(red: 0.07, green: 0.07, blue: 0.09, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        let H = CGFloat(h)
        drawCentered(ctx, w, VCam.name, size: H / 14, y: H * 0.52, gray: 0.92)
        drawCentered(ctx, w, "Open DSLR Webcam Studio and start the camera to show your picture here.", size: H / 30, y: H * 0.42, gray: 0.6)
        placeholders[w] = pb
        return pb
    }

    private func drawCentered(_ ctx: CGContext, _ w: Int, _ text: String, size: CGFloat, y: CGFloat, gray: CGFloat) {
        let font = CTFontCreateUIFontForLanguage(.system, size, nil) ?? CTFontCreateWithName("Helvetica" as CFString, size, nil)
        let attrs: [NSAttributedString.Key: Any] = [
            NSAttributedString.Key(kCTFontAttributeName as String): font,
            NSAttributedString.Key(kCTForegroundColorAttributeName as String): CGColor(gray: gray, alpha: 1),
        ]
        let line = CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: attrs))
        let width = CGFloat(CTLineGetTypographicBounds(line, nil, nil, nil))
        ctx.textPosition = CGPoint(x: (CGFloat(w) - width) / 2, y: y)
        CTLineDraw(line, ctx)
    }
}

@available(macOS 12.3, *)
final class StreamSource: NSObject, CMIOExtensionStreamSource {
    private weak var device: DeviceSource?
    private let isSink: Bool
    let formats: [CMIOExtensionStreamFormat]
    private let lock = NSLock()
    private var _activeFormatIndex = 0
    var activeFormatIndex: Int { lock.lock(); defer { lock.unlock() }; return _activeFormatIndex }

    init(device: DeviceSource, formats: [CMIOExtensionStreamFormat], isSink: Bool) {
        self.device = device
        self.formats = formats
        self.isSink = isSink
    }

    var availableProperties: Set<CMIOExtensionProperty> {
        var p: Set<CMIOExtensionProperty> = [.streamActiveFormatIndex, .streamFrameDuration]
        if isSink {
            p.formUnion([.streamSinkBufferQueueSize, .streamSinkBuffersRequiredForStartup,
                         .streamSinkBufferUnderrunCount, .streamSinkEndOfData])
        }
        return p
    }

    func streamProperties(forProperties properties: Set<CMIOExtensionProperty>) throws -> CMIOExtensionStreamProperties {
        let p = CMIOExtensionStreamProperties(dictionary: [:])
        if properties.contains(.streamActiveFormatIndex) { p.activeFormatIndex = activeFormatIndex }
        if properties.contains(.streamFrameDuration) { p.frameDuration = CMTime(value: 1, timescale: CMTimeScale(VCam.fps)) }
        if properties.contains(.streamSinkBufferQueueSize) { p.sinkBufferQueueSize = 2 }
        if properties.contains(.streamSinkBuffersRequiredForStartup) { p.sinkBuffersRequiredForStartup = 1 }
        if properties.contains(.streamSinkBufferUnderrunCount) { p.sinkBufferUnderrunCount = 0 }
        if properties.contains(.streamSinkEndOfData) { p.sinkEndOfData = 0 }
        return p
    }

    func setStreamProperties(_ streamProperties: CMIOExtensionStreamProperties) throws {
        if let i = streamProperties.activeFormatIndex, i >= 0, i < formats.count {
            lock.lock(); _activeFormatIndex = i; lock.unlock()
        }
    }

    func authorizedToStartStream(for client: CMIOExtensionClient) -> Bool {
        if isSink { device?.authorizeSink(client) }
        return true
    }

    func startStream() throws { isSink ? device?.startSink() : device?.startSource() }
    func stopStream() throws { isSink ? device?.stopSink() : device?.stopSource() }
}
