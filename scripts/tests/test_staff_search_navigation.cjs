// Real Razor exports and application assets, synthetic fixtures only; no production access.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const root=process.env.REQUESTPORTAL_STAFF_NAV_RENDER_OUTPUT;
if(!root)throw new Error('Set REQUESTPORTAL_STAFF_NAV_RENDER_OUTPUT to the StaffSearchNavigationPageTests exports.');
const assets=path.resolve(__dirname,'../../RequestPortal.Web/wwwroot');
const organization='22222222-2222-4222-8222-222222222222',request='33333333-3333-4333-8333-333333333333';
const orgDoc='44444444-4444-4444-8444-444444444444',requestDoc='55555555-5555-4555-8555-555555555555';
(async()=>{
 const browser=await chromium.launch({headless:true,...(process.env.CHROME_EXECUTABLE_PATH?{executablePath:process.env.CHROME_EXECUTABLE_PATH}:{})});
 try{
  const page=await browser.newPage();let errors=[];page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text())});
  await page.route('**/*',route=>{
   const req=route.request(),url=new URL(req.url());
   if(req.resourceType()==='document'){
    let name=url.pathname==='/Staff/Integrations'?(url.searchParams.has('fixtureReviewer')?'integrations-reviewer':'integrations'):url.pathname==='/Staff/Organizations'?'organizations':url.pathname.startsWith('/Staff/Review/')?'request-document':url.pathname==='/Staff/Organization'?'organization-document':url.searchParams.has('Search')?(url.searchParams.get('PageSize')==='50'?'search-50':'search'):url.searchParams.get('PageSize')==='50'?'queue-size-50':url.searchParams.get('PageSize')==='100'?'queue-size-100':url.searchParams.get('PageNumber')==='2'?'queue-page-2':'queue';
    return route.fulfill({contentType:'text/html',body:fs.readFileSync(path.join(root,name+'.html'))});
   }
   if(url.pathname.endsWith('/messages.js')||url.hostname==='cdnjs.cloudflare.com')return route.fulfill({contentType:'text/javascript',body:''});
   if(req.resourceType()==='image')return route.fulfill({contentType:'image/png',body:Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jR5kAAAAASUVORK5CYII=','base64')});
   const file=path.join(assets,url.pathname);if(fs.existsSync(file)&&fs.statSync(file).isFile())return route.fulfill({contentType:url.pathname.endsWith('.css')?'text/css':'text/javascript',body:fs.readFileSync(file)});
   return route.fulfill({status:204,body:''});
  });
  let cases=0;
  for(const width of [1440,768,390]){
   await page.setViewportSize({width,height:900});errors=[];
   await page.goto('http://127.0.0.1:5499/Staff');
   assert.equal(await page.locator('.request-panel tbody tr').count(),25);
   assert.match(await page.locator('.staff-pagination').innerText(),/Showing 1–25 of 46 requests/);
   await page.locator('.staff-pagination').getByRole('link',{name:'Next',exact:true}).click();await page.waitForURL('**PageNumber=2');
   assert.equal(new URL(page.url()).searchParams.get('PageSize'),'25');
   assert.equal(await page.locator('.request-panel tbody tr').count(),21);
   await page.selectOption('#request-page-size','50');await page.getByRole('button',{name:'Apply page size',exact:true}).click();await page.waitForURL('**PageSize=50');
   assert(!new URL(page.url()).searchParams.has('PageNumber'));
   assert.equal(await page.locator('.request-panel tbody tr').count(),46);
   await page.getByRole('textbox',{name:'Search by applicant name or request reference',exact:true}).fill('John');
   await page.getByRole('button',{name:'Search',exact:true}).click();await page.waitForURL(url=>url.pathname==='/Staff'&&url.searchParams.get('Search')==='John');
   assert(await page.getByRole('cell',{name:'John Brown'}).count()>0);
   await page.getByRole('link',{name:'Next',exact:true}).click();await page.waitForURL('**PageNumber=2');
   assert.equal(new URL(page.url()).searchParams.get('PageSize'),'50');
   assert.equal(new URL(page.url()).searchParams.get('Search'),'John');
   await page.getByRole('link',{name:'Clear Search',exact:true}).click();await page.waitForURL('**/Staff');
   for(const index of [0,1,2,3])for(const mode of ['row','link','keyboard']){
    await page.goto('http://127.0.0.1:5499/Staff/Organizations');
    const row=page.locator('tr[data-row-href]').nth(index),dest=await row.getAttribute('data-row-href');
    if(mode==='row')await row.locator('td').first().click();
    else if(mode==='link')await row.locator('a').click();
    else {await row.focus();await page.keyboard.press('Enter');}
    await page.waitForURL('**'+dest);
    const url=new URL(page.url());
    if(index>=2){
     const doc=index===2?orgDoc:requestDoc;
     assert.equal(url.hash,'#document-'+doc.replaceAll('-',''));
     assert.equal(url.searchParams.get('selectedDocumentId'),doc);
     assert.equal(url.pathname,index===2?'/Staff/Organization':'/Staff/Review/'+request);
     if(index===2)assert.equal(url.searchParams.get('id'),organization);
     const selected=page.locator('.selected-document');assert.equal(await selected.count(),1);
     const box=await selected.boundingBox();assert(box.y<900&&box.y+box.height>0);
     assert.equal(await selected.evaluate(el=>getComputedStyle(el).outlineWidth),'2px');
     assert(await selected.getByRole('button',{name:'Verify',exact:true}).count()>0);
    }
    assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Page-wide overflow at '+width+' '+dest);
    cases++;
   }
   assert.deepEqual(errors,[]);
   await page.screenshot({path:path.join(root,`staff-documents-${width}.png`),fullPage:true});
   for(const reviewer of [false,true]) {
    await page.goto('http://127.0.0.1:5499/Staff/Integrations'+(reviewer?'?fixtureReviewer=true':''));
    assert.equal(await page.getByRole('heading',{name:'Integrations',exact:true}).count(),1);
    await page.getByRole('button',{name:'View Full Diagnostics',exact:true}).click();
    assert.equal(await page.getByRole('heading',{name:'Attempt history',exact:true}).count(),1);
    assert.equal(await page.locator('#return-events-title').count(),1);
    assert.equal(await page.getByRole('button',{name:'Retry Integration',exact:true}).count(),reviewer?0:1);
    assert.equal(await page.getByRole('link',{name:/export.*csv|export complete attempt history/i}).count(),0);
    assert.equal(await page.locator('a[href*="handler=Export"],a[href*="handler=HistoryExport"]').count(),0);
    const requestLink=await page.getByRole('link',{name:'Open original request',exact:true}).getAttribute('href');
    assert.equal(requestLink,'/Staff/Review/'+request);
    assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Integration page-wide overflow at '+width);
    assert.deepEqual(errors,[]);
    await page.screenshot({path:path.join(root,`integrations-${reviewer?'reviewer':'admin'}-${width}.png`),fullPage:true});
    cases++;
   }
  }
  console.log(`${cases} browser navigation cases passed at desktop/tablet/mobile: row, explicit link, and Enter activation; organization and request document anchors/highlights, search, pagination, integration diagnostics, CSV export absence and Administrator/Reviewer controls, and clear; no console errors or page-wide overflow.`);
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
