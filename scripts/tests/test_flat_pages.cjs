// Browser checks use actual Razor exports, local assets, and synthetic records only.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const rendered=process.env.REQUESTPORTAL_IDENTITY_RENDER_OUTPUT;if(!rendered)throw new Error('Set REQUESTPORTAL_IDENTITY_RENDER_OUTPUT to ApplicantIdentityRecoveryTests exports.');
const assets=path.resolve(__dirname,'../../RequestPortal.Web/wwwroot');
(async()=>{
 const browser=await chromium.launch({headless:true,...(process.env.CHROME_EXECUTABLE_PATH?{executablePath:process.env.CHROME_EXECUTABLE_PATH}:{})});
 try{
  const page=await browser.newPage();let errors=[];page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text())});
  await page.route('**/*',route=>{
   const req=route.request(),url=new URL(req.url());
   if(req.resourceType()==='document'){
    let name=url.pathname==='/Account/IdentityRecovery'?'recovery':url.pathname==='/Administration/ApplicantAccounts'?(url.searchParams.has('Id')?'identity-details':'identity-search'):
      url.searchParams.get('CountryCode')==='US'?'reports-empty':url.searchParams.get('Search')==='Pagination'?'reports-paged':!url.searchParams.has('Search')?'reports-clear':'reports';
    return route.fulfill({contentType:'text/html',body:fs.readFileSync(path.join(rendered,name+'.html'))});
   }
   if(url.pathname.endsWith('/messages.js')||url.hostname==='cdnjs.cloudflare.com')return route.fulfill({contentType:'text/javascript',body:''});
   if(req.resourceType()==='image')return route.fulfill({contentType:'image/png',body:Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jR5kAAAAASUVORK5CYII=','base64')});
   const file=path.join(assets,url.pathname);if(fs.existsSync(file)&&fs.statSync(file).isFile())return route.fulfill({contentType:url.pathname.endsWith('.css')?'text/css':'text/javascript',body:fs.readFileSync(file)});
   return route.fulfill({status:204,body:''});
  });
  async function assertFits(){const metrics=await page.evaluate(()=>({width:innerWidth,scroll:document.documentElement.scrollWidth,path:location.pathname,overflow:[...document.querySelectorAll('body *')].filter(el=>el.getBoundingClientRect().right>innerWidth+1).slice(0,8).map(el=>({tag:el.tagName,cls:el.className,width:el.getBoundingClientRect().width}))}));assert(metrics.scroll<=metrics.width,JSON.stringify(metrics));}
  let cases=0;
  for(const width of [1440,768,390,320]){
   await page.setViewportSize({width,height:1000});errors=[];
   await page.goto('http://127.0.0.1:5499/Staff/Reports?Search=Filter%20fixture');
   assert.equal(await page.locator('.staff-reports .card').count(),0);assert.equal(await page.locator('.reports-summary>div').count(),3);
   for(const element of await page.locator('.reports-summary>div,.reports-results,.reports-filters').all())assert(await element.evaluate(el=>getComputedStyle(el).boxShadow==='none'));
   const values={};for(const key of ['Search','Status','CountryCode','Category','Priority','FromDate','ToDate'])values[key]=await page.locator('[name="'+key+'"]').inputValue();
   await page.getByRole('button',{name:'Apply Filters',exact:true}).click();await page.waitForURL('**/Staff/Reports?*');
   const query=new URL(page.url()).searchParams;for(const [key,value]of Object.entries(values))assert.equal(query.get(key),value);
   assert.equal(await page.locator('.reports-exports a').count(),4);
   for(const link of await page.locator('.reports-exports a').all()){const target=new URL(await link.getAttribute('href'),'http://127.0.0.1:5499');for(const key of Object.keys(values))assert.equal(target.searchParams.get(key),values[key]);}
   await assertFits();
   await page.screenshot({path:path.join(rendered,`reports-${width}.png`),fullPage:true});
   await page.getByRole('link',{name:'Clear',exact:true}).click();await page.waitForURL('**/Staff/Reports');assert.equal(await page.locator('[name="Search"]').inputValue(),'');
   await page.goto('http://127.0.0.1:5499/Staff/Reports?Search=Pagination&PageNumber=2');await page.getByRole('link',{name:'Previous',exact:true}).click();await page.waitForURL('**PageNumber=1*');assert.equal(new URL(page.url()).searchParams.get('Search'),'Pagination');
   await page.goto('http://127.0.0.1:5499/Staff/Reports?CountryCode=US');assert(await page.getByText('No requests match the selected filters.').count()>0);
   for(const url of ['/Administration/ApplicantAccounts','/Administration/ApplicantAccounts?Id=fixture']){
    await page.goto('http://127.0.0.1:5499'+url);assert.equal(await page.getByRole('heading',{name:'Account & Identity Management',exact:true}).count(),1);
    await assertFits();assert(!await page.locator('body').innerText().then(text=>text.includes('subject_id')));
   }
   await page.goto('http://127.0.0.1:5499/Account/IdentityRecovery#token=synthetic-invitation-code');await page.waitForFunction(()=>location.hash==='');
   assert.equal(await page.locator('[name="Token"]').inputValue(),'synthetic-invitation-code');assert.equal(new URL(page.url()).search,'');
   await assertFits();assert.deepEqual(errors,[]);cases++;
  }
  console.log(`${cases} viewport scenarios passed: flat Reports filters/summary/exports, clear and pagination, empty state, identity directory/details, fragment privacy, desktop/tablet/mobile overflow, and console checks.`);
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
