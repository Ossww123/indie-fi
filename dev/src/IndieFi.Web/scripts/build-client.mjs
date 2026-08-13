import { readdir, rm } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { build, context } from "esbuild";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const entryRoot = path.join(projectRoot, "ClientApp", "entries");
const outputParent = path.join(projectRoot, "wwwroot", "js");
const outputRoot = path.join(outputParent, "dist");

if (path.dirname(outputRoot) !== outputParent) {
  throw new Error(`Refusing to clean unexpected output directory: ${outputRoot}`);
}

const entryPoints = await findEntryPoints(entryRoot);

if (entryPoints.length === 0) {
  throw new Error(`No TypeScript entry points found under ${entryRoot}`);
}

await rm(outputRoot, { recursive: true, force: true });

const options = {
  absWorkingDir: projectRoot,
  entryPoints,
  outbase: entryRoot,
  outdir: outputRoot,
  entryNames: "[dir]/[name]",
  bundle: true,
  format: "esm",
  platform: "browser",
  target: "es2022",
  sourcemap: true,
  minify: process.argv.includes("--minify"),
  charset: "utf8",
  logLevel: "info",
};

if (process.argv.includes("--watch")) {
  const buildContext = await context(options);
  await buildContext.watch();
  console.log(`Watching ${entryPoints.length} client entry point(s).`);
} else {
  await build(options);
}

async function findEntryPoints(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = await Promise.all(
    entries.map(async (entry) => {
      const fullPath = path.join(directory, entry.name);

      if (entry.isDirectory()) {
        return findEntryPoints(fullPath);
      }

      if (entry.isFile() && entry.name.endsWith(".ts") && !entry.name.endsWith(".d.ts")) {
        return [fullPath];
      }

      return [];
    }),
  );

  return files.flat().sort();
}
