import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const contractsDirectory = path.join(root, "Shared", "Contracts");
const fixturesDirectory = path.join(root, "Shared", "TestVectors");
const failures = [];

const expectedSchemas = new Map([
  ["v1/app-state.schema.json", {
    id: "urn:emke:contracts:v1:app-state",
    title: "EMKE App State v1",
  }],
  ["v1/compatibility.schema.json", {
    id: "urn:emke:contracts:v1:compatibility",
    title: "EMKE Compatibility v1",
  }],
  ["v1/translation-events.schema.json", {
    id: "urn:emke:contracts:v1:translation-events",
    title: "EMKE Translation Events v1",
  }],
]);

const expectedTranslationTypes = [
  "session.update",
  "input_audio_buffer.append",
  "session.close",
  "session.created",
  "session.updated",
  "translation_audio.delta",
  "translation_audio.done",
  "input_audio_transcription.delta",
  "input_audio_transcription.done",
  "error",
  "session.closed",
];

const expectedAppStateEnums = {
  runtimeState: ["stopped", "starting", "running", "stopping", "degraded", "failed"],
  channelState: ["inactive", "connecting", "connected", "reconnecting", "bypassed", "degraded", "failed"],
  inboundRoute: ["stopped", "translated", "originalFailOpen", "originalBypass"],
  outboundRoute: ["stopped", "translated", "mutedFailClosed", "originalBypass"],
  errorCategory: ["configuration", "permission", "driver", "device", "authentication", "endpointModel", "protocol", "network", "backpressure", "closeTimeout"],
  recoveryAction: ["none", "editSettings", "openPrivacySettings", "installDriver", "selectDevice", "updateApiKey", "retry", "reportCompatibility"],
};

const expectedFixtureCases = new Map([
  ["Realtime/text-frame-handshake.json", [
    "normal handshake sends session update as text and connects",
    "client JSON session update sent as binary is protocol failure",
    "session updated before session created is protocol failure",
    "same language uses local bypass with no outbound socket",
    "two language setup creates two independent sockets",
  ]],
  ["Realtime/close-deadline.json", [
    "close deadline starts before close send",
    "inbound and outbound close run concurrently",
    "session closed within 1000 ms delivers queued tail audio",
    "blocked close send reaches local close timeout at 1000 ms",
    "two close callers await the same completion",
    "old generation close completion cannot clear new generation",
  ]],
  ["Routing/inbound-language-gate.json", [
    "bcp 47 Chinese confidence aggregates to native original",
    "non native confidence 0.60 routes translated",
    "native confidence 0.75 routes original",
    "voiced undecided at 250 ms routes translated",
    "unvoiced undecided at 250 ms routes original",
    "vad end waits 500 ms for late input",
    "late audio at 450 ms restarts 500 ms window",
    "late transcript at 450 ms restarts 500 ms window",
    "recovery during utterance remains original fail open until next utterance",
  ]],
  ["Routing/channel-failure-safety.json", [
    "inbound network failure routes original fail open",
    "outbound network failure routes muted fail closed",
    "outbound underrun outputs zeros and forbids physical microphone",
    "explicit outbound bypass routes original bypass",
    "explicit bypass persists through disconnect and reconnect",
    "stop stops both routes",
  ]],
  ["Audio/pcm-batching.json", [
    "one exact network batch emits immediately",
    "two half batches combine into one network batch",
    "odd PCM16 append fails before buffering",
    "incomplete even tail remains buffered",
    "append larger than one batch retains the exact tail",
    "stop flush discards an incomplete tail",
  ]],
  ["Audio/pcm-conversion.json", [
    "encoder clamps Float32 endpoints exactly",
    "encoder downmixes stereo before averaging two frames",
    "encoder packs signed PCM16 in little endian byte order",
    "decoder duplicates each interpolated sample to left and right",
    "decoder rejects an odd PCM16 byte count",
    "chunked FIR decode matches contiguous decode across aligned chunks",
    "decoder FIR history resets only after explicit reset or stop",
  ]],
  ["Settings/v1-migration.json", [
    "empty object migrates to safe defaults",
    "schema version 1 is semantic identity",
    "unknown future schema version is unsupported",
    "malformed JSON is quarantined",
  ]],
  ["Settings/compatibility-gate.json", [
    "exact versions",
    "compatible below recommended",
    "missing driver",
    "invalid signature",
    "abi mismatch",
    "one endpoint only",
  ]],
]);

const expectedFixturePaths = [...expectedFixtureCases.keys()];

function fail(message) {
  failures.push(message);
}

function isObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function regularFile(relativePath) {
  try {
    if (!fs.lstatSync(path.join(root, relativePath)).isFile()) {
      fail(`${relativePath}: must be a regular non-symlink file`);
      return false;
    }
    return true;
  } catch {
    fail(`${relativePath}: must be a regular non-symlink file`);
    return false;
  }
}

function readObjectJson(relativePath) {
  if (!regularFile(relativePath)) return undefined;
  let value;
  try {
    value = JSON.parse(fs.readFileSync(path.join(root, relativePath), "utf8"));
  } catch {
    fail(`${relativePath}: invalid JSON`);
    return undefined;
  }
  if (!isObject(value)) {
    fail(`${relativePath}: root must be a non-null object`);
    return undefined;
  }
  return value;
}

