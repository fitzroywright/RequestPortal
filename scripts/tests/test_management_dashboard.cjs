// Offline UI regression: real Razor HTML/geography/assets; synthetic records and test tiles only.
const {chromium}=require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const assets=process.env.DASHBOARD_ASSET_ROOT||path.resolve(__dirname,'../../RequestPortal.Web/wwwroot');
const snapshots=process.env.REQUESTPORTAL_DASHBOARD_RENDER_OUTPUT;
if(!snapshots) throw new Error('Set REQUESTPORTAL_DASHBOARD_RENDER_OUTPUT to the synthetic HTML exported by ManagementDashboardPageTests.');
const output=process.env.DASHBOARD_BROWSER_OUTPUT||snapshots;
const png=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jR5kAAAAASUVORK5CYII=','base64');
(async()=>{
 const browser=await chromium.launch({...(process.env.CHROME_EXECUTABLE_PATH?{executablePath:process.env.CHROME_EXECUTABLE_PATH}:{}),headless:true,ignoreDefaultArgs:['--hide-scrollbars']});
 try{
 const context=await browser.newContext();const page=await context.newPage();let variant='full',failure='',errors=[];
 page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text())});
 await context.route('**/*',route=>{
  const req=route.request(),u=new URL(req.url());
  if(u.hostname==='tile.openstreetmap.org'){
   if(failure==='tiles')return route.abort();
   return route.fulfill({contentType:'image/png',body:png,status:failure==='tiles'?503:200});
  }
  if(req.resourceType()==='document'){
   if(u.pathname==='/Management/Requests')return route.fulfill({contentType:'text/html',body:'<h1>Filtered requests</h1>'});
   return route.fulfill({contentType:'text/html',body:fs.readFileSync(path.join(snapshots,variant+'.html'))});
  }
  if(u.pathname==='/api/messages/unread-count')return route.fulfill({contentType:'application/json',body:'{"count":0,"unreadCount":0}'});
  if(u.pathname.endsWith('/messages.js')||u.hostname==='cdnjs.cloudflare.com')return route.fulfill({contentType:'text/javascript',body:''});
  if(req.resourceType()==='image')return route.fulfill({contentType:'image/png',body:png});
  const file=path.join(assets,u.pathname);
  if(fs.existsSync(file)&&fs.statSync(file).isFile()){
   if(failure==='geography'&&u.pathname.endsWith('.geojson'))return route.fulfill({status:503,body:'unavailable'});
   const contentType=u.pathname.endsWith('.css')?'text/css':u.pathname.endsWith('.js')?'text/javascript':u.pathname.endsWith('.geojson')?'application/geo+json':'application/octet-stream';
   return route.fulfill({contentType,body:fs.readFileSync(file)});
  }
  return route.fulfill({status:204,body:''});
 });
 async function load(width,view='full'){
  variant=view;errors=[];await page.setViewportSize({width,height:1000});await page.goto('http://127.0.0.1:5499/Management');
  await page.locator('.mg-map').scrollIntoViewIfNeeded();
  await page.waitForFunction(()=>document.querySelector('.mg-map').getAttribute('aria-busy')==='false');
 }
 let cases=0;
 for(const view of ['full','zero','long'])for(const width of [1600,1440,1024,820,600,390,320]){
  await load(width,view);assert.equal(await page.locator('.mg-card').count(),0);assert(await page.locator('.mg-kpi').first().evaluate(el=>getComputedStyle(el).boxShadow==='none'&&getComputedStyle(el).backgroundColor==='rgba(0, 0, 0, 0)'));assert.equal(await page.locator('.mg-map path[role=link]').count(),14);
  const m=await page.evaluate(()=>{
   const r=s=>document.querySelector(s).getBoundingClientRect().toJSON();
   return {doc:document.documentElement.scrollWidth,kpi:getComputedStyle(document.querySelector('.mg-kpis')).gridTemplateColumns.split(' ').length,
    map:r('.mg-geography'),insights:r('.mg-insights'),container:r('.mg-map'),paths:[...document.querySelectorAll('.mg-map path[role=link]')].map(e=>e.getBoundingClientRect().toJSON())};
  });
  if(m.doc>width){console.log(await page.evaluate(()=>[...document.querySelectorAll('body *')].map(e=>({tag:e.tagName,cls:e.className,text:e.textContent.slice(0,60),r:e.getBoundingClientRect().right,w:e.getBoundingClientRect().width})).filter(e=>e.r>innerWidth+1&&e.w>0).slice(0,25)));await page.screenshot({path:path.join(output,'overflow.png'),fullPage:true});}assert(m.doc<=width,JSON.stringify({view,width,m}));assert.equal(m.kpi,width>1100?5:2);
  if(width>850){assert.equal(m.map.top,m.insights.top);assert(Math.abs(m.map.bottom-m.insights.bottom)<1)}
  else assert(m.map.bottom<=m.insights.top);
  for(const p of m.paths){assert(p.width>0);assert(p.left>=m.container.left-1);assert(p.right<=m.container.right+1);assert(p.top>=m.container.top-1);assert(p.bottom<=m.container.bottom+1)}
  assert(!(await page.locator('[data-map-retry]').isVisible()));assert(await page.locator('[data-map-message]').isHidden());
  await page.selectOption('#management-parish','St. Catherine');assert.match(await page.locator('[data-parish-selection]').innerText(),/5 total requests; 4 outstanding; 1 completed/);
  assert.match(await page.locator('[data-parish-details]').getAttribute('href'),/Management\/Parish\?parish=St/);
  await page.locator('[data-map-clear]').click();assert.equal(await page.locator('#management-parish').inputValue(),'');
  for(const canvas of await page.locator('.mg-chart canvas').all()){
   await canvas.scrollIntoViewIfNeeded();await page.waitForFunction(id=>!!window.Chart?.getChart(document.getElementById(id)),await canvas.getAttribute('id'));
  }
  const series=await page.evaluate(()=>({delivery:Chart.getChart('delivery-outcome-chart').data.datasets.map(s=>s.data),requests:Chart.getChart('received-completed-chart').data.datasets.map(s=>s.data)}));
  if(view==='zero')assert(series.delivery.flat().every(x=>x===0));
  assert.deepEqual(series.requests[0],[1,2,3,4]);assert.deepEqual(series.requests[1],[0,0,0,2]);
  assert.deepEqual(errors,[],JSON.stringify({view,width,errors}));cases++;
 }
 await load(1440);
 const catherine=page.locator('.mg-map path[aria-label^="St. Catherine:"]');
 await catherine.focus();await page.locator('.mg-parish-tooltip').waitFor();assert.match(await page.locator('.mg-parish-tooltip').innerText(),/5 total requests/);
 const initial=await page.locator('.leaflet-map-pane').getAttribute('style');
 await page.locator('.leaflet-control-zoom-in').click();await page.waitForTimeout(400);
 await page.locator('[data-map-reset]').click();await page.waitForTimeout(150);
 const mapBox=await page.locator('.mg-map').boundingBox();await page.mouse.move(mapBox.x+mapBox.width/2,mapBox.y+mapBox.height/2);await page.mouse.down();await page.mouse.move(mapBox.x+mapBox.width/2+70,mapBox.y+mapBox.height/2+30,{steps:6});await page.mouse.up();
 assert.notEqual(await page.locator('.leaflet-map-pane').getAttribute('style'),initial);
 await page.locator('[data-map-reset]').click();
 await catherine.press('Enter');await page.waitForURL('**/Management/Requests?parish=St.+Catherine');
 await load(1440);await page.selectOption('#management-parish','Unknown / not provided');await page.locator('.mg-parish-filter button[type=submit]').click();await page.waitForURL('**/Management/Requests?parish=Unknown*');
 failure='geography';await load(820);assert.equal(await page.locator('.mg-map path').count(),0);assert(await page.locator('[data-map-retry]').isVisible());assert.match(await page.locator('[data-map-message]').innerText(),/could not be loaded/);
 failure='';await page.locator('[data-map-retry]').click();await page.waitForFunction(()=>document.querySelectorAll('.mg-map path[role=link]').length===14);
 failure='tiles';await load(820);assert.equal(await page.locator('.mg-map path[role=link]').count(),14);await page.waitForFunction(()=>document.querySelector('[data-map-message]').textContent.includes('Street tiles are unavailable'));
 failure='';await load(1440);for(const canvas of await page.locator('.mg-chart canvas').all()){await canvas.scrollIntoViewIfNeeded();await page.waitForFunction(id=>!!window.Chart?.getChart(document.getElementById(id)),await canvas.getAttribute('id'));}await page.evaluate(()=>window.scrollTo(0,0));await page.screenshot({path:path.join(output,'desktop.png'),fullPage:true});
 await page.setViewportSize({width:390,height:844});await page.screenshot({path:path.join(output,'mobile.png'),fullPage:true});
 console.log(`${cases} layout/data/console scenarios passed, plus map pan/zoom/reset, keyboard navigation, parish/unknown filters, geography retry and tile-failure checks. Automated interactions used test tiles only.`);
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
