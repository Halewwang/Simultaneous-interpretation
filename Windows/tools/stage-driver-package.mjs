import {
  copyFile,
  lstat,
  mkdir,
  readFile,
  readdir,
  realpath,
  rm,
} from "node:fs/promises";
import path from "node:path";

function parseArguments(argv) {
  const values = new Map();
  for (let index = 0; index < argv.length; index += 2) {
    const key = argv[index];
    const value = argv[index + 1];
    if (!key?.startsWith("--") || value === undefined) {
      throw new Error("Malformed package-staging arguments.");
    }
    values.set(key.slice(2), value);
  }
  const required = [
    "repository-root",
    "artifact-root",
    "source-package",
    "artifact-directory",
  ];
  for (const key of required) {
    if (!values.has(key)) {
      throw new Error(`Missing --${key}.`);
    }
  }
  return Object.fromEntries(
    required.map((key) => [key, path.resolve(values.get(key))]),
  );
}

function normalizedForComparison(value) {
  const normalized = path.resolve(value);
  return process.platform === "win32"
    ? normalized.toLowerCase()
    : normalized;
}

function isStrictlyWithin(candidate, root) {
  const relative = path.relative(root, candidate);
  return (
    relative.length > 0 &&
    relative !== ".." &&
    !relative.startsWith(`..${path.sep}`) &&
    !path.isAbsolute(relative)
  );
}

async function pathExists(value) {
  try {
    await lstat(value);
    return true;
  } catch (error) {
    if (error.code === "ENOENT") {
      return false;
    }
    throw error;
  }
}

async function assertNoLinksOnExistingPath(root, candidate) {
  const relative = path.relative(root, candidate);
  if (
    relative === ".." ||
    relative.startsWith(`..${path.sep}`) ||
    path.isAbsolute(relative)
  ) {
    throw new Error(`Path is outside the required root: ${candidate}`);
  }

  let current = root;
  const segments = relative === "" ? [] : relative.split(path.sep);
  for (const segment of segments) {
    current = path.join(current, segment);
    if (!(await pathExists(current))) {
      break;
    }
    const stats = await lstat(current);
    if (stats.isSymbolicLink()) {
      throw new Error(
        `Refusing symbolic link or reparse-point path: ${current}`,
      );
    }
  }
}

async function requireDirectory(value, description) {
  const stats = await lstat(value);
  if (stats.isSymbolicLink() || !stats.isDirectory()) {
    throw new Error(`${description} must be a real directory: ${value}`);
  }
}

async function singleFileWithExtension(directory, extension) {
  const entries = await readdir(directory, { withFileTypes: true });
  const matches = entries.filter(
    (entry) =>
      entry.isFile() && path.extname(entry.name).toLowerCase() === extension,
  );
  if (matches.length !== 1) {
    throw new Error(
      `WDK package must contain exactly one ${extension} file; ` +
        `found ${matches.length}.`,
    );
  }
  return path.join(directory, matches[0].name);
}

async function main() {
  const arguments_ = parseArguments(process.argv.slice(2));
  const repositoryRoot = arguments_["repository-root"];
  const artifactRoot = arguments_["artifact-root"];
  const sourcePackage = arguments_["source-package"];
  const artifactDirectory = arguments_["artifact-directory"];

  await requireDirectory(repositoryRoot, "repository root");
  const repositoryWindowsRoot = path.join(repositoryRoot, "Windows");
  await requireDirectory(repositoryWindowsRoot, "repository Windows root");
  const expectedArtifactRoot = path.join(
    repositoryWindowsRoot,
    "artifacts",
  );
  if (
    normalizedForComparison(artifactRoot) !==
    normalizedForComparison(expectedArtifactRoot)
  ) {
    throw new Error(
      "Artifact root must be the repository-owned Windows/artifacts directory.",
    );
  }
  await assertNoLinksOnExistingPath(repositoryWindowsRoot, artifactRoot);
  if (!(await pathExists(artifactRoot))) {
    await mkdir(artifactRoot);
  }
  await requireDirectory(artifactRoot, "artifact root");

  const resolvedRepositoryRoot = await realpath(repositoryRoot);
  const windowsRoot = path.join(resolvedRepositoryRoot, "Windows");
  await requireDirectory(windowsRoot, "repository Windows root");
  const resolvedArtifactRoot = await realpath(artifactRoot);
  if (!isStrictlyWithin(resolvedArtifactRoot, resolvedRepositoryRoot)) {
    throw new Error("Resolved artifact root is outside the repository.");
  }

  if (!isStrictlyWithin(artifactDirectory, artifactRoot)) {
    throw new Error(
      "Artifact directory is outside the repository-owned artifact root.",
    );
  }
  await assertNoLinksOnExistingPath(artifactRoot, artifactDirectory);

  await requireDirectory(sourcePackage, "WDK package output");
  const resolvedSourcePackage = await realpath(sourcePackage);
  await requireDirectory(resolvedSourcePackage, "resolved WDK package output");
  const sourceInf = await singleFileWithExtension(
    resolvedSourcePackage,
    ".inf",
  );
  const sourceSys = await singleFileWithExtension(
    resolvedSourcePackage,
    ".sys",
  );
  const infText = await readFile(sourceInf, "utf8");
  const unresolvedStampToken = infText.match(
    /\$[A-Za-z_][A-Za-z0-9_]*\$/,
  );
  if (unresolvedStampToken !== null) {
    throw new Error(
      `WDK package INF is not stamped; unresolved token ` +
        `${unresolvedStampToken[0]}.`,
    );
  }

  // Re-check immediately before recursive cleanup to close a time-of-check
  // gap against an existing symlink/reparse point.
  await assertNoLinksOnExistingPath(artifactRoot, artifactDirectory);
  if (await pathExists(artifactDirectory)) {
    const resolvedArtifactDirectory = await realpath(artifactDirectory);
    if (!isStrictlyWithin(resolvedArtifactDirectory, resolvedArtifactRoot)) {
      throw new Error(
        "Resolved artifact directory is outside the artifact root.",
      );
    }
    await rm(artifactDirectory, { recursive: true, force: false });
  }

  await mkdir(artifactDirectory, { recursive: true });
  await assertNoLinksOnExistingPath(artifactRoot, artifactDirectory);
  await copyFile(sourceInf, path.join(artifactDirectory, path.basename(sourceInf)));
  await copyFile(sourceSys, path.join(artifactDirectory, path.basename(sourceSys)));

  process.stdout.write(
    `Staged exact WDK package INF and SYS: ${artifactDirectory}\n`,
  );
}

main().catch((error) => {
  process.stderr.write(`Driver package staging failed: ${error.message}\n`);
  process.exitCode = 1;
});
