#!/usr/bin/env swift

import AppKit
import Foundation

struct GeometryManifest: Decodable {
    let contentWidth: Int
    let contentHeight: Int
    let panelWidth: Int
    let panelHeight: Int
    let surfaceInset: Int
    let implementationScaleX: Double
    let implementationScaleY: Double
    let sourceNormalization: String
}

struct PixelImage {
    let path: String
    let bitmap: NSBitmapImageRep

    init(_ path: String) throws {
        self.path = path
        let data = try Data(contentsOf: URL(fileURLWithPath: path))
        guard let bitmap = NSBitmapImageRep(data: data) else {
            throw VerificationError("cannot decode image: \(path)")
        }
        self.bitmap = bitmap
    }

    var width: Int { bitmap.pixelsWide }
    var height: Int { bitmap.pixelsHigh }
}

struct VerificationError: Error, CustomStringConvertible {
    let description: String
    init(_ description: String) { self.description = description }
}

func require(_ condition: @autoclosure () -> Bool, _ message: String) throws {
    guard condition() else { throw VerificationError(message) }
}

func requireSize(_ image: PixelImage, _ width: Int, _ height: Int) throws {
    try require(
        image.width == width && image.height == height,
        "\(image.path) is \(image.width)x\(image.height), expected \(width)x\(height)"
    )
}

func meanSampleDifference(
    _ left: PixelImage,
    origin leftOrigin: (x: Int, y: Int),
    _ right: PixelImage,
    origin rightOrigin: (x: Int, y: Int),
    size: (width: Int, height: Int),
    inset: Int = 0
) throws -> Double {
    var total = 0.0
    var samples = 0
    let xStride = max(1, (size.width - (inset * 2)) / 47)
    let yStride = max(1, (size.height - (inset * 2)) / 61)

    for y in stride(from: inset, to: size.height - inset, by: yStride) {
        for x in stride(from: inset, to: size.width - inset, by: xStride) {
            guard
                let leftColor = left.bitmap.colorAt(
                    x: leftOrigin.x + x,
                    y: leftOrigin.y + y
                )?.usingColorSpace(.sRGB),
                let rightColor = right.bitmap.colorAt(
                    x: rightOrigin.x + x,
                    y: rightOrigin.y + y
                )?.usingColorSpace(.sRGB)
            else {
                throw VerificationError("cannot sample compared pixels")
            }
            total += abs(leftColor.redComponent - rightColor.redComponent)
            total += abs(leftColor.greenComponent - rightColor.greenComponent)
            total += abs(leftColor.blueComponent - rightColor.blueComponent)
            samples += 3
        }
    }

    return total / Double(samples)
}

let arguments = CommandLine.arguments
guard arguments.count == 2 else {
    fputs("usage: verify-pass-6.swift <pass-6-output-directory>\n", stderr)
    exit(2)
}

let outputDirectory = URL(fileURLWithPath: arguments[1])
func artifact(_ name: String) -> String {
    outputDirectory.appendingPathComponent(name).path
}

do {
    let manifestData = try Data(contentsOf: URL(fileURLWithPath: artifact("geometry.json")))
    let manifest = try JSONDecoder().decode(GeometryManifest.self, from: manifestData)
    try require(manifest.contentWidth == 840, "content width must be 840 px")
    try require(manifest.contentHeight == 1240, "content height must be 1240 px")
    try require(manifest.panelWidth == 888, "panel width must be 888 px")
    try require(manifest.panelHeight == 1288, "panel height must be 1288 px")
    try require(manifest.surfaceInset == 24, "surface inset must be 24 px")
    try require(manifest.implementationScaleX == 1, "implementation x scale must be 1")
    try require(manifest.implementationScaleY == 1, "implementation y scale must be 1")
    try require(
        manifest.sourceNormalization == "surface-crop-uniform-aspect-fill-center-crop",
        "source must use measured surface crop plus uniform aspect-fill center crop"
    )

    let source = try PixelImage(artifact("source-normalized.png"))
    let implementation = try PixelImage(artifact("implementation-raw.png"))
    let sourcePanel = try PixelImage(artifact("source-panel.png"))
    let implementationSurface = try PixelImage(artifact("implementation-surface.png"))
    let contentComparison = try PixelImage(artifact("comparison-content.png"))
    let surfaceComparison = try PixelImage(artifact("comparison-surface.png"))

    try requireSize(source, 840, 1240)
    try requireSize(implementation, 840, 1240)
    try requireSize(sourcePanel, 888, 1288)
    try requireSize(implementationSurface, 888, 1288)
    try requireSize(contentComparison, 1680, 1240)
    try requireSize(surfaceComparison, 1776, 1288)

    let implementationEmbeddingDifference = try meanSampleDifference(
        implementation,
        origin: (0, 0),
        implementationSurface,
        origin: (24, 24),
        size: (840, 1240),
        inset: 40
    )
    try require(
        implementationEmbeddingDifference < 0.002,
        "surface content is not a 1:1 implementation embedding; mean RGB difference \(implementationEmbeddingDifference)"
    )

    let leftContentDifference = try meanSampleDifference(
        source,
        origin: (0, 0),
        contentComparison,
        origin: (0, 0),
        size: (840, 1240)
    )
    let rightContentDifference = try meanSampleDifference(
        implementation,
        origin: (0, 0),
        contentComparison,
        origin: (840, 0),
        size: (840, 1240)
    )
    try require(leftContentDifference < 0.002, "source content comparison is not 1:1")
    try require(rightContentDifference < 0.002, "implementation content comparison is not 1:1")

    let leftSurfaceDifference = try meanSampleDifference(
        sourcePanel,
        origin: (0, 0),
        surfaceComparison,
        origin: (0, 0),
        size: (888, 1288)
    )
    let rightSurfaceDifference = try meanSampleDifference(
        implementationSurface,
        origin: (0, 0),
        surfaceComparison,
        origin: (888, 0),
        size: (888, 1288)
    )
    try require(leftSurfaceDifference < 0.002, "source surface comparison is not 1:1")
    try require(rightSurfaceDifference < 0.002, "implementation surface comparison is not 1:1")

    print("PASS: Pass 6 uses exact 840x1240 content regions at 1:1 pixels")
    print("implementation embedding mean RGB difference: \(implementationEmbeddingDifference)")
} catch {
    fputs("FAIL: \(error)\n", stderr)
    exit(1)
}
