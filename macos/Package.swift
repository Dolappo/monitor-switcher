// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "MonitorSwitcher",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(
            name: "MonitorSwitcher",
            path: "Sources/MonitorSwitcher",
            linkerSettings: [
                .linkedFramework("Carbon")
            ]
        )
    ]
)
