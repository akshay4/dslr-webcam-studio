// swift-tools-version:5.7
import PackageDescription

let package = Package(
    name: "DSLRWebcamStudio",
    platforms: [.macOS(.v12)],
    targets: [
        .executableTarget(
            name: "DSLRWebcamStudio",
            dependencies: ["VCamShared"],
            path: "Sources/DSLRWebcamStudio",
            swiftSettings: [.unsafeFlags(["-Ounchecked"], .when(configuration: .release))],
            linkerSettings: [.linkedFramework("ImageCaptureCore"), .linkedFramework("AppKit"),
                             .linkedFramework("AVFoundation"), .linkedFramework("CoreMediaIO"),
                             .linkedFramework("SystemExtensions")]
        ),
        // The virtual camera (CoreMediaIO Camera Extension, macOS 12.3+), bundled inside the app by build.sh.
        .executableTarget(
            name: "DSLRWebcamStudioCamera",
            dependencies: ["VCamShared"],
            path: "Sources/DSLRWebcamStudioCamera",
            linkerSettings: [.linkedFramework("CoreMediaIO"), .linkedFramework("Accelerate")]
        ),
        .target(name: "VCamShared", path: "Sources/VCamShared"),
    ]
)
