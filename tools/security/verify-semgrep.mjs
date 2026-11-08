import { readFileSync } from 'node:fs';

const report = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const primaryConstructor = /^\s*(?:(?:public|internal|private|protected|sealed|abstract|partial)\s+)*class\s+\w+\s*\(/;
const programExceptions = new Set([
  'var builder = WebApplication.CreateBuilder(seedAdmin ? [] : args);',
  'public partial class Program;'
]);

function knownSyntaxWarning(span) {
  const path = span.path.replaceAll('\\', '/');
  if (!/^src\/[A-Za-z0-9_./-]+\.cs$/.test(path) || path.split('/').includes('..')) return false;

  const lines = readFileSync(path, 'utf8').split(/\r?\n/);
  const start = span.start.line - 1;
  const end = span.end.line - 1;
  if (start < 0 || end < start || end >= lines.length) return false;
  if (path === 'src/chat.Api/Program.cs' && start === end && programExceptions.has(lines[start].trim())) return true;

  for (let line = start; line >= Math.max(0, start - 6); line--) {
    if (!primaryConstructor.test(lines[line])) continue;
    let last = line;
    while (last < lines.length && last <= line + 6 && !/[()]/.test(lines[last].slice(lines[last].indexOf('(') + 1))) {
      last++;
    }
    const signature = lines.slice(line, last + 1).join('\n');
    const declaration = signature.slice(signature.indexOf('(') + 1);
    return end <= last && /^[\w\s,<>.\[\]?:]*\)(?:\s*:\s*[\w\s,.<>]+)?\s*$/.test(declaration);
  }
  return false;
}

if (!Array.isArray(report.results) || !Array.isArray(report.errors) || !report.paths?.scanned?.length) {
  throw new Error('Missing Semgrep results or scanned source paths');
}

let warnings = 0;
let failed = false;
for (const result of report.results) {
  console.error(`${result.check_id} ${result.path}:${result.start.line}`);
  failed = true;
}

for (const error of report.errors) {
  const [kind, spans] = Array.isArray(error.type) ? error.type : [error.type];
  if (kind === 'PartialParsing' && error.level === 'warn' && spans?.length && spans.every(knownSyntaxWarning)) {
    warnings++;
  } else {
    console.error(`Unaccepted Semgrep diagnostic ${kind} in ${error.path ?? '(global)'}`);
    failed = true;
  }
}

console.log(`Semgrep scanned ${report.paths.scanned.length} files. Known syntax warnings: ${warnings}.`);
if (failed) process.exitCode = 1;
