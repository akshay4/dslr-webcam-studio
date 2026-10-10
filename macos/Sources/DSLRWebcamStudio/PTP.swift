// PTP / Canon EOS protocol constants and pure parsing helpers (no I/O).
import Foundation

enum PTP {
    static let ocGetDeviceInfo: UInt16 = 0x1001
    static let ocEosSetDevicePropValueEx: UInt16 = 0x9110
    static let ocEosSetRemoteMode: UInt16 = 0x9114
    static let ocEosSetEventMode: UInt16 = 0x9115
    static let ocEosGetEvent: UInt16 = 0x9116
    static let ocEosGetViewFinderData: UInt16 = 0x9153

    static let rcOK: UInt16 = 0x2001
    static let rcDeviceBusy: UInt16 = 0x2019
    static let rcCanonNotReady: UInt16 = 0xA102

    static let dpcAperture: UInt32 = 0xD101
    static let dpcShutterSpeed: UInt32 = 0xD102
    static let dpcISOSpeed: UInt32 = 0xD103
    static let dpcAutoExposureMode: UInt32 = 0xD105
    static let dpcEVFOutputDevice: UInt32 = 0xD1B0
    static let dpcEVFMode: UInt32 = 0xD1B3
    static let evfOff: UInt32 = 0, evfPC: UInt32 = 2

    static func rcName(_ rc: UInt16) -> String {
        switch rc {
        case 0x2001: return "OK"
        case 0x2002: return "GeneralError"
        case 0x2003: return "SessionNotOpen"
        case 0x2005: return "OperationNotSupported"
        case 0x2019: return "DeviceBusy"
        case 0x201D: return "InvalidParameter"
        case 0xA102: return "Canon NotReady"
        default: return String(format: "0x%04X", rc)
        }
    }

    // Live-view data is a series of [u32 length][u32 type][payload] blocks; type 1 is the JPEG.
    // Falls back to scanning for JPEG SOI/EOI markers.
    static func extractJPEG(_ d: [UInt8]) -> ArraySlice<UInt8>? {
        if d.count < 4 { return nil }
        var pos = 0
        while pos + 8 <= d.count {
            let len = Int(rd32(d, pos)), type = rd32(d, pos + 4)
            if len < 8 || pos + len > d.count { break }
            if type == 1 && len > 10 && d[pos + 8] == 0xFF && d[pos + 9] == 0xD8 { return d[(pos + 8)..<(pos + len)] }
            pos += len
        }
        var i = 0
        while i + 1 < d.count {
            if d[i] == 0xFF && d[i + 1] == 0xD8 {
                var e = d.count - 2
                while e > i {
                    if d[e] == 0xFF && d[e + 1] == 0xD9 { return d[i..<(e + 2)] }
                    e -= 1
                }
                return nil
            }
            i += 1
        }
        return nil
    }

    // Canon event data: [u32 size][u32 type][payload] records; 0xC189 = property changed [u32 prop][u32 value].
    static func parseEvents(_ d: [UInt8], into props: inout [UInt32: UInt32]) {
        var pos = 0
        while pos + 8 <= d.count {
            let size = Int(rd32(d, pos)), type = rd32(d, pos + 4)
            if size < 8 || pos + size > d.count || type == 0 { break }
            if type == 0xC189 && size >= 16 { props[rd32(d, pos + 8)] = rd32(d, pos + 12) }
            pos += size
        }
    }

    struct DeviceInfo {
        var manufacturer = "", model = "", version = ""
        var operations: [UInt16] = []
    }

    static func parseDeviceInfo(_ d: [UInt8]) -> DeviceInfo? {
        var r = Reader(d: d)
        var info = DeviceInfo()
        guard r.skip(8), r.str() != nil, r.skip(2), let ops = r.u16Array() else { return nil }
        info.operations = ops
        for _ in 0..<4 { guard r.u16Array() != nil else { return nil } }
        guard let m = r.str(), let model = r.str(), let v = r.str() else { return nil }
        info.manufacturer = m; info.model = model; info.version = v
        return info
    }

    struct Reader {
        let d: [UInt8]
        var pos = 0
        mutating func skip(_ n: Int) -> Bool { pos += n; return pos <= d.count }
        mutating func u16Array() -> [UInt16]? {
            guard pos + 4 <= d.count else { return nil }
            let n = Int(PTP.rd32(d, pos)); pos += 4
            guard pos + n * 2 <= d.count else { return nil }
            var out: [UInt16] = []
            out.reserveCapacity(n)
            for i in 0..<n { out.append(PTP.rd16(d, pos + i * 2)) }
            pos += n * 2
            return out
        }
        mutating func str() -> String? {
            guard pos < d.count else { return nil }
            let n = Int(d[pos]); pos += 1
            guard pos + n * 2 <= d.count else { return nil }
            var units: [UInt16] = []
            for i in 0..<n { let c = PTP.rd16(d, pos + i * 2); if c == 0 { break }; units.append(c) }
            pos += n * 2
            return String(decoding: units, as: UTF16.self)
        }
    }

