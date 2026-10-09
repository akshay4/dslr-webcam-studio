// Canon EOS live view on macOS through Apple's ImageCaptureCore PTP passthrough.
// ImageCaptureCore already owns the USB connection and the PTP session, so this sends
// raw Canon operations with requestSendPTPCommand: no libusb and no fighting the
// system camera daemon. Blocking calls; use from one background thread.
import Foundation
import ImageCaptureCore

struct CameraError: LocalizedError {
    let message: String
    init(_ m: String) { message = m }
    var errorDescription: String? { message }
}

final class CameraBrowser: NSObject, ICDeviceBrowserDelegate {
    static let shared = CameraBrowser()
    private let browser = ICDeviceBrowser()
    private let lock = NSLock()
    private var devices: [ICCameraDevice] = []
    private var started = false

    func start() {
        DispatchQueue.main.async {
            if self.started { return }
            self.started = true
            self.browser.delegate = self
            self.browser.browsedDeviceTypeMask = ICDeviceTypeMask(
                rawValue: ICDeviceTypeMask.camera.rawValue | ICDeviceLocationTypeMask.local.rawValue)!
            self.browser.start()
        }
    }

    // First connected Canon camera (USB vendor 0x04A9), waiting up to `timeout` seconds for discovery.
    func canonCamera(timeout: Double) -> ICCameraDevice? {
        start()
        let deadline = Date().addingTimeInterval(timeout)
        repeat {
            lock.lock()
            let cam = devices.first { $0.usbVendorID == 0x04A9 }
            lock.unlock()
            if let cam = cam { return cam }
            Thread.sleep(forTimeInterval: 0.1)
        } while Date() < deadline
        return nil
    }

    func deviceBrowser(_ browser: ICDeviceBrowser, didAdd device: ICDevice, moreComing: Bool) {
        guard let cam = device as? ICCameraDevice else { return }
        lock.lock(); devices.append(cam); lock.unlock()
    }

    func deviceBrowser(_ browser: ICDeviceBrowser, didRemove device: ICDevice, moreGoing: Bool) {
        lock.lock(); devices.removeAll { $0 === device }; lock.unlock()
    }
}

final class CanonCamera: NSObject, ICCameraDeviceDelegate {
    private let device: ICCameraDevice
    private var tid: UInt32 = 1
    private var openSignal = DispatchSemaphore(value: 0)
    private var openError: Error?
    private var lastEventPoll = Date.distantPast
    private var liveViewOn = false
    private(set) var props: [UInt32: UInt32] = [:]
    private(set) var info = PTP.DeviceInfo()

    var model: String { info.model.isEmpty ? (device.name ?? "Canon camera") : info.model }

    private init(device: ICCameraDevice) { self.device = device }

    static func open() throws -> CanonCamera {
        guard let dev = CameraBrowser.shared.canonCamera(timeout: 3) else {
            throw CameraError("No Canon camera found. Connect your Canon EOS camera over USB and switch it on.")
        }
        let cam = CanonCamera(device: dev)
        try cam.openSession()
        do { try cam.startLiveView() } catch { cam.close(); throw error }
        return cam
    }

    private func openSession() throws {
        DispatchQueue.main.async {
            self.device.delegate = self
            self.device.requestOpenSession()
        }
        if openSignal.wait(timeout: .now() + 15) == .timedOut { throw CameraError("Timed out opening the camera.") }
        if let e = openError { throw CameraError("Could not open the camera: \(e.localizedDescription)") }
    }

    private func startLiveView() throws {
        let di = try send(PTP.ocGetDeviceInfo, [])
        if di.code == PTP.rcOK, let parsed = PTP.parseDeviceInfo(di.data) {
            info = parsed
            if !info.operations.contains(PTP.ocEosGetViewFinderData) {
                throw CameraError("\(info.model) does not support live view over USB.")
            }
        }
        try check("SetRemoteMode", send(PTP.ocEosSetRemoteMode, [1]))
        try check("SetEventMode", send(PTP.ocEosSetEventMode, [1]))
        drainEvents()
        _ = try? setProp(PTP.dpcEVFMode, 1) // read-only on some bodies
        try check("Start live view (enable Live View in the camera menu)", setProp(PTP.dpcEVFOutputDevice, PTP.evfPC))
        liveViewOn = true
        drainEvents()
    }

    // Returns one live-view JPEG, or nil if the camera has no new frame yet.
    func readJPEG() throws -> Data? {
        if Date().timeIntervalSince(lastEventPoll) > 0.5 { drainEvents() }
        let r = try send(PTP.ocEosGetViewFinderData, [0x0010_0000])
        if r.code == PTP.rcCanonNotReady || r.code == PTP.rcDeviceBusy { return nil }
        try check("GetViewFinderData", r)
        guard let j = PTP.extractJPEG(r.data) else { return nil }
        return Data(j)
    }

