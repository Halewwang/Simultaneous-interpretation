#!/usr/bin/env swift
import AppKit
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

guard CommandLine.arguments.count == 3 else {
    FileHandle.standardError.write(
        Data("usage: build-menu-bar-icon.swift INPUT OUTPUT\n".utf8)
    )
    exit(64)
}

let inputURL = URL(fileURLWithPath: CommandLine.arguments[1])
let outputURL = URL(fileURLWithPath: CommandLine.arguments[2])
guard
    let source = CGImageSourceCreateWithURL(inputURL as CFURL, nil),
    let sourceImage = CGImageSourceCreateImageAtIndex(source, 0, nil)
else {
    fatalError("unable to decode approved app icon")
}

let sourceSide = min(sourceImage.width, sourceImage.height)
let cropSide = Int((Double(sourceSide) * 0.56).rounded(.down))
let cropRect = CGRect(
    x: (sourceImage.width - cropSide) / 2,
    y: (sourceImage.height - cropSide) / 2,
    width: cropSide,
    height: cropSide
)
guard let cropped = sourceImage.cropping(to: cropRect) else {
    fatalError("unable to crop approved logo mark")
}

let outputSide = 36
let colorSpace = CGColorSpaceCreateDeviceRGB()
var pixels = [UInt8](repeating: 0, count: outputSide * outputSide * 4)
guard let context = CGContext(
    data: &pixels,
    width: outputSide,
    height: outputSide,
    bitsPerComponent: 8,
    bytesPerRow: outputSide * 4,
    space: colorSpace,
    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
) else {
    fatalError("unable to create menu-bar icon context")
}
context.interpolationQuality = .high
context.draw(
    cropped,
    in: CGRect(x: 0, y: 0, width: outputSide, height: outputSide)
)

for index in stride(from: 0, to: pixels.count, by: 4) {
    let red = Double(pixels[index])
    let green = Double(pixels[index + 1])
    let blue = Double(pixels[index + 2])
    let luminance = (0.2126 * red) + (0.7152 * green) + (0.0722 * blue)
    let normalized = max(0, min(1, (luminance - 38) / 217))
    pixels[index] = 0
    pixels[index + 1] = 0
    pixels[index + 2] = 0
    pixels[index + 3] = UInt8((normalized * 255).rounded())
}

guard
    let provider = CGDataProvider(data: Data(pixels) as CFData),
    let outputImage = CGImage(
        width: outputSide,
        height: outputSide,
        bitsPerComponent: 8,
        bitsPerPixel: 32,
        bytesPerRow: outputSide * 4,
        space: colorSpace,
        bitmapInfo: CGBitmapInfo(
            rawValue: CGImageAlphaInfo.premultipliedLast.rawValue
        ),
        provider: provider,
        decode: nil,
        shouldInterpolate: true,
        intent: .defaultIntent
    ),
    let destination = CGImageDestinationCreateWithURL(
        outputURL as CFURL,
        UTType.png.identifier as CFString,
        1,
        nil
    )
else {
    fatalError("unable to encode menu-bar icon")
}

CGImageDestinationAddImage(destination, outputImage, nil)
guard CGImageDestinationFinalize(destination) else {
    fatalError("unable to write menu-bar icon")
}
