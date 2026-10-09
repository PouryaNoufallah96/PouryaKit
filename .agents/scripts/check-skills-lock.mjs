#!/usr/bin/env node
// Checks that every vendored skill under .agents/skills still matches the hash skills-lock.json pins for it;
// exits 1 on any that does not. check-ai-setup.py runs it.
//
//   node .agents/scripts/check-skills-lock.mjs [--write] [root]
//
// --write sets each entry's computedHash from its directory instead of checking it: the step after a skill's
// directory was replaced from its upstream. It adds and removes no entry. root defaults to the repository.
//
// The hash is the one the skills CLI writes: SHA-256 over each file's path relative to the skill directory and
// then its bytes, with the files in localeCompare order of those paths. That order is JavaScript's (it puts
// agents/openai.yaml before SKILL.md, a byte sort the other way round), which is why this check is not Python.

import { createHash } from "node:crypto";
import { existsSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

// As the CLI collects them: regular files only, so a symlink is not hashed, and no .git or node_modules directory.
function collect(dir, base, out) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name !== ".git" && entry.name !== "node_modules") collect(full, base, out);
    } else if (entry.isFile() && entry.name !== ".DS_Store") { // Finder's file is never tracked
      out.push({ path: relative(base, full).split("\\").join("/"), bytes: readFileSync(full) });
    }
  }
}

function folderHash(dir) {
  const files = [];
  collect(dir, dir, files);
  files.sort((a, b) => a.path.localeCompare(b.path));
  const hash = createHash("sha256");
  for (const file of files) {
    hash.update(file.path);
    hash.update(file.bytes);
  }
  return hash.digest("hex");
}

const args = process.argv.slice(2);
const write = args.includes("--write");
const root = resolve(args.find((arg) => arg !== "--write") ?? join(dirname(fileURLToPath(import.meta.url)), "../.."));
const lockPath = join(root, "skills-lock.json");
const text = readFileSync(lockPath, "utf8");
const lock = JSON.parse(text);
const failures = [];
const written = [];

for (const [name, entry] of Object.entries(lock.skills)) {
  const dir = join(root, ".agents/skills", name);
  if (!existsSync(dir)) {
    failures.push(`${name}: .agents/skills/${name} is missing`);
    continue;
  }
  const hash = folderHash(dir);
  if (hash === entry.computedHash) continue;
  if (write) {
    entry.computedHash = hash;
    written.push(name);
  } else {
    failures.push(`${name}: .agents/skills/${name} does not match its computedHash; re-vendor it from ${entry.source}, a vendored skill is not edited here`);
  }
}

if (written.length) {
  writeFileSync(lockPath, JSON.stringify(lock, null, 2) + (text.endsWith("\n") ? "\n" : ""));
  console.log(`wrote  ${written.join(", ")}`);
}
for (const failure of failures) console.log(`FAIL  skills-lock.json: ${failure}`);
console.log(`check-skills-lock: ${failures.length} failure(s) in ${Object.keys(lock.skills).length} skill(s)`);
process.exit(failures.length ? 1 : 0);
