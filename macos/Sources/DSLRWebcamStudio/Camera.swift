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

// Canon's own apps talk to the camera the same way and hold it while they run. EOS Webcam
// Utility's background service (a LaunchAgent, running from login) opens any Canon camera it
// sees, so its live view settings fight ours.
enum CanonSoftware {
    static let serviceLabel = "com.canon.usa.EWCService"
    static let servicePlist = "/Library/LaunchAgents/com.canon.usa.EWCService.plist"
    private static let names = ["EOSWebcamService", "EOS Webcam Utility", "EOS Utility"]

    // Names of running Canon camera processes.
    static func running() -> [String] {
        let n = proc_listallpids(nil, 0)
        guard n > 0 else { return [] }
        var pids = [pid_t](repeating: 0, count: Int(n) + 32)
        let got = Int(proc_listallpids(&pids, Int32(pids.count * MemoryLayout<pid_t>.size)))
        var out = Set<String>()
        var buf = [CChar](repeating: 0, count: 1024)
        for pid in pids.prefix(max(0, got)) where pid > 0 {
            guard proc_name(pid, &buf, UInt32(buf.count)) > 0 else { continue }
            let name = String(cString: buf)
            if names.contains(where: { name.hasPrefix($0) }) { out.insert(name) }
        }
        return out.sorted()
    }

    static var serviceRunning: Bool { running().contains("EOSWebcamService") }

    // Appended to camera errors when Canon software may be the cause.
    static var hint: String {
        let r = running()
        if r.isEmpty { return "" }
        return " Canon software is running and may be using the camera (\(r.joined(separator: ", "))): quit it"
            + (r.contains("EOSWebcamService") ? ", or use Camera > Pause Canon webcam service." : ".")
    }

    // Stops / restarts EOS Webcam Utility's background service for this login session (no admin
    // rights needed; it starts again at the next login anyway). Blocking: the service takes a few
    // seconds to exit, and launchd refuses to start it again until it has.
    @discardableResult static func pauseService() -> Bool {
        guard launchctl(["bootout", "gui/\(getuid())/\(serviceLabel)"]) else { return false }
        waitForServiceExit()
        return true
    }

    @discardableResult static func resumeService() -> Bool {
        waitForServiceExit()
        for _ in 0..<5 {
            if launchctl(["bootstrap", "gui/\(getuid())", servicePlist]) { return true }
            Thread.sleep(forTimeInterval: 1)
        }
        return false
    }

    private static func waitForServiceExit() {
        let deadline = Date().addingTimeInterval(10)
        while serviceRunning && Date() < deadline { Thread.sleep(forTimeInterval: 0.25) }
    }

    private static func launchctl(_ args: [String]) -> Bool {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        p.arguments = args
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { return false }
        p.waitUntilExit()
        return p.terminationStatus == 0
    }
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
    // Like Canon's EDSDK, prefer a device that accepts raw PTP commands.
    func canonCamera(timeout: Double) -> ICCameraDevice? {
        start()
        let deadline = Date().addingTimeInterval(timeout)
        repeat {
            lock.lock()
            let canon = devices.filter { $0.usbVendorID == 0x04A9 }
            lock.unlock()
            if let cam = canon.first(where: CameraBrowser.acceptsPTP) ?? canon.first { return cam }
            Thread.sleep(forTimeInterval: 0.1)
        } while Date() < deadline
        return nil
    }

