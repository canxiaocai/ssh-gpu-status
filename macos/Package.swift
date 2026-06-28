// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "GPUStatus",
    platforms: [
        .macOS(.v13) // MenuBarExtra requires macOS 13+
    ],
    targets: [
        .executableTarget(
            name: "GPUStatus",
            path: "Sources/GPUStatus"
        )
    ]
)
