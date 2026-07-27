import EMKELanguageBaseline
import Foundation

guard CommandLine.arguments.count == 3 else {
    FileHandle.standardError.write(
        Data("usage: EMKELanguageBaselineTool INPUT OUTPUT\n".utf8)
    )
    exit(64)
}

let inputURL = URL(fileURLWithPath: CommandLine.arguments[1])
let outputURL = URL(fileURLWithPath: CommandLine.arguments[2])

do {
    let count = try LanguageBaselineGenerator.generate(
        inputURL: inputURL,
        outputURL: outputURL
    )
    FileHandle.standardOutput.write(
        Data("\(count) macOS baselines -> \(outputURL.path)\n".utf8)
    )
} catch let error as LanguageBaselineGenerationError {
    FileHandle.standardError.write(Data("\(error)\n".utf8))
    exit(65)
} catch {
    FileHandle.standardError.write(Data("\(error)\n".utf8))
    exit(74)
}
