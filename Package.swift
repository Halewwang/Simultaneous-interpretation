// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "EMKETranslation",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "EMKECore", targets: ["EMKECore"]),
    ],
    dependencies: [
        .package(url: "https://github.com/swiftlang/swift-testing.git", exact: "6.2.3"),
    ],
    targets: [
        .target(name: "EMKECore"),
        .testTarget(
            name: "EMKECoreTests",
            dependencies: [
                "EMKECore",
                .product(name: "Testing", package: "swift-testing"),
            ]
        ),
    ]
)
