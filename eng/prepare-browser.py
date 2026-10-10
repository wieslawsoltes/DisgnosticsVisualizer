"""Stage the SDK's actual runtime bundle and explicit application assets for static hosting."""
from pathlib import Path
import json
import os
import shutil

published = Path('artifacts/browser-publish')
output = Path('artifacts/site')
evidence = Path('artifacts/browser')
evidence.mkdir(parents=True, exist_ok=True)
files = sorted(str(p) for p in published.rglob('*') if p.is_file())
(evidence / 'publish-files.txt').write_text('\n'.join(files), encoding='utf-8')
frameworks = [p for p in published.rglob('dotnet.js') if p.parent.name == '_framework']
if len(frameworks) != 1:
    raise SystemExit(f'Expected one published _framework/dotnet.js, found {frameworks}; see publish-files.txt')
source = frameworks[0].parent.parent
if output.exists():
    shutil.rmtree(output)
shutil.copytree(source, output)
assets = Path('src/DiagnosticsVisualizer.Browser/wwwroot')
for path in assets.rglob('*'):
    if path.is_file():
        destination = output / path.relative_to(assets)
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, destination)
for required in ('index.html', 'main.js', 'icon.svg', '_framework/dotnet.js'):
    if not (output / required).is_file():
        raise SystemExit(f'Missing browser asset: {required}')
(output / '.nojekyll').touch()
shutil.copy2(output / 'index.html', output / '404.html')
(output / 'deployment.json').write_text(json.dumps({'commit': os.environ.get('GITHUB_SHA', 'local')}), encoding='utf-8')
print(f'Staged {sum(1 for p in output.rglob("*") if p.is_file())} files from {source}')
