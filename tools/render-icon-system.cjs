// Rasterize the code-native vector specimen and compare converted paths to upstream SVGs.
// Requires sharp on NODE_PATH. Does not compile or run the Musync application.
const fs = require('node:fs');
const path = require('node:path');
const sharp = require('sharp');
const root = path.resolve(__dirname, '..');
const assets = path.join(root, 'src/Musync.App/Assets/Icons/Lucide');
const manifest = JSON.parse(fs.readFileSync(path.join(assets, 'manifest.json'), 'utf8'));
const generated = fs.readFileSync(path.join(root, 'src/Musync.App/Controls/LucideGeometry.cs'), 'utf8');
const geometry = new Map([...generated.matchAll(/\["([^"]+)"\] = \[(.*?)\],/g)]
  .map(match => [match[1], JSON.parse(`[${match[2]}]`)]));

(async () => {
  const comparisons = [];
  for (const asset of manifest.assets) {
    const original = fs.readFileSync(path.join(assets, asset.file), 'utf8');
    const converted = `<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">`
      + geometry.get(asset.name).map(d => `<path d="${d}"/>`).join('') + '</svg>';
    for (const size of [16, 20, 24]) {
      const render = async svg => {
        const resized = svg.replace(/width="24"/, `width="${size}"`).replace(/height="24"/, `height="${size}"`);
        return sharp(Buffer.from(resized)).ensureAlpha().raw().toBuffer();
      };
      const [a, b] = await Promise.all([render(original), render(converted)]);
      let totalDelta = 0;
      for (let i = 0; i < a.length; i++) totalDelta += Math.abs(a[i] - b[i]);
      const meanChannelDelta = totalDelta / a.length;
      comparisons.push({name: asset.name, size, meanChannelDelta});
      if (meanChannelDelta > 2) throw new Error(`${asset.name} at ${size}px differs from upstream (${meanChannelDelta})`);
    }
  }
  const preview = path.join(root, 'design/icon-system-preview.png');
  await sharp(path.join(root, 'design/icon-system-preview.svg')).png().toFile(preview);
  const metadata = await sharp(preview).metadata();
  const report = {
    scope: 'Static vector rendering; not Avalonia runtime or compiled application validation',
    upstreamRevision: manifest.revision,
    iconCount: manifest.assets.length,
    displayedSizes: [16, 20, 24],
    comparisonCount: comparisons.length,
    comparison: 'Rendered source-derived SVG paths vs unmodified upstream SVG at each displayed size',
    worstMeanChannelDelta: Math.max(...comparisons.map(c => c.meanChannelDelta)),
    allowedMeanChannelDelta: 2,
    preview: {file: path.basename(preview), width: metadata.width, height: metadata.height},
    applicationBuildPerformed: false,
    comparisons,
  };
  fs.writeFileSync(path.join(root, 'design/icon-render-validation.json'), JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify({icons: report.iconCount, comparisons: report.comparisonCount,
    worstMeanChannelDelta: report.worstMeanChannelDelta, preview: report.preview, applicationBuildPerformed: false}));
})().catch(error => { console.error(error.message); process.exitCode = 1; });
