import AppKit
import CryptoKit
import Foundation

// SnapTranslatePasteboard
// 与桌面客户端交换 JSON 的剪贴板辅助程序。
// 用法：
//   SnapTranslatePasteboard [--images-dir <目录>]   读取剪贴板并输出 JSON
//   SnapTranslatePasteboard --change-count          只输出 changeCount
//   SnapTranslatePasteboard --copy <json>           把 JSON 描述的内容写回剪贴板
// 读取输出字段：changeCount、kind（text/image/files/none）、text、path、hash。

func fail(_ message: String, code: Int32 = 1) -> Never {
    if let data = (message + "\n").data(using: .utf8) {
        FileHandle.standardError.write(data)
    }
    exit(code)
}

func sha256Hex(_ data: Data) -> String {
    SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
}

func sha256Hex(_ text: String) -> String {
    sha256Hex(Data(text.utf8))
}

// 与 C# 侧 ClipboardHash 保持一致：SHA-256(kind + NUL + payload)。
func contentHash(kind: String, payload: String) -> String {
    sha256Hex(Data((kind + "\u{0}" + payload).utf8))
}

func usage() -> Never {
    fail("Usage: SnapTranslatePasteboard [--images-dir <directory>] | --change-count | --copy <json>", code: 64)
}

var imagesDirectory: String?
var changeCountOnly = false
var copyPayload: String?

var index = 1
let arguments = CommandLine.arguments
while index < arguments.count {
    let argument = arguments[index]
    switch argument {
    case "--change-count":
        changeCountOnly = true
        index += 1
    case "--images-dir":
        guard index + 1 < arguments.count else { usage() }
        imagesDirectory = arguments[index + 1]
        index += 2
    case "--copy":
        guard index + 1 < arguments.count else { usage() }
        copyPayload = arguments[index + 1]
        index += 2
    case "--help", "-h":
        usage()
    default:
        usage()
    }
}

let pasteboard = NSPasteboard.general

if changeCountOnly {
    print(pasteboard.changeCount)
    exit(0)
}

func writeJSON(_ object: [String: Any]) -> Never {
    do {
        let data = try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])
        FileHandle.standardOutput.write(data)
        exit(0)
    } catch {
        fail("无法生成 JSON：\(error.localizedDescription)")
    }
}

func resolveImagesDirectory() -> String {
    if let directory = imagesDirectory, !directory.isEmpty {
        return directory
    }
    return (NSTemporaryDirectory() as NSString).appendingPathComponent("SnapTranslate/clipboard-images")
}

func copyToPasteboard(_ json: String) -> Never {
    guard let data = json.data(using: .utf8),
          let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
        fail("无效的 --copy JSON。", code: 64)
    }

    let kind = (object["kind"] as? String) ?? "text"
    _ = pasteboard.clearContents()
    switch kind {
    case "files":
        var paths: [String] = []
        if let files = object["files"] as? [String] {
            paths = files.filter { !$0.isEmpty }
        }
        if paths.isEmpty, let text = object["text"] as? String {
            paths = text.split(separator: "\n").map(String.init).filter { !$0.isEmpty }
        }
        if paths.isEmpty, let path = object["path"] as? String, !path.isEmpty {
            paths = [path]
        }
        guard !paths.isEmpty else { fail("要复制的文件列表为空。") }
        let urls = paths.map { URL(fileURLWithPath: $0) as NSURL }
        guard pasteboard.writeObjects(urls) else { fail("写入文件剪贴板失败。") }
    case "image":
        guard let path = object["path"] as? String, !path.isEmpty else { fail("要复制的图片缺少路径。") }
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: path)) else { fail("无法读取图片文件：\(path)") }
        guard let image = NSImage(data: data) else { fail("无法解析图片文件：\(path)") }
        _ = pasteboard.setData(data, forType: .png)
        if let tiff = image.tiffRepresentation {
            _ = pasteboard.setData(tiff, forType: .tiff)
        }
    case "text":
        guard let text = object["text"] as? String, !text.isEmpty else { fail("要复制的文本为空。") }
        guard pasteboard.setString(text, forType: .string) else { fail("写入文本剪贴板失败。") }
    default:
        fail("不支持的剪贴板类型：\(kind)")
    }

    writeJSON(["changeCount": pasteboard.changeCount])
}

if let payload = copyPayload {
    copyToPasteboard(payload)
}

func readFilePaths() -> [String]? {
    let objects = pasteboard.readObjects(forClasses: [NSURL.self], options: nil) ?? []
    var paths: [String] = []
    for object in objects {
        if let url = object as? URL, url.isFileURL {
            paths.append(url.path)
        } else if let url = object as? NSURL, let path = url.path, url.isFileURL {
            paths.append(path)
        }
    }
    return paths.isEmpty ? nil : paths
}

func readText() -> String? {
    guard let text = pasteboard.string(forType: .string) else { return nil }
    return text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? nil : text
}

func writeImagePNG() -> (path: String, digest: String)? {
    var pngData: Data?
    if let data = pasteboard.data(forType: .png) {
        pngData = data
    } else if let tiff = pasteboard.data(forType: .tiff), let image = NSImage(data: tiff) {
        if let tiffRepresentation = image.tiffRepresentation,
           let representation = NSBitmapImageRep(data: tiffRepresentation),
           let converted = representation.representation(using: .png, properties: [:]) {
            pngData = converted
        }
    }
    guard let png = pngData else { return nil }

    let digest = sha256Hex(png)
    let directory = resolveImagesDirectory()
    let manager = FileManager.default
    do {
        try manager.createDirectory(atPath: directory, withIntermediateDirectories: true)
    } catch {
        fail("无法创建剪贴板图片目录：\(error.localizedDescription)")
    }
    let path = (directory as NSString).appendingPathComponent(digest + ".png")
    if !manager.fileExists(atPath: path) {
        do {
            try png.write(to: URL(fileURLWithPath: path), options: .atomic)
        } catch {
            fail("无法写入剪贴板图片：\(error.localizedDescription)")
        }
    }
    return (path, digest)
}

let changeCount = pasteboard.changeCount

if let paths = readFilePaths() {
    let text = paths.joined(separator: "\n")
    writeJSON([
        "changeCount": changeCount,
        "kind": "files",
        "text": text,
        "path": paths[0],
        "files": paths,
        "hash": contentHash(kind: "Files", payload: text)
    ])
}

if let image = writeImagePNG() {
    writeJSON([
        "changeCount": changeCount,
        "kind": "image",
        "text": "",
        "path": image.path,
        "hash": contentHash(kind: "Image", payload: image.digest)
    ])
}

if let text = readText() {
    writeJSON([
        "changeCount": changeCount,
        "kind": "text",
        "text": text,
        "hash": contentHash(kind: "Text", payload: text)
    ])
}

writeJSON(["changeCount": changeCount, "kind": "none", "text": ""])
