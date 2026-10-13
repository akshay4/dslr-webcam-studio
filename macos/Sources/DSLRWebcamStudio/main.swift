// DSLRWebcamStudio              -> app
// DSLRWebcamStudio --selftest   -> built-in checks (no camera, no window)
// DSLRWebcamStudio --diag       -> camera diagnostics report
// DSLRWebcamStudio --uninstall-vcam -> remove the virtual camera
// DSLRWebcamStudio --pause-canon-service / --resume-canon-service -> stop / restart Canon's EOS Webcam Utility service
import AppKit
import Foundation

let args = CommandLine.arguments
if args.contains("--selftest") {
    exit(SelfTest.run() ? 0 : 1)
}
if args.contains("--version") {
    print("DSLR Webcam Studio \(AppInfo.version)")
    exit(0)
}
if args.contains("--diag") {
    // Camera callbacks arrive on the main run loop, so run the report on a background thread.
    DispatchQueue.global().async {
        print(Diagnostics.run())
        exit(0)
    }
    RunLoop.main.run()
}
if args.contains("--pause-canon-service") || args.contains("--resume-canon-service") {
    let pause = args.contains("--pause-canon-service")
    let ok = pause ? CanonSoftware.pauseService() : CanonSoftware.resumeService()
    print(ok ? (pause ? "Canon's webcam service paused until your next login." : "Canon's webcam service restarted.")
             : "Could not \(pause ? "pause" : "restart") Canon's webcam service.")
    exit(ok ? 0 : 1)
}
if args.contains("--uninstall-vcam") {
    let installer = VirtualCameraInstaller()
    installer.uninstall { msg in
        print(msg)
        if !msg.hasPrefix("Approve") { exit(msg.hasPrefix("Could not") ? 1 : 0) }
    }
    RunLoop.main.run()
}
DSLRWebcamStudioApp.main()
