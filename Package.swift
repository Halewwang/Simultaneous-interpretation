// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "EMKETranslation",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "EMKECore", targets: ["EMKECore"]),
        .library(name: "EMKERealtime", targets: ["EMKERealtime"]),
        .library(name: "EMKERouting", targets: ["EMKERouting"]),
        .library(name: "EMKESecurity", targets: ["EMKESecurity"]),
    ],
    dependencies: [
        .package(url: "https://github.com/swiftlang/swift-testing.git", exact: "6.2.3"),
    ],
    targets: [
        .target(name: "EMKECore"),
        .target(name: "EMKERealtime", dependencies: ["EMKECore"]),
        .target(name: "EMKERouting", dependencies: ["EMKECore"]),
        .target(name: "EMKESecurity"),
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
        .testTarget(
            name: "EMKERoutingTests",
            dependencies: [
                "EMKECore",
                "EMKERouting",
                .product(name: "Testing", package: "swift-testing"),
            ]
        ),
        .testTarget(
            name: "EMKESecurityTests",
            dependencies: [
                "EMKESecurity",
                .product(name: "Testing", package: "swift-testing"),
            ]
        ),
    ]
)
