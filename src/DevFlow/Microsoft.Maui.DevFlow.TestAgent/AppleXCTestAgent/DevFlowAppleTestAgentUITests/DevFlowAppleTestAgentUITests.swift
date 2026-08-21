import CryptoKit
import Foundation
import XCTest

final class DevFlowAppleTestAgentUITests: XCTestCase {
    func testHostDrivenOperationsKeepApprovedTargetForeground() throws {
        let configuration = try Configuration(environment: ProcessInfo.processInfo.environment)
        let application = XCUIApplication(bundleIdentifier: configuration.targetBundleId)
        application.launchEnvironment["DEVFLOW_TEST_PORT"] = String(configuration.inAppAgentPort)
        application.launchEnvironment["DEVFLOW_INTEGRATION_TEST_SEED"] = configuration.seedId
        application.launch()
        XCTAssertTrue(application.wait(for: .runningForeground, timeout: 15), "The approved target did not reach foreground.")

        let client = HostClient(configuration: configuration)
        try client.hello(
            targetForegroundOwned: application.state == .runningForeground,
            targetProcessId: Int(application.processID))

        var elements: [String: XCUIElement] = [:]
        let deadline = Date().addingTimeInterval(configuration.maximumDuration)
        while Date() < deadline {
            guard let command = try client.nextCommand() else {
                continue
            }
            guard client.verifyHostCommand(command) else {
                throw AgentError.authentication
            }

            let completion = try execute(command, app: application, elements: &elements, client: client)
            try client.complete(completion, command: command)
            if command.operation == "shutdown" {
                return
            }
        }

        throw AgentError.timeout
    }

    private func execute(
        _ command: Command,
        app: XCUIApplication,
        elements: inout [String: XCUIElement],
        client: HostClient) throws -> Completion {
        switch command.operation {
        case "status":
            return Completion.ok(command, body: client.targetStatus(
                app: app,
                targetProcessId: Int(app.processID)))

        case "tree":
            let values = collectElements(app: app, elements: &elements)
            return Completion.ok(command, body: values)

        case "query":
            let type = command.arguments?["type"]
            let automationId = command.arguments?["automationId"]
            let text = command.arguments?["text"]
            let values = collectElements(app: app, elements: &elements).filter { value in
                let candidateType = value["type"] as? String ?? ""
                let candidateId = value["automationId"] as? String ?? ""
                let candidateText = value["text"] as? String ?? ""
                return (type == nil || type!.isEmpty || candidateType.caseInsensitiveCompare(type!) == .orderedSame) &&
                    (automationId == nil || automationId!.isEmpty || candidateId == automationId) &&
                    (text == nil || text!.isEmpty || candidateText == text)
            }
            return Completion.ok(command, body: values)

        case "element":
            guard let identifier = command.arguments?["elementId"], let element = elements[identifier] else {
                return Completion.error(command, code: "apple-agent-element-not-found", message: "The requested element is not available.")
            }
            return Completion.ok(command, body: elementProjection(element, id: identifier))

        case "property":
            guard let identifier = command.arguments?["elementId"],
                  let name = command.arguments?["propertyName"],
                  let element = elements[identifier] else {
                return Completion.error(command, code: "apple-agent-element-not-found", message: "The requested element is not available.")
            }
            return Completion.ok(command, body: ["value": property(name, from: element)])

        case "tap":
            guard let identifier = command.arguments?["elementId"], let element = elements[identifier],
                  element.exists, element.isHittable else {
                return Completion.error(command, code: "apple-agent-not-hittable", message: "The requested element is not hittable.")
            }
            element.tap()
            return Completion.ok(command, body: ["success": true])

        case "fill":
            guard let identifier = command.arguments?["elementId"],
                  let text = command.arguments?["text"],
                  let element = elements[identifier],
                  element.exists, element.isHittable else {
                return Completion.error(command, code: "apple-agent-not-hittable", message: "The requested element is not hittable.")
            }
            element.tap()
            element.typeText(text)
            return Completion.ok(command, body: ["success": true])

        case "scroll":
            let element = command.arguments?["elementId"].flatMap { elements[$0] }
            let deltaY = Double(command.arguments?["deltaY"] ?? "0") ?? 0
            let deltaX = Double(command.arguments?["deltaX"] ?? "0") ?? 0
            let target = element ?? app
            if abs(deltaY) >= abs(deltaX) {
                deltaY >= 0 ? target.swipeUp() : target.swipeDown()
            } else {
                deltaX >= 0 ? target.swipeLeft() : target.swipeRight()
            }
            return Completion.ok(command, body: ["success": true])

        case "screenshot":
            let png = XCUIScreen.main.screenshot().pngRepresentation
            let reference = try client.uploadArtifact(kind: "screenshot-png", content: png, command: command)
            return Completion.ok(command, body: ["success": true], artifacts: [reference])

        case "wait":
            let duration = Int(command.arguments?["durationMs"] ?? "0") ?? 0
            let until = Date().addingTimeInterval(Double(max(0, min(duration, 30_000))) / 1000)
            while Date() < until {
                if try client.isCancelled(command) {
                    return Completion.cancelled(command)
                }
                RunLoop.current.run(until: Date().addingTimeInterval(0.05))
            }
            return Completion.ok(command, body: ["success": true])

        case "navigate", "back", "set-theme", "set-property":
            return try client.forwardToInAppAgent(command)

        case "shutdown":
            return Completion.ok(command, body: ["success": true])

        default:
            return Completion.error(command, code: "apple-agent-capability-missing", message: "The operation is not supported by the XCTest agent.")
        }
    }

