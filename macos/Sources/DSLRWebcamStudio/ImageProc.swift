// Frames, geometry, bicubic scaling with mirroring, and live-view cleanup.
// Pixels are packed 0xAARRGGBB UInt32 (little-endian BGRA in memory, which CoreGraphics
// reads with byteOrder32Little + premultipliedFirst). Same algorithms as the Windows/Linux apps.
import CoreGraphics
import Foundation
import ImageIO

final class Frame {
    let w: Int, h: Int
    var px: [UInt32]
    init(w: Int, h: Int, fill: UInt32 = 0xFF00_0000) { self.w = w; self.h = h; px = [UInt32](repeating: fill, count: w * h) }
    init(w: Int, h: Int, px: [UInt32]) { self.w = w; self.h = h; self.px = px }

    static func decodeJPEG(_ data: Data) -> Frame? {
        guard let src = CGImageSourceCreateWithData(data as CFData, nil),
              let img = CGImageSourceCreateImageAtIndex(src, 0, nil) else { return nil }
        let f = Frame(w: img.width, h: img.height)
        let ok = f.px.withUnsafeMutableBytes { buf -> Bool in
            guard let ctx = CGContext(data: buf.baseAddress, width: f.w, height: f.h, bitsPerComponent: 8,
                                      bytesPerRow: f.w * 4, space: CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: Frame.bitmapInfo) else { return false }
            ctx.draw(img, in: CGRect(x: 0, y: 0, width: f.w, height: f.h))
            return true
        }
        return ok ? f : nil
    }

    static let bitmapInfo = CGBitmapInfo.byteOrder32Little.rawValue | CGImageAlphaInfo.premultipliedFirst.rawValue

    func cgImage() -> CGImage? {
        let data = px.withUnsafeBytes { Data($0) }
        guard let provider = CGDataProvider(data: data as CFData) else { return nil }
        return CGImage(width: w, height: h, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: w * 4,
                       space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGBitmapInfo(rawValue: Frame.bitmapInfo),
                       provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent)
    }
}

struct IRect: Equatable { var x, y, w, h: Int }

enum Geometry {
    private static func round(_ v: Double) -> Int { Int((v + 0.5).rounded(.down)) }

    // Largest rect with the source aspect that fits inside dst, centered (pillar/letterbox).
    static func fit(_ sw: Int, _ sh: Int, _ dw: Int, _ dh: Int) -> IRect {
        let scale = min(Double(dw) / Double(sw), Double(dh) / Double(sh))
        let w = min(dw, round(Double(sw) * scale)), h = min(dh, round(Double(sh) * scale))
        return IRect(x: (dw - w) / 2, y: (dh - h) / 2, w: w, h: h)
    }

    // Centered region of the source with the dst aspect (crop to fill).
    static func crop(_ sw: Int, _ sh: Int, _ dw: Int, _ dh: Int) -> IRect {
        if sw * dh > dw * sh {
            let w = round(Double(sh) * Double(dw) / Double(dh))
            return IRect(x: (sw - w) / 2, y: 0, w: w, h: sh)
        }
        let h = round(Double(sw) * Double(dh) / Double(dw))
        return IRect(x: 0, y: (sh - h) / 2, w: sw, h: h)
    }
}

// Splits rows across CPU cores.
func parallelRows(_ n: Int, _ body: (Int, Int) -> Void) {
    let chunks = min(max(1, ProcessInfo.processInfo.activeProcessorCount), 16)
    if n < 64 || chunks < 2 { body(0, n); return }
    let per = (n + chunks - 1) / chunks
    DispatchQueue.concurrentPerform(iterations: chunks) { c in
        let y0 = c * per, y1 = min(n, y0 + per)
        if y0 < y1 { body(y0, y1) }
    }
}

// Catmull-Rom bicubic resize with optional mirroring. Destination outside `dr` is filled black.
final class Scaler {
    private var tmp: [Float] = []

    private struct Axis { let taps: Int; let idx: [Int]; let w: [Float] }

    private static func catmullRom(_ x0: Double) -> Float {
        let x = abs(x0)
        if x < 1 { return Float((1.5 * x - 2.5) * x * x + 1) }
        if x < 2 { return Float(((-0.5 * x + 2.5) * x - 4) * x + 2) }
        return 0
    }

