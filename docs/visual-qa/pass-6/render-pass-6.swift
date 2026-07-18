#!/usr/bin/env swift

import AppKit
import Foundation

struct RenderError: Error, CustomStringConvertible {
    let description: String
    init(_ description: String) { self.description = description }
}

struct PixelImage {
    let path: String
    let bitmap: NSBitmapImageRep
    let image: NSImage

    init(path: String) throws {
        self.path = path
        let data = try Data(contentsOf: URL(fileURLWithPath: path))
        guard
            let bitmap = NSBitmapImageRep(data: data),
            let cgImage = bitmap.cgImage
        else {
            throw RenderError("cannot decode image: \(path)")
        }
        self.bitmap = bitmap
        image = NSImage(
            cgImage: cgImage,
            size: NSSize(width: bitmap.pixelsWide, height: bitmap.pixelsHigh)
        )
    }

    var width: Int { bitmap.pixelsWide }
    var height: Int { bitmap.pixelsHigh }
}

func renderBitmap(
    width: Int,
    height: Int,
    draw: (NSGraphicsContext, NSRect) throws -> Void
) throws -> NSBitmapImageRep {
    guard let bitmap = NSBitmapImageRep(
        bitmapDataPlanes: nil,
        pixelsWide: width,
        pixelsHigh: height,
        bitsPerSample: 8,
        samplesPerPixel: 4,
        hasAlpha: true,
        isPlanar: false,
        colorSpaceName: .deviceRGB,
        bytesPerRow: 0,
        bitsPerPixel: 0
    ), let context = NSGraphicsContext(bitmapImageRep: bitmap) else {
        throw RenderError("cannot allocate \(width)x\(height) bitmap")
    }

    bitmap.size = NSSize(width: width, height: height)
    let canvas = NSRect(x: 0, y: 0, width: width, height: height)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = context
    try draw(context, canvas)
    context.flushGraphics()
    NSGraphicsContext.restoreGraphicsState()
    return bitmap
}

func writePNG(_ bitmap: NSBitmapImageRep, to path: String) throws {
    guard let data = bitmap.representation(using: .png, properties: [:]) else {
        throw RenderError("cannot encode PNG: \(path)")
    }
    try data.write(to: URL(fileURLWithPath: path), options: .atomic)
}

func drawOneToOne(_ image: NSImage, in rect: NSRect) {
    image.draw(
        in: rect,
        from: NSRect(origin: .zero, size: image.size),
        operation: .copy,
        fraction: 1,
        respectFlipped: false,
        hints: [.interpolation: NSImageInterpolation.none]
    )
}

let arguments = CommandLine.arguments
guard arguments.count == 4 else {
    fputs(
        "usage: render-pass-6.swift <approved-source.png> <840x1240-implementation.png> <output-directory>\n",
        stderr
    )
    exit(2)
}

let sourcePath = arguments[1]
let implementationPath = arguments[2]
let outputDirectory = URL(fileURLWithPath: arguments[3])
let contentWidth = 840
let contentHeight = 1240
let surfaceInset = 24
let panelWidth = contentWidth + (surfaceInset * 2)
let panelHeight = contentHeight + (surfaceInset * 2)

