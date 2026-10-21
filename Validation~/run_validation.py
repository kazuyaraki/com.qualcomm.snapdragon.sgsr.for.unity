#!/usr/bin/env python3
"""Run the macOS Editor rendering smoke test; a failed image check returns 2."""
import argparse,json,os,pathlib,subprocess,sys
parser=argparse.ArgumentParser()
parser.add_argument('--version',required=True)
parser.add_argument('--project',required=True,type=pathlib.Path)
parser.add_argument('--results',required=True,type=pathlib.Path)
parser.add_argument('--package',type=pathlib.Path,default=pathlib.Path(__file__).resolve().parents[1])
args=parser.parse_args();here=pathlib.Path(__file__).resolve().parent
project=args.project.resolve();results=args.results.resolve();results.mkdir(parents=True,exist_ok=True)
if not project.exists():
 subprocess.run([sys.executable,str(here/'prepare_project.py'),'--version',args.version,'--project',str(project),'--package',str(args.package.resolve())],check=True)
else:
 runner=project/'Assets/SGSRValidation/SGSRValidationRunner.cs'
 if not runner.exists():raise SystemExit('Refusing to modify a project not created by this validation tool')
 runner.write_bytes((here/'SGSRValidationRunner.cs').read_bytes())
data=json.loads(subprocess.check_output(['unity','editors','path',args.version,'--format','json'],text=True))
editor=pathlib.Path(data['data']['path'])/'Unity.app/Contents/MacOS/Unity'
env=dict(os.environ,SGSR_RESULTS=str(results))
command=[str(editor),'-batchmode','-projectPath',str(project),'-accept-apiupdate']
render=subprocess.run(command+['-executeMethod','SGSRValidationRunner.Run','-logFile',str(results/'render.log')],env=env)
if render.returncode not in [0,2]:raise SystemExit(render.returncode)
if int(args.version.split('.')[1])>=5:
 migration=subprocess.run(command+['-nographics','-quit','-executeMethod','SGSRValidationRunner.CheckUpgrade','-logFile',str(results/'migration.log')],env=env)
 if migration.returncode:raise SystemExit(migration.returncode)
report=json.loads((results/'report.json').read_text())
print(json.dumps({'unity':report['unity'],'renderPassed':report['passed'],'samples':len(report['samples']),'failedImages':[x['variant']+'/'+x['stage'] for x in report['samples'] if not x['passed']],'errors':len(report['errors'])},indent=2))
raise SystemExit(render.returncode)
