#!/usr/bin/env node

import { createHash } from "node:crypto";
import { mkdir, readFile, stat, writeFile } from "node:fs/promises";
import { basename, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const generatorVersion = "emke-language-profile/1.1.0";
const allowedSourceHost = "www.gutenberg.org";
const defaultMaximumSourceBytes = 16 * 1024 * 1024;
const defaultDownloadTimeoutMilliseconds = 15_000;
const maximumRedirects = 4;
const sources = [
  {
    language: "zh",
    title: "紅樓夢",
    url: "https://www.gutenberg.org/cache/epub/24264/pg24264.txt",
    license: "Project Gutenberg License; source is marked public domain in the United States",
    licenseUrl: "https://www.gutenberg.org/policy/license.html",
    sha256: "ff1526996bf4b81807651921a85e5c1c0f1d1d123c9fa4553057ba6a3ec72011",
  },
  {
    language: "en",
    title: "Pride and Prejudice",
    url: "https://www.gutenberg.org/cache/epub/1342/pg1342.txt",
    license: "Project Gutenberg License; source is marked public domain in the United States",
    licenseUrl: "https://www.gutenberg.org/policy/license.html",
    sha256: "74f2665d6e6925fc2c17dec644bec9e87df478a0f1836822125e8acbb3777806",
  },
  {
    language: "de",
    title: "Faust: Der Tragödie erster Teil",
    url: "https://www.gutenberg.org/cache/epub/2229/pg2229.txt",
    license: "Project Gutenberg License; source is marked public domain in the United States",
    licenseUrl: "https://www.gutenberg.org/policy/license.html",
    sha256: "fed2a58edd6910ee96d27ada614d34bcc41f03750d86541c41246bb74c564ff7",
  },
];

function sha256(data) {
  return createHash("sha256").update(data).digest("hex");
}

function compareCodePoints(left, right) {
  const leftPoints = [...left];
  const rightPoints = [...right];
  const count = Math.min(leftPoints.length, rightPoints.length);
  for (let index = 0; index < count; index += 1) {
    const comparison =
      leftPoints[index].codePointAt(0) - rightPoints[index].codePointAt(0);
    if (comparison !== 0) {
      return comparison;
    }
  }

  return leftPoints.length - rightPoints.length;
}

function stripGutenbergEnvelope(text) {
  const start = text.search(/\*{3}\s*START OF THE PROJECT GUTENBERG EBOOK/i);
  const end = text.search(/\*{3}\s*END OF THE PROJECT GUTENBERG EBOOK/i);
  if (start < 0 || end <= start) {
    throw new Error("Project Gutenberg envelope markers were not found");
  }

  const afterStart = text.indexOf("\n", start);
  return text.slice(afterStart + 1, end);
}

function normalize(text) {
  return text
    .normalize("NFKC")
    .toLowerCase()
    .replace(/[^\p{L}]+/gu, " ")
    .replace(/\s+/gu, " ")
    .trim();
}

function buildProfile(text) {
  const characters = [...normalize(text)];
  const countsByWidth = [new Map(), new Map(), new Map(), new Map()];
  const totalsByWidth = [0, 0, 0, 0];
  for (let index = 0; index < characters.length; index += 1) {
    for (let width = 1; width <= 3 && index + width <= characters.length; width += 1) {
      const feature = characters.slice(index, index + width).join("");
      const counts = countsByWidth[width];
      counts.set(feature, (counts.get(feature) ?? 0) + 1);
      totalsByWidth[width] += 1;
    }
  }

  const probabilities = [];
  for (let width = 1; width <= 3; width += 1) {
    const top = [...countsByWidth[width].entries()]
      .sort(
        (left, right) =>
          right[1] - left[1] || compareCodePoints(left[0], right[0]),
      )
      .slice(0, 2048);
    probabilities.push(
      ...top.map(([feature, count]) => [
        feature,
        Number((count / totalsByWidth[width]).toPrecision(12)),
      ]),
    );
  }

  return Object.fromEntries(
    probabilities
      .sort((left, right) => compareCodePoints(left[0], right[0])),
  );
}

function parseBoundedIntegerOption(name, fallback, maximum) {
  const optionIndex = process.argv.indexOf(name);
  if (optionIndex < 0) {
    return fallback;
  }

  const raw = process.argv[optionIndex + 1];
  if (!raw || !/^[1-9]\d*$/u.test(raw)) {
    throw new Error(`${name} requires a positive integer`);
  }

  const value = Number(raw);
  if (!Number.isSafeInteger(value) || value > maximum) {
    throw new Error(`${name} exceeds its hard maximum of ${maximum}`);
  }

  return value;
}

function validateSourceUrl(value) {
  const url = new URL(value);
  if (
    url.protocol !== "https:" ||
    url.hostname !== allowedSourceHost ||
    url.username ||
    url.password ||
    url.port
  ) {
    throw new Error(
      `Source URL is not allowed; expected https://${allowedSourceHost}/`,
    );
  }

  return url;
}

function contentLength(response, maximumSourceBytes) {
  const raw = response.headers.get("content-length");
  if (raw === null) {
    return;
  }

  if (!/^\d+$/u.test(raw)) {
    throw new Error("Download Content-Length is invalid");
  }

  const length = BigInt(raw);
  if (length > BigInt(maximumSourceBytes)) {
    throw new Error(
      `Download Content-Length ${length} exceeds the hard source byte limit`,
    );
  }
}

async function readBoundedResponseBody(
  response,
  maximumSourceBytes,
  controller,
) {
  if (!response.body) {
    throw new Error("Downloaded source response has no body");
  }

  const reader = response.body.getReader();
  const chunks = [];
  let total = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) {
      break;
    }

    if (value.byteLength > maximumSourceBytes - total) {
      controller.abort();
      await reader.cancel();
      throw new Error(
        "Download stream exceeds the hard source byte limit",
      );
    }

    chunks.push(Buffer.from(value));
    total += value.byteLength;
  }

  return Buffer.concat(chunks, total);
}