    private static func axis(_ srcStart: Int, _ srcLen: Int, _ dstLen: Int, _ flip: Bool) -> Axis {
        let scale = Double(srcLen) / Double(dstLen)
        let fscale = max(1.0, scale)                 // widen the kernel when shrinking
        let support = 2 * fscale
        let taps = Int((support * 2).rounded(.up)) + 1
        var idx = [Int](repeating: 0, count: dstLen * taps), w = [Float](repeating: 0, count: dstLen * taps)
        for i in 0..<dstLen {
            let o = (flip ? dstLen - 1 - i : i) * taps
            let center = (Double(i) + 0.5) * scale - 0.5
            let first = Int((center - support).rounded(.down)) + 1
            var sum: Float = 0
            for t in 0..<taps {
                let s = first + t
                let wt = catmullRom((Double(s) - center) / fscale)
                idx[o + t] = srcStart + min(max(s, 0), srcLen - 1)
                w[o + t] = wt
                sum += wt
            }
            if sum != 0 { for t in 0..<taps { w[o + t] /= sum } }
        }
        return Axis(taps: taps, idx: idx, w: w)
    }

    func draw(_ src: Frame, _ sr: IRect, _ dst: Frame, _ dr: IRect, flipH: Bool = false, flipV: Bool = false) {
        let ax = Scaler.axis(sr.x, sr.w, dr.w, flipH)
        let ay = Scaler.axis(0, sr.h, dr.h, flipV)
        let dw = dr.w, W = dst.w
        let need = sr.h * dw * 3
        if tmp.count < need { tmp = [Float](repeating: 0, count: need) }

        src.px.withUnsafeBufferPointer { s in
            tmp.withUnsafeMutableBufferPointer { t in
                parallelRows(sr.h) { y0, y1 in
                    for row in y0..<y1 {
                        let srow = (sr.y + row) * src.w
                        var to = row * dw * 3
                        for x in 0..<dw {
                            var r: Float = 0, g: Float = 0, b: Float = 0
                            let o = x * ax.taps
                            for k in 0..<ax.taps {
                                let p = s[srow + ax.idx[o + k]], wt = ax.w[o + k]
                                r += Float((p >> 16) & 255) * wt
                                g += Float((p >> 8) & 255) * wt
                                b += Float(p & 255) * wt
                            }
                            t[to] = r; t[to + 1] = g; t[to + 2] = b
                            to += 3
                        }
                    }
                }
            }
        }

        tmp.withUnsafeBufferPointer { t in
            dst.px.withUnsafeMutableBufferPointer { d in
                parallelRows(dst.h) { y0, y1 in
                    for y in y0..<y1 {
                        let row = y * W
                        if y < dr.y || y >= dr.y + dr.h {
                            for x in 0..<W { d[row + x] = 0xFF00_0000 }
                            continue
                        }
                        for x in 0..<dr.x { d[row + x] = 0xFF00_0000 }
                        for x in (dr.x + dw)..<max(dr.x + dw, W) { d[row + x] = 0xFF00_0000 }
                        let o = (y - dr.y) * ay.taps
                        for x in 0..<dw {
                            var r: Float = 0, g: Float = 0, b: Float = 0
                            for k in 0..<ay.taps {
                                let i = (ay.idx[o + k] * dw + x) * 3, wt = ay.w[o + k]
                                r += t[i] * wt; g += t[i + 1] * wt; b += t[i + 2] * wt
                            }
                            d[row + dr.x + x] = 0xFF00_0000 | Scaler.clamp(r) << 16 | Scaler.clamp(g) << 8 | Scaler.clamp(b)
                        }
                    }
                }
            }
        }
    }

    private static func clamp(_ v: Float) -> UInt32 {
        let i = Int(v + 0.5)
        return UInt32(i < 0 ? 0 : i > 255 ? 255 : i)
    }
}

// 1. Temporal noise reduction: each pixel blends with its running average. Motion is detected
//    from the brightness change averaged over 3x3; random sensor noise cancels out in that
//    average but real movement does not, so moving areas take the new frame directly.
// 2. Unsharp mask (3x3) with a small dead zone so leftover noise isn't sharpened.
final class Enhancer {
    private var w = 0, h = 0
    private var acc: [UInt32] = [], out: [UInt32] = [], diff: [Int] = []
    private var haveAcc = false
    //                          Off  Low  Medium High
    private static let nrBase = [256, 96, 64, 40]
    private static let nrThresh = [1, 6, 9, 12]
    private static let sharpAmount = [0, 128, 256, 420]

