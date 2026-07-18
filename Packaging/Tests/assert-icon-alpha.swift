#!/usr/bin/env swift
import CoreGraphics
import Foundation
import ImageIO

guard CommandLine.arguments.count == 2 else { exit(64) }
let url = URL(fileURLWithPath: CommandLine.arguments[1]) as CFURL
guard let source = CGImageSourceCreateWithURL(url, nil),
      let image = CGImageSourceCreateImageAtIndex(source, 0, nil),
      image.width == 1024,
      image.height == 1024,
      let data = image.dataProvider?.data,
      let bytes = CFDataGetBytePtr(data) else { exit(65) }

let bytesPerPixel = image.bitsPerPixel / 8
guard bytesPerPixel == 4 else { exit(66) }
func alpha(x: Int, y: Int) -> UInt8 {
    bytes[(y * image.bytesPerRow) + (x * bytesPerPixel) + 3]
}
guard alpha(x: 0, y: 0) == 0 else { exit(67) }
guard alpha(x: 512, y: 512) == 255 else { exit(68) }
print("PASS: icon alpha contract")
