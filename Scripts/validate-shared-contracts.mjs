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
  "session.input_audio_buffer.append",
  "session.close",
  "session.created",
  "session.updated",
  "session.output_audio.delta",
  "session.input_transcript.delta",
  "session.output_transcript.delta",
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
const expectedAuxiliaryFixturePaths = [
  "Routing/LanguageCorpus/language-corpus-v1.json",
];

const expectedFixtureCaseJson = new Map([
  ["Realtime/text-frame-handshake.json", String.raw`[{"name":"normal handshake sends session update as text and connects","configuration":{"nativeLanguage":"zh","meetingLanguage":"en"},"expected":{"inboundSocketCount":1,"outboundSocketCount":0,"inboundChannelState":"connected","inboundRoute":"translated"},"steps":[{"direction":"serverToClient","frameType":"text","eventType":"session.created","payloadEncoding":"json","payload":{"type":"session.created"},"expectedState":"created"},{"direction":"clientToServer","frameType":"text","eventType":"session.update","payloadEncoding":"json","payload":{"type":"session.update","session":{"audio":{"output":{"language":"zh"}}}},"expectedState":"updating"},{"direction":"serverToClient","frameType":"text","eventType":"session.updated","payloadEncoding":"json","payload":{"type":"session.updated"},"expectedState":"connected"}]},{"name":"client JSON session update sent as binary is protocol failure","configuration":{"nativeLanguage":"zh","meetingLanguage":"en"},"expected":{"inboundSocketCount":1,"outboundSocketCount":0,"inboundChannelState":"failed","inboundRoute":"originalFailOpen","errorCategory":"protocol"},"steps":[{"direction":"serverToClient","frameType":"text","eventType":"session.created","payloadEncoding":"json","payload":{"type":"session.created"},"expectedState":"created"},{"direction":"clientToServer","frameType":"binary","eventType":"session.update","payloadEncoding":"json","payload":{"type":"session.update","session":{"audio":{"output":{"language":"zh"}}}},"expectedState":"protocolFailure"}]},{"name":"session updated before session created is protocol failure","configuration":{"nativeLanguage":"zh","meetingLanguage":"en"},"expected":{"inboundSocketCount":1,"outboundSocketCount":0,"inboundChannelState":"failed","inboundRoute":"originalFailOpen","errorCategory":"protocol"},"steps":[{"direction":"serverToClient","frameType":"text","eventType":"session.updated","payloadEncoding":"json","payload":{"type":"session.updated"},"expectedState":"protocolFailure"}]},{"name":"same language uses local bypass with no outbound socket","configuration":{"nativeLanguage":"zh","meetingLanguage":"zh"},"expected":{"inboundSocketCount":1,"outboundSocketCount":0,"inboundChannelState":"connected","inboundRoute":"translated","outboundChannelState":"bypassed","outboundRoute":"originalBypass"},"steps":[{"direction":"local","eventType":"language.match","localInput":{"nativeLanguage":"zh","meetingLanguage":"zh"},"expectedState":"localBypass"}]},{"name":"two language setup creates two independent sockets","configuration":{"nativeLanguage":"zh","meetingLanguage":"en"},"expected":{"inboundSocketCount":1,"outboundSocketCount":1,"inboundChannelState":"connected","outboundChannelState":"connected","inboundRoute":"translated","outboundRoute":"translated"},"sockets":[{"socketId":"inbound","steps":[{"direction":"serverToClient","frameType":"text","eventType":"session.created","payloadEncoding":"json","payload":{"type":"session.created"},"expectedState":"created"},{"direction":"clientToServer","frameType":"text","eventType":"session.update","payloadEncoding":"json","payload":{"type":"session.update","session":{"audio":{"output":{"language":"zh"}}}},"expectedState":"updating"},{"direction":"serverToClient","frameType":"text","eventType":"session.updated","payloadEncoding":"json","payload":{"type":"session.updated"},"expectedState":"connected"}]},{"socketId":"outbound","steps":[{"direction":"serverToClient","frameType":"text","eventType":"session.created","payloadEncoding":"json","payload":{"type":"session.created"},"expectedState":"created"},{"direction":"clientToServer","frameType":"text","eventType":"session.update","payloadEncoding":"json","payload":{"type":"session.update","session":{"audio":{"output":{"language":"en"}}}},"expectedState":"updating"},{"direction":"serverToClient","frameType":"text","eventType":"session.updated","payloadEncoding":"json","payload":{"type":"session.updated"},"expectedState":"connected"}]}]}]`],
  ["Realtime/close-deadline.json", String.raw`[{"name":"close deadline starts before close send","input":{"generation":1,"deadlineMs":1000,"startDeadlineBeforeCloseSend":true,"closeSend":"blocked"},"expected":{"completion":"closeTimeout","completionAtMs":1000,"deadlineStartsAtMs":0}},{"name":"inbound and outbound close run concurrently","input":{"generation":1,"deadlineMs":1000,"startDeadlineBeforeCloseSend":true,"closeRequests":["inbound","outbound"]},"expected":{"concurrent":true,"completion":"closed","completionAtMs":400,"routeCompletions":{"inbound":300,"outbound":400}}},{"name":"session closed within 1000 ms delivers queued tail audio","input":{"generation":1,"deadlineMs":1000,"startDeadlineBeforeCloseSend":true,"sessionClosedAtMs":999,"queuedTailAudio":true},"expected":{"completion":"closed","completionAtMs":999,"tailState":"draining"}},{"name":"blocked close send reaches local close timeout at 1000 ms","input":{"generation":1,"deadlineMs":1000,"startDeadlineBeforeCloseSend":true,"closeSend":"blocked"},"expected":{"completion":"closeTimeout","completionAtMs":1000,"localCompletion":true}},{"name":"two close callers await the same completion","input":{"generation":1,"deadlineMs":1000,"startDeadlineBeforeCloseSend":true,"closeCallerCount":2,"sessionClosedAtMs":200},"expected":{"completion":"closed","completionAtMs":200,"sameCompletion":true,"completionCount":1}},{"name":"old generation close completion cannot clear new generation","input":{"closingGeneration":1,"activeGeneration":2,"deadlineMs":1000,"startDeadlineBeforeCloseSend":true,"oldGenerationCompletionAtMs":300},"expected":{"completion":"closed","completionGeneration":1,"activeGenerationAfterCompletion":2,"clearActiveGeneration":false}}]`],
  ["Routing/inbound-language-gate.json", String.raw`[{"name":"bcp 47 Chinese confidence aggregates to native original","input":{"nativeLanguage":"zh","confidenceByTag":{"zh-Hans":0.45,"zh-Hant":0.4},"threshold":0.75},"expected":{"aggregatedConfidenceByLanguage":{"zh":0.85},"gateDecision":"original","tailState":"none","nextUtterancePolicy":"languageGate"}},{"name":"non native confidence 0.60 routes translated","input":{"nativeLanguage":"zh","confidenceByTag":{"en":0.6},"threshold":0.6},"expected":{"aggregatedConfidenceByLanguage":{"en":0.6},"gateDecision":"translated","tailState":"none","nextUtterancePolicy":"languageGate"}},{"name":"native confidence 0.75 routes original","input":{"nativeLanguage":"zh","confidenceByTag":{"zh":0.75},"threshold":0.75},"expected":{"aggregatedConfidenceByLanguage":{"zh":0.75},"gateDecision":"original","tailState":"none","nextUtterancePolicy":"languageGate"}},{"name":"voiced undecided at 250 ms routes translated","input":{"nativeLanguage":"zh","voiced":true,"decisionAtMs":250,"deadlineMs":250},"expected":{"gateDecision":"translated","tailState":"none","nextUtterancePolicy":"languageGate"}},{"name":"unvoiced undecided at 250 ms routes original","input":{"nativeLanguage":"zh","voiced":false,"decisionAtMs":250,"deadlineMs":250},"expected":{"gateDecision":"original","tailState":"none","nextUtterancePolicy":"languageGate"}},{"name":"vad end waits 500 ms for late input","input":{"event":"vad.end","deadlineMs":250,"restartMs":500},"expected":{"gateDecision":"undecided","tailState":"waiting","nextUtterancePolicy":"languageGate","waitForLateInputMs":500}},{"name":"late audio at 450 ms restarts 500 ms window","input":{"event":"late.audio","arrivalAfterVadEndMs":450,"restartMs":500},"expected":{"gateDecision":"undecided","tailState":"waiting","nextUtterancePolicy":"languageGate","restartWindowMs":500}},{"name":"late transcript at 450 ms restarts 500 ms window","input":{"event":"late.transcript","arrivalAfterVadEndMs":450,"restartMs":500},"expected":{"gateDecision":"undecided","tailState":"waiting","nextUtterancePolicy":"languageGate","restartWindowMs":500}},{"name":"recovery during utterance remains original fail open until next utterance","input":{"inboundRoute":"originalFailOpen","recoveryEvent":"connected"},"expected":{"inboundRoute":"originalFailOpen","gateDecision":"original","tailState":"draining","nextUtterancePolicy":"languageGate"}}]`],
  ["Routing/channel-failure-safety.json", String.raw`[{"name":"inbound network failure routes original fail open","input":{"event":"inbound.networkFailure"},"expected":{"inboundChannelState":"failed","inboundRoute":"originalFailOpen","errorCategory":"network"}},{"name":"outbound network failure routes muted fail closed","input":{"event":"outbound.networkFailure"},"expected":{"outboundChannelState":"failed","outboundRoute":"mutedFailClosed","errorCategory":"network"}},{"name":"outbound underrun outputs zeros and forbids physical microphone","input":{"event":"outbound.underrun"},"expected":{"outboundChannelState":"degraded","outboundRoute":"mutedFailClosed","errorCategory":"backpressure","outputSamples":"zeros","physicalMicrophone":"forbidden"}},{"name":"explicit outbound bypass routes original bypass","input":{"event":"outbound.bypassEnabled"},"expected":{"outboundChannelState":"bypassed","outboundRoute":"originalBypass"}},{"name":"explicit bypass persists through disconnect and reconnect","input":{"initialOutboundRoute":"originalBypass","events":["disconnect","reconnect"]},"expected":{"outboundChannelState":"bypassed","outboundRoute":"originalBypass","bypassPersisted":true}},{"name":"stop stops both routes","input":{"event":"stop"},"expected":{"inboundChannelState":"inactive","outboundChannelState":"inactive","inboundRoute":"stopped","outboundRoute":"stopped"}}]`],
  ["Audio/pcm-batching.json", String.raw`[{"name":"one exact network batch emits immediately","operation":"appendPCM16Bytes","input":{"appendByteCounts":[9600]},"expected":{"emittedFrameByteCounts":[9600],"retainedByteCount":0}},{"name":"two half batches combine into one network batch","operation":"appendPCM16Bytes","input":{"appendByteCounts":[4800,4800]},"expected":{"emittedFrameByteCounts":[9600],"retainedByteCount":0}},{"name":"odd PCM16 append fails before buffering","operation":"appendPCM16Bytes","input":{"appendByteCounts":[9601]},"expected":{"errorCode":"invalidPCM16ByteCount","retainedByteCount":0}},{"name":"incomplete even tail remains buffered","operation":"appendPCM16Bytes","input":{"appendByteCounts":[2000,2000]},"expected":{"emittedFrameByteCounts":[],"retainedByteCount":4000}},{"name":"append larger than one batch retains the exact tail","operation":"appendPCM16Bytes","input":{"appendByteCounts":[12000]},"expected":{"emittedFrameByteCounts":[9600],"retainedByteCount":2400}},{"name":"stop flush discards an incomplete tail","operation":"appendPCM16BytesThenStop","input":{"appendByteCounts":[2400],"flushAction":"stop"},"expected":{"emittedFrameByteCounts":[],"retainedByteCountBeforeFlush":2400,"discardedByteCount":2400,"retainedByteCountAfterFlush":0}}]`],
  ["Audio/pcm-conversion.json", String.raw`[{"name":"encoder clamps Float32 endpoints exactly","operation":"encode48kStereoFloat32To24kMonoPCM16","input":{"interleavedStereoFloat32":[-1.5,-1.5,-1.5,-1.5,0.0,0.0,0.0,0.0,1.5,1.5,1.5,1.5]},"expected":{"pcm16SignedSamples":[-32768,0,32767],"pcm16LittleEndianBytes":[0,128,0,0,255,127]},"assertion":"exact","tolerance":0.0},{"name":"encoder downmixes stereo before averaging two frames","operation":"encode48kStereoFloat32To24kMonoPCM16","input":{"interleavedStereoFloat32":[1.0,-1.0,0.5,0.5]},"expected":{"downmixedMonoFrames":[0.0,0.5],"averagedMonoFrames":[0.25],"pcm16SignedSamples":[8192],"pcm16LittleEndianBytes":[0,32]},"assertion":"exact","tolerance":0.0},{"name":"encoder packs signed PCM16 in little endian byte order","operation":"encode48kStereoFloat32To24kMonoPCM16","input":{"interleavedStereoFloat32":[1.0,1.0,1.0,1.0,-1.0,-1.0,-1.0,-1.0]},"expected":{"pcm16SignedSamples":[32767,-32768],"pcm16LittleEndianBytes":[255,127,0,128]},"assertion":"exact","tolerance":0.0},{"name":"decoder duplicates each interpolated sample to left and right","operation":"decode24kMonoPCM16To48kStereoFloat32","input":{"pcm16LittleEndianBytes":[0,0,255,127]},"expected":{"outputFramesPerInputSample":2,"outputSampleCount":8,"channelPairEquality":true},"assertion":"frameCountAndChannelPairs","tolerance":0.0},{"name":"decoder rejects an odd PCM16 byte count","operation":"decode24kMonoPCM16To48kStereoFloat32","input":{"pcm16LittleEndianBytes":[0]},"expected":{"errorCode":"misalignedPCM16"},"assertion":"errorCode","tolerance":0.0},{"name":"chunked FIR decode matches contiguous decode across aligned chunks","operation":"decode24kMonoPCM16To48kStereoFloat32","input":{"pcm16LittleEndianBytes":[0,32,0,64,0,96],"alignedChunkByteCounts":[2,4]},"expected":{"contiguousAndChunkedOutputEqual":true,"outputFramesPerInputSample":2},"assertion":"absoluteDifferenceAtMostTolerance","tolerance":0.000001},{"name":"decoder FIR history resets only after explicit reset or stop","operation":"decode24kMonoPCM16To48kStereoFloat32WithExplicitLifecycle","ownerDomain":{"domain":"platformAdapterLifecycle","owner":"NetworkPCMDecoderAdapterOwner","internalDecoder":"NetworkPCMDecoder","lifecycleSemantics":"Owner-level replaceDecoder and stop followed by start create a new internal decoder; this contract does not require a public decoder reset API."},"actionVocabulary":{"decode":{"domain":"platformAdapterLifecycle","requires":"startedInternalDecoder","inputRef":"one named PCM16 input","resultId":"unique per decode action"},"replaceDecoder":{"domain":"platformAdapterLifecycle","effect":"replace the internal decoder with a new decoder"},"stop":{"domain":"platformAdapterLifecycle","effect":"discard the internal decoder and enter stopped state"},"start":{"domain":"platformAdapterLifecycle","effect":"create a new internal decoder and enter started state"}},"input":{"warmupPCM16LittleEndianBytes":[255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127,255,127],"probePCM16LittleEndianBytes":[0,0]},"runs":[{"runId":"fresh","initialOwnerState":"startedWithNewInternalDecoder","steps":[{"action":"decode","inputRef":"probePCM16LittleEndianBytes","resultId":"freshProbe"}]},{"runId":"warmedWithoutReset","initialOwnerState":"startedWithNewInternalDecoder","steps":[{"action":"decode","inputRef":"warmupPCM16LittleEndianBytes","resultId":"warmedWarmup","discardResult":true},{"action":"decode","inputRef":"probePCM16LittleEndianBytes","resultId":"warmedProbe"}]},{"runId":"afterOwnerReplacement","initialOwnerState":"startedWithNewInternalDecoder","steps":[{"action":"decode","inputRef":"warmupPCM16LittleEndianBytes","resultId":"replacementWarmup","discardResult":true},{"action":"replaceDecoder"},{"action":"decode","inputRef":"probePCM16LittleEndianBytes","resultId":"replacementProbe"}]},{"runId":"afterStopRestart","initialOwnerState":"startedWithNewInternalDecoder","steps":[{"action":"decode","inputRef":"warmupPCM16LittleEndianBytes","resultId":"stopRestartWarmup","discardResult":true},{"action":"stop"},{"action":"start"},{"action":"decode","inputRef":"probePCM16LittleEndianBytes","resultId":"stopRestartProbe"}]}],"comparisons":[{"leftResultId":"warmedProbe","operator":"notEquals","rightResultId":"freshProbe","tolerance":0.0},{"leftResultId":"replacementProbe","operator":"equals","rightResultId":"freshProbe","tolerance":0.0},{"leftResultId":"stopRestartProbe","operator":"equals","rightResultId":"freshProbe","tolerance":0.0}],"assertion":"exactSequenceEquivalence","tolerance":0.0}]`],
  ["Settings/v1-migration.json", String.raw`[{"name":"empty object migrates to safe defaults","input":{"kind":"object","settings":{}},"expected":{"outcome":"migrated","overwrite":true,"quarantine":false,"resultSettings":{"schemaVersion":1,"baseUrl":"https://api.302.ai","modelId":"gpt-realtime-translate","nativeLanguage":"zh","meetingLanguage":"en","interfaceLanguage":"system","inputEndpointId":null,"outputEndpointId":null}}},{"name":"schema version 1 is semantic identity","input":{"kind":"object","settings":{"schemaVersion":1,"baseUrl":"https://api.302.ai","modelId":"gpt-realtime-translate","nativeLanguage":"zh","meetingLanguage":"en","interfaceLanguage":"system","inputEndpointId":null,"outputEndpointId":null}},"expected":{"outcome":"identity","overwrite":false,"quarantine":false,"resultSettings":{"schemaVersion":1,"baseUrl":"https://api.302.ai","modelId":"gpt-realtime-translate","nativeLanguage":"zh","meetingLanguage":"en","interfaceLanguage":"system","inputEndpointId":null,"outputEndpointId":null}}},{"name":"unknown future schema version is unsupported","input":{"kind":"object","settings":{"schemaVersion":2}},"expected":{"outcome":"unsupported","overwrite":false,"quarantine":false,"resultSettings":{"schemaVersion":1,"baseUrl":"https://api.302.ai","modelId":"gpt-realtime-translate","nativeLanguage":"zh","meetingLanguage":"en","interfaceLanguage":"system","inputEndpointId":null,"outputEndpointId":null}}},{"name":"malformed JSON is quarantined","input":{"kind":"raw","raw":"{\"schemaVersion\":"},"expected":{"outcome":"quarantined","overwrite":false,"quarantine":true,"resultSettings":{"schemaVersion":1,"baseUrl":"https://api.302.ai","modelId":"gpt-realtime-translate","nativeLanguage":"zh","meetingLanguage":"en","interfaceLanguage":"system","inputEndpointId":null,"outputEndpointId":null}}}]`],
  ["Settings/compatibility-gate.json", String.raw`[{"name":"exact versions","installed":{"present":true,"signatureValid":true,"abi":1,"version":"0.1.0","endpointCount":2},"expected":{"allowed":true,"reason":"compatible","updateRecommended":false}},{"name":"compatible below recommended","installed":{"present":true,"signatureValid":true,"abi":1,"version":"0.1.0","endpointCount":2},"manifestOverride":{"recommendedDriverVersion":"0.2.0"},"expected":{"allowed":true,"reason":"compatibleUpdateRecommended","updateRecommended":true}},{"name":"missing driver","installed":{"present":false,"signatureValid":false,"abi":0,"version":"0.0.0","endpointCount":0},"expected":{"allowed":false,"reason":"driverMissing","updateRecommended":true}},{"name":"invalid signature","installed":{"present":true,"signatureValid":false,"abi":1,"version":"0.1.0","endpointCount":2},"expected":{"allowed":false,"reason":"driverSignatureInvalid","updateRecommended":true}},{"name":"abi mismatch","installed":{"present":true,"signatureValid":true,"abi":2,"version":"0.2.0","endpointCount":2},"expected":{"allowed":false,"reason":"driverAbiMismatch","updateRecommended":true}},{"name":"one endpoint only","installed":{"present":true,"signatureValid":true,"abi":1,"version":"0.1.0","endpointCount":1},"expected":{"allowed":false,"reason":"virtualEndpointsIncomplete","updateRecommended":true}}]`],
]);

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

