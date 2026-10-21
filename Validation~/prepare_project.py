#!/usr/bin/env python3
"""Create a disposable URP smoke-test project from an installed Editor template."""
import argparse,json,pathlib,re,subprocess,tarfile
parser=argparse.ArgumentParser()
parser.add_argument('--version',required=True)
parser.add_argument('--project',required=True,type=pathlib.Path)
parser.add_argument('--package',required=True,type=pathlib.Path)
args=parser.parse_args()
editor_data=json.loads(subprocess.check_output(['unity','editors','path',args.version,'--format','json'],text=True))
editor=pathlib.Path(editor_data['data']['path'])/'Unity.app'
templates=editor/'Contents/Resources/PackageManager/ProjectTemplates'
candidates=list(templates.glob('*urp-blank*.tgz')) or list(templates.glob('*3d-cross-platform*.tgz'))
if not candidates:raise RuntimeError('No URP template found')
project=args.project.resolve()
if project.exists():raise RuntimeError('Use a new project directory; refusing to overwrite existing files')
project.mkdir(parents=True)
with tarfile.open(candidates[0]) as archive:
 for member in archive.getmembers():
  prefix='package/ProjectData~/'
  if member.isfile() and member.name.startswith(prefix):
   relative=pathlib.PurePosixPath(member.name[len(prefix):])
   if '..' in relative.parts:raise RuntimeError('Unsafe archive path')
   destination=project/relative
   destination.parent.mkdir(parents=True,exist_ok=True)
   destination.write_bytes(archive.extractfile(member).read())
manifest=project/'Packages/manifest.json'
data=json.loads(manifest.read_text())
data['dependencies']['com.qualcomm.snapdragon.sgsr.for.unity']='file:'+str(args.package.resolve())
manifest.write_text(json.dumps(data,indent=2)+'\n')
settings=project/'ProjectSettings/ProjectSettings.asset'
s=settings.read_text()
s=re.sub(r'  scriptingDefineSymbols:.*?(?=\n  \w)', '  scriptingDefineSymbols:\n    Standalone: ENABLE_UPSCALER_FRAMEWORK\n    Android: ENABLE_UPSCALER_FRAMEWORK',s,flags=re.S)
settings.write_text(s)
runner=project/'Assets/SGSRValidation/SGSRValidationRunner.cs'
runner.parent.mkdir(parents=True,exist_ok=True)
runner.write_bytes((pathlib.Path(__file__).parent/'SGSRValidationRunner.cs').read_bytes())
print(json.dumps({'project':str(project),'editor':str(editor/'Contents/MacOS/Unity'),'template':str(candidates[0])}))
