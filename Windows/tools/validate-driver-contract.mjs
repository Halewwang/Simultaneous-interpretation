import { readFile } from "node:fs/promises";
import path from "node:path";

function parseArguments(argv) {
  const values = new Map();
  for (let index = 0; index < argv.length; index += 2) {
    const key = argv[index];
    const value = argv[index + 1];
    if (!key?.startsWith("--") || value === undefined) {
      throw new Error("Expected --header <path> --inf <path>.");
    }
    values.set(key.slice(2), value);
  }
  if (!values.has("header") || !values.has("inf")) {
    throw new Error("Expected --header <path> --inf <path>.");
  }
  return {
    header: path.resolve(values.get("header")),
    inf: path.resolve(values.get("inf")),
  };
}

function readMacro(text, name) {
  const match = text.match(
    new RegExp(`^\\s*#define\\s+${name}\\s+([^\\r\\n]+)`, "m"),
  );
  if (match === null) {
    throw new Error(`Shared header is missing ${name}.`);
  }
  return match[1].trim();
}

function readStringMacro(text, name) {
  const value = readMacro(text, name);
  const match = value.match(/^L?"([^"]+)"$/);
  if (match === null) {
    throw new Error(`Shared header ${name} must be one literal string.`);
  }
  return match[1];
}

function readUnsignedMacro(text, name) {
  const value = readMacro(text, name);
  if (!/^[0-9]+u?$/i.test(value)) {
    throw new Error(`Shared header ${name} must be one unsigned integer.`);
  }
  return Number.parseInt(value, 10);
}

function readHexMacro(text, name, maximum) {
  const value = readMacro(text, name);
  if (!/^0x[0-9a-f]+u?$/i.test(value)) {
    throw new Error(`Shared header ${name} must be one hexadecimal integer.`);
  }
  const parsed = Number.parseInt(value.slice(2), 16);
  if (!Number.isSafeInteger(parsed) || parsed > maximum) {
    throw new Error(`Shared header ${name} is outside its GUID component range.`);
  }
  return parsed;
}

function formatGuidFromNumericAuthority(header) {
  const data1 = readHexMacro(
    header,
    "EMKE_ENDPOINT_ROLE_PROPERTY_GUID_DATA1",
    0xffff_ffff,
  );
  const data2 = readHexMacro(
    header,
    "EMKE_ENDPOINT_ROLE_PROPERTY_GUID_DATA2",
    0xffff,
  );
  const data3 = readHexMacro(
    header,
    "EMKE_ENDPOINT_ROLE_PROPERTY_GUID_DATA3",
    0xffff,
  );
  const data4 = Array.from({ length: 8 }, (_, index) =>
    readHexMacro(
      header,
      `EMKE_ENDPOINT_ROLE_PROPERTY_GUID_DATA4_${index}`,
      0xff,
    ),
  );
  const hex = (value, width) =>
    value.toString(16).toUpperCase().padStart(width, "0");
  return (
    `{${hex(data1, 8)}-${hex(data2, 4)}-${hex(data3, 4)}-` +
    `${hex(data4[0], 2)}${hex(data4[1], 2)}-` +
    `${data4.slice(2).map((value) => hex(value, 2)).join("")}}`
  );
}

function readInfString(inf, name) {
  const match = inf.match(
    new RegExp(`^${name}="([^"]+)"\\s*$`, "mi"),
  );
  if (match === null) {
    throw new Error(`INF is missing ${name}.`);
  }
  return match[1];
}

function requireEqual(actual, expected, description) {
  if (actual !== expected) {
    throw new Error(
      `${description} diverges from the shared header: expected ` +
        `'${expected}', got '${actual}'.`,
    );
  }
}

async function main() {
  const arguments_ = parseArguments(process.argv.slice(2));
  const [header, inf] = await Promise.all([
    readFile(arguments_.header, "utf8"),
    readFile(arguments_.inf, "utf8"),
  ]);

  const roleDefinitions = [
    [
      "RoleMeetingSpeakerRender",
      "EMKE_ROLE_MEETING_SPEAKER_RENDER_UTF8",
    ],
    ["RoleAppSpeakerCapture", "EMKE_ROLE_APP_SPEAKER_CAPTURE_UTF8"],
    [
      "RoleAppMicrophoneRender",
      "EMKE_ROLE_APP_MICROPHONE_RENDER_UTF8",
    ],
    [
      "RoleMeetingMicrophoneCapture",
      "EMKE_ROLE_MEETING_MICROPHONE_CAPTURE_UTF8",
    ],
  ];
  for (const [infName, macroName] of roleDefinitions) {
    requireEqual(
      readInfString(inf, infName),
      readStringMacro(header, macroName),
      infName,
    );
  }

  const expectedPropertyKey =
    `${formatGuidFromNumericAuthority(header)},` +
    `${readUnsignedMacro(header, "EMKE_ENDPOINT_ROLE_PROPERTY_PID")}`;
  requireEqual(
    readInfString(inf, "PKEY_EMKE_EndpointRole").toUpperCase(),
    expectedPropertyKey.toUpperCase(),
    "endpoint-role property key",
  );

  const abi = readUnsignedMacro(header, "EMKE_DRIVER_ABI");
  const abiMatch = inf.match(
    /^\s*HKR\s*,\s*,\s*DriverAbi\s*,\s*0x00010001\s*,\s*0x([0-9a-f]+)\s*$/mi,
  );
  if (abiMatch === null) {
    throw new Error("INF driver ABI registration is missing.");
  }
  const infAbi = Number.parseInt(abiMatch[1], 16);
  requireEqual(infAbi, abi, "driver ABI");

  process.stdout.write(
    `Driver INF contract validation passed: ABI ${abi}, ` +
      `${roleDefinitions.length} endpoint roles.\n`,
  );
}

main().catch((error) => {
  process.stderr.write(`Driver INF contract validation failed: ${error.message}\n`);
  process.exitCode = 1;
});
