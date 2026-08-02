import { readFile } from "node:fs/promises";
import path from "node:path";

const repositoryRoot = path.resolve(import.meta.dirname, "..", "..");
const defaultPaths = {
  header: path.join(
    repositoryRoot,
    "Windows",
    "shared",
    "emke_endpoint_contract.h",
  ),
  inf: path.join(
    repositoryRoot,
    "Windows",
    "driver",
    "EMKE.VirtualAudio",
    "EMKE.VirtualAudio.inf",
  ),
  project: path.join(
    repositoryRoot,
    "Windows",
    "driver",
    "EMKE.VirtualAudio",
    "EMKE.VirtualAudio.vcxproj",
  ),
  version: path.join(repositoryRoot, "Windows", "version.json"),
  compatibility: path.join(
    repositoryRoot,
    "Windows",
    "packaging",
    "compatibility.internal.json",
  ),
};

function parseArguments(argv) {
  const values = new Map();
  const allowed = new Set(Object.keys(defaultPaths));
  for (let index = 0; index < argv.length; index += 2) {
    const key = argv[index];
    const value = argv[index + 1];
    if (!key?.startsWith("--") || value === undefined) {
      throw new Error(
        "Expected pairs of --header, --inf, --project, --version, and " +
          "--compatibility paths.",
      );
    }
    const name = key.slice(2);
    if (!allowed.has(name) || values.has(name)) {
      throw new Error(`Unsupported or duplicate argument: ${key}.`);
    }
    values.set(name, value);
  }
  return Object.fromEntries(
    Object.entries(defaultPaths).map(([name, defaultPath]) => [
      name,
      path.resolve(values.get(name) ?? defaultPath),
    ]),
  );
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

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function singleCapture(text, pattern, description) {
  const matches = [...text.matchAll(pattern)];
  if (matches.length !== 1) {
    throw new Error(
      `${description} must occur exactly once; found ${matches.length}.`,
    );
  }
  return matches[0][1];
}

function readInfString(inf, name) {
  return singleCapture(
    inf,
    new RegExp(`^${escapeRegExp(name)}="([^"]+)"\\s*$`, "gmi"),
    `INF string ${name}`,
  );
}

function readXmlValue(project, name) {
  const activeProject = project.replace(/<!--[\s\S]*?-->/g, "");
  if (activeProject.includes("<!--") || activeProject.includes("-->")) {
    throw new Error("Driver project contains a malformed XML comment.");
  }
  return singleCapture(
    activeProject,
    new RegExp(
      `<${escapeRegExp(name)}>\\s*([^<]+?)\\s*</${escapeRegExp(name)}>`,
      "gi",
    ),
    `driver project ${name}`,
  );
}

function parseActiveInfSections(inf) {
  const sections = new Map();
  let currentSection = null;
  for (const rawLine of inf.split(/\r?\n/)) {
    const line = rawLine.split(";", 1)[0].trim();
    if (line === "") {
      continue;
    }
    const sectionMatch = line.match(/^\[([^\]]+)\]$/);
    if (sectionMatch !== null) {
      const name = sectionMatch[1].trim();
      const key = name.toUpperCase();
      if (sections.has(key)) {
        throw new Error(`INF section [${name}] occurs more than once.`);
      }
      currentSection = { name, lines: [] };
      sections.set(key, currentSection);
      continue;
    }
    if (currentSection !== null) {
      currentSection.lines.push(line);
    }
  }
  return sections;
}

function readInfSection(sections, name) {
  const section = sections.get(name.toUpperCase());
  if (section === undefined) {
    throw new Error(`INF is missing active section [${name}].`);
  }
  return section;
}

function readRequiredString(object, name, description) {
  const value = object[name];
  if (typeof value !== "string" || value.trim() !== value || value === "") {
    throw new Error(`${description} ${name} must be a non-blank string.`);
  }
  return value;
}

function readRequiredInteger(object, name, description) {
  const value = object[name];
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new Error(`${description} ${name} must be a positive integer.`);
  }
  return value;
}

