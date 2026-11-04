// Invitations use a fragment so the bearer secret is never sent in URLs or Referer headers.
(()=>{const input=document.querySelector('[data-identity-recovery] input[name="Token"]');if(!input)return;const token=new URLSearchParams(location.hash.slice(1)).get('token');history.replaceState(null,'',location.pathname);if(token&&token.length<=100)input.value=token;})();
