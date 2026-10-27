import Foundation
import Vision
import AppKit

guard CommandLine.arguments.count > 1 else {
    FileHandle.standardError.write("usage: vision_ocr <image_path>\n".data(using: .utf8)!)
    exit(2)
}
let path = CommandLine.arguments[1]
guard let image = NSImage(contentsOfFile: path),
      let tiff = image.tiffRepresentation,
      let bitmap = NSBitmapImageRep(data: tiff),
      let cgImage = bitmap.cgImage else {
    FileHandle.standardError.write("cannot load image\n".data(using: .utf8)!)
    exit(3)
}
let request = VNRecognizeTextRequest()
request.recognitionLevel = .accurate
request.recognitionLanguages = ["ja", "en"]
request.usesLanguageCorrection = true
let handler = VNImageRequestHandler(cgImage: cgImage, options: [:])
do {
    try handler.perform([request])
} catch {
    FileHandle.standardError.write("ocr failed\n".data(using: .utf8)!)
    exit(4)
}
var lines: [String] = []
var blocks: [[String: Any]] = []
for obs in request.results ?? [] {
    guard let top = obs.topCandidates(1).first else { continue }
    lines.append(top.string)
    let box = obs.boundingBox
    blocks.append([
        "text": top.string,
        "confidence": Double(top.confidence),
        "bbox": [box.origin.x, box.origin.y, box.size.width, box.size.height],
    ])
}
let out: [String: Any] = ["text": lines.joined(separator: "\n"), "blocks": blocks]
let data = try JSONSerialization.data(withJSONObject: out, options: [])
FileHandle.standardOutput.write(data)