function isSafeRelativePath(relativePath) {
  if (typeof relativePath !== "string" || relativePath.length === 0) return false;
  if (relativePath.includes("\\") || path.isAbsolute(relativePath) || /^[A-Za-z]:[\\/]/.test(relativePath)) return false;
  return relativePath.split("/").every((segment) => segment.length > 0 && segment !== "." && segment !== "..");
}

function collectJsonPaths(directory, excludedFile, label) {
  try {
    return fs.readdirSync(directory, { recursive: true, withFileTypes: true })
      .filter((entry) => entry.name.endsWith(".json"))
      .map((entry) => path.relative(directory, path.join(entry.parentPath, entry.name)).split(path.sep).join("/"))
      .filter((relativePath) => relativePath !== excludedFile)
      .sort();
  } catch {
    fail(`${label}: inventory unreadable`);
    return [];
  }
}

function validateInventory(name, listedPaths, directory, excludedFile, relativeDirectory) {
  if (!Array.isArray(listedPaths)) {
    fail(`${name} manifest: inventory must be an array`);
    return [];
  }

  const listed = new Set();
  for (const relativePath of listedPaths) {
    if (!isSafeRelativePath(relativePath) || !relativePath.endsWith(".json")) {
      fail(`${name} manifest: unsafe inventory path`);
      continue;
    }
    if (listed.has(relativePath)) fail(`${name} manifest: duplicate inventory path`);
    listed.add(relativePath);
  }

  const sortedListed = [...listed].sort();
  const actual = collectJsonPaths(directory, excludedFile, `${relativeDirectory}`);
  const actualSet = new Set(actual);
  for (const relativePath of sortedListed) {
    const contractPath = `${relativeDirectory}/${relativePath}`;
    regularFile(contractPath);
    if (!actualSet.has(relativePath)) fail(`${contractPath}: missing from actual inventory`);
  }
  for (const relativePath of actual) {
    const contractPath = `${relativeDirectory}/${relativePath}`;
    regularFile(contractPath);
    if (!listed.has(relativePath)) fail(`${contractPath}: unlisted inventory file`);
  }
  return sortedListed;
}

function hasClosedRoot(schema) {
  if (schema.additionalProperties === false) return true;
  return Array.isArray(schema.oneOf)
    && schema.oneOf.length > 0
    && schema.oneOf.every((variant) => isObject(variant) && variant.additionalProperties === false);
}

function sameArray(actual, expected) {
  return Array.isArray(actual)
    && actual.length === expected.length
    && actual.every((value, index) => value === expected[index]);
}

function deepEqual(actual, expected) {
  if (Object.is(actual, expected)) return true;
  if (Array.isArray(actual) || Array.isArray(expected)) {
    return Array.isArray(actual)
      && Array.isArray(expected)
      && actual.length === expected.length
      && actual.every((value, index) => deepEqual(value, expected[index]));
  }
  if (!isObject(actual) || !isObject(expected)) return false;
  const actualKeys = Object.keys(actual).sort();
  const expectedKeys = Object.keys(expected).sort();
  return sameArray(actualKeys, expectedKeys)
    && actualKeys.every((key) => deepEqual(actual[key], expected[key]));
}

function fixtureRule(relativePath, rule, condition) {
  if (!condition) fail(`Shared/TestVectors/${relativePath}: ${rule}`);
}

function fixtureCase(fixture, name) {
  return fixtureCases(fixture).find((candidate) => isObject(candidate) && candidate.name === name);
}

function fixtureCases(fixture) {
  return Array.isArray(fixture?.cases) ? fixture.cases : [];
}

function validateCaseInventory(relativePath, fixture) {
  const expectedNames = expectedFixtureCases.get(relativePath);
  const actualNames = fixtureCases(fixture).map((candidate) => candidate?.name);
  fixtureRule(
    relativePath,
    "case inventory drifted",
    expectedNames !== undefined && sameArray(actualNames, expectedNames),
  );
}