    static func acceptsPTP(_ cam: ICCameraDevice) -> Bool {
        cam.capabilities.contains(ICDeviceCapability.cameraDeviceCanAcceptPTPCommands.rawValue)
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
    private var readySignal = DispatchSemaphore(value: 0)
    private var openError: Error?
    private var lastEventPoll = Date.distantPast
    private var lastKeepAlive = Date()
    private var liveViewOn = false
    private var viewFinderParams = 0 // index into PTP.viewFinderParams
    private(set) var props: [UInt32: UInt32] = [:]
    private(set) var info = PTP.DeviceInfo()

    var model: String { info.model.isEmpty ? (device.name ?? "Canon camera") : info.model }
    // The camera's live view output device, as last reported (or as we last set it).
    var evfOutput: UInt32? { props[PTP.dpcEVFOutputDevice] }
    // Without a device info dataset assume the camera has the op and let it answer.
    func supports(_ op: UInt16) -> Bool { info.operations.isEmpty || info.operations.contains(op) }

    private init(device: ICCameraDevice) { self.device = device }

    static func open() throws -> CanonCamera {
        guard let dev = CameraBrowser.shared.canonCamera(timeout: 3) else {
            throw CameraError("No Canon camera found. Connect your Canon EOS camera over USB and switch it on.")
        }
        if !CameraBrowser.acceptsPTP(dev) {
            throw CameraError("macOS doesn't allow camera control for \(dev.name ?? "this camera"). Set the camera's USB/communication mode to PTP (not Mass Storage) if it has one.")
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
        if openSignal.wait(timeout: .now() + 15) == .timedOut {
            throw CameraError("Timed out opening the camera. Quit other camera apps (EOS Utility, EOS Webcam Utility, Photos, Image Capture) and try again.")
        }
        if let e = openError { throw CameraError("Could not open the camera: \(e.localizedDescription)") }
        // ImageCaptureCore queues its own requests right after the session opens; PTP commands sent
        // before the device reports ready can wait behind them. Usually immediate; never fatal.
        _ = readySignal.wait(timeout: .now() + 3)
    }

    private func startLiveView() throws {
        let di = try send(PTP.ocGetDeviceInfo, [])
        if di.code == PTP.rcOK, let parsed = PTP.parseDeviceInfo(di.data) {
            info = parsed
            for op in [PTP.ocEosSetRemoteMode, PTP.ocEosSetDevicePropValueEx, PTP.ocEosGetViewFinderData]
            where !info.operations.contains(op) {
                throw CameraError("\(info.model) does not support live view over USB.")
            }
        }
        try check("SetRemoteMode", retryBusy { try self.send(PTP.ocEosSetRemoteMode, [1]) })
        try check("SetEventMode", retryBusy { try self.send(PTP.ocEosSetEventMode, [1]) })
        drainEvents()
        _ = try? setProp(PTP.dpcEVFMode, 1) // read-only on some bodies
        try check("Start live view (enable Live View in the camera menu)", setEvfToPC())
        liveViewOn = true
        drainEvents()
    }

    // Sends live view to the computer. Remembers the value so a stale event from before the
    // change doesn't look like the camera switching it off again.
    private func setEvfToPC() throws -> Response {
        let r = try retryBusy { try self.setProp(PTP.dpcEVFOutputDevice, PTP.evfPC) }
        if r.code == PTP.rcOK { props[PTP.dpcEVFOutputDevice] = PTP.evfPC }
        return r
    }

    // Turns live view back on after the camera dropped it (Canon's service does the same).
    func restartLiveView() throws {
        _ = try? setProp(PTP.dpcEVFMode, 1)
        try check("Restart live view", setEvfToPC())
        drainEvents()
    }

    // Retries while the camera answers DeviceBusy, as Canon's EDSDK does for remote/event mode.
    private func retryBusy(_ op: () throws -> Response) throws -> Response {
        var r = try op()
        var tries = 1
        while r.code == PTP.rcDeviceBusy && tries < PTP.busyRetries {
            Thread.sleep(forTimeInterval: PTP.busyRetryDelay)
            r = try op()
            tries += 1
        }
        return r
    }

    // Returns one live-view JPEG, or nil if the camera has no new frame yet.
    func readJPEG() throws -> Data? {
        if Date().timeIntervalSince(lastEventPoll) > 0.5 { drainEvents() }
        if Date().timeIntervalSince(lastKeepAlive) > 60 { keepAwake() }
        let r = try send(PTP.ocEosGetViewFinderData, PTP.viewFinderParams[viewFinderParams])
        if r.code == PTP.rcCanonNotReady || r.code == PTP.rcDeviceBusy { return nil }
        if (r.code == PTP.rcInvalidParameter || r.code == PTP.rcParameterNotSupported)
            && viewFinderParams + 1 < PTP.viewFinderParams.count {
            viewFinderParams += 1
            return nil
        }
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
        if liveViewOn { _ = try? retryBusy { try self.setProp(PTP.dpcEVFOutputDevice, PTP.evfOff) }; liveViewOn = false }
        _ = try? retryBusy { try self.send(PTP.ocEosSetEventMode, [0]) }
        _ = try? retryBusy { try self.send(PTP.ocEosSetRemoteMode, [0]) }
        let sem = DispatchSemaphore(value: 0)
        DispatchQueue.main.async { self.device.requestCloseSession(); sem.signal() }
        _ = sem.wait(timeout: .now() + 2)
    }

    // Set once the camera is unplugged or stops answering, so the remaining calls (including
    // close) fail at once instead of each waiting out the timeout before a reconnect.
    private let goneLock = NSLock()
    private var _gone = false
    private var gone: Bool {
        get { goneLock.lock(); defer { goneLock.unlock() }; return _gone }
        set { goneLock.lock(); _gone = newValue; goneLock.unlock() }
    }

    // ---- PTP plumbing ------------------------------------------------------------

    struct Response { let code: UInt16; let params: [UInt32]; let data: [UInt8] }

    private func setProp(_ prop: UInt32, _ value: UInt32) throws -> Response {
        try send(PTP.ocEosSetDevicePropValueEx, [], out: Data(PTP.le32(12) + PTP.le32(prop) + PTP.le32(value)))
    }

    // Canon queues property/state events after remote mode; reading them keeps the camera responsive.
    private func drainEvents() {
        lastEventPoll = Date()
        if let r = try? send(PTP.ocEosGetEvent, []), r.code == PTP.rcOK, PTP.parseEvents(r.data, into: &props) { keepAwake() }
    }

    // Resets the camera's auto power off timer (Canon's service answers the camera's
    // "will soon shut down" event this way; we also send it every minute).
    private func keepAwake() {
        lastKeepAlive = Date()
        if supports(PTP.ocEosKeepDeviceOn) { _ = try? send(PTP.ocEosKeepDeviceOn, []) }
    }

    private func check(_ what: String, _ r: Response) throws {
        if r.code != PTP.rcOK { throw CameraError("\(what) failed: \(PTP.rcName(r.code))") }
    }

    private func send(_ code: UInt16, _ params: [UInt32], out: Data? = nil) throws -> Response {
        if gone { throw CameraError("Camera disconnected.") }
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
        if sem.wait(timeout: .now() + 10) == .timedOut { gone = true; throw CameraError("Camera did not respond.") }
        if let e = err { gone = true; throw CameraError("USB error: \(e.localizedDescription)") }

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
    func deviceDidBecomeReady(_ device: ICDevice) { readySignal.signal() }
    func device(_ device: ICDevice, didCloseSessionWithError error: Error?) {}
    func didRemove(_ device: ICDevice) { gone = true }
    func cameraDevice(_ camera: ICCameraDevice, didAdd items: [ICCameraItem]) {}
    func cameraDevice(_ camera: ICCameraDevice, didRemove items: [ICCameraItem]) {}
    func cameraDevice(_ camera: ICCameraDevice, didRenameItems items: [ICCameraItem]) {}
    func cameraDevice(_ camera: ICCameraDevice, didReceiveThumbnail thumbnail: CGImage?, for item: ICCameraItem, error: Error?) {}
    func cameraDevice(_ camera: ICCameraDevice, didReceiveMetadata metadata: [AnyHashable: Any]?, for item: ICCameraItem, error: Error?) {}
    func cameraDeviceDidChangeCapability(_ camera: ICCameraDevice) {}
    func cameraDevice(_ camera: ICCameraDevice, didReceivePTPEvent eventData: Data) {}
    func deviceDidBecomeReady(withCompleteContentCatalog device: ICCameraDevice) { readySignal.signal() }
    // Live view only: don't let ImageCaptureCore fetch thumbnails and metadata of every photo on
    // the card over the same USB pipe while we stream.
    func cameraDevice(_ camera: ICCameraDevice, shouldGetThumbnailOf item: ICCameraItem) -> Bool { false }
    func cameraDevice(_ camera: ICCameraDevice, shouldGetMetadataOf item: ICCameraItem) -> Bool { false }
    func cameraDeviceDidRemoveAccessRestriction(_ device: ICDevice) {}
    func cameraDeviceDidEnableAccessRestriction(_ device: ICDevice) {}
}
