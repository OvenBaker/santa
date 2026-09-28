#!/usr/bin/env python3
"""Offline integration check: two account roots, one index, correct raw export."""
import pathlib,tempfile,subprocess,os,json,sqlite3,sys
cli=pathlib.Path(sys.argv[1]).resolve()
with tempfile.TemporaryDirectory(prefix='santa-profiles-') as temp:
 home=pathlib.Path(temp); registry=home/'.config/cockpit/accounts';registry.mkdir(parents=True)
 beta=home/'beta';(registry/'work2.configdir').write_text(str(beta))
 ids=['11111111-1111-4111-8111-111111111111','22222222-2222-4222-8222-222222222222']
 for root,sid in zip([home/'.claude',beta],ids):
  p=root/'projects'/'-fixture';p.mkdir(parents=True)
  records=[{'type':'user','timestamp':'2026-09-28T10:00:00Z','sessionId':sid,'cwd':'/fixture','message':{'role':'user','content':'Profile fixture '+sid}}, {'type':'assistant','timestamp':'2026-09-28T10:00:01Z','sessionId':sid,'cwd':'/fixture','message':{'id':'reply-'+sid,'role':'assistant','model':'fixture','content':[{'type':'text','text':'Fixture response.'}],'usage':{'input_tokens':10,'output_tokens':3}}}]
  (p/(sid+'.jsonl')).write_text(''.join(json.dumps(r)+'\n' for r in records))
 env=os.environ|{'COCKPIT_ACCOUNTS_DIR':str(registry),'DOTNET_CLI_HOME':str(home)}
 db=home/'index.db'
 def run(*args):
  r=subprocess.run(['dotnet',str(cli),*args],env=env,capture_output=True,text=True)
  assert r.returncode==0,r.stdout+r.stderr
  return r.stdout
 run('ingest','--projects',str(home/'.claude/projects'),'--db',str(db),'--no-codex','--provider','keyword-only')
 with sqlite3.connect(db) as conn:
  rows=conn.execute('select id,account,source_path from sessions order by id').fetchall()
  assert len(rows)==2,rows
  assert rows[0][1]=='legacy' and rows[1][1]=='work2',rows
  assert rows[1][2].startswith(str(beta)),rows
 text=run('export',ids[1],'--db',str(db),'--format','jsonl')
 assert ids[1] in text and ids[0] not in text,text
 run('ingest','--projects',str(home/'.claude/projects'),'--db',str(db),'--no-codex','--provider','keyword-only')
 with sqlite3.connect(db) as conn: assert conn.execute('select count(*) from sessions').fetchone()[0]==2
 print('PASS: both roots indexed; account/source retained; Beta export correct; repeat ingest deduplicated')
