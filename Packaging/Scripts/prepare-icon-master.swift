#!/usr/bin/env swift
import AppKit
import CoreGraphics
import Foundation
import ImageIO

guard CommandLine.arguments.count == 3 else {
    fputs("usage: prepare-icon-master.swift INPUT OUTPUT\n", stderr)
    exit(64)
}
let inputURL = URL(fileURLWithPath: CommandLine.arguments[1])
let outputURL = URL(fileURLWithPath: CommandLine.arguments[2])
guard let source = CGImageSourceCreateWithURL(inputURL as CFURL, nil),
      let input = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
    fputs("cannot decode icon master\n", stderr)
    exit(65)
}

let size = 1024
var pixels = [UInt8](repeating: 0, count: size * size * 4)
let colorSpace = CGColorSpaceCreateDeviceRGB()
guard let context = CGContext(
    data: &pixels,
    width: size,
    height: size,
    bitsPerComponent: 8,
    bytesPerRow: size * 4,
    space: colorSpace,
    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        | CGBitmapInfo.byteOrder32Big.rawValue
) else { exit(66) }
context.interpolationQuality = .high
context.draw(input, in: CGRect(x: 0, y: 0, width: size, height: size))

var visited = [Bool](repeating: false, count: size * size)
var queue = [Int]()
queue.reserveCapacity(size * 4)
for x in 0..<size { queue.append(x); queue.append((size - 1) * size + x) }
for y in 0..<size { queue.append(y * size); queue.append(y * size + size - 1) }
var cursor = 0
func isBackground(_ index: Int) -> Bool {
    let offset = index * 4
    return pixels[offset] >= 238
        && pixels[offset + 1] >= 238
        && pixels[offset + 2] >= 238
}
while cursor < queue.count {
    let index = queue[cursor]
    cursor += 1
    guard !visited[index], isBackground(index) else { continue }
    visited[index] = true
    let offset = index * 4
    pixels[offset] = 0
    pixels[offset + 1] = 0
    pixels[offset + 2] = 0
    pixels[offset + 3] = 0
    let x = index % size
    let y = index / size
    if x > 0 { queue.append(index - 1) }
    if x + 1 < size { queue.append(index + 1) }
    if y > 0 { queue.append(index - size) }
    if y + 1 < size { queue.append(index + size) }
}

guard let outputImage = context.makeImage(),
      let destination = CGImageDestinationCreateWithURL(
        outputURL as CFURL,
        "public.png" as CFString,
        1,
        nil
      ) else { exit(67) }
CGImageDestinationAddImage(destination, outputImage, nil)
guard CGImageDestinationFinalize(destination) else { exit(68) }