    static func rd16(_ d: [UInt8], _ p: Int) -> UInt16 { UInt16(d[p]) | UInt16(d[p + 1]) << 8 }
    static func rd32(_ d: [UInt8], _ p: Int) -> UInt32 {
        UInt32(d[p]) | UInt32(d[p + 1]) << 8 | UInt32(d[p + 2]) << 16 | UInt32(d[p + 3]) << 24
    }
    static func le32(_ v: UInt32) -> [UInt8] { [UInt8(v & 255), UInt8(v >> 8 & 255), UInt8(v >> 16 & 255), UInt8(v >> 24)] }
    static func le16(_ v: UInt16) -> [UInt8] { [UInt8(v & 255), UInt8(v >> 8)] }
}

// Canon exposure value codes (same encoding over PTP as in Canon's SDK headers).
enum CameraValues {
    static let isoChoices: [(UInt32, String)] = [(0x00, "Auto"), (0x48, "100"), (0x50, "200"), (0x58, "400"),
                                                 (0x60, "800"), (0x68, "1600"), (0x70, "3200"), (0x78, "6400")]
    static let shutterChoices: [(UInt32, String)] = [(0x60, "1/30"), (0x63, "1/40"), (0x65, "1/50"), (0x68, "1/60"),
                                                     (0x6B, "1/80"), (0x6D, "1/100"), (0x70, "1/125")]
    static let iso: [UInt32: String] = [
        0x00: "Auto", 0x48: "100", 0x4B: "125", 0x4D: "160", 0x50: "200", 0x53: "250", 0x55: "320", 0x58: "400",
        0x5B: "500", 0x5D: "640", 0x60: "800", 0x63: "1000", 0x65: "1250", 0x68: "1600", 0x6B: "2000",
        0x6D: "2500", 0x70: "3200", 0x73: "4000", 0x75: "5000", 0x78: "6400", 0x7B: "8000", 0x7D: "10000", 0x80: "12800",
    ]
    static let shutter: [UInt32: String] = [
        0x0C: "bulb", 0x48: "1/4", 0x4B: "1/5", 0x4D: "1/6", 0x50: "1/8", 0x53: "1/10", 0x55: "1/13", 0x58: "1/15",
        0x5B: "1/20", 0x5D: "1/25", 0x60: "1/30", 0x63: "1/40", 0x65: "1/50", 0x68: "1/60", 0x6B: "1/80",
        0x6D: "1/100", 0x70: "1/125", 0x73: "1/160", 0x75: "1/200", 0x78: "1/250", 0x7B: "1/320", 0x7D: "1/400",
        0x80: "1/500", 0x83: "1/640", 0x85: "1/800", 0x88: "1/1000", 0x8B: "1/1250", 0x8D: "1/1600",
        0x90: "1/2000", 0x93: "1/2500", 0x95: "1/3200", 0x98: "1/4000",
    ]
    static let aperture: [UInt32: String] = [
        0x18: "f/2.0", 0x1B: "f/2.2", 0x1C: "f/2.5", 0x1D: "f/2.5", 0x20: "f/2.8", 0x23: "f/3.2", 0x24: "f/3.5",
        0x25: "f/3.5", 0x28: "f/4.0", 0x2B: "f/4.5", 0x2C: "f/4.5", 0x2D: "f/5.0", 0x30: "f/5.6", 0x33: "f/6.3",
        0x34: "f/6.7", 0x35: "f/7.1", 0x38: "f/8.0", 0x3B: "f/9.0", 0x3C: "f/9.5", 0x3D: "f/10", 0x40: "f/11",
        0x43: "f/13", 0x45: "f/14", 0x48: "f/16", 0x4B: "f/18", 0x4D: "f/20", 0x50: "f/22",
    ]
    static let modes: [UInt32: String] = [
        0: "P", 1: "Tv", 2: "Av", 3: "M", 4: "Bulb", 5: "A-DEP", 9: "Auto", 10: "Night portrait", 11: "Sports",
        12: "Portrait", 13: "Landscape", 14: "Close-up", 15: "Flash off", 19: "Creative Auto", 20: "Movie",
        22: "A+ (Scene Intelligent Auto)",
    ]

    // Picker entries: the presets plus the camera's current value when it is set to something else
    // (e.g. ISO 1000 from the camera body), so the picker shows it instead of an invalid selection.
    static func choices(_ presets: [(UInt32, String)], _ table: [UInt32: String], _ current: UInt32?) -> [(UInt32, String)] {
        guard let c = current, !presets.contains(where: { $0.0 == c }) else { return presets }
        return (presets + [(c, name(table, c))]).sorted { $0.0 < $1.0 }
    }

    static func name(_ table: [UInt32: String], _ v: UInt32?) -> String {
        guard let v = v else { return "?" }
        return table[v] ?? String(format: "0x%X", v)
    }

    // ISO can be set in P/Tv/Av/M (and movie mode with manual exposure); shutter only in Tv/M/movie.
    static func isoSettable(_ mode: UInt32?) -> Bool { guard let m = mode else { return false }; return m <= 3 || m == 20 }
    static func shutterSettable(_ mode: UInt32?) -> Bool { guard let m = mode else { return false }; return m == 1 || m == 3 || m == 20 }
}