function readRequiredStringArray(object, name, description) {
  const value = object[name];
  if (
    !Array.isArray(value) ||
    value.some(
      (item) =>
        typeof item !== "string" || item.trim() !== item || item === "",
    )
  ) {
    throw new Error(`${description} ${name} must be an array of strings.`);
  }
  return value;
}

function requireEqual(actual, expected, description) {
  if (actual !== expected) {
    throw new Error(
      `${description} diverges from the release contract: expected ` +
        `'${expected}', got '${actual}'.`,
    );
  }
}

function requireSharedEqual(actual, expected, description) {
  if (actual !== expected) {
    throw new Error(
      `${description} diverges from the shared header: expected ` +
        `'${expected}', got '${actual}'.`,
    );
  }
}

function requireArrayEqual(actual, expected, description) {
  if (
    actual.length !== expected.length ||
    actual.some((value, index) => value !== expected[index])
  ) {
    throw new Error(
      `${description} diverges from the release contract: expected ` +
        `${JSON.stringify(expected)}, got ${JSON.stringify(actual)}.`,
    );
  }
}

async function readJson(file, description) {
  let parsed;
  try {
    parsed = JSON.parse(await readFile(file, "utf8"));
  } catch (error) {
    throw new Error(`${description} is not valid JSON: ${error.message}`);
  }
  if (parsed === null || Array.isArray(parsed) || typeof parsed !== "object") {
    throw new Error(`${description} must contain one JSON object.`);
  }
  return parsed;
}