do {
    let source = try PixelImage(path: sourcePath)
    let implementation = try PixelImage(path: implementationPath)
    guard implementation.width == contentWidth, implementation.height == contentHeight else {
        throw RenderError(
            "implementation is \(implementation.width)x\(implementation.height); expected exact 840x1240 pixels"
        )
    }

    try FileManager.default.createDirectory(
        at: outputDirectory,
        withIntermediateDirectories: true
    )

    func output(_ name: String) -> String {
        outputDirectory.appendingPathComponent(name).path
    }

    // Approved reference surface bounds, measured once in its original
    // 1033x1523 pixel image. Coordinates below use a top-left origin.
    let sourceCropLeft = 24
    let sourceCropTop = 22
    let sourceCropWidth = 984
    let sourceCropHeight = 1471
    guard
        source.width == 1033,
        source.height == 1523,
        sourceCropLeft + sourceCropWidth <= source.width,
        sourceCropTop + sourceCropHeight <= source.height
    else {
        throw RenderError(
            "approved source dimensions changed; remeasure its surface crop before rendering"
        )
    }
    let sourceCropRect = NSRect(
        x: sourceCropLeft,
        y: source.height - sourceCropTop - sourceCropHeight,
        width: sourceCropWidth,
        height: sourceCropHeight
    )
    let sourceScale = max(
        Double(contentWidth) / Double(sourceCropWidth),
        Double(contentHeight) / Double(sourceCropHeight)
    )
    let sourceDrawWidth = Double(sourceCropWidth) * sourceScale
    let sourceDrawHeight = Double(sourceCropHeight) * sourceScale
    let sourceDrawRect = NSRect(
        x: (Double(contentWidth) - sourceDrawWidth) / 2,
        y: (Double(contentHeight) - sourceDrawHeight) / 2,
        width: sourceDrawWidth,
        height: sourceDrawHeight
    )

    let normalizedSource = try renderBitmap(
        width: contentWidth,
        height: contentHeight
    ) { context, canvas in
        NSColor.white.setFill()
        canvas.fill()
        context.imageInterpolation = .high
        source.image.draw(
            in: sourceDrawRect,
            from: sourceCropRect,
            operation: .copy,
            fraction: 1,
            respectFlipped: false,
            hints: [.interpolation: NSImageInterpolation.high]
        )
    }
    try writePNG(normalizedSource, to: output("source-normalized.png"))

    let implementationOutput = output("implementation-raw.png")
    if FileManager.default.fileExists(atPath: implementationOutput) {
        try FileManager.default.removeItem(atPath: implementationOutput)
    }
    try FileManager.default.copyItem(
        at: URL(fileURLWithPath: implementationPath),
        to: URL(fileURLWithPath: implementationOutput)
    )

    let normalizedSourceImage = try PixelImage(path: output("source-normalized.png"))
    let implementationImage = try PixelImage(path: implementationOutput)
    let contentRect = NSRect(
        x: surfaceInset,
        y: surfaceInset,
        width: contentWidth,
        height: contentHeight
    )

    let sourcePanel = try renderBitmap(width: panelWidth, height: panelHeight) { _, canvas in
        NSColor(calibratedWhite: 0.985, alpha: 1).setFill()
        canvas.fill()
        drawOneToOne(normalizedSourceImage.image, in: contentRect)
    }
    try writePNG(sourcePanel, to: output("source-panel.png"))

    let implementationSurface = try renderBitmap(
        width: panelWidth,
        height: panelHeight
    ) { context, canvas in
        NSColor(calibratedWhite: 0.985, alpha: 1).setFill()
        canvas.fill()
        let path = NSBezierPath(
            roundedRect: contentRect,
            xRadius: 32,
            yRadius: 32
        )

        context.cgContext.saveGState()
        context.cgContext.setShadow(
            offset: CGSize(width: 0, height: -5),
            blur: 22,
            color: NSColor(calibratedWhite: 0, alpha: 0.18).cgColor
        )
        NSColor.white.setFill()
        path.fill()
        context.cgContext.restoreGState()

        context.cgContext.saveGState()
        path.addClip()
        drawOneToOne(implementationImage.image, in: contentRect)
        context.cgContext.restoreGState()

        NSColor.separatorColor.withAlphaComponent(0.7).setStroke()
        path.lineWidth = 1
        path.stroke()
    }
    try writePNG(implementationSurface, to: output("implementation-surface.png"))

    let contentComparison = try renderBitmap(
        width: contentWidth * 2,
        height: contentHeight
    ) { _, _ in
        drawOneToOne(
            normalizedSourceImage.image,
            in: NSRect(x: 0, y: 0, width: contentWidth, height: contentHeight)
        )
        drawOneToOne(
            implementationImage.image,
            in: NSRect(
                x: contentWidth,
                y: 0,
                width: contentWidth,
                height: contentHeight
            )
        )
    }
    try writePNG(contentComparison, to: output("comparison-content.png"))

    let sourcePanelImage = try PixelImage(path: output("source-panel.png"))
    let implementationSurfaceImage = try PixelImage(
        path: output("implementation-surface.png")
    )
    let surfaceComparison = try renderBitmap(
        width: panelWidth * 2,
        height: panelHeight
    ) { _, _ in
        drawOneToOne(
            sourcePanelImage.image,
            in: NSRect(x: 0, y: 0, width: panelWidth, height: panelHeight)
        )
        drawOneToOne(
            implementationSurfaceImage.image,
            in: NSRect(
                x: panelWidth,
                y: 0,
                width: panelWidth,
                height: panelHeight
            )
        )
    }
    try writePNG(surfaceComparison, to: output("comparison-surface.png"))

    let geometry: [String: Any] = [
        "contentWidth": contentWidth,
        "contentHeight": contentHeight,
        "panelWidth": panelWidth,
        "panelHeight": panelHeight,
        "surfaceInset": surfaceInset,
        "implementationScaleX": 1.0,
        "implementationScaleY": 1.0,
        "sourceOriginalWidth": source.width,
        "sourceOriginalHeight": source.height,
        "sourceSurfaceCrop": [
            sourceCropLeft,
            sourceCropTop,
            sourceCropWidth,
            sourceCropHeight,
        ],
        "sourceUniformScale": sourceScale,
        "sourceDrawRect": [
            sourceDrawRect.origin.x,
            sourceDrawRect.origin.y,
            sourceDrawRect.width,
            sourceDrawRect.height,
        ],
        "sourceNormalization": "surface-crop-uniform-aspect-fill-center-crop",
    ]
    let geometryData = try JSONSerialization.data(
        withJSONObject: geometry,
        options: [.prettyPrinted, .sortedKeys]
    )
    try geometryData.write(
        to: URL(fileURLWithPath: output("geometry.json")),
        options: .atomic
    )

    print("rendered Pass 6 with exact 840x1240 implementation content at 1:1 pixels")
    print("source: \(source.width)x\(source.height), uniform scale \(sourceScale)")
    print("surface panel: \(panelWidth)x\(panelHeight)")
} catch {
    fputs("FAIL: \(error)\n", stderr)
    exit(1)
}
