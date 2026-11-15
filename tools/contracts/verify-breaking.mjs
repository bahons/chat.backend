import { readFileSync } from 'node:fs';
import assert from 'node:assert/strict';

// B06 explicitly requires these response enum extensions. No other breaking change is accepted.
// Keep the published baseline intact and compare exact pinned oasdiff findings.
const parse = (path) => JSON.parse(readFileSync(path, 'utf8').replace(/^\uFEFF/, ''));
const approved = parse(new URL('./b06-enum-extensions.json', import.meta.url));
const findings = parse(process.argv[2]);
assert.ok(Array.isArray(findings), 'Expected oasdiff JSON findings');
const keys = ['id', 'text', 'operation', 'path', 'fingerprint'];
const matches = (finding, exception) => keys.every((key) => finding[key] === exception[key]);
const errors = findings.filter((finding) => finding.level >= 3);
const unexpected = errors.filter((finding) => !approved.some((exception) => matches(finding, exception)));
for (const finding of findings) console.log(`${finding.operation} ${finding.path}: ${finding.id}: ${finding.text}`);
assert.equal(unexpected.length, 0, 'Unapproved breaking API change; inspect oasdiff report');
console.log(`Contract gate passed: ${errors.length} explicitly documented B06 enum extensions; no other breaking errors.`);
