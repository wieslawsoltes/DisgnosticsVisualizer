"""Stage .NET static assets, retaining SDK fingerprints instead of guessing runtime names."""
from pathlib import Path
import json
import os
import re
import shutil

published = Path('artifacts/browser-publish')
output = Path('artifacts/site')
evidence = Path('artifacts/browser')
evidence.mkdir(parents=True, exist_ok=True)
files = sorted(str(p) for p in published.rglob('*') if p.is_file())
(evidence / 'publish-files.txt').write_text('\n'.join(files), encoding='utf-8')
loaders = [p for p in published.rglob('*.js')
           if p.parent.name == '_framework' and re.fullmatch(r'dotnet(?:\.[a-zA-Z0-9_-]+)?\.js', p.name)
           and not p.name.startswith(('dotnet.native.', 'dotnet.runtime.'))]
if len(loaders) != 1:
    raise SystemExit(f'Expected one published .NET loader, found {loaders}; see publish-files.txt')
loader = loaders[0]
source = loader.parent.parent
if output.exists():
    shutil.rmtree(output)
shutil.copytree(source, output)
assets = Path('src/DiagnosticsVisualizer.Browser/wwwroot')
for path in assets.rglob('*'):
    if path.is_file():
        destination = output / path.relative_to(assets)
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, destination)
        # App assets are deliberately replaced; do not retain stale precompressed bytes.
        for suffix in ('.br', '.gz'):
            Path(str(destination) + suffix).unlink(missing_ok=True)
main = output / 'main.js'
text = main.read_text(encoding='utf-8')
assert "'./_framework/dotnet.js'" in text, 'Missing bootstrap import'
main.write_text(text.replace("'./_framework/dotnet.js'", repr('./_framework/' + loader.name)), encoding='utf-8')
for required in ('index.html', 'main.js', 'icon.svg', '_framework/' + loader.name):
    if not (output / required).is_file():
        raise SystemExit(f'Missing browser asset: {required}')
(output / '.nojekyll').touch()
shutil.copy2(output / 'index.html', output / '404.html')
(output / 'deployment.json').write_text(json.dumps({'commit': os.environ.get('GITHUB_SHA', 'local')}), encoding='utf-8')
print(f'Staged {sum(1 for p in output.rglob("*") if p.is_file())} files; runtime loader: {loader.name}')
