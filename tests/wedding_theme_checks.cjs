'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
module.exports = async function checkWeddingTheme(page) {
  await page.evaluate(() => document.fonts.ready);
  const assets = await page.evaluate(() => ({
    images: [...document.querySelectorAll('.brand img,.hero-floral')].map(i => ({src:i.getAttribute('src'),ok:i.complete && i.naturalWidth > 0})),
    fonts: [...document.fonts].map(f => ({name:f.family,status:f.status})),
    text: document.body.innerText,
    bodyFont: getComputedStyle(document.body).fontFamily,
    headingFont: getComputedStyle(document.querySelector('h1')).fontFamily,
    framePosition: getComputedStyle(document.body,'::before').position,
    green: getComputedStyle(document.documentElement).getPropertyValue('--green').trim()
  }));
  assert(assets.images.length === 3 && assets.images.every(i=>i.ok), 'Original wedding artwork did not load');
  for (const family of ['Cormorant Garamond','Libre Baskerville','Great Vibes'])
    assert(assets.fonts.some(f => f.name.replaceAll('"','') === family && f.status === 'loaded'), `Font not loaded: ${family}`);
  assert(assets.bodyFont.includes('Libre Baskerville') && assets.headingFont.includes('Cormorant Garamond'));
  assert.equal(assets.green, '#18372c');
  assert.equal(assets.framePosition, 'absolute', 'Page frame must not cross content at the viewport bottom');
  assert(assets.text.includes('within your household') && assets.text.includes('wider world'));
  assert(!assets.text.includes('questionable dance moves') && !assets.text.includes('Who’s behind the camera'));
  for (const width of [320,390,1440]) {
    await page.setViewportSize({width,height:width===1440?1000:844});
    await page.evaluate(() => document.fonts.ready);
    const layout = await page.evaluate(() => ({
      viewport:innerWidth,scroll:document.documentElement.scrollWidth,
      upload:document.querySelector('#drop-zone').getBoundingClientRect().toJSON(),
      input:document.querySelector('#guest-name').getBoundingClientRect().toJSON(),
      inputFont:parseFloat(getComputedStyle(document.querySelector('#guest-name')).fontSize)
    }));
    assert(layout.scroll <= layout.viewport+1, `Horizontal overflow at ${width}px: ${JSON.stringify(layout)}`);
    assert(layout.upload.top >= 0 && layout.upload.bottom < (width===1440?1000:844), `Upload chooser below first viewport: ${width}px`);
    assert(layout.upload.width >= 210 && layout.inputFont >= 16, 'Mobile chooser/input too small');
    if (width===390 || width===1440) await page.screenshot({path:`artifacts/wedding-design-${width===390?'mobile':'desktop'}.png`,fullPage:true});
  }
  await page.setViewportSize({width:390,height:844});
  fs.writeFileSync('artifacts/wedding-design-checks.json',JSON.stringify(assets,null,2));
  console.log('PASS: original artwork, local wedding typography, concise copy, 320/390/1440px layouts and first-screen upload chooser.');
};