async function downloadSourceBytes(
  source,
  maximumSourceBytes,
  timeoutMilliseconds,
) {
  const controller = new AbortController();
  let timedOut = false;
  const timeout = setTimeout(() => {
    timedOut = true;
    controller.abort();
  }, timeoutMilliseconds);
  let url = validateSourceUrl(source.url);

  try {
    for (let redirectCount = 0; ; redirectCount += 1) {
      const response = await fetch(url, {
        headers: {
          "user-agent": `${generatorVersion} (+https://github.com/)`,
        },
        redirect: "manual",
        signal: controller.signal,
      });
      if ([301, 302, 303, 307, 308].includes(response.status)) {
        if (redirectCount >= maximumRedirects) {
          throw new Error(
            `Download redirect limit of ${maximumRedirects} was exceeded`,
          );
        }

        const location = response.headers.get("location");
        if (!location) {
          throw new Error("Download redirect did not include a Location");
        }

        url = validateSourceUrl(new URL(location, url));
        continue;
      }

      if (!response.ok) {
        throw new Error(
          `Failed to download ${url}: HTTP ${response.status}`,
        );
      }

      contentLength(response, maximumSourceBytes);
      return await readBoundedResponseBody(
        response,
        maximumSourceBytes,
        controller,
      );
    }
  } catch (error) {
    if (timedOut) {
      throw new Error(
        `Download timed out after ${timeoutMilliseconds} ms`,
        { cause: error },
      );
    }

    throw error;
  } finally {
    clearTimeout(timeout);
  }
}