async function main() {
  const arguments_ = parseArguments(process.argv.slice(2));
  const [header, inf, project, version, compatibility] = await Promise.all([
    readFile(arguments_.header, "utf8"),
    readFile(arguments_.inf, "utf8"),
    readFile(arguments_.project, "utf8"),
    readJson(arguments_.version, "Windows version metadata"),
    readJson(arguments_.compatibility, "Windows compatibility metadata"),
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
  const sharedRoles = roleDefinitions.map(([, macroName]) =>
    readStringMacro(header, macroName),
  );
  const infRoles = roleDefinitions.map(([infName], index) => {
    const actual = readInfString(inf, infName);
    requireSharedEqual(actual, sharedRoles[index], infName);
    return actual;
  });
  if (new Set(infRoles).size !== 4) {
    throw new Error("INF must declare exactly four unique endpoint roles.");
  }

  const infSections = parseActiveInfSections(inf);
  const bindingDefinitions = [
    ["EMKE.I.WaveMeetingSpeaker.AddReg", "RoleMeetingSpeakerRender"],
    ["EMKE.I.WaveAppSpeakerCapture.AddReg", "RoleAppSpeakerCapture"],
    ["EMKE.I.WaveAppMicrophoneRender.AddReg", "RoleAppMicrophoneRender"],
    ["EMKE.I.WaveMeetingMicrophone.AddReg", "RoleMeetingMicrophoneCapture"],
  ];
  const allBindingLines = [...infSections.values()].flatMap((section) =>
    section.lines
      .filter(
        (line) =>
          /^HKR\s*,/i.test(line) &&
          /%PKEY_EMKE_EndpointRole%/i.test(line),
      )
      .map((line) => ({ section: section.name, line })),
  );
  if (allBindingLines.length !== bindingDefinitions.length) {
    throw new Error(
      "INF must contain exactly four active endpoint-role binding lines; " +
        `found ${allBindingLines.length}.`,
    );
  }
  const bindingPattern =
    /^HKR\s*,\s*EP\\0\s*,\s*%PKEY_EMKE_EndpointRole%\s*,\s*0x00000000\s*,\s*%(Role[A-Za-z0-9]+)%$/;
  const boundRoles = [];
  for (const [sectionName, expectedRole] of bindingDefinitions) {
    const sectionBindings = readInfSection(infSections, sectionName).lines.filter(
      (line) =>
        /^HKR\s*,/i.test(line) &&
        /%PKEY_EMKE_EndpointRole%/i.test(line),
    );
    if (sectionBindings.length !== 1) {
      throw new Error(
        `INF section [${sectionName}] must contain exactly one active ` +
          `endpoint-role binding; found ${sectionBindings.length}.`,
      );
    }
    const bindingMatch = sectionBindings[0].match(bindingPattern);
    if (bindingMatch === null) {
      throw new Error(
        `INF section [${sectionName}] has a malformed endpoint-role binding.`,
      );
    }
    requireEqual(
      bindingMatch[1],
      expectedRole,
      `INF section [${sectionName}] endpoint role`,
    );
    boundRoles.push(bindingMatch[1]);
  }
  requireArrayEqual(
    boundRoles,
    roleDefinitions.map(([infName]) => infName),
    "INF active endpoint-role bindings",
  );

  const expectedPropertyKey =
    `${formatGuidFromNumericAuthority(header)},` +
    `${readUnsignedMacro(header, "EMKE_ENDPOINT_ROLE_PROPERTY_PID")}`;
  requireSharedEqual(
    readInfString(inf, "PKEY_EMKE_EndpointRole").toUpperCase(),
    expectedPropertyKey.toUpperCase(),
    "endpoint-role property key",
  );

  const abi = readUnsignedMacro(header, "EMKE_DRIVER_ABI");
  const infAbiHex = singleCapture(
    inf,
    /^\s*HKR\s*,\s*,\s*DriverAbi\s*,\s*0x00010001\s*,\s*0x([0-9a-f]+)\s*$/gmi,
    "INF driver ABI registration",
  );
  const infAbi = Number.parseInt(infAbiHex, 16);
  requireSharedEqual(infAbi, abi, "driver ABI");

  const versionDriverVersion = readRequiredString(
    version,
    "driverPackageVersion",
    "Windows version metadata",
  );
  const versionMinimumBuild = readRequiredInteger(
    version,
    "minimumWindowsBuild",
    "Windows version metadata",
  );
  const versionAbi = readRequiredInteger(
    version,
    "driverAbiVersion",
    "Windows version metadata",
  );
  const versionHardwareId = readRequiredString(
    version,
    "driverHardwareId",
    "Windows version metadata",
  );
  const versionKmdf = readRequiredString(
    version,
    "driverKmdfLibraryVersion",
    "Windows version metadata",
  );
  const versionRoles = readRequiredStringArray(
    version,
    "driverEndpointRoles",
    "Windows version metadata",
  );

  const compatibilityMinimumVersion = readRequiredString(
    compatibility,
    "minimumDriverVersion",
    "Windows compatibility metadata",
  );
  const compatibilityRecommendedVersion = readRequiredString(
    compatibility,
    "recommendedDriverVersion",
    "Windows compatibility metadata",
  );
  const compatibilityMinimumBuild = readRequiredInteger(
    compatibility,
    "minimumWindowsBuild",
    "Windows compatibility metadata",
  );
  const compatibilityAbi = readRequiredInteger(
    compatibility,
    "driverAbiVersion",
    "Windows compatibility metadata",
  );
  requireEqual(versionDriverVersion, "1.0.0.2", "driver package version");
  requireEqual(versionMinimumBuild, 19045, "minimum Windows build");
  requireEqual(versionAbi, abi, "version metadata driver ABI");
  requireEqual(
    versionHardwareId.toUpperCase(),
    "ROOT\\EMKEVIRTUALAUDIO",
    "version metadata hardware ID",
  );
  requireEqual(versionKmdf, "1.31", "version metadata KMDF version");
  requireArrayEqual(versionRoles, sharedRoles, "version metadata endpoint roles");
  requireEqual(
    compatibilityMinimumVersion,
    versionDriverVersion,
    "compatibility minimum driver version",
  );
  requireEqual(
    compatibilityRecommendedVersion,
    versionDriverVersion,
    "compatibility recommended driver version",
  );
  requireEqual(
    compatibilityMinimumBuild,
    versionMinimumBuild,
    "compatibility minimum Windows build",
  );
  requireEqual(compatibilityAbi, abi, "compatibility driver ABI");

  const driverVer = singleCapture(
    inf,
    /^\s*DriverVer\s*=\s*([^\r\n]+?)\s*$/gmi,
    "INF DriverVer",
  );
  requireEqual(
    driverVer,
    `08/01/2026,${versionDriverVersion}`,
    "INF DriverVer",
  );

  const manufacturer = readInfSection(infSections, "Manufacturer");
  if (manufacturer.lines.length !== 1) {
    throw new Error(
      "INF [Manufacturer] must contain exactly one active Models mapping; " +
        `found ${manufacturer.lines.length}.`,
    );
  }
  const manufacturerMatch = manufacturer.lines[0].match(
    /^%ManufacturerName%\s*=\s*EMKE\s*,\s*NTamd64\.10\.0\.\.\.([0-9]+)$/,
  );
  if (manufacturerMatch === null) {
    throw new Error(
      "INF [Manufacturer] has an unauthorized Models decoration.",
    );
  }
  const manufacturerBuild = Number.parseInt(manufacturerMatch[1], 10);
  requireEqual(
    manufacturerBuild,
    versionMinimumBuild,
    "INF minimum Windows model build",
  );
  const expectedModelSection =
    `EMKE.NTamd64.10.0...${versionMinimumBuild}`;
  const activeModelSections = [...infSections.values()].filter((section) =>
    /^EMKE\.NT/i.test(section.name),
  );
  if (
    activeModelSections.length !== 1 ||
    activeModelSections[0].name !== expectedModelSection
  ) {
    throw new Error(
      "INF must contain exactly the authorized active Models section " +
        `[${expectedModelSection}] and no undecorated, lower-floor, or extra ` +
        "Models paths.",
    );
  }
  const modelSection = readInfSection(infSections, expectedModelSection);
  if (modelSection.lines.length !== 1) {
    throw new Error(
      `INF [${expectedModelSection}] must contain exactly one active model ` +
        `entry; found ${modelSection.lines.length}.`,
    );
  }
  const modelMatch = modelSection.lines[0].match(
    /^%DeviceDescription%\s*=\s*EMKE_Install\s*,\s*(ROOT\\[^\s,]+)$/,
  );
  if (modelMatch === null) {
    throw new Error(
      `INF [${expectedModelSection}] contains an unauthorized model entry.`,
    );
  }
  const modelHardwareId = modelMatch[1];
  requireEqual(
    modelHardwareId.toUpperCase(),
    versionHardwareId.toUpperCase(),
    "INF hardware ID",
  );

  const infKmdf = singleCapture(
    inf,
    /^\s*KmdfLibraryVersion\s*=\s*([^\s;]+)\s*$/gmi,
    "INF KmdfLibraryVersion",
  );
  if (/\$[A-Za-z_][A-Za-z0-9_]*\$/.test(infKmdf)) {
    throw new Error("INF KmdfLibraryVersion contains an unresolved token.");
  }
  requireEqual(infKmdf, versionKmdf, "INF KMDF library version");

  requireEqual(
    readXmlValue(project, "EMKETargetOS"),
    "Windows10",
    "driver project OS target",
  );
  requireEqual(
    readXmlValue(project, "KMDF_VERSION_MAJOR"),
    "1",
    "driver project KMDF major version",
  );
  requireEqual(
    readXmlValue(project, "KMDF_VERSION_MINOR"),
    "31",
    "driver project KMDF minor version",
  );
  requireEqual(
    readXmlValue(project, "DateStamp"),
    "08/01/2026",
    "driver project INF date stamp",
  );
  requireEqual(
    readXmlValue(project, "TimeStamp"),
    versionDriverVersion,
    "driver project INF version stamp",
  );
  requireEqual(
    readXmlValue(project, "KmdfVersionNumber"),
    versionKmdf,
    "driver project INF KMDF stamp input",
  );

  process.stdout.write(
    `Driver INF contract validation passed: release version ${versionDriverVersion}, ` +
      `Windows build ${versionMinimumBuild}, KMDF ${versionKmdf}, ABI ${abi}, ` +
      `${infRoles.length} endpoint roles.\n`,
  );
}

main().catch((error) => {
  process.stderr.write(
    `Driver release contract validation failed: ${error.message}\n`,
  );
  process.exitCode = 1;
});