    private func collectElements(app: XCUIApplication, elements: inout [String: XCUIElement]) -> [[String: Any]] {
        return app.descendants(matching: .any).allElementsBoundByIndex.enumerated().map { index, element in
            let identity = element.identifier.isEmpty ? "index-\(index)" : element.identifier
            let id = "xcui-\(index)-\(identity.sha256Prefix)"
            elements[id] = element
            return elementProjection(element, id: id)
        }
    }

    private func elementProjection(_ element: XCUIElement, id: String) -> [String: Any] {
        return [
            "id": id,
            "type": String(describing: element.elementType),
            "fullType": "XCUIElement",
            "framework": "xcuitest",
            "automationId": element.identifier,
            "text": element.label,
            "isVisible": element.exists,
            "isEnabled": element.isEnabled,
            "isFocused": false,
            "opacity": 1.0,
            "bounds": [
                "x": Double(element.frame.origin.x),
                "y": Double(element.frame.origin.y),
                "width": Double(element.frame.size.width),
                "height": Double(element.frame.size.height),
            ],
            "nativeType": "XCUIElement",
            "nativeAutomationIdentity": element.identifier,
            "nativeAutomationIdentityKind": "accessibilityIdentifier",
        ]
    }

    private func property(_ name: String, from element: XCUIElement) -> String {
        switch name.lowercased() {
        case "text", "label": return element.label
        case "value": return String(describing: element.value ?? "")
        case "isenabled": return element.isEnabled ? "True" : "False"
        case "isvisible", "exists": return element.exists ? "True" : "False"
        case "ishittable": return element.isHittable ? "True" : "False"
        default: return ""
        }
    }
}

private enum AgentError: Error {
    case configuration
    case authentication
    case timeout
    case transport
}

private struct Configuration {
    let endpoint: URL
    let sessionId: String
    let secret: SymmetricKey
    let targetBundleId: String
    let platform: String
    let maximumDuration: TimeInterval
    let inAppAgentPort: Int
    let seedId: String
    let targetAppDigest: String?

    init(environment: [String: String]) throws {
        guard let endpointText = environment["DEVFLOW_APPLE_AGENT_ENDPOINT"],
              let endpoint = URL(string: endpointText),
              let sessionId = environment["DEVFLOW_APPLE_AGENT_SESSION_ID"],
              let secretText = environment["DEVFLOW_APPLE_AGENT_SESSION_SECRET"],
              let secretData = Data(hex: secretText),
              secretData.count >= 32,
              let targetBundleId = environment["DEVFLOW_TARGET_BUNDLE_ID"],
              !targetBundleId.isEmpty,
              let platform = environment["DEVFLOW_APPLE_AGENT_PLATFORM"],
              platform == "ios" || platform == "maccatalyst" || platform == "macos" else {
            throw AgentError.configuration
        }
        self.endpoint = endpoint
        self.sessionId = sessionId
        self.secret = SymmetricKey(data: secretData)
        self.targetBundleId = targetBundleId
        self.platform = platform
        self.maximumDuration = Double(environment["DEVFLOW_APPLE_AGENT_TIMEOUT_SECONDS"] ?? "120") ?? 120
        self.inAppAgentPort = Int(environment["DEVFLOW_APPLE_IN_APP_AGENT_PORT"] ?? "9223") ?? 9223
        self.seedId = environment["DEVFLOW_APPLE_TEST_SEED"] ?? "devflow-sample-v1"
        self.targetAppDigest = environment["DEVFLOW_TARGET_APP_DIGEST"]
    }
}