    private static func luma(_ p: UInt32) -> Int { Int(2 * ((p >> 16) & 255) + 5 * ((p >> 8) & 255) + (p & 255)) >> 3 }
    private static func ch(_ p: UInt32, _ s: UInt32) -> Int { Int((p >> s) & 255) }

    // Returns src when both are off, otherwise a new frame.
    func process(_ src: Frame, noise: Int, sharp: Int) -> Frame {
        if noise <= 0 && sharp <= 0 { haveAcc = false; return src }
        if w != src.w || h != src.h {
            w = src.w; h = src.h
            acc = [UInt32](repeating: 0, count: w * h); out = acc
            diff = [Int](repeating: 0, count: w * h)
            haveAcc = false
        }
        var img = src.px
        if noise > 0 {
            if !haveAcc { acc = src.px; haveAcc = true } else { temporal(src.px, base: Enhancer.nrBase[min(noise, 3)], thr: Enhancer.nrThresh[min(noise, 3)]) }
            img = acc
        } else {
            haveAcc = false
        }
        if sharp > 0 {
            sharpen(img, amount: Enhancer.sharpAmount[min(sharp, 3)])
            img = out
        }
        let f = Frame(w: w, h: h)
        f.px = img
        return f
    }

    private func temporal(_ cur: [UInt32], base: Int, thr: Int) {
        let W = w, H = h
        cur.withUnsafeBufferPointer { c in
            acc.withUnsafeMutableBufferPointer { a in
                diff.withUnsafeMutableBufferPointer { df in
                    parallelRows(H) { y0, y1 in
                        for i in (y0 * W)..<(y1 * W) { df[i] = Enhancer.luma(c[i]) - Enhancer.luma(a[i]) }
                    }
                    parallelRows(H) { y0, y1 in
                        for y in y0..<y1 {
                            let ym = max(0, y - 1) * W, yc = y * W, yp = min(H - 1, y + 1) * W
                            for x in 0..<W {
                                let xm = max(0, x - 1), xp = min(W - 1, x + 1)
                                let sum = df[ym + xm] + df[ym + x] + df[ym + xp] + df[yc + xm] + df[yc + x] + df[yc + xp]
                                    + df[yp + xm] + df[yp + x] + df[yp + xp]
                                let m = abs(sum) / 9
                                let k = m >= thr ? 256 : base + (256 - base) * m / thr
                                let p = a[yc + x], n = c[yc + x]
                                var pr = Int((p >> 16) & 255), pg = Int((p >> 8) & 255), pb = Int(p & 255)
                                pr += ((Int((n >> 16) & 255) - pr) * k + 128) >> 8
                                pg += ((Int((n >> 8) & 255) - pg) * k + 128) >> 8
                                pb += ((Int(n & 255) - pb) * k + 128) >> 8
                                a[yc + x] = 0xFF00_0000 | UInt32(pr) << 16 | UInt32(pg) << 8 | UInt32(pb)
                            }
                        }
                    }
                }
            }
        }
    }

    private func sharpen(_ src: [UInt32], amount: Int) {
        let W = w, H = h
        src.withUnsafeBufferPointer { s in
            out.withUnsafeMutableBufferPointer { o in
                parallelRows(H) { y0, y1 in
                    for y in y0..<y1 {
                        let ym = max(0, y - 1) * W, yc = y * W, yp = min(H - 1, y + 1) * W
                        for x in 0..<W {
                            let xm = max(0, x - 1), xp = min(W - 1, x + 1)
                            let c = s[yc + x]
                            var result: UInt32 = 0xFF00_0000
                            for sh in [UInt32(0), 8, 16] {
                                var blur = Enhancer.ch(s[ym + xm], sh) + 2 * Enhancer.ch(s[ym + x], sh) + Enhancer.ch(s[ym + xp], sh)
                                blur += 2 * Enhancer.ch(s[yc + xm], sh) + 4 * Enhancer.ch(c, sh) + 2 * Enhancer.ch(s[yc + xp], sh)
                                blur += Enhancer.ch(s[yp + xm], sh) + 2 * Enhancer.ch(s[yp + x], sh) + Enhancer.ch(s[yp + xp], sh)
                                blur = (blur + 8) >> 4
                                var v = Enhancer.ch(c, sh)
                                var d = v - blur
                                d = d > 2 ? d - 2 : d < -2 ? d + 2 : 0 // dead zone
                                v += (d * amount) >> 8
                                result |= UInt32(min(255, max(0, v))) << sh
                            }
                            o[yc + x] = result
                        }
                    }
                }
            }
        }
    }
}
