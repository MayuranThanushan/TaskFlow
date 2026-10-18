const {chromium}=require('playwright-core');
(async()=>{
 const { launch, origin, artifact, submitLogin } = require('./support.cjs');
 const browser = await launch(chromium);
 try {
  const context=await browser.newContext({viewport:{width:1440,height:1000}}); const page=await context.newPage(); const errors=[];page.on('pageerror',e=>errors.push(e.message));

  async function login(){await page.getByLabel('Email address').fill(process.env.PROJECT_OWNER_EMAIL);await page.getByLabel('Password',{exact:true}).fill(process.env.PROJECT_OWNER_PASSWORD);await submitLogin(page);}
  await page.goto(origin+'/profile');await page.getByRole('heading',{name:'Sign in to TaskFlow'}).waitFor();await login();await page.getByRole('heading',{name:'Profile',exact:true}).waitFor();
  const input=page.getByLabel('Display name');if(await input.inputValue()!=='Deployment smoke')throw Error('Wrong initial name');
  await input.fill('   ');await page.getByRole('button',{name:'Save profile',exact:true}).click();if(!await input.evaluate(e=>e===document.activeElement))throw Error('Profile validation focus missing');
  await input.fill('  Alex <Team>  ');await page.getByRole('button',{name:'Save profile',exact:true}).click();await page.getByText('Profile saved.',{exact:true}).waitFor();if(await input.inputValue()!=='Alex <Team>')throw Error('Trim failed');
  await page.locator('.user-name').filter({hasText:'Alex <Team>'}).waitFor();if(await page.locator('.user-name team').count())throw Error('Unsafe name markup');
  await page.reload();await page.getByLabel('Display name').waitFor();if(await input.inputValue()!=='Alex <Team>')throw Error('Profile reload lost name');
  const persisted=await page.evaluate(async()=>await(await fetch('/api/v1/auth/me')).json());if(persisted.displayName!=='Alex <Team>'||persisted.email!==process.env.PROJECT_OWNER_EMAIL)throw Error('Incorrect persisted profile');
  const blocked=await page.evaluate(async()=>{const csrf=await(await fetch('/api/v1/auth/csrf')).json();const r=await fetch('/api/v1/users/me',{method:'PATCH',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':csrf.requestToken},body:JSON.stringify({displayName:'Injected',email:'attacker@example.test',id:'1',role:'Admin'})});return r.status;});if(blocked!==400)throw Error('Mass assignment allowed');
  await page.setViewportSize({width:390,height:844});await page.screenshot({path:artifact('profile-mobile.png'),fullPage:true});if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth))throw Error('Profile mobile overflow');
  await page.getByRole('link',{name:'Projects',exact:true}).click();await page.getByRole('link',{name:'New project',exact:true}).click();await page.getByLabel('Project name').fill('Profile name verification');await page.getByRole('button',{name:'Create project',exact:true}).click();await page.getByRole('heading',{name:'Profile name verification',exact:true}).waitFor();await page.locator('.member-list strong').filter({hasText:'Alex <Team>'}).waitFor();
  await page.getByRole('button',{name:'Sign out',exact:true}).click();await page.getByRole('heading',{name:'Sign in to TaskFlow'}).waitFor();await page.goto(origin+'/profile');await page.getByRole('heading',{name:'Sign in to TaskFlow'}).waitFor();await login();await page.getByLabel('Display name').waitFor();if(await input.inputValue()!=='Alex <Team>')throw Error('Re-login lost name');
  await page.setViewportSize({width:1440,height:1000});await page.screenshot({path:artifact('profile-desktop.png'),fullPage:true});
  if(errors.length)throw Error('Browser page error');
  console.log('Edge profile: protected deep link, validation focus, trimmed save, navigation update, reload/re-login persistence, immutable email/ID, mass-assignment denial, member name and mobile layout passed.');
 } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exit(1)});