private struct Command: Codable {
    let sessionId: String
    let target: CommandTarget
    let commandId: String
    let sequence: Int64
    let actionDigest: String
    let authorityEpoch: Int64
    let approvalDigest: String?
    let deadline: String
    let operation: String
    let arguments: [String: String]?
    let hostSignature: String?
}

private struct CommandTarget: Codable {
    let platform: String
    let targetBundleId: String
    let appInstanceId: String?
    let appBuildDigest: String?
}

private struct Receipt: Codable {
    let sessionId: String
    let commandId: String
    let sequence: Int64
    let actionDigest: String
    let authorityEpoch: Int64
    let approvalDigest: String?
    let acknowledgementState: String
    let completionCertainty: String
    let at: String
}

private struct Completion: Codable {
    let receipt: Receipt
    let ok: Bool
    let completionCertainty: String
    let resultBase64: String?
    let error: AgentProtocolError?
    let artifacts: [ArtifactReference]

    static func ok(_ command: Command, body: Any, artifacts: [ArtifactReference] = []) -> Completion {
        return Completion(
            receipt: Receipt.forCommand(command, state: "completed", certainty: "certain"),
            ok: true,
            completionCertainty: "certain",
            resultBase64: JSON.data(body)?.base64EncodedString(),
            error: nil,
            artifacts: artifacts)
    }

    static func error(_ command: Command, code: String, message: String) -> Completion {
        return Completion(
            receipt: Receipt.forCommand(command, state: "completed", certainty: "certain"),
            ok: false,
            completionCertainty: "certain",
            resultBase64: nil,
            error: AgentProtocolError(code: code, category: "operation", message: message, retryable: false),
            artifacts: [])
    }

    static func cancelled(_ command: Command) -> Completion {
        return Completion(
            receipt: Receipt.forCommand(command, state: "cancelled", certainty: "certain"),
            ok: false,
            completionCertainty: "certain",
            resultBase64: nil,
            error: AgentProtocolError(code: "apple-agent-cancelled", category: "cancelled", message: "The host cancelled the operation.", retryable: false),
            artifacts: [])
    }
}

private extension Receipt {
    static func forCommand(_ command: Command, state: String, certainty: String) -> Receipt {
        return Receipt(
            sessionId: command.sessionId,
            commandId: command.commandId,
            sequence: command.sequence,
            actionDigest: command.actionDigest,
            authorityEpoch: command.authorityEpoch,
            approvalDigest: command.approvalDigest,
            acknowledgementState: state,
            completionCertainty: certainty,
            at: ISO8601DateFormatter().string(from: Date()))
    }
}

private struct AgentProtocolError: Codable {
    let code: String
    let category: String
    let message: String
    let retryable: Bool
}

private struct ArtifactReference: Codable {
    let artifactId: String
    let kind: String
    let sha256: String
    let sizeBytes: Int
    let truncated: Bool
}

private struct ArtifactChunk: Codable {
    let sessionId: String
    let artifactId: String
    let kind: String
    let chunkIndex: Int
    let totalChunks: Int
    let contentBase64: String
    let contentDigest: String
    let isFinal: Bool
}

private final class HostClient {
    private let configuration: Configuration
    private var targetProcessId: Int?

    init(configuration: Configuration) {
        self.configuration = configuration
    }

    func hello(targetForegroundOwned: Bool, targetProcessId: Int) throws {
        self.targetProcessId = targetProcessId
        var target: [String: Any] = [
            "platform": configuration.platform,
            "targetBundleId": configuration.targetBundleId,
            "appInstanceId": "process-\(targetProcessId)",
        ]
        if let targetAppDigest = configuration.targetAppDigest {
            target["appBuildDigest"] = targetAppDigest
        }
        let body: [String: Any] = [
            "sessionId": configuration.sessionId,
            "target": target,
            "agentInstanceId": UUID().uuidString,
            "attachedAt": ISO8601DateFormatter().string(from: Date()),
            "capabilities": [
                "protocol": "maui-apple-test-agent-v1",
                "operations": ["status", "tree", "query", "element", "property", "tap", "fill", "scroll", "navigate", "back", "set-theme", "set-property", "screenshot", "wait", "shutdown"],
                "targetForegroundOwned": targetForegroundOwned,
                "authenticatedTransport": true,
                "maxArtifactChunkBytes": 65536,
                "deviceAgentVersion": "1.0.0-experimental",
                "targetProcessId": targetProcessId,
                "webViewContextIdentity": "unsupported-by-xcuitest-agent",
            ],
        ]
        _ = try request(method: "POST", path: "/v1/session/\(configuration.sessionId)/hello", body: JSON.data(body))
    }

