// DSLRWebcamStudio              -> app
// DSLRWebcamStudio --selftest   -> built-in checks (no camera, no window)
// DSLRWebcamStudio --diag       -> camera diagnostics report
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
DSLRWebcamStudioApp.main()
