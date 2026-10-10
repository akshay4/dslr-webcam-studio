// Virtual webcam: installs the "DSLR Webcam Studio" camera extension and feeds it the output frames.
// The app finds the extension's device through CoreMediaIO, opens its sink stream and queues each
// output frame there; the extension hands them to Teams, Meet, Zoom, OBS and other video apps.
import AVFoundation
import CoreMedia
import CoreMediaIO
import CoreVideo
import Foundation
import SystemExtensions
import VCamShared

// ---- frame output (called on the output-engine thread) -------------------------------------------

final class VirtualCameraOutput {
    private let lock = NSLock()
    private var device: CMIOObjectID = 0, stream: CMIOStreamID = 0
    private var queue: CMSimpleQueue?
    private var pool: CVPixelBufferPool?
    private var poolSize = (0, 0)
    private var nextAttempt = 0.0
    private var _connected = false

    // True while frames are reaching the camera extension.
    var connected: Bool { lock.lock(); defer { lock.unlock() }; return _connected }

    func send(_ f: Frame) {
        lock.lock(); defer { lock.unlock() }
        guard let q = queue ?? connect() else { return }
        if CMSimpleQueueGetCount(q) >= CMSimpleQueueGetCapacity(q) { return } // extension is behind: drop this frame
        if pool == nil || poolSize != (f.w, f.h) { pool = VCam.makePool(f.w, f.h); poolSize = (f.w, f.h) }
        guard let p = pool, let pb = VirtualCameraOutput.pixelBuffer(f, p), let sb = VCam.sampleBuffer(pb, at: VCam.hostNow()) else { return }
        let item = Unmanaged.passRetained(sb)
        if CMSimpleQueueEnqueue(q, element: item.toOpaque()) != noErr {
            item.release()
            disconnect() // the extension was restarted or removed; look for it again
        }
    }

    func stop() { lock.lock(); disconnect(); lock.unlock() }

    static func pixelBuffer(_ f: Frame, _ pool: CVPixelBufferPool) -> CVPixelBuffer? {
        guard let pb = VCam.makeBuffer(pool) else { return nil }
        CVPixelBufferLockBaseAddress(pb, []); defer { CVPixelBufferUnlockBaseAddress(pb, []) }
        guard let base = CVPixelBufferGetBaseAddress(pb) else { return nil }
        let rowBytes = CVPixelBufferGetBytesPerRow(pb), w4 = f.w * 4
        // Frame pixels are already 32BGRA in memory; copy row by row since the buffer rows may be padded.
        f.px.withUnsafeBytes { src in
            for y in 0..<f.h { memcpy(base + y * rowBytes, src.baseAddress! + y * w4, w4) }
        }
        return pb
    }

    // Finds the extension's device and starts its sink stream; retried at most every 2 seconds.
    private func connect() -> CMSimpleQueue? {
        let now = monoNow()
        if now < nextAttempt { return nil }
        nextAttempt = now + 2
        // Only touch CoreMediaIO once the camera exists (AVFoundation's lookup never asks for camera access).
        guard AVCaptureDevice(uniqueID: VCam.deviceUID) != nil,
              let dev = CMIO.devices().first(where: { CMIO.string($0, kCMIODevicePropertyDeviceUID) == VCam.deviceUID }),
              let sink = CMIO.streams(dev).first(where: { CMIO.isSink($0) }) else { return nil }
        var q: Unmanaged<CMSimpleQueue>?
        guard CMIOStreamCopyBufferQueue(sink, { _, _, _ in }, nil, &q) == noErr, let queue = q?.takeRetainedValue() else { return nil }
        guard CMIODeviceStartStream(dev, sink) == noErr else { return nil }
        device = dev; stream = sink; self.queue = queue; _connected = true
        return queue
    }

    private func disconnect() {
        if queue != nil { CMIODeviceStopStream(device, stream) }
        queue = nil; device = 0; stream = 0; _connected = false
    }
}

