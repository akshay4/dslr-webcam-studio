// Values shared by the app and its camera extension (like windows/vcam/vcam_shared.h).
import CoreMedia
import CoreVideo
import Foundation

public enum VCam {
    public static let name = "DSLR Webcam Studio"
    public static let extensionID = "io.github.akshay4.dslrwebcamstudio.camera"
    // Fixed so the app can find the extension's device among all cameras.
    public static let deviceUID = "5A1E0C2B-7D3F-4E8A-9B61-0D5C3F2A8E41"
    public static let sourceStreamUID = "5A1E0C2B-7D3F-4E8A-9B61-0D5C3F2A8E42"
    public static let sinkStreamUID = "5A1E0C2B-7D3F-4E8A-9B61-0D5C3F2A8E43"
    // The camera offers the app's three output sizes; the first is the default. All 16:9.
    public static let sizes: [(Int, Int)] = [(1280, 720), (1920, 1080), (640, 360)]
    public static let fps = 30
    // With no frame from the app for this long, the camera shows the "start the app" picture.
    public static let staleSeconds = 1.5
}

// ---- pixel buffers ----------------------------------------------------------------------------
// Frames travel as IOSurface-backed 32BGRA pixel buffers, the app's own pixel layout.

public extension VCam {
    static func makePool(_ w: Int, _ h: Int) -> CVPixelBufferPool? {
        let attrs: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: w,
            kCVPixelBufferHeightKey as String: h,
            kCVPixelBufferIOSurfacePropertiesKey as String: [String: Any](),
        ]
        var pool: CVPixelBufferPool?
        CVPixelBufferPoolCreate(kCFAllocatorDefault, nil, attrs as CFDictionary, &pool)
        return pool
    }

    static func makeBuffer(_ pool: CVPixelBufferPool) -> CVPixelBuffer? {
        var pb: CVPixelBuffer?
        CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault, pool, &pb)
        return pb
    }

    static func hostNow() -> CMTime { CMClockGetTime(CMClockGetHostTimeClock()) }

    static func sampleBuffer(_ pb: CVPixelBuffer, at time: CMTime) -> CMSampleBuffer? {
        var fmt: CMFormatDescription?
        guard CMVideoFormatDescriptionCreateForImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: pb,
                                                           formatDescriptionOut: &fmt) == noErr, let fmt = fmt else { return nil }
        var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: CMTimeScale(fps)),
                                        presentationTimeStamp: time, decodeTimeStamp: .invalid)
        var sb: CMSampleBuffer?
        CMSampleBufferCreateReadyWithImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: pb, formatDescription: fmt,
                                                 sampleTiming: &timing, sampleBufferOut: &sb)
        return sb
    }
}