    func setProperty(_ prop: UInt32, _ value: UInt32) -> UInt16 {
        let rc = (try? setProp(prop, value).code) ?? 0
        drainEvents()
        return rc
    }

    func close() {
        if liveViewOn { _ = try? setProp(PTP.dpcEVFOutputDevice, PTP.evfOff); liveViewOn = false }
        _ = try? send(PTP.ocEosSetEventMode, [0])
        _ = try? send(PTP.ocEosSetRemoteMode, [0])
        let sem = DispatchSemaphore(value: 0)
        DispatchQueue.main.async { self.device.requestCloseSession(); sem.signal() }
        _ = sem.wait(timeout: .now() + 2)
    }

    // ---- PTP plumbing ------------------------------------------------------------

    struct Response { let code: UInt16; let params: [UInt32]; let data: [UInt8] }

    private func setProp(_ prop: UInt32, _ value: UInt32) throws -> Response {
        try send(PTP.ocEosSetDevicePropValueEx, [], out: Data(PTP.le32(12) + PTP.le32(prop) + PTP.le32(value)))
    }

    // Canon queues property/state events after remote mode; reading them keeps the camera responsive.
    private func drainEvents() {
        lastEventPoll = Date()
        if let r = try? send(PTP.ocEosGetEvent, []), r.code == PTP.rcOK { PTP.parseEvents(r.data, into: &props) }
    }

    private func check(_ what: String, _ r: Response) throws {
        if r.code != PTP.rcOK { throw CameraError("\(what) failed: \(PTP.rcName(r.code))") }
    }

    private func send(_ code: UInt16, _ params: [UInt32], out: Data? = nil) throws -> Response {
        let t = tid
        tid &+= 1
        var cmd = PTP.le32(UInt32(12 + 4 * params.count)) + PTP.le16(1) + PTP.le16(code) + PTP.le32(t)
        for p in params { cmd += PTP.le32(p) }
        let command = Data(cmd)

        let sem = DispatchSemaphore(value: 0)
        var inData = Data(), respData = Data()
        var err: Error?
        DispatchQueue.main.async {
            self.device.requestSendPTPCommand(command, outData: out) { data, response, error in
                inData = data
                respData = response
                err = error
                sem.signal()
            }
        }
        if sem.wait(timeout: .now() + 10) == .timedOut { throw CameraError("Camera did not respond.") }
        if let e = err { throw CameraError("USB error: \(e.localizedDescription)") }

        let resp = [UInt8](respData)
        guard resp.count >= 8 else { throw CameraError("Short PTP response.") }
        let rc = PTP.rd16(resp, 6)
        var params2: [UInt32] = []
        var p = 12
        while p + 4 <= resp.count { params2.append(PTP.rd32(resp, p)); p += 4 }
        var payload = [UInt8](inData)
        // Strip a PTP data-container header if ImageCaptureCore left it on.
        if payload.count >= 12 && PTP.rd16(payload, 4) == 2 && Int(PTP.rd32(payload, 0)) == payload.count {
            payload.removeFirst(12)
        }
        return Response(code: rc, params: params2, data: payload)
    }

    // ---- ICCameraDeviceDelegate --------------------------------------------------

    func device(_ device: ICDevice, didOpenSessionWithError error: Error?) { openError = error; openSignal.signal() }
    func device(_ device: ICDevice, didCloseSessionWithError error: Error?) {}
    func didRemove(_ device: ICDevice) {}
    func cameraDevice(_ camera: ICCameraDevice, didAdd items: [ICCameraItem]) {}
    func cameraDevice(_ camera: ICCameraDevice, didRemove items: [ICCameraItem]) {}
    func cameraDevice(_ camera: ICCameraDevice, didRenameItems items: [ICCameraItem]) {}
    func cameraDevice(_ camera: ICCameraDevice, didReceiveThumbnail thumbnail: CGImage?, for item: ICCameraItem, error: Error?) {}
    func cameraDevice(_ camera: ICCameraDevice, didReceiveMetadata metadata: [AnyHashable: Any]?, for item: ICCameraItem, error: Error?) {}
    func cameraDeviceDidChangeCapability(_ camera: ICCameraDevice) {}
    func cameraDevice(_ camera: ICCameraDevice, didReceivePTPEvent eventData: Data) {}
    func deviceDidBecomeReady(withCompleteContentCatalog device: ICCameraDevice) {}
    func cameraDeviceDidRemoveAccessRestriction(_ device: ICDevice) {}
    func cameraDeviceDidEnableAccessRestriction(_ device: ICDevice) {}
}