function collectJsonPaths(directory, excludedFiles, label) {
  try {
    return fs.readdirSync(directory, { recursive: true, withFileTypes: true })
      .filter((entry) => entry.name.endsWith(".json"))
      .map((entry) => path.relative(directory, path.join(entry.parentPath, entry.name)).split(path.sep).join("/"))
      .filter((relativePath) => !excludedFiles.includes(relativePath))
      .sort();
  } catch {
    fail(`${label}: inventory unreadable`);
    return [];
  }
}

function validateInventory(name, listedPaths, directory, excludedFiles, relativeDirectory) {
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
  const actual = collectJsonPaths(directory, excludedFiles, `${relativeDirectory}`);
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

function jsonTypeMatches(value, expectedType) {
  switch (expectedType) {
    case "object":
      return isObject(value);
    case "array":
      return Array.isArray(value);
    case "string":
      return typeof value === "string";
    case "number":
      return typeof value === "number" && Number.isFinite(value);
    case "integer":
      return typeof value === "number" && Number.isInteger(value);
    case "boolean":
      return typeof value === "boolean";
    case "null":
      return value === null;
    default:
      return false;
  }
}

function resolveLocalReference(rootSchema, reference) {
  if (typeof reference !== "string" || !reference.startsWith("#/")) return undefined;
  let current = rootSchema;
  for (const encodedSegment of reference.slice(2).split("/")) {
    const segment = encodedSegment.replaceAll("~1", "/").replaceAll("~0", "~");
    if (!isObject(current) || !Object.hasOwn(current, segment)) return undefined;
    current = current[segment];
  }
  return isObject(current) ? current : undefined;
}

function matchesJsonSchema(value, schema, rootSchema = schema) {
  if (!isObject(schema)) return false;

  if (schema.$ref !== undefined) {
    const referencedSchema = resolveLocalReference(rootSchema, schema.$ref);
    if (referencedSchema === undefined || !matchesJsonSchema(value, referencedSchema, rootSchema)) return false;
  }

  if (schema.oneOf !== undefined) {
    if (!Array.isArray(schema.oneOf)) return false;
    const matchCount = schema.oneOf.filter((variant) =>
      isObject(variant) && matchesJsonSchema(value, variant, rootSchema)).length;
    if (matchCount !== 1) return false;
  }

  if (schema.type !== undefined) {
    const acceptedTypes = Array.isArray(schema.type) ? schema.type : [schema.type];
    if (!acceptedTypes.every((type) => typeof type === "string")
      || !acceptedTypes.some((type) => jsonTypeMatches(value, type))) return false;
  }
  if (schema.const !== undefined && !deepEqual(value, schema.const)) return false;
  if (schema.enum !== undefined) {
    if (!Array.isArray(schema.enum) || !schema.enum.some((candidate) => deepEqual(value, candidate))) return false;
  }

  if (schema.minimum !== undefined) {
    if (typeof value !== "number" || typeof schema.minimum !== "number" || value < schema.minimum) return false;
  }
  if (schema.maximum !== undefined) {
    if (typeof value !== "number" || typeof schema.maximum !== "number" || value > schema.maximum) return false;
  }
  if (schema.pattern !== undefined) {
    if (typeof value !== "string" || typeof schema.pattern !== "string") return false;
    try {
      if (!new RegExp(schema.pattern).test(value)) return false;
    } catch {
      return false;
    }
  }

  if (schema.required !== undefined) {
    if (!isObject(value) || !Array.isArray(schema.required)
      || !schema.required.every((key) => typeof key === "string" && Object.hasOwn(value, key))) return false;
  }

  if (schema.properties !== undefined || schema.additionalProperties !== undefined) {
    if (!isObject(value)) return false;
    const properties = schema.properties ?? {};
    if (!isObject(properties)) return false;
    for (const [key, propertySchema] of Object.entries(properties)) {
      if (Object.hasOwn(value, key)
        && (!isObject(propertySchema) || !matchesJsonSchema(value[key], propertySchema, rootSchema))) return false;
    }
    for (const [key, propertyValue] of Object.entries(value)) {
      if (Object.hasOwn(properties, key)) continue;
      if (schema.additionalProperties === false) return false;
      if (isObject(schema.additionalProperties)
        && !matchesJsonSchema(propertyValue, schema.additionalProperties, rootSchema)) return false;
    }
  }

  return true;
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

function validateVocabularyOccurrences(owner, relativePath) {
  if (Array.isArray(owner)) {
    for (const item of owner) validateVocabularyOccurrences(item, relativePath);
    return;
  }
  if (!isObject(owner)) return;

  for (const [key, vocabulary] of Object.entries(owner)) {
    if (!key.endsWith("Vocabulary")) continue;
    const field = key.slice(0, -"Vocabulary".length);
    const allowed = Array.isArray(vocabulary)
      ? vocabulary
      : isObject(vocabulary)
        ? Object.keys(vocabulary)
        : [];
    let occurrenceCount = 0;
    let valuesAreValid = allowed.length > 0 && allowed.every((value) => typeof value === "string");
    const inspect = (value, isVocabularyDefinition = false) => {
      if (Array.isArray(value)) {
        for (const item of value) inspect(item, isVocabularyDefinition);
        return;
      }
      if (!isObject(value)) return;
      for (const [nestedKey, nestedValue] of Object.entries(value)) {
        if (!isVocabularyDefinition && nestedKey === field) {
          occurrenceCount += 1;
          if (typeof nestedValue !== "string" || !allowed.includes(nestedValue)) valuesAreValid = false;
        }
        inspect(nestedValue, isVocabularyDefinition || nestedKey === key);
      }
    };
    inspect(owner);
    fixtureRule(relativePath, `${field} values outside owned vocabulary`, occurrenceCount > 0 && valuesAreValid);
  }

  for (const nestedValue of Object.values(owner)) validateVocabularyOccurrences(nestedValue, relativePath);
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
  const targetLanguages = new Set(
    sessionUpdateSchema?.properties?.session?.properties?.audio?.properties
      ?.output?.properties?.language?.enum ?? [],
  );
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
      && eventTypes.has(step.eventType)
      && matchesJsonSchema(step.payload, translationSchema)));
  fixtureRule(relativePath, "session.update target language drifted", wireSteps
    .filter((step) => step?.eventType === "session.update")
    .every((step) => {
      const language = step.payload?.session?.audio?.output?.language;
      return typeof language === "string" && targetLanguages.has(language);
    }));
  fixtureRule(relativePath, "session.update schema must require session.audio.output.language",
    !matchesJsonSchema({ type: "session.update" }, translationSchema));
  fixtureRule(relativePath, "translation wire schemas must reject extra properties",
    !matchesJsonSchema({ type: "session.created", unexpected: true }, translationSchema));

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

function validateAppVisibleFixtureValues(value, relativePath, appStateSchema) {
  if (Array.isArray(value)) {
    for (const item of value) validateAppVisibleFixtureValues(item, relativePath, appStateSchema);
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
    if (enumName !== undefined
      && !matchesJsonSchema(nestedValue, { $ref: `#/$defs/${enumName}` }, appStateSchema)) {
      fixtureRule(relativePath, `app-state ${key} value outside schema`, false);
    }
    validateAppVisibleFixtureValues(nestedValue, relativePath, appStateSchema);
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

const schemaPaths = validateInventory("schema", manifest?.schemas, contractsDirectory, ["contract-manifest.json"], "Shared/Contracts");
const fixturePaths = validateInventory(
  "fixture",
  fixtureManifest?.fixtures,
  fixturesDirectory,
  ["fixture-manifest.json", ...expectedAuxiliaryFixturePaths],
  "Shared/TestVectors",
);

for (const auxiliaryPath of expectedAuxiliaryFixturePaths) {
  const relativePath = `Shared/TestVectors/${auxiliaryPath}`;
  const auxiliary = readObjectJson(relativePath);
  if (auxiliary?.contractVersion !== 1) fail(`${relativePath}: contractVersion must be 1`);
  if (auxiliary?.corpusId !== "routing.language-corpus.v1") fail(`${relativePath}: corpusId drifted`);
  if (!Array.isArray(auxiliary?.cases) || auxiliary.cases.length === 0) {
    fail(`${relativePath}: cases must be a non-empty array`);
  }
}

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
  const expectedCaseJson = expectedFixtureCaseJson.get(relativePath);
  let expectedCases;
  try {
    expectedCases = JSON.parse(expectedCaseJson);
    if (relativePath === "Audio/pcm-conversion.json") {
      expectedCases[6].input.warmupPCM16LittleEndianBytes =
        Array.from({ length: 128 }, (_, index) => index % 2 === 0 ? 255 : 127);
    }
  } catch {
    expectedCases = undefined;
  }
  fixtureRule(relativePath, "full named case trigger/expected drifted",
    expectedCases !== undefined && deepEqual(fixtureCases(fixture), expectedCases));
  validateVocabularyOccurrences(fixture, relativePath);
}

const appStateSchema = schemaDocuments.get("v1/app-state.schema.json");
for (const [relativePath, fixture] of fixtureDocuments) {
  validateAppVisibleFixtureValues(fixture, relativePath, appStateSchema);
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
