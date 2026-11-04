// Real Razor exports and application assets; synthetic messages only. No production requests or writes.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const rendered=process.env.REQUESTPORTAL_MESSAGES_RENDER_OUTPUT;
if(!rendered)throw new Error('Set REQUESTPORTAL_MESSAGES_RENDER_OUTPUT to MessageNavigationPageTests HTML exports.');
const assets=process.env.MESSAGE_ASSET_ROOT||path.resolve(__dirname,'../../RequestPortal.Web/wwwroot');
const ids=['44444444-4444-4444-8444-444444444444','55555555-5555-4555-8555-555555555555'];
const folders=['Inbox','Assigned','Unassigned','Sent','Archived'];
const png=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jR5kAAAAASUVORK5CYII=','base64');
(async()=>{
 const browser=await chromium.launch({headless:true,...(process.env.CHROME_EXECUTABLE_PATH?{executablePath:process.env.CHROME_EXECUTABLE_PATH}:{})});
 try{
 const page=await browser.newPage();let errors=[],origin='Inbox';
 page.on('pageerror',error=>errors.push(error.message));
 page.on('console',message=>{if(message.type()==='error')errors.push(message.text())});
 await page.route('**/*',route=>{
  const req=route.request(),url=new URL(req.url());
  if(req.resourceType()==='document'){
   let name=url.pathname.split('/').pop();
   if(url.pathname.startsWith('/Messages/Thread/'))name=(url.searchParams.get('returnTo')?.split('/').pop()||origin)+(name===ids[0]?'-linked':'-standalone');
   const file=path.join(rendered,name+'.html');
   return route.fulfill({status:fs.existsSync(file)?200:404,contentType:'text/html',body:fs.existsSync(file)?fs.readFileSync(file):'Not found'});
  }
  if(url.pathname.endsWith('/messages.js')||url.hostname==='cdnjs.cloudflare.com')return route.fulfill({contentType:'text/javascript',body:''});
  if(req.resourceType()==='image')return route.fulfill({contentType:'image/png',body:png});
  const file=path.join(assets,url.pathname);
  if(fs.existsSync(file)&&fs.statSync(file).isFile())return route.fulfill({contentType:url.pathname.endsWith('.css')?'text/css':'text/javascript',body:fs.readFileSync(file)});
  return route.fulfill({status:204,body:''});
 });
 let passed=0;
 for(const width of [1440,390])for(const folder of folders)for(let index=0;index<2;index++){
  origin=folder;errors=[];await page.setViewportSize({width,height:900});await page.goto('http://127.0.0.1:5499/Messages/'+folder);
  const row=page.locator('.outlook-message-row').nth(index);
  assert.equal(new URL(await row.getAttribute('href'),'http://127.0.0.1:5499').pathname,'/Messages/Thread/'+ids[index]);
  await row.locator('.outlook-subject strong').click();await page.waitForURL('**/Messages/Thread/'+ids[index]+'?*');
  assert.equal(new URL(page.url()).searchParams.get('returnTo'),'/Messages/'+folder);
  assert.equal(await page.locator('.conversation-heading h1').innerText(),index===0?'Linked selected subject':'Standalone selected subject');
  assert.equal(await page.locator('.message-body').count(),2);
  const view=page.getByRole('link',{name:'View Request',exact:true});
  if(index===0){assert.equal(await view.getAttribute('href'),'/Staff/Review/66666666-6666-4666-8666-666666666666');assert(await page.getByText('RP-MESSAGE-FIXTURE',{exact:false}).count()>0)}else assert.equal(await view.count(),0);
  assert.equal(await page.locator('.message-attachment').count(),1);
  assert.equal(await page.locator('.conversation-composer').count(),folder==='Archived'?0:1);
  assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
  await page.getByRole('link',{name:'Back to mailbox',exact:true}).click();await page.waitForURL('**/Messages/'+folder);
  assert.equal(await page.locator('.outlook-message-row').count(),2);assert.deepEqual(errors,[]);passed++;
 }
 console.log(`${passed} real Razor browser click cases passed: 5 folders × linked/standalone × desktop/mobile; exact thread, history, request link, attachment link, and originating-folder return verified with no console errors.`);
 }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1});
