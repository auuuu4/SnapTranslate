import AppKit
import Foundation
import Vision

func fail(_ message: String, code: Int32 = 1) -> Never {
    if let data = (message + "\n").data(using: .utf8) {
        FileHandle.standardError.write(data)
    }
    exit(code)
}

guard CommandLine.arguments.count == 2 else {
    fail("Usage: SnapTranslateVision <image-path>", code: 64)
}

let imagePath = CommandLine.arguments[1]
guard let image = NSImage(contentsOfFile: imagePath) else {
    fail("Unable to open image: \(imagePath)")
}

var proposedRect = NSRect(origin: .zero, size: image.size)
guard let cgImage = image.cgImage(forProposedRect: &proposedRect, context: nil, hints: nil) else {
    fail("Unable to decode image: \(imagePath)")
}

let request = VNRecognizeTextRequest()
request.recognitionLevel = .accurate
request.usesLanguageCorrection = true
request.recognitionLanguages = ["zh-Hans", "zh-Hant", "en-US", "ja-JP", "ko-KR"]

do {
    let handler = VNImageRequestHandler(cgImage: cgImage, options: [:])
    try handler.perform([request])
} catch {
    fail("Vision OCR failed: \(error.localizedDescription)")
}

let observations = (request.results ?? []).sorted { first, second in
    let verticalDistance = abs(first.boundingBox.midY - second.boundingBox.midY)
    if verticalDistance > 0.015 {
        return first.boundingBox.midY > second.boundingBox.midY
    }
    return first.boundingBox.minX < second.boundingBox.minX
}

let text = observations.compactMap { observation in
    observation.topCandidates(1).first?.string
}.joined(separator: "\n")

FileHandle.standardOutput.write(text.data(using: .utf8) ?? Data())
