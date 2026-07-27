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
        .library(name: "EMKEAudioBridge", targets: ["EMKEAudioBridge"]),
        .library(name: "EMKEAudioHAL", targets: ["EMKEAudioHAL"]),
        .library(name: "EMKEAudioEngine", targets: ["EMKEAudioEngine"]),
        .library(name: "EMKECoordinator", targets: ["EMKECoordinator"]),
        .executable(name: "EMKEMenuBarApp", targets: ["EMKEMenuBarApp"]),
    ],
    dependencies: [
        .package(
            url: "https://github.com/sparkle-project/Sparkle",
            exact: "2.9.2"
        ),
        .package(url: "https://github.com/swiftlang/swift-testing.git", exact: "6.2.3"),
    ],
    targets: [
        .target(name: "EMKECore"),
        .target(name: "EMKERealtime", dependencies: ["EMKECore"]),
        .target(name: "EMKERouting", dependencies: ["EMKECore"]),
        .target(name: "EMKESecurity"),
        .target(
            name: "EMKEAudioBridge",
            publicHeadersPath: "include",
            cSettings: [.unsafeFlags(["-std=c11"])]
        ),
        .target(
            name: "EMKEAudioEngine",
            dependencies: ["EMKEAudioHAL", "EMKERouting"],
            linkerSettings: [.linkedFramework("CoreAudio")]
        ),
        .target(
            name: "EMKEAudioHAL",
            dependencies: ["EMKEAudioBridge"],
            publicHeadersPath: "include",
            cSettings: [.unsafeFlags(["-std=c11"])],
            linkerSettings: [
                .linkedFramework("CoreAudio"),
                .linkedFramework("AudioUnit"),
            ]
        ),
        .target(
            name: "EMKECoordinator",
            dependencies: [
                "EMKEAudioEngine",
                "EMKECore",
                "EMKERealtime",
                "EMKERouting",
            ]
        ),
        .executableTarget(
            name: "EMKEMenuBarApp",
            dependencies: [
                "EMKEAudioEngine",
                "EMKECoordinator",
                "EMKECore",
                "EMKESecurity",
                .product(name: "Sparkle", package: "Sparkle"),
            ],
            resources: [.process("Resources")]
        ),
        .executableTarget(
            name: "EMKELanguageBaselineTool",
            dependencies: [
                "EMKECore",
                "EMKERouting",
            ],
            path: "Tools/EMKELanguageBaselineTool"
        ),
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
        .testTarget(
            name: "EMKEAudioBridgeTests",
            dependencies: [
                "EMKEAudioBridge",
                .product(name: "Testing", package: "swift-testing"),
            ]
        ),
        .testTarget(
            name: "EMKEAudioEngineTests",
            dependencies: [
                "EMKEAudioHAL",
                "EMKEAudioEngine",
                "EMKECoordinator",
                "EMKERouting",
                "EMKEMenuBarApp",
                .product(name: "Testing", package: "swift-testing"),
            ]
        ),
        .testTarget(
            name: "EMKECoordinatorTests",
            dependencies: [
                "EMKECoordinator",
                .product(name: "Testing", package: "swift-testing"),
            ]
        ),
        .testTarget(
            name: "EMKEContractTests",
            dependencies: [
                .product(name: "Testing", package: "swift-testing"),
            ]
        ),
    ]
)