async function sourceBytes(
  source,
  sourceDirectory,
  maximumSourceBytes,
  timeoutMilliseconds,
) {
  if (sourceDirectory) {
    const path = resolve(sourceDirectory, `${source.language}.txt`);
    const metadata = await stat(path);
    if (metadata.size > maximumSourceBytes) {
      throw new Error(
        `source-dir file ${path} exceeds the hard source byte limit`,
      );
    }

    const bytes = await readFile(path);
    if (bytes.byteLength > maximumSourceBytes) {
      throw new Error(
        `source-dir file ${path} exceeds the hard source byte limit`,
      );
    }

    return bytes;
  }

  return downloadSourceBytes(
    source,
    maximumSourceBytes,
    timeoutMilliseconds,
  );
}

async function main() {
  const scriptPath = fileURLToPath(import.meta.url);
  const scriptDirectory = dirname(scriptPath);
  const repositoryRoot = resolve(scriptDirectory, "..", "..");
  const outputPath = resolve(
    repositoryRoot,
    "Windows/src/EMKE.Routing/Resources/language-profile-v1.json",
  );
  const noticePath = resolve(
    repositoryRoot,
    "Windows/src/EMKE.Routing/Resources/THIRD_PARTY_NOTICES.md",
  );
  const sourceDirectoryIndex = process.argv.indexOf("--source-dir");
  const sourceDirectory =
    sourceDirectoryIndex < 0 ? undefined : process.argv[sourceDirectoryIndex + 1];
  if (sourceDirectoryIndex >= 0 && !sourceDirectory) {
    throw new Error("--source-dir requires a directory");
  }
  const maximumSourceBytes = parseBoundedIntegerOption(
    "--max-source-bytes",
    defaultMaximumSourceBytes,
    defaultMaximumSourceBytes,
  );
  const timeoutMilliseconds = parseBoundedIntegerOption(
    "--timeout-ms",
    defaultDownloadTimeoutMilliseconds,
    60_000,
  );

  const profiles = {};
  for (const source of sources) {
    const bytes = await sourceBytes(
      source,
      sourceDirectory,
      maximumSourceBytes,
      timeoutMilliseconds,
    );
    const actualHash = sha256(bytes);
    if (actualHash !== source.sha256) {
      throw new Error(
        `${source.language} corpus SHA256 mismatch: expected ${source.sha256}, got ${actualHash}`,
      );
    }

    profiles[source.language] = buildProfile(
      stripGutenbergEnvelope(bytes.toString("utf8")),
    );
  }

  const canonicalProfiles = JSON.stringify(profiles);
  const model = {
    version: 1,
    generatorVersion,
    featureKind: "normalized-character-1-to-3-grams",
    featureSha256: sha256(canonicalProfiles),
    sources,
    profiles,
  };
  const modelBytes = Buffer.from(`${JSON.stringify(model)}\n`, "utf8");
  const modelSha256 = sha256(modelBytes);
  const notice = `# Third-party notices

## Offline language profile v1

The embedded zh/en/de language profile is generated from the following Project
Gutenberg UTF-8 sources. The source files are not included in the application
or repository. Project Gutenberg states that these works are public domain in
the United States and permits reuse under the Project Gutenberg License; users
outside the United States must check local copyright law.

| Language | Source | License | Corpus SHA256 |
| --- | --- | --- | --- |
${sources
    .map(
      (source) =>
        `| ${source.language} | [${source.title}](${source.url}) | [Project Gutenberg License](${source.licenseUrl}) | \`${source.sha256}\` |`,
    )
    .join("\n")}

- Generator: \`${generatorVersion}\`
- Feature data SHA256: \`${model.featureSha256}\`
- Generated model SHA256: \`${modelSha256}\`
- Reproduction: run \`node Windows/tools/${basename(scriptPath)}\`. The
  generator rejects downloaded source bytes unless every corpus SHA256 matches.
`;

  await mkdir(dirname(outputPath), { recursive: true });
  await writeFile(outputPath, modelBytes);
  await writeFile(noticePath, notice);
  process.stdout.write(`${modelSha256}  ${outputPath}\n`);
}

await main();
