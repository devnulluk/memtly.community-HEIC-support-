// Fetch the original wedding website's public, OFL-licensed font families at
// image-build time. Serve locally: guests make no Google/CDN requests and the
// existing self-only Content Security Policy remains unchanged.
'use strict';
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const revision = 'e7db6ca603fd4862010383ff17ffd6cc4ffac1c0';
const files = [
  ['ofl/cormorantgaramond/CormorantGaramond[wght].ttf', 'cormorant-garamond.ttf', 'd992a83ce525c330fad3a19087746bcb2dc038ee'],
  ['ofl/cormorantgaramond/OFL.txt', 'cormorant-garamond-OFL.txt', '507d70f4565352dbfcf2dfc9b42eb092b57c0be8'],
  ['ofl/librebaskerville/LibreBaskerville[wght].ttf', 'libre-baskerville.ttf', 'eae4eba83203138da3a3a7603b598b7ebc5a5238'],
  ['ofl/librebaskerville/OFL.txt', 'libre-baskerville-OFL.txt', '92b1e6ca1cfa6f9dfe4ec67468596606fc334c4f'],
  ['ofl/greatvibes/GreatVibes-Regular.ttf', 'great-vibes.ttf', 'a1327ff3b862246f1f253639040edcc473e5f3a9'],
  ['ofl/greatvibes/OFL.txt', 'great-vibes-OFL.txt', '70025926f5684ebed9d119424b178faa4e0a5cc6']
];
(async () => {
  const output = path.resolve('Memtly.Community/wwwroot/wedding/assets/fonts');
  await fs.mkdir(output, {recursive:true});
  for (const [source, name, expected] of files) {
    const url = `https://raw.githubusercontent.com/google/fonts/${revision}/${source.split('/').map(encodeURIComponent).join('/')}`;
    let data;
    for (let attempt = 0; attempt < 3; attempt++) {
      try {
        const response = await fetch(url, {signal:AbortSignal.timeout(45000)});
        if (!response.ok) throw new Error(`${name}: HTTP ${response.status}`);
        const chunks = []; let length = 0;
        for await (const chunk of response.body) {
          length += chunk.length;
          if (length > 3 * 1024 * 1024) throw new Error('Unexpectedly large font asset');
          chunks.push(chunk);
        }
        data = Buffer.concat(chunks);
        const actual = crypto.createHash('sha1').update(`blob ${data.length}\0`).update(data).digest('hex');
        if (actual !== expected) throw new Error(`Pinned asset digest mismatch: ${name}`);
        break;
      } catch (error) {
        if (attempt === 2) throw error;
        await new Promise(resolve => setTimeout(resolve, 1000 * (attempt + 1)));
      }
    }
    await fs.writeFile(path.join(output, name), data);
    console.log(`Verified wedding typography asset: ${name}`);
  }
})().catch(error => { console.error(error.message); process.exit(1); });
