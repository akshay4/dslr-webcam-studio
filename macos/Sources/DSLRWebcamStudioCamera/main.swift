// Camera extension entry point. macOS starts this process (as a system extension) when a video
// app looks for cameras; the DSLR Webcam Studio app installs it with "Install virtual camera".
import CoreMediaIO
import Foundation

if #available(macOS 12.3, *) {
    let providerSource = ProviderSource(clientQueue: nil)
    CMIOExtensionProvider.startService(provider: providerSource.provider)
    CFRunLoopRun()
}