    func nextCommand() throws -> Command? {
        let response = try request(method: "GET", path: "/v1/session/\(configuration.sessionId)/next", body: nil)
        if response.statusCode == 204 {
            return nil
        }
        guard response.statusCode == 200 else { throw AgentError.transport }
        return try JSONDecoder().decode(Command.self, from: response.data)
    }

    func complete(_ completion: Completion, command: Command) throws {
        let body = try JSONEncoder().encode(completion)
        let response = try request(
            method: "POST",
            path: "/v1/session/\(configuration.sessionId)/commands/\(command.commandId)/complete",
            body: body,
            commandId: command.commandId,
            sequence: command.sequence)
        guard response.statusCode == 200 else { throw AgentError.transport }
    }

    func isCancelled(_ command: Command) throws -> Bool {
        let response = try request(
            method: "GET",
            path: "/v1/session/\(configuration.sessionId)/commands/\(command.commandId)/cancelled",
            body: nil,
            commandId: command.commandId,
            sequence: 0)
        guard response.statusCode == 200,
              let object = try JSONSerialization.jsonObject(with: response.data) as? [String: Any] else {
            throw AgentError.transport
        }
        return object["cancelled"] as? Bool ?? false
    }

    func uploadArtifact(kind: String, content: Data, command: Command) throws -> ArtifactReference {
        let artifactId = "artifact-\(UUID().uuidString.lowercased())"
        let chunkSize = 48 * 1024
        let maximumBytes = 4 * 1024 * 1024
        let bounded = content.count > maximumBytes ? content.prefix(maximumBytes) : content
        let boundedData = Data(bounded)
        let chunks = stride(from: 0, to: boundedData.count, by: chunkSize).map {
            boundedData.subdata(in: $0 ..< min($0 + chunkSize, boundedData.count))
        }
        for (index, chunk) in chunks.enumerated() {
            let body = try JSONEncoder().encode(ArtifactChunk(
                sessionId: configuration.sessionId,
                artifactId: artifactId,
                kind: kind,
                chunkIndex: index,
                totalChunks: chunks.count,
                contentBase64: chunk.base64EncodedString(),
                contentDigest: digest(chunk),
                isFinal: index == chunks.count - 1))
            let response = try request(
                method: "POST",
                path: "/v1/session/\(configuration.sessionId)/artifacts",
                body: body,
                commandId: artifactId,
                sequence: Int64(index))
            guard response.statusCode == 200 else { throw AgentError.transport }
        }
        return ArtifactReference(
            artifactId: artifactId,
            kind: kind,
            sha256: digest(boundedData),
            sizeBytes: boundedData.count,
            truncated: content.count > boundedData.count)
    }

    func forwardToInAppAgent(_ command: Command) throws -> Completion {
        guard let endpoint = URL(string: "http://127.0.0.1:\(configuration.inAppAgentPort)") else {
            return Completion.error(command, code: "apple-agent-capability-missing", message: "The approved in-app DevFlow endpoint is unavailable.")
        }
        let path: String
        let method: String
        switch command.operation {
        case "navigate": path = "/api/v1/ui/actions/navigate"; method = "POST"
        case "back": path = "/api/v1/ui/actions/back"; method = "POST"
        case "set-theme": path = "/api/v1/device/app/theme"; method = "PUT"
        case "set-property":
            guard let id = command.arguments?["elementId"], let property = command.arguments?["propertyName"] else {
                return Completion.error(command, code: "apple-agent-invalid-request", message: "The in-app property operation is incomplete.")
            }
            path = "/api/v1/ui/elements/\(id)/properties/\(property)"; method = "PUT"
        default:
            return Completion.error(command, code: "apple-agent-capability-missing", message: "The in-app operation is unsupported.")
        }
        guard let requestUrl = URL(string: path, relativeTo: endpoint) else {
            return Completion.error(command, code: "apple-agent-capability-missing", message: "The in-app endpoint is invalid.")
        }
        var forwarded = URLRequest(url: requestUrl)
        forwarded.httpMethod = method
        forwarded.httpBody = JSON.data(command.arguments ?? [:])
        forwarded.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let semaphore = DispatchSemaphore(value: 0)
        var success = false
        URLSession.shared.dataTask(with: forwarded) { _, response, _ in
            success = (response as? HTTPURLResponse)?.statusCode ?? 500 < 300
            semaphore.signal()
        }.resume()
        _ = semaphore.wait(timeout: .now() + 10)
        return success
            ? Completion.ok(command, body: ["success": true])
            : Completion.error(command, code: "apple-agent-in-app-operation-failed", message: "The in-app DevFlow operation failed.")
    }

