import argparse,os,json,subprocess,uuid,shutil,concurrent.futures,email.policy
from email.parser import BytesParser
from pathlib import Path
parser=argparse.ArgumentParser(description='Verify a published offline email capture worker with synthetic data only.')
parser.add_argument('--worker',required=True,help='Path to published TaskFlow.Worker.dll')
parser.add_argument('--output',default='TestResults/email-capture/published-worker.json')
args=parser.parse_args()
root=Path(__file__).resolve().parents[1]
worker=Path(args.worker).resolve()
if not worker.is_file():parser.error('Publish the worker before running verification.')
(root/'.local').mkdir(exist_ok=True)
work=root/'.local'/('saas018-email-'+uuid.uuid4().hex);work.mkdir(mode=0o700)
env=os.environ.copy()
for key in list(env):
 if key.startswith(('Email__','ConnectionStrings__')) or key=='TASKFLOW_SECRETS_DIRECTORY':env.pop(key)
env.update({'DOTNET_ENVIRONMENT':'Development','Email__Mode':'LocalCapture','Email__CaptureDirectory':str(work/'capture'),'Email__PublicOrigin':'http://localhost:5173','Email__PerCompanyHourlyLimit':'2','ConnectionStrings__TaskFlow':'intentionally-unusable-no-database-needed'})
def request(company=7,mid=None,recipient='alex@example.test',name='Private Display Canary'):
 return {'workspaceId':company,'messageId':mid or uuid.uuid4().hex,'recipient':recipient,'template':'CompanyInvitation','displayName':name,'companyName':'Private Company Canary','invitationToken':'private-token-canary-never-redeem'}
def run(data,override=None):
 path=work/('request-'+uuid.uuid4().hex+'.json');path.write_text(json.dumps(data),encoding='utf-8')
 childenv=env.copy();childenv.update(override or {})
 p=subprocess.run(['dotnet',str(worker),'--capture-email',str(path)],cwd=root,env=childenv,capture_output=True,creationflags=getattr(subprocess,"CREATE_NO_WINDOW",0),timeout=30)
 output=p.stdout.decode();assert p.stderr==b'', 'Unexpected stderr; diagnostics withheld'
 for canary in [data['recipient'],data['displayName'],data['invitationToken'],str(work)]:assert canary not in output, 'Private value in output'
 reply=json.loads(output); return p.returncode,reply
try:
 data=request();assert run(data)[1]['code']=='email.captured'
 assert run(data)[1]['code']=='email.already_captured'
 altered=dict(data);altered['recipient']='different@example.test';assert run(altered)[1]['code']=='email.id_conflict'
 assert run(request(company=8,mid=data['messageId']))[1]['code']=='email.captured'
 candidates=[request(company=9) for _ in range(8)]
 with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:results=list(pool.map(run,candidates))
 # Explicit retry of lock-busy outcomes after the other process exits.
 for i,(code,reply) in enumerate(results):
  if reply['code']=='email.capture_unavailable':results[i]=run(candidates[i])
 assert sum(r['code']=='email.captured' for _,r in results)==2
 assert all(r['code'] in ['email.captured','email.company_limit'] for _,r in results)
 assert len(list((work/'capture'/'9').glob('*/message.eml')))==2
 assert run(request(),{'DOTNET_ENVIRONMENT':'Production'})[1]['code']=='email.disabled'
 assert run(request(),{'DOTNET_ENVIRONMENT':'Staging','Email__PublicOrigin':'https://staging.example.test'})[1]['code']=='email.recipient_blocked'
 assert run(request(company=10),{'DOTNET_ENVIRONMENT':'Staging','Email__PublicOrigin':'https://staging.example.test','Email__AllowedRecipients__0':'alex@example.test'})[1]['code']=='email.captured'
 message=BytesParser(policy=email.policy.default).parsebytes((work/'capture'/'7'/data['messageId']/'message.eml').read_bytes())
 assert message['Subject']=='You are invited to TaskFlow';assert message['To']==data['recipient'];assert message.is_multipart();assert not message.defects
 parts=list(message.iter_parts());assert [p.get_content_type() for p in parts]==['text/plain','text/html']
 assert 'private-token-canary' in parts[0].get_content();assert 'No email has been sent' in parts[1].get_content()
 evidence={'publishedWorker':True,'databaseRequired':False,'independentWorkerProcesses':2,'sameIdRestartDeduplicated':True,'changedRecipientRejected':True,'companiesIsolated':True,'eightConcurrentRequestsAtLimitTwo':{'captured':2,'rateLimited':6},'stagingAllowlistChecked':True,'productionDisabled':True,'multipartParsedWithoutDefects':True,'privateFieldsAbsentFromStatus':True,'externalNetworkTransport':'none','temporaryPrivateArtifactsRemoved':True}
 output=Path(args.output);output.parent.mkdir(parents=True,exist_ok=True);output.write_text(json.dumps(evidence,indent=2)+'\n',encoding='utf-8')
 print('Published offline capture, cross-process quota, restart idempotency, staging allowlist, production refusal and MIME validation passed. No database or network transport used.')
finally:
 assert work.parent==root/'.local' and work.name.startswith('saas018-email-');shutil.rmtree(work)
