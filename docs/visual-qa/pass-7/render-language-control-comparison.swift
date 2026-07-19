#!/usr/bin/env swift
import AppKit
import Foundation

guard CommandLine.arguments.count == 4 else {
    FileHandle.standardError.write(
        Data(
            "usage: render-language-control-comparison.swift SOURCE READY OUTPUT\n".utf8
        )
    )
    exit(64)
}

let sourceURL = URL(fileURLWithPath: CommandLine.arguments[1])
let readyURL = URL(fileURLWithPath: CommandLine.arguments[2])
let outputURL = URL(fileURLWithPath: CommandLine.arguments[3])
guard
    let source = NSImage(contentsOf: sourceURL),
    let ready = NSImage(contentsOf: readyURL),
    let sourceBitmap = NSBitmapImageRep(
        data: try Data(contentsOf: sourceURL)
    ),
    let readyBitmap = NSBitmapImageRep(
        data: try Data(contentsOf: readyURL)
    ),
    sourceBitmap.pixelsWide == 840,
    sourceBitmap.pixelsHigh == 1240,
    readyBitmap.pixelsWide == 840,
    readyBitmap.pixelsHigh == 1240
else {
    fatalError("both inputs must be exact 840 x 1240 px dashboard captures")
}
source.size = NSSize(width: 840, height: 1240)
ready.size = NSSize(width: 840, height: 1240)

let crop = NSRect(x: 0, y: 585, width: 840, height: 210)
let padding: CGFloat = 24
let gap: CGFloat = 24
let outputSize = NSSize(
    width: (crop.width * 2) + (padding * 2) + gap,
    height: crop.height + (padding * 2)
)
guard let output = NSBitmapImageRep(
    bitmapDataPlanes: nil,
    pixelsWide: Int(outputSize.width),
    pixelsHigh: Int(outputSize.height),
    bitsPerSample: 8,
    samplesPerPixel: 4,
    hasAlpha: true,
    isPlanar: false,
    colorSpaceName: .deviceRGB,
    bytesPerRow: 0,
    bitsPerPixel: 0
) else {
    fatalError("unable to create comparison bitmap")
}
output.size = outputSize
guard let graphics = NSGraphicsContext(bitmapImageRep: output) else {
    fatalError("unable to create comparison graphics context")
}
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = graphics
NSColor.windowBackgroundColor.setFill()
NSRect(origin: .zero, size: outputSize).fill()

source.draw(
    in: NSRect(x: padding, y: padding, width: crop.width, height: crop.height),
    from: crop,
    operation: .copy,
    fraction: 1,
    respectFlipped: false,
    hints: [.interpolation: NSImageInterpolation.none]
)
ready.draw(
    in: NSRect(
        x: padding + crop.width + gap,
        y: padding,
        width: crop.width,
        height: crop.height
    ),
    from: crop,
    operation: .copy,
    fraction: 1,
    respectFlipped: false,
    hints: [.interpolation: NSImageInterpolation.none]
)
NSColor.separatorColor.setFill()
NSRect(
    x: padding + crop.width + (gap / 2),
    y: padding,
    width: 1,
    height: crop.height
).fill()
graphics.flushGraphics()
NSGraphicsContext.restoreGraphicsState()

guard let png = output.representation(using: .png, properties: [:]) else {
    fatalError("unable to encode comparison")
}
try png.write(to: outputURL, options: .atomic)
print("rendered 1:1 language-control comparison: source left, ready state right")
