// swift-tools-version:5.7
import PackageDescription

let package = Package(
    name: "DSLRWebcamStudio",
    platforms: [.macOS(.v12)],
    targets: [
        .executableTarget(
            name: "DSLRWebcamStudio",
            path: "Sources/DSLRWebcamStudio",
            swiftSettings: [.unsafeFlags(["-Ounchecked"], .when(configuration: .release))],
            linkerSettings: [.linkedFramework("ImageCaptureCore"), .linkedFramework("AppKit")]
        ),
    ]
)