    func targetStatus(app: XCUIApplication, targetProcessId: Int) -> [String: Any] {
        let deadline = Date().addingTimeInterval(20)
        repeat {
            if var status = inAppJson(path: "/api/v1/status") {
                status["running"] = true
                status["app"] = [
                    "packageId": app.bundleIdentifier,
                    "processId": targetProcessId,
                ]
                status["agent"] = ["instanceId": "xctest-target-\(targetProcessId)"]
                status["device"] = ["platform": "apple"]
                if let state = inAppJson(path: "/api/v1/ext/com.example.devflow.integrationtest/state") {
                    if let route = state["route"] {
                        status["route"] = route
                    }
                    status["testState"] = state
                    return status
                }
            }
            RunLoop.current.run(until: Date().addingTimeInterval(0.25))
        } while Date() < deadline

        return [
            "running": true,
            "route": NSNull(),
            "window": "xctest-target",
            "app": ["packageId": app.bundleIdentifier, "processId": targetProcessId],
            "agent": ["instanceId": "xctest-target-\(targetProcessId)"],
            "device": ["platform": "apple"],
        ]
    }

    func verifyHostCommand(_ command: Command) -> Bool {
        guard let signature = command.hostSignature,
              let deadline = ISO8601DateFormatter.withFractional.date(from: command.deadline)
                ?? ISO8601DateFormatter().date(from: command.deadline),
              command.sessionId == configuration.sessionId,
              command.target.platform == configuration.platform,
              command.target.targetBundleId == configuration.targetBundleId,
              command.target.appInstanceId == targetProcessId.map({ "process-\($0)" }),
              command.target.appBuildDigest == configuration.targetAppDigest,
              deadline > Date(),
              constantTimeEquals(command.actionDigest, actionDigest(for: command)) else {
            return false
        }
        let expected = signatureFor(
            method: "COMMAND",
            path: "/v1/session/\(configuration.sessionId)/next",
            sessionId: command.sessionId,
            commandId: command.commandId,
            sequence: command.sequence,
            timestamp: Int64(deadline.timeIntervalSince1970),
            nonce: command.actionDigest,
            bodyDigest: command.actionDigest)
        return constantTimeEquals(expected, signature)
    }

    private func actionDigest(for command: Command) -> String {
        var material = [
            command.operation,
            command.target.platform,
            command.target.targetBundleId,
            String(command.authorityEpoch),
            command.approvalDigest ?? "",
        ]
        if let arguments = command.arguments {
            material.append(contentsOf: arguments.keys.sorted().map { key in
                let encodedKey = Data(key.utf8).base64EncodedString()
                let encodedValue = Data((arguments[key] ?? "").utf8).base64EncodedString()
                return "\(encodedKey)=\(encodedValue)"
            })
        }
        return digest(Data(material.joined(separator: "\n").utf8))
    }

    private func request(
        method: String,
        path: String,
        body: Data?,
        commandId: String? = nil,
        sequence: Int64 = 0) throws -> (data: Data, statusCode: Int) {
        var lastError: Error = AgentError.transport
        for attempt in 0 ..< 3 {
            do {
                return try requestOnce(
                    method: method,
                    path: path,
                    body: body,
                    commandId: commandId,
                    sequence: sequence)
            } catch {
                lastError = error
                if attempt < 2 {
                    RunLoop.current.run(until: Date().addingTimeInterval(0.2 * Double(attempt + 1)))
                }
            }
        }
        throw lastError
    }

