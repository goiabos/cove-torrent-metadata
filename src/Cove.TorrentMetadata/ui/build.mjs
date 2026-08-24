/**
 * Bundles the extension's frontend into a single ESM module.
 *
 * Everything under `@cove/runtime/*` is marked external. Cove serves those specifiers through an import
 * map backed by the host's own React, react-query and API client, so bundling copies of them would give
 * the extension a second React instance — hooks would break at runtime in ways that look like random
 * rendering bugs. Leaving them external is what makes the extension share the host's singletons.
 *
 * The output lands next to the compiled DLL so `extension.json`'s `jsBundle` path resolves without any
 * extra copy step.
 */

import { build, context } from "esbuild";

const watch = process.argv.includes("--watch");

/** Kept in sync with ui/scripts/extension-runtime-contract.ts in the host repo. */
const coveRuntime = [
  "@cove/runtime/react",
  "@cove/runtime/react-dom",
  "@cove/runtime/react-dom-client",
  "@cove/runtime/react-jsx-runtime",
  "@cove/runtime/react-jsx-dev-runtime",
  "@cove/runtime/react-query",
  "@cove/runtime/lucide-react",
  "@cove/runtime/components",
  "@cove/runtime/api",
];

const options = {
  entryPoints: ["src/main.tsx"],
  outfile: "../dist-ui/main.js",
  bundle: true,
  format: "esm",
  target: "es2022",
  platform: "browser",
  // Classic JSX, deliberately. esbuild's automatic runtime emits `${jsxImportSource}/jsx-runtime`,
  // and the host publishes `@cove/runtime/react-jsx-runtime` — no value of jsxImportSource produces
  // that specifier, so an automatic build would emit an import the import map cannot resolve. Classic
  // instead compiles to React.createElement, and React arrives through the external import below.
  jsx: "transform",
  jsxFactory: "React.createElement",
  jsxFragment: "React.Fragment",
  external: coveRuntime,
  minify: !watch,
  sourcemap: watch ? "inline" : false,
  logLevel: "info",
};

if (watch) {
  const ctx = await context(options);
  await ctx.watch();
  console.log("[torrent-metadata-ui] watching…");
} else {
  await build(options);
}