function scanDecodedString(value, relativePath) {
  if (/["']?authorization["']?\s*[:=]/i.test(value)) fail(`${relativePath}: forbidden Authorization key`);
  if (/(?:["']?(?:api[_ -]?key|access[_ -]?token|secret|token)["']?)\s*[:=]/i.test(value)) fail(`${relativePath}: forbidden secret-like content`);
  if (/sk-[a-z0-9_-]{16,}/i.test(value)) fail(`${relativePath}: forbidden secret-like content`);
  if (/(?:^|[^A-Za-z0-9])\/(?:Users|Volumes|private|var|tmp)\//.test(value)) fail(`${relativePath}: forbidden absolute path`);
  if (/(?:^|[^A-Za-z0-9])[A-Za-z]:\\(?:Users|Windows|ProgramData)\\/.test(value)) fail(`${relativePath}: forbidden absolute path`);
}

function inspectDecodedJson(value, relativePath) {
  if (typeof value === "string") {
    scanDecodedString(value, relativePath);
    return;
  }
  if (Array.isArray(value)) {
    for (const item of value) inspectDecodedJson(item, relativePath);
    return;
  }
  if (!isObject(value)) return;

  for (const [key, nestedValue] of Object.entries(value)) {
    scanDecodedString(key, relativePath);
    const normalizedKey = key.replace(/[ _-]/g, "").toLowerCase();
    if (normalizedKey === "authorization") fail(`${relativePath}: forbidden Authorization key`);
    if (["apikey", "accesstoken", "token", "secret"].includes(normalizedKey)) fail(`${relativePath}: forbidden secret-like content`);
    inspectDecodedJson(nestedValue, relativePath);
  }
}

function inspectSharedContent() {
  let entries;
  try {
    entries = fs.readdirSync(path.join(root, "Shared"), { recursive: true, withFileTypes: true });
  } catch {
    fail("Shared: content inventory unreadable");
    return;
  }
  const patterns = [
    ["forbidden Authorization key", /["']?authorization["']?\s*[:=]/i],
    ["forbidden secret-like content", /(?:["']?(?:api[_ -]?key|access[_ -]?token|secret|token)["']?)\s*[:=]\s*["']?[A-Za-z0-9._~+/-]{12,}/i],
    ["forbidden secret-like content", /sk-[a-z0-9_-]{16,}/i],
    ["forbidden absolute path", /(?:^|["'\s])\/(?:Users|Volumes|private|var|tmp)\//m],
    ["forbidden absolute path", /(?:^|["'\s])[A-Za-z]:\\Users\\/m],
    ["forbidden absolute path", /(?:^|["'\s])[A-Za-z]:\\\\Users\\\\/m],
  ];
  for (const entry of entries.filter((candidate) => candidate.isFile()).sort((left, right) => left.parentPath.localeCompare(right.parentPath) || left.name.localeCompare(right.name))) {
    const relativePath = path.relative(root, path.join(entry.parentPath, entry.name)).split(path.sep).join("/");
    let content;
    try {
      content = fs.readFileSync(path.join(entry.parentPath, entry.name), "utf8");
    } catch {
      fail(`${relativePath}: content unreadable`);
      continue;
    }
    for (const [rule, pattern] of patterns) {
      if (pattern.test(content)) fail(`${relativePath}: ${rule}`);
    }
    if (entry.name.endsWith(".json")) {
      try {
        inspectDecodedJson(JSON.parse(content), relativePath);
      } catch {
        // Invalid JSON is reported by the manifest/inventory validation path.
      }
    }
  }
}

function validateHandshakeFixture(fixture, translationSchema) {
  const relativePath = "Realtime/text-frame-handshake.json";
  const eventTypes = new Set(
    Array.isArray(translationSchema?.oneOf)
      ? translationSchema.oneOf.map((variant) => variant?.properties?.type?.const)
      : [],
  );
  const sessionUpdateSchema = Array.isArray(translationSchema?.oneOf)
    ? translationSchema.oneOf.find((variant) => variant?.properties?.type?.const === "session.update")
    : undefined;
  const targetLanguages = new Set(sessionUpdateSchema?.properties?.target_language?.enum ?? []);
  const allSteps = [];
  for (const testCase of fixtureCases(fixture)) {
    if (Array.isArray(testCase?.steps)) allSteps.push(...testCase.steps);
    for (const socket of testCase?.sockets ?? []) {
      if (Array.isArray(socket?.steps)) allSteps.push(...socket.steps);
    }
  }
  const wireSteps = allSteps.filter((step) => ["clientToServer", "serverToClient"].includes(step?.direction));
  fixtureRule(relativePath, "wire payload contract drifted", wireSteps.length > 0 && wireSteps.every((step) =>
    step?.payloadEncoding === "json"
      && isObject(step.payload)
      && step.payload.type === step.eventType
      && eventTypes.has(step.eventType)));
  fixtureRule(relativePath, "session.update target language drifted", wireSteps
    .filter((step) => step?.eventType === "session.update")
    .every((step) => typeof step.payload?.target_language === "string" && targetLanguages.has(step.payload.target_language)));

  const normal = fixtureCase(fixture, "normal handshake sends session update as text and connects");
  fixtureRule(relativePath, "normal text handshake drifted",
    Array.isArray(normal?.steps)
      && sameArray(normal.steps.map((step) => step.eventType), ["session.created", "session.update", "session.updated"])
      && normal.steps.every((step) => step.frameType === "text")
      && normal.expected?.inboundChannelState === "connected"
      && normal.expected?.inboundRoute === "translated");

  const binary = fixtureCase(fixture, "client JSON session update sent as binary is protocol failure");
  const binaryUpdate = binary?.steps?.find((step) => step?.eventType === "session.update");
  fixtureRule(relativePath, "binary JSON negative drifted",
    binaryUpdate?.frameType === "binary"
      && binaryUpdate?.expectedState === "protocolFailure"
      && binary?.expected?.inboundChannelState === "failed"
      && binary?.expected?.inboundRoute === "originalFailOpen"
      && binary?.expected?.errorCategory === "protocol");

  const earlyUpdated = fixtureCase(fixture, "session updated before session created is protocol failure");
  fixtureRule(relativePath, "out-of-order handshake negative drifted",
    earlyUpdated?.steps?.length === 1
      && earlyUpdated.steps[0]?.eventType === "session.updated"
      && earlyUpdated.steps[0]?.expectedState === "protocolFailure");

  const bypass = fixtureCase(fixture, "same language uses local bypass with no outbound socket");
  fixtureRule(relativePath, "same-language bypass drifted",
    bypass?.configuration?.nativeLanguage === bypass?.configuration?.meetingLanguage
      && bypass?.expected?.outboundSocketCount === 0
      && bypass?.expected?.outboundChannelState === "bypassed"
      && bypass?.expected?.outboundRoute === "originalBypass");

  const twoSockets = fixtureCase(fixture, "two language setup creates two independent sockets");
  fixtureRule(relativePath, "two-socket handshake drifted",
    twoSockets?.expected?.inboundSocketCount === 1
      && twoSockets?.expected?.outboundSocketCount === 1
      && sameArray(twoSockets?.sockets?.map((socket) => socket.socketId), ["inbound", "outbound"]));
}

function validateCloseFixture(fixture) {
  const relativePath = "Realtime/close-deadline.json";
  fixtureRule(relativePath, "close vocabulary drifted",
    sameArray(fixture?.completionVocabulary, ["closed", "closeTimeout"])
      && sameArray(fixture?.tailStateVocabulary, ["none", "draining"]));
  fixtureRule(relativePath, "1000ms close deadline drifted",
    fixtureCases(fixture).length === 6
      && fixtureCases(fixture).every((testCase) => testCase?.input?.deadlineMs === 1000 && testCase.input.startDeadlineBeforeCloseSend === true));

  const start = fixtureCase(fixture, "close deadline starts before close send");
  fixtureRule(relativePath, "deadline start ordering drifted",
    start?.input?.closeSend === "blocked"
      && start?.expected?.completion === "closeTimeout"
      && start?.expected?.completionAtMs === 1000
      && start?.expected?.deadlineStartsAtMs === 0);
  const concurrent = fixtureCase(fixture, "inbound and outbound close run concurrently");
  fixtureRule(relativePath, "concurrent route close drifted",
    sameArray(concurrent?.input?.closeRequests, ["inbound", "outbound"])
      && concurrent?.expected?.concurrent === true
      && deepEqual(concurrent?.expected?.routeCompletions, { inbound: 300, outbound: 400 }));
  const tail = fixtureCase(fixture, "session closed within 1000 ms delivers queued tail audio");
  fixtureRule(relativePath, "tail delivery drifted",
    tail?.input?.sessionClosedAtMs === 999
      && tail?.input?.queuedTailAudio === true
      && tail?.expected?.completion === "closed"
      && tail?.expected?.tailState === "draining");
  const timeout = fixtureCase(fixture, "blocked close send reaches local close timeout at 1000 ms");
  fixtureRule(relativePath, "local close timeout drifted",
    timeout?.input?.closeSend === "blocked"
      && timeout?.expected?.completionAtMs === 1000
      && timeout?.expected?.localCompletion === true);
  const callers = fixtureCase(fixture, "two close callers await the same completion");
  fixtureRule(relativePath, "shared close completion drifted",
    callers?.input?.closeCallerCount === 2
      && callers?.expected?.sameCompletion === true
      && callers?.expected?.completionCount === 1);
  const generation = fixtureCase(fixture, "old generation close completion cannot clear new generation");
  fixtureRule(relativePath, "close generation isolation drifted",
    generation?.input?.closingGeneration === 1
      && generation?.input?.activeGeneration === 2
      && generation?.expected?.completionGeneration === 1
      && generation?.expected?.activeGenerationAfterCompletion === 2
      && generation?.expected?.clearActiveGeneration === false);
}

function validateGateFixture(fixture) {
  const relativePath = "Routing/inbound-language-gate.json";
  fixtureRule(relativePath, "gate vocabulary drifted",
    sameArray(fixture?.gateDecisionVocabulary, ["undecided", "original", "translated"])
      && sameArray(fixture?.tailStateVocabulary, ["none", "waiting", "draining"])
      && sameArray(fixture?.nextUtterancePolicyVocabulary, ["languageGate"]));
  const aggregate = fixtureCase(fixture, "bcp 47 Chinese confidence aggregates to native original");
  fixtureRule(relativePath, "language confidence aggregation drifted",
    aggregate?.input?.confidenceByTag?.["zh-Hans"] === 0.45
      && aggregate?.input?.confidenceByTag?.["zh-Hant"] === 0.4
      && aggregate?.expected?.aggregatedConfidenceByLanguage?.zh === 0.85
      && aggregate?.expected?.gateDecision === "original");
  const voiced = fixtureCase(fixture, "voiced undecided at 250 ms routes translated");
  const unvoiced = fixtureCase(fixture, "unvoiced undecided at 250 ms routes original");
  fixtureRule(relativePath, "250ms voiced decision drifted",
    voiced?.input?.decisionAtMs === 250
      && voiced?.input?.deadlineMs === 250
      && voiced?.expected?.gateDecision === "translated"
      && unvoiced?.input?.decisionAtMs === 250
      && unvoiced?.input?.deadlineMs === 250
      && unvoiced?.expected?.gateDecision === "original");
  const wait = fixtureCase(fixture, "vad end waits 500 ms for late input");
  const lateAudio = fixtureCase(fixture, "late audio at 450 ms restarts 500 ms window");
  const lateTranscript = fixtureCase(fixture, "late transcript at 450 ms restarts 500 ms window");
  fixtureRule(relativePath, "500ms late-input window drifted",
    wait?.input?.restartMs === 500
      && wait?.expected?.waitForLateInputMs === 500
      && lateAudio?.input?.arrivalAfterVadEndMs === 450
      && lateAudio?.expected?.restartWindowMs === 500
      && lateTranscript?.input?.arrivalAfterVadEndMs === 450
      && lateTranscript?.expected?.restartWindowMs === 500);
  const recovery = fixtureCase(fixture, "recovery during utterance remains original fail open until next utterance");
  fixtureRule(relativePath, "utterance recovery boundary drifted",
    recovery?.input?.inboundRoute === "originalFailOpen"
      && recovery?.input?.recoveryEvent === "connected"
      && recovery?.expected?.inboundRoute === "originalFailOpen"
      && recovery?.expected?.tailState === "draining"
      && recovery?.expected?.nextUtterancePolicy === "languageGate");
}

function validateSafetyFixture(fixture) {
  const relativePath = "Routing/channel-failure-safety.json";
  const inbound = fixtureCase(fixture, "inbound network failure routes original fail open");
  fixtureRule(relativePath, "inbound fail-open drifted",
    inbound?.input?.event === "inbound.networkFailure"
      && inbound?.expected?.inboundChannelState === "failed"
      && inbound?.expected?.inboundRoute === "originalFailOpen"
      && inbound?.expected?.errorCategory === "network");
  const outbound = fixtureCase(fixture, "outbound network failure routes muted fail closed");
  fixtureRule(relativePath, "outbound fail-closed drifted",
    outbound?.input?.event === "outbound.networkFailure"
      && outbound?.expected?.outboundChannelState === "failed"
      && outbound?.expected?.outboundRoute === "mutedFailClosed"
      && outbound?.expected?.errorCategory === "network");
  const underrun = fixtureCase(fixture, "outbound underrun outputs zeros and forbids physical microphone");
  fixtureRule(relativePath, "outbound underrun safety drifted",
    underrun?.input?.event === "outbound.underrun"
      && underrun?.expected?.outboundRoute === "mutedFailClosed"
      && underrun?.expected?.outputSamples === "zeros"
      && underrun?.expected?.physicalMicrophone === "forbidden");
  const bypass = fixtureCase(fixture, "explicit outbound bypass routes original bypass");
  const persisted = fixtureCase(fixture, "explicit bypass persists through disconnect and reconnect");
  fixtureRule(relativePath, "explicit bypass drifted",
    bypass?.expected?.outboundRoute === "originalBypass"
      && persisted?.input?.initialOutboundRoute === "originalBypass"
      && sameArray(persisted?.input?.events, ["disconnect", "reconnect"])
      && persisted?.expected?.bypassPersisted === true);
  const stop = fixtureCase(fixture, "stop stops both routes");
  fixtureRule(relativePath, "stop route safety drifted",
    stop?.input?.event === "stop"
      && stop?.expected?.inboundChannelState === "inactive"
      && stop?.expected?.outboundChannelState === "inactive"
      && stop?.expected?.inboundRoute === "stopped"
      && stop?.expected?.outboundRoute === "stopped");
}

function validateCompatibilityFixture(fixture) {
  const relativePath = "Settings/compatibility-gate.json";
  const expected = new Map([
    ["exact versions", [true, "compatible", false]],
    ["compatible below recommended", [true, "compatibleUpdateRecommended", true]],
    ["missing driver", [false, "driverMissing", true]],
    ["invalid signature", [false, "driverSignatureInvalid", true]],
    ["abi mismatch", [false, "driverAbiMismatch", true]],
    ["one endpoint only", [false, "virtualEndpointsIncomplete", true]],
  ]);
  const stable = fixtureCases(fixture).every((testCase) => {
    const values = expected.get(testCase?.name);
    return values !== undefined
      && testCase?.expected?.allowed === values[0]
      && testCase?.expected?.reason === values[1]
      && testCase?.expected?.updateRecommended === values[2];
  });
  fixtureRule(relativePath, "compatibility reason/update matrix drifted", stable && fixtureCases(fixture).length === 6);
  const exact = fixtureCase(fixture, "exact versions");
  const recommended = fixtureCase(fixture, "compatible below recommended");
  fixtureRule(relativePath, "compatible version inputs drifted",
    exact?.installed?.present === true
      && exact?.installed?.signatureValid === true
      && exact?.installed?.abi === 1
      && exact?.installed?.version === "0.1.0"
      && exact?.installed?.endpointCount === 2
      && recommended?.manifestOverride?.recommendedDriverVersion === "0.2.0");
}

function validateMigrationFixture(fixture) {
  const relativePath = "Settings/v1-migration.json";
  const expectedDefaults = {
    schemaVersion: 1,
    baseUrl: "https://api.302.ai",
    modelId: "gpt-realtime-translate",
    nativeLanguage: "zh",
    meetingLanguage: "en",
    interfaceLanguage: "system",
    inputEndpointId: null,
    outputEndpointId: null,
  };
  const expected = new Map([
    ["empty object migrates to safe defaults", ["migrated", true, false]],
    ["schema version 1 is semantic identity", ["identity", false, false]],
    ["unknown future schema version is unsupported", ["unsupported", false, false]],
    ["malformed JSON is quarantined", ["quarantined", false, true]],
  ]);
  const stable = fixtureCases(fixture).every((testCase) => {
    const values = expected.get(testCase?.name);
    return values !== undefined
      && testCase?.expected?.outcome === values[0]
      && testCase?.expected?.overwrite === values[1]
      && testCase?.expected?.quarantine === values[2]
      && deepEqual(testCase?.expected?.resultSettings, expectedDefaults);
  });
  fixtureRule(relativePath, "migration outcome/default matrix drifted", stable && fixtureCases(fixture).length === 4);
  const empty = fixtureCase(fixture, "empty object migrates to safe defaults");
  const identity = fixtureCase(fixture, "schema version 1 is semantic identity");
  const future = fixtureCase(fixture, "unknown future schema version is unsupported");
  const malformed = fixtureCase(fixture, "malformed JSON is quarantined");
  fixtureRule(relativePath, "migration input matrix drifted",
    empty?.input?.kind === "object"
      && deepEqual(empty?.input?.settings, {})
      && identity?.input?.settings?.schemaVersion === 1
      && future?.input?.settings?.schemaVersion === 2
      && malformed?.input?.kind === "raw"
      && malformed?.input?.raw === "{\"schemaVersion\":");
}

function validateBatchingFixture(fixture) {
  const relativePath = "Audio/pcm-batching.json";
  fixtureRule(relativePath, "PCM batching metadata drifted",
    deepEqual(fixture?.metadata?.localNormalizedFormat, { sampleRateHz: 48000, channels: 2, sampleType: "float32" })
      && deepEqual(fixture?.metadata?.networkFormat, { sampleRateHz: 24000, channels: 1, sampleType: "pcm16", signed: true, byteOrder: "littleEndian" })
      && deepEqual(fixture?.metadata?.networkBatch, { byteCount: 9600, sampleCount: 4800, durationMs: 200 }));
  const exact = fixtureCase(fixture, "one exact network batch emits immediately");
  const halves = fixtureCase(fixture, "two half batches combine into one network batch");
  const odd = fixtureCase(fixture, "odd PCM16 append fails before buffering");
  const tail = fixtureCase(fixture, "incomplete even tail remains buffered");
  const large = fixtureCase(fixture, "append larger than one batch retains the exact tail");
  const flush = fixtureCase(fixture, "stop flush discards an incomplete tail");
  fixtureRule(relativePath, "exact PCM batch frames drifted",
    deepEqual(exact?.input?.appendByteCounts, [9600])
      && deepEqual(exact?.expected, { emittedFrameByteCounts: [9600], retainedByteCount: 0 })
      && deepEqual(halves?.input?.appendByteCounts, [4800, 4800])
      && deepEqual(halves?.expected, { emittedFrameByteCounts: [9600], retainedByteCount: 0 }));
  fixtureRule(relativePath, "PCM batching error/remainder drifted",
    deepEqual(odd?.input?.appendByteCounts, [9601])
      && deepEqual(odd?.expected, { errorCode: "invalidPCM16ByteCount", retainedByteCount: 0 })
      && deepEqual(tail?.expected, { emittedFrameByteCounts: [], retainedByteCount: 4000 })
      && deepEqual(large?.expected, { emittedFrameByteCounts: [9600], retainedByteCount: 2400 }));
  fixtureRule(relativePath, "PCM stop flush drifted",
    flush?.operation === "appendPCM16BytesThenStop"
      && deepEqual(flush?.input, { appendByteCounts: [2400], flushAction: "stop" })
      && deepEqual(flush?.expected, {
        emittedFrameByteCounts: [],
        retainedByteCountBeforeFlush: 2400,
        discardedByteCount: 2400,
        retainedByteCountAfterFlush: 0,
      }));
}

function validateConversionFixture(fixture) {
  const relativePath = "Audio/pcm-conversion.json";
  fixtureRule(relativePath, "PCM conversion metadata drifted",
    deepEqual(fixture?.metadata?.localNormalizedFormat, { sampleRateHz: 48000, channels: 2, sampleType: "float32" })
      && deepEqual(fixture?.metadata?.networkFormat, { sampleRateHz: 24000, channels: 1, sampleType: "pcm16", signed: true, byteOrder: "littleEndian" })
      && fixture?.metadata?.conversion?.decoderFIRTaps === 127
      && fixture?.metadata?.conversion?.maximumInputSamplesPerVector === 256
      && fixture?.metadata?.conversion?.numericArrays === "decimalOnly");
  const clamp = fixtureCase(fixture, "encoder clamps Float32 endpoints exactly");
  fixtureRule(relativePath, "PCM clamp vector drifted",
    deepEqual(clamp?.expected?.pcm16SignedSamples, [-32768, 0, 32767])
      && deepEqual(clamp?.expected?.pcm16LittleEndianBytes, [0, 128, 0, 0, 255, 127])
      && clamp?.assertion === "exact"
      && clamp?.tolerance === 0);
  const downmix = fixtureCase(fixture, "encoder downmixes stereo before averaging two frames");
  fixtureRule(relativePath, "PCM downmix vector drifted",
    deepEqual(downmix?.expected?.downmixedMonoFrames, [0, 0.5])
      && deepEqual(downmix?.expected?.averagedMonoFrames, [0.25])
      && deepEqual(downmix?.expected?.pcm16SignedSamples, [8192])
      && deepEqual(downmix?.expected?.pcm16LittleEndianBytes, [0, 32]));
  const littleEndian = fixtureCase(fixture, "encoder packs signed PCM16 in little endian byte order");
  fixtureRule(relativePath, "PCM little-endian vector drifted",
    deepEqual(littleEndian?.expected?.pcm16SignedSamples, [32767, -32768])
      && deepEqual(littleEndian?.expected?.pcm16LittleEndianBytes, [255, 127, 0, 128]));
  const decode = fixtureCase(fixture, "decoder duplicates each interpolated sample to left and right");
  const odd = fixtureCase(fixture, "decoder rejects an odd PCM16 byte count");
  fixtureRule(relativePath, "PCM decode/error vectors drifted",
    decode?.expected?.outputFramesPerInputSample === 2
      && decode?.expected?.outputSampleCount === 8
      && decode?.expected?.channelPairEquality === true
      && decode?.assertion === "frameCountAndChannelPairs"
      && odd?.expected?.errorCode === "misalignedPCM16"
      && odd?.assertion === "errorCode");
  const chunked = fixtureCase(fixture, "chunked FIR decode matches contiguous decode across aligned chunks");
  fixtureRule(relativePath, "PCM chunk equivalence drifted",
    deepEqual(chunked?.input?.alignedChunkByteCounts, [2, 4])
      && chunked?.expected?.contiguousAndChunkedOutputEqual === true
      && chunked?.expected?.outputFramesPerInputSample === 2
      && chunked?.assertion === "absoluteDifferenceAtMostTolerance"
      && chunked?.tolerance === 0.000001);

  const lifecycle = fixtureCase(fixture, "decoder FIR history resets only after explicit reset or stop");
  const expectedRuns = [
    {
      runId: "fresh",
      initialOwnerState: "startedWithNewInternalDecoder",
      steps: [{ action: "decode", inputRef: "probePCM16LittleEndianBytes", resultId: "freshProbe" }],
    },
    {
      runId: "warmedWithoutReset",
      initialOwnerState: "startedWithNewInternalDecoder",
      steps: [
        { action: "decode", inputRef: "warmupPCM16LittleEndianBytes", resultId: "warmedWarmup", discardResult: true },
        { action: "decode", inputRef: "probePCM16LittleEndianBytes", resultId: "warmedProbe" },
      ],
    },
    {
      runId: "afterOwnerReplacement",
      initialOwnerState: "startedWithNewInternalDecoder",
      steps: [
        { action: "decode", inputRef: "warmupPCM16LittleEndianBytes", resultId: "replacementWarmup", discardResult: true },
        { action: "replaceDecoder" },
        { action: "decode", inputRef: "probePCM16LittleEndianBytes", resultId: "replacementProbe" },
      ],
    },
    {
      runId: "afterStopRestart",
      initialOwnerState: "startedWithNewInternalDecoder",
      steps: [
        { action: "decode", inputRef: "warmupPCM16LittleEndianBytes", resultId: "stopRestartWarmup", discardResult: true },
        { action: "stop" },
        { action: "start" },
        { action: "decode", inputRef: "probePCM16LittleEndianBytes", resultId: "stopRestartProbe" },
      ],
    },
  ];
  const expectedComparisons = [
    { leftResultId: "warmedProbe", operator: "notEquals", rightResultId: "freshProbe", tolerance: 0 },
    { leftResultId: "replacementProbe", operator: "equals", rightResultId: "freshProbe", tolerance: 0 },
    { leftResultId: "stopRestartProbe", operator: "equals", rightResultId: "freshProbe", tolerance: 0 },
  ];
  fixtureRule(relativePath, "PCM lifecycle runs drifted",
    lifecycle?.operation === "decode24kMonoPCM16To48kStereoFloat32WithExplicitLifecycle"
      && lifecycle?.ownerDomain?.domain === "platformAdapterLifecycle"
      && deepEqual(lifecycle?.runs, expectedRuns));
  fixtureRule(relativePath, "PCM lifecycle comparisons drifted",
    deepEqual(lifecycle?.comparisons, expectedComparisons)
      && lifecycle?.assertion === "exactSequenceEquivalence"
      && lifecycle?.tolerance === 0);
  const inputRefs = new Set(Object.keys(lifecycle?.input ?? {}));
  const resultIds = new Set();
  let referencesValid = true;
  for (const run of lifecycle?.runs ?? []) {
    for (const step of run?.steps ?? []) {
      if (step?.action === "decode") {
        if (!inputRefs.has(step.inputRef) || typeof step.resultId !== "string" || resultIds.has(step.resultId)) referencesValid = false;
        resultIds.add(step.resultId);
      }
    }
  }
  for (const comparison of lifecycle?.comparisons ?? []) {
    if (!resultIds.has(comparison?.leftResultId) || !resultIds.has(comparison?.rightResultId)) referencesValid = false;
  }
  fixtureRule(relativePath, "PCM lifecycle references drifted", referencesValid);
}

function validateAppVisibleFixtureValues(value, relativePath, enumSets) {
  if (Array.isArray(value)) {
    for (const item of value) validateAppVisibleFixtureValues(item, relativePath, enumSets);
    return;
  }
  if (!isObject(value)) return;
  const fieldToEnum = {
    inboundChannelState: "channelState",
    outboundChannelState: "channelState",
    inboundRoute: "inboundRoute",
    outboundRoute: "outboundRoute",
    errorCategory: "errorCategory",
  };
  for (const [key, nestedValue] of Object.entries(value)) {
    const enumName = fieldToEnum[key];
    if (enumName !== undefined && !enumSets[enumName]?.has(nestedValue)) {
      fixtureRule(relativePath, `app-state ${key} value outside schema`, false);
    }
    validateAppVisibleFixtureValues(nestedValue, relativePath, enumSets);
  }
}

const manifest = readObjectJson("Shared/Contracts/contract-manifest.json");
const fixtureManifest = readObjectJson("Shared/TestVectors/fixture-manifest.json");

if (manifest !== undefined) {
  if (manifest.contractVersion !== 1 || manifest.status !== "frozen") fail("Shared/Contracts/contract-manifest.json: must freeze version 1");
  if (manifest.fixtureManifest !== "../TestVectors/fixture-manifest.json") fail("Shared/Contracts/contract-manifest.json: must reference the canonical fixture manifest");
}
if (fixtureManifest !== undefined && manifest !== undefined && fixtureManifest.contractVersion !== manifest.contractVersion) {
  fail("Shared/TestVectors/fixture-manifest.json: contract version differs from contract manifest");
}

const schemaPaths = validateInventory("schema", manifest?.schemas, contractsDirectory, "contract-manifest.json", "Shared/Contracts");
const fixturePaths = validateInventory("fixture", fixtureManifest?.fixtures, fixturesDirectory, "fixture-manifest.json", "Shared/TestVectors");

const schemaDocuments = new Map();
for (const relativePath of schemaPaths) {
  const contractPath = `Shared/Contracts/${relativePath}`;
  const schema = readObjectJson(contractPath);
  if (schema === undefined) continue;
  schemaDocuments.set(relativePath, schema);
  if (schema.$schema !== "https://json-schema.org/draft/2020-12/schema") fail(`${contractPath}: wrong JSON Schema dialect`);
  if (!hasClosedRoot(schema)) fail(`${contractPath}: root must be closed or use closed oneOf variants`);
  if (typeof schema.$id !== "string" || schema.$id.length === 0 || typeof schema.title !== "string" || schema.title.length === 0) {
    fail(`${contractPath}: missing stable schema values`);
  }
  const expectedSchema = expectedSchemas.get(relativePath);
  if (!expectedSchema || expectedSchema.id !== schema.$id || expectedSchema.title !== schema.title) {
    fail(`${contractPath}: unexpected stable schema values`);
  }

  if (relativePath === "v1/translation-events.schema.json") {
    const eventTypes = Array.isArray(schema.oneOf)
      ? schema.oneOf.map((variant) => variant?.properties?.type?.const)
      : undefined;
    if (!sameArray(eventTypes, expectedTranslationTypes)) fail(`${contractPath}: translation event types drifted`);
  }
  if (relativePath === "v1/app-state.schema.json") {
    const enumValues = Object.fromEntries(
      Object.keys(expectedAppStateEnums).map((name) => [name, schema.$defs?.[name]?.enum]),
    );
    if (!Object.entries(expectedAppStateEnums).every(([name, expected]) => sameArray(enumValues[name], expected))) {
      fail(`${contractPath}: app-state enum values drifted`);
    }
  }
  if (relativePath === "v1/compatibility.schema.json") {
    if (!sameArray(schema.properties?.channel?.enum, ["internal", "beta", "stable"])) {
      fail(`${contractPath}: compatibility channel values drifted`);
    }
    if (schema.properties?.contractVersion?.const !== 1) {
      fail(`${contractPath}: compatibility contractVersion must be const 1`);
    }
  }
}
for (const relativePath of expectedSchemas.keys()) {
  if (!schemaPaths.includes(relativePath)) fail(`Shared/Contracts/contract-manifest.json: missing stable schema inventory entry`);
}

if (!sameArray(fixtureManifest?.fixtures, expectedFixturePaths)) {
  fail("Shared/TestVectors/fixture-manifest.json: exact v1 fixture inventory drifted");
}

const fixtureIds = new Set();
const fixtureDocuments = new Map();
for (const relativePath of fixturePaths) {
  const contractPath = `Shared/TestVectors/${relativePath}`;
  const fixture = readObjectJson(contractPath);
  if (fixture === undefined) continue;
  fixtureDocuments.set(relativePath, fixture);
  if (fixture.contractVersion !== manifest?.contractVersion) fail(`${contractPath}: contractVersion mismatch`);
  if (typeof fixture.fixtureId !== "string" || fixture.fixtureId.length === 0) {
    fail(`${contractPath}: missing fixtureId`);
  } else if (fixtureIds.has(fixture.fixtureId)) {
    fail(`${contractPath}: duplicate fixtureId`);
  } else {
    fixtureIds.add(fixture.fixtureId);
  }
  if (typeof fixture.category !== "string" || fixture.category.length === 0) fail(`${contractPath}: missing category`);
  validateCaseInventory(relativePath, fixture);
}

const appStateSchema = schemaDocuments.get("v1/app-state.schema.json");
const enumSets = Object.fromEntries(
  ["channelState", "inboundRoute", "outboundRoute", "errorCategory"].map((name) => [
    name,
    new Set(appStateSchema?.$defs?.[name]?.enum ?? []),
  ]),
);
for (const [relativePath, fixture] of fixtureDocuments) {
  validateAppVisibleFixtureValues(fixture, relativePath, enumSets);
}

validateHandshakeFixture(
  fixtureDocuments.get("Realtime/text-frame-handshake.json"),
  schemaDocuments.get("v1/translation-events.schema.json"),
);
validateCloseFixture(fixtureDocuments.get("Realtime/close-deadline.json"));
validateGateFixture(fixtureDocuments.get("Routing/inbound-language-gate.json"));
validateSafetyFixture(fixtureDocuments.get("Routing/channel-failure-safety.json"));
validateCompatibilityFixture(fixtureDocuments.get("Settings/compatibility-gate.json"));
validateMigrationFixture(fixtureDocuments.get("Settings/v1-migration.json"));
validateBatchingFixture(fixtureDocuments.get("Audio/pcm-batching.json"));
validateConversionFixture(fixtureDocuments.get("Audio/pcm-conversion.json"));

inspectSharedContent();

const outputFailures = [...new Set(failures)].sort();
if (outputFailures.length > 0) {
  process.stderr.write(`${outputFailures.join("\n")}\n`);
  process.exit(1);
}
process.stdout.write(`contract v${manifest.contractVersion}: ${schemaPaths.length} schemas, ${fixturePaths.length} fixtures\n`);
