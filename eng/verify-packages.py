"""Validate the produced packages, including the executable MSBuild weaver payload."""
import pathlib
import sys
import xml.etree.ElementTree as ET
import zipfile

root = pathlib.Path(sys.argv[1])
version = sys.argv[2]
packages = ('Core', 'Protocol', 'Runtime', 'Analysis', 'Transport', 'Agent.Avalonia', 'Charts', 'Controls', 'Workbench', 'Build')
ns = {'n': 'http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd'}
for suffix in packages:
    package_id = 'DiagnosticsVisualizer.' + suffix
    path = root / f'{package_id}.{version}.nupkg'
    if not path.is_file():
        raise SystemExit(f'Missing package: {path}')
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        manifest = ET.fromstring(archive.read(next(n for n in names if n.endswith('.nuspec'))))
        metadata = next(e for e in manifest if e.tag.split('}')[-1] == 'metadata')
        values = {e.tag.split('}')[-1]: e.text for e in metadata}
        assert values['id'] == package_id, path
        assert values['version'] == version, path
        if suffix == 'Build':
            for required in ('buildTransitive/DiagnosticsVisualizer.Build.targets',
                             'tools/net8.0/any/DiagnosticsVisualizer.Weaver.dll',
                             'tools/net8.0/any/DiagnosticsVisualizer.Weaver.runtimeconfig.json',
                             'tools/net8.0/any/DiagnosticsVisualizer.Runtime.dll',
                             'tools/net8.0/any/DiagnosticsVisualizer.Core.dll',
                             'tools/net8.0/any/Mono.Cecil.dll'):
                assert required in names, (path, required)
        else:
            assert f'lib/net8.0/{package_id}.dll' in names, path
        for element in metadata.iter():
            if element.tag.split('}')[-1] == 'dependency' and element.attrib['id'].startswith('DiagnosticsVisualizer.'):
                assert element.attrib['version'].strip('[]') == version, (path, element.attrib)
    print(f'Validated {path.name}')