    private func requestOnce(
        method: String,
        path: String,
        body: Data?,
        commandId: String? = nil,
        sequence: Int64 = 0) throws -> (data: Data, statusCode: Int) {
        guard let url = URL(string: path, relativeTo: configuration.endpoint) else { throw AgentError.transport }
        let data = body ?? Data()
        let timestamp = Int64(Date().timeIntervalSince1970)
        let nonce = UUID().uuidString.lowercased()
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.httpBody = body
        request.setValue(configuration.sessionId, forHTTPHeaderField: "X-Maui-Apple-Session")
        request.setValue(String(timestamp), forHTTPHeaderField: "X-Maui-Apple-Timestamp")
        request.setValue(nonce, forHTTPHeaderField: "X-Maui-Apple-Nonce")
        request.setValue(signatureFor(
            method: method,
            path: path,
            sessionId: configuration.sessionId,
            commandId: commandId ?? "",
            sequence: sequence,
            timestamp: timestamp,
            nonce: nonce,
            bodyDigest: digest(data)),
            forHTTPHeaderField: "X-Maui-Apple-Signature")
        if body != nil {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }

        let semaphore = DispatchSemaphore(value: 0)
        var result: Result<(Data, HTTPURLResponse), Error> = .failure(AgentError.transport)
        URLSession.shared.dataTask(with: request) { responseData, response, error in
            if let error {
                result = .failure(error)
            } else if let response = response as? HTTPURLResponse {
                result = .success((responseData ?? Data(), response))
            }
            semaphore.signal()
        }.resume()
        guard semaphore.wait(timeout: .now() + 30) == .success else { throw AgentError.timeout }
        let response = try result.get()
        return (response.0, response.1.statusCode)
    }

    private func inAppJson(path: String) -> [String: Any]? {
        guard let url = URL(string: path, relativeTo: URL(string: "http://127.0.0.1:\(configuration.inAppAgentPort)")) else {
            return nil
        }
        let semaphore = DispatchSemaphore(value: 0)
        var result: (Data, HTTPURLResponse)?
        URLSession.shared.dataTask(with: url) { data, response, _ in
            if let response = response as? HTTPURLResponse, response.statusCode < 300 {
                result = (data ?? Data(), response)
            }
            semaphore.signal()
        }.resume()
        guard semaphore.wait(timeout: .now() + 2) == .success,
              let data = result?.0,
              let value = try? JSONSerialization.jsonObject(with: data),
              let object = value as? [String: Any] else {
            return nil
        }
        return object
    }

    private func signatureFor(
        method: String,
        path: String,
        sessionId: String,
        commandId: String,
        sequence: Int64,
        timestamp: Int64,
        nonce: String,
        bodyDigest: String) -> String {
        let material = [method.uppercased(), path, sessionId, commandId, String(sequence), String(timestamp), nonce, bodyDigest].joined(separator: "\n")
        let code = HMAC<SHA256>.authenticationCode(for: Data(material.utf8), using: configuration.secret)
        return code.map { String(format: "%02x", $0) }.joined()
    }

    private func digest(_ value: Data) -> String {
        return "sha256:" + SHA256.hash(data: value).map { String(format: "%02x", $0) }.joined()
    }

}

private enum JSON {
    static func data(_ value: Any) -> Data? {
        return try? JSONSerialization.data(withJSONObject: value, options: [])
    }
}

private extension Data {
    init?(hex: String) {
        guard hex.count.isMultiple(of: 2) else { return nil }
        var result = Data()
        var index = hex.startIndex
        while index < hex.endIndex {
            let next = hex.index(index, offsetBy: 2)
            guard let byte = UInt8(hex[index..<next], radix: 16) else { return nil }
            result.append(byte)
            index = next
        }
        self = result
    }
}

private extension String {
    var sha256Prefix: String {
        let hash = SHA256.hash(data: Data(utf8))
        return hash.prefix(8).map { String(format: "%02x", $0) }.joined()
    }
}

private extension ISO8601DateFormatter {
    static let withFractional: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter
    }()
}

private func constantTimeEquals(_ left: String, _ right: String) -> Bool {
    let leftBytes = Array(left.utf8)
    let rightBytes = Array(right.utf8)
    guard leftBytes.count == rightBytes.count else { return false }
    return zip(leftBytes, rightBytes).reduce(0) { $0 | Int($1.0 ^ $1.1) } == 0
}
