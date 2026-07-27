#!/usr/bin/env node

import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { basename, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const generatorVersion = "emke-language-profile/1.0.0";
const sources = [
  {
    language: "zh",
    title: "紅樓夢",
    url: "https://www.gutenberg.org/ebooks/24264.txt.utf-8",
    license: "Project Gutenberg License; source is marked public domain in the United States",
    licenseUrl: "https://www.gutenberg.org/policy/license.html",
    sha256: "ff1526996bf4b81807651921a85e5c1c0f1d1d123c9fa4553057ba6a3ec72011",
  },
  {
    language: "en",
    title: "Pride and Prejudice",
    url: "https://www.gutenberg.org/ebooks/1342.txt.utf-8",
    license: "Project Gutenberg License; source is marked public domain in the United States",
    licenseUrl: "https://www.gutenberg.org/policy/license.html",
    sha256: "74f2665d6e6925fc2c17dec644bec9e87df478a0f1836822125e8acbb3777806",
  },
  {
    language: "de",
    title: "Faust: Der Tragödie erster Teil",
    url: "https://www.gutenberg.org/ebooks/2229.txt.utf-8",
    license: "Project Gutenberg License; source is marked public domain in the United States",
    licenseUrl: "https://www.gutenberg.org/policy/license.html",
    sha256: "fed2a58edd6910ee96d27ada614d34bcc41f03750d86541c41246bb74c564ff7",
  },
];

function sha256(data) {
  return createHash("sha256").update(data).digest("hex");
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
      .sort((left, right) => right[1] - left[1] || left[0].localeCompare(right[0], "en"))
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
      .sort((left, right) => left[0].localeCompare(right[0], "en")),
  );
}

async function sourceBytes(source, sourceDirectory) {
  if (sourceDirectory) {
    return readFile(resolve(sourceDirectory, `${source.language}.txt`));
  }

  const response = await fetch(source.url, {
    headers: { "user-agent": `${generatorVersion} (+https://github.com/)` },
  });
  if (!response.ok) {
    throw new Error(`Failed to download ${source.url}: HTTP ${response.status}`);
  }

  return Buffer.from(await response.arrayBuffer());
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

  const profiles = {};
  for (const source of sources) {
    const bytes = await sourceBytes(source, sourceDirectory);
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