// Small wrappers over the CoreMediaIO C property API.
enum CMIO {
    private static func address(_ selector: Int, _ scope: Int = kCMIOObjectPropertyScopeGlobal) -> CMIOObjectPropertyAddress {
        CMIOObjectPropertyAddress(mSelector: CMIOObjectPropertySelector(selector), mScope: CMIOObjectPropertyScope(scope),
                                  mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementMain))
    }

    private static func ids(_ object: CMIOObjectID, _ selector: Int) -> [CMIOObjectID] {
        var addr = address(selector)
        var size: UInt32 = 0
        guard CMIOObjectGetPropertyDataSize(object, &addr, 0, nil, &size) == noErr, size > 0 else { return [] }
        var out = [CMIOObjectID](repeating: 0, count: Int(size) / MemoryLayout<CMIOObjectID>.size)
        var used: UInt32 = 0
        guard CMIOObjectGetPropertyData(object, &addr, 0, nil, size, &used, &out) == noErr else { return [] }
        return Array(out.prefix(Int(used) / MemoryLayout<CMIOObjectID>.size))
    }

    static func devices() -> [CMIOObjectID] { ids(CMIOObjectID(kCMIOObjectSystemObject), kCMIOHardwarePropertyDevices) }
    static func streams(_ device: CMIOObjectID) -> [CMIOStreamID] { ids(device, kCMIODevicePropertyStreams) }

    static func string(_ object: CMIOObjectID, _ selector: Int) -> String? {
        var addr = address(selector)
        var value: Unmanaged<CFString>?
        var used: UInt32 = 0
        let size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard CMIOObjectGetPropertyData(object, &addr, 0, nil, size, &used, &value) == noErr else { return nil }
        return value?.takeRetainedValue() as String?
    }

    // Direction 0 = output from this app into the device, i.e. the extension's sink stream.
    static func isSink(_ stream: CMIOStreamID) -> Bool {
        var addr = address(kCMIOStreamPropertyDirection)
        var direction: UInt32 = 1
        var used: UInt32 = 0
        return CMIOObjectGetPropertyData(stream, &addr, 0, nil, UInt32(MemoryLayout<UInt32>.size), &used, &direction) == noErr
            && direction == 0
    }
}

// ---- install / uninstall ------------------------------------------------------------------------

final class VirtualCameraInstaller: NSObject, OSSystemExtensionRequestDelegate {
    private var done: ((String) -> Void)?
    private var installing = true

    // Why the virtual camera can't be installed from this copy of the app, or nil if it can.
    static var unavailableReason: String? {
        let plist = Bundle.main.bundleURL.appendingPathComponent(
            "Contents/Library/SystemExtensions/\(VCam.extensionID).systemextension/Contents/Info.plist")
        guard let info = NSDictionary(contentsOf: plist),
              let cmio = info["CMIOExtension"] as? [String: Any],
              let service = cmio["CMIOExtensionMachServiceName"] as? String else {
            return "This copy of the app doesn't include the camera extension. Build it with macos/build.sh."
        }
        if service.hasPrefix("UNSIGNED") {
            return "This build isn't signed with an Apple Developer ID, so macOS won't load its virtual camera. "
                + "See \"macOS virtual camera\" in the README for how to build a signed copy."
        }
        if !Bundle.main.bundlePath.hasPrefix("/Applications/") {
            return "Move DSLR Webcam Studio to the Applications folder first, then open it from there and try again."
        }
        return nil
    }

    func install(_ done: @escaping (String) -> Void) { submit(install: true, done) }
    func uninstall(_ done: @escaping (String) -> Void) { submit(install: false, done) }

    private func submit(install: Bool, _ done: @escaping (String) -> Void) {
        self.done = done
        installing = install
        let r = install
            ? OSSystemExtensionRequest.activationRequest(forExtensionWithIdentifier: VCam.extensionID, queue: .main)
            : OSSystemExtensionRequest.deactivationRequest(forExtensionWithIdentifier: VCam.extensionID, queue: .main)
        r.delegate = self
        OSSystemExtensionManager.shared.submitRequest(r)
    }

    private func finish(_ message: String) { done?(message); done = nil }

    func request(_ request: OSSystemExtensionRequest, actionForReplacingExtension existing: OSSystemExtensionProperties,
                 withExtension ext: OSSystemExtensionProperties) -> OSSystemExtensionRequest.ReplacementAction { .replace }

    func requestNeedsUserApproval(_ request: OSSystemExtensionRequest) {
        done?("Approve the camera in System Settings > General > Login Items & Extensions > Camera Extensions, then restart your video app.")
    }

    func request(_ request: OSSystemExtensionRequest, didFinishWithResult result: OSSystemExtensionRequest.Result) {
        let restart = result == .willCompleteAfterReboot ? " Restart your Mac to finish." : ""
        finish(installing
            ? "Virtual camera installed. Pick \"\(VCam.name)\" as the camera in Teams, Meet, Zoom or OBS.\(restart)"
            : "Virtual camera removed.\(restart)")
    }

    func request(_ request: OSSystemExtensionRequest, didFailWithError error: Error) {
        let e = error as NSError
        var why = e.localizedDescription
        if e.domain == OSSystemExtensionErrorDomain {
            switch OSSystemExtensionError.Code(rawValue: e.code) {
            case .unsupportedParentBundleLocation: why = "Move the app to the Applications folder and open it from there."
            case .missingEntitlement, .codeSignatureInvalid, .validationFailed:
                why = "This build isn't signed with an Apple Developer ID that allows camera extensions."
            case .extensionNotFound: why = installing ? "The camera extension is missing from the app." : "The virtual camera isn't installed."
            case .requestCanceled: why = "The request was cancelled."
            default: break
            }
        }
        finish((installing ? "Could not install the virtual camera: " : "Could not remove the virtual camera: ") + why)
    }
}
