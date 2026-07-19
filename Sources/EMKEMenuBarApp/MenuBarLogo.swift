import AppKit
import Foundation

@MainActor
enum MenuBarLogo {
    static let image: NSImage = {
        let filename = "EMKE-MenuBarIcon"
        let resourceURL = Bundle.main.url(
            forResource: filename,
            withExtension: "png"
        ) ?? Bundle.module.url(
            forResource: filename,
            withExtension: "png"
        )

        guard
            let resourceURL,
            let image = NSImage(contentsOf: resourceURL)
        else {
            preconditionFailure("Missing approved EMKE menu-bar logo resource")
        }

        image.size = NSSize(width: 18, height: 18)
        image.isTemplate = true
        image.accessibilityDescription = "EMKE Translation"
        return image
    }()
}
