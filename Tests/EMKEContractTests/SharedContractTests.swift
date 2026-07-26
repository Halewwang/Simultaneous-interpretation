import Foundation
import Testing

private struct FixtureManifest: Decodable {
    let contractVersion: Int
    let fixtures: [String]
}

private let repositoryRoot = URL(fileURLWithPath: #filePath)
    .deletingLastPathComponent()
    .deletingLastPathComponent()
    .deletingLastPathComponent()

@Test("Every shared fixture is readable, versioned, categorized, and uniquely identified")
func everySharedFixtureIsReadableAndVersioned() throws {
    let manifestURL = repositoryRoot
        .appendingPathComponent("Shared/TestVectors/fixture-manifest.json")
    let manifest = try JSONDecoder().decode(
        FixtureManifest.self,
        from: Data(contentsOf: manifestURL)
    )

    #expect(manifest.contractVersion == 1)
    #expect(manifest.fixtures.count == 8)

    var fixtureIDs = Set<String>()
    for relativePath in manifest.fixtures {
        let fixtureURL = repositoryRoot
            .appendingPathComponent("Shared/TestVectors")
            .appendingPathComponent(relativePath)
        let object = try JSONSerialization.jsonObject(
            with: Data(contentsOf: fixtureURL)
        )
        let dictionary = try #require(object as? [String: Any])
        #expect(dictionary["contractVersion"] as? Int == 1)
        let fixtureID = try #require(dictionary["fixtureId"] as? String)
        #expect(!fixtureID.isEmpty)
        #expect(fixtureIDs.insert(fixtureID).inserted)
        let category = try #require(dictionary["category"] as? String)
        #expect(!category.isEmpty)
    }
}
