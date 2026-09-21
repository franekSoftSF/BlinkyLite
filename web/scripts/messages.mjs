// Turns the one message catalogue (Messages.resx and its three translations)
// into JSON the web application can load.
//
// At build time rather than from an endpoint: the login page needs its words
// before anybody has signed in, and the repository allows exactly two
// anonymous endpoints (/health and /api/auth/login). A third one for text
// would be a rule broken for a convenience. This keeps one catalogue and no
// new door.
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const resources = join(here, '..', '..', 'src', 'BlinkyLite.Contracts', 'Resources');
const out = join(here, '..', 'public', 'i18n');

const languages = { en: 'Messages.resx', de: 'Messages.de.resx', sv: 'Messages.sv.resx', pl: 'Messages.pl.resx' };

// resx is XML, but its shape is fixed; a regular expression is enough and
// saves a dependency whose only job would be this.
const entry = /<data name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>/g;

const decode = (text) =>
  text
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, '&');

mkdirSync(out, { recursive: true });

for (const [code, file] of Object.entries(languages)) {
  const xml = readFileSync(join(resources, file), 'utf8');
  const messages = {};
  for (const match of xml.matchAll(entry)) {
    messages[match[1]] = decode(match[2]);
  }
  writeFileSync(join(out, `${code}.json`), JSON.stringify(messages, null, 2) + '\n', 'utf8');
  console.log(`i18n/${code}.json: ${Object.keys(messages).length} kluczy`);
}
