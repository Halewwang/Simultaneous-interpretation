// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "EMKETranslation",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "EMKECore", targets: ["EMKECore"]),
        .library(name: "EMKERealtime", targets: ["EMKERealtime"]),
    ],
    dependencies: [
        .package(url: "https://github.com/swiftlang/swift-testing.git", exact: "6.2.3"),
    ],
    targets: [
        .target(name: "EMKECore"),
        .target(name: "EMKERealtime", dependencies: ["EMKECore"]),
        .testTarget(
            name: "EMKECoreTests",
            dependencies: [
                "EMKECore",
                .product(name: "Testing", package: "swift-testing"),
            ]
        ),
        .testTarget(
            name: "EMKERealtimeTests",
            dependencies: [
                "EMKECore",
                "EMKERealtime",
                .product(name: "Testing", package: "swift-testing"),
            ]
        ),
    ]
)
