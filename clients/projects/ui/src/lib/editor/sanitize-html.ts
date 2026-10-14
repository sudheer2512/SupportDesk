/**
 * Client-side clean-up of editor HTML before it leaves the browser ("sanitised-ish": the server sanitises
 * again). Drops script-capable elements, inline event handlers, javascript:/vbscript:/data: URLs and any
 * iframe that is not a YouTube embed.
 */
const BLOCKED_TAGS = new Set(['SCRIPT', 'STYLE', 'OBJECT', 'EMBED', 'FORM', 'INPUT', 'BUTTON', 'TEXTAREA', 'SELECT', 'LINK', 'META', 'BASE']);
const URL_ATTRS = new Set(['href', 'src', 'xlink:href', 'action', 'formaction']);
const YOUTUBE_HOSTS = /^(www\.)?(youtube\.com|youtube-nocookie\.com|youtu\.be)$/i;

export function sanitizeEditorHtml(html: string): string {
  if (!html || typeof DOMParser === 'undefined') return html ?? '';
  const doc = new DOMParser().parseFromString(`<body>${html}</body>`, 'text/html');
  const walker = doc.createTreeWalker(doc.body, NodeFilter.SHOW_ELEMENT);
  const remove: Element[] = [];
  for (let node = walker.nextNode() as Element | null; node; node = walker.nextNode() as Element | null) {
    if (BLOCKED_TAGS.has(node.tagName)) {
      remove.push(node);
      continue;
    }
    if (node.tagName === 'IFRAME' && !isYoutube(node.getAttribute('src'))) {
      remove.push(node);
      continue;
    }
    for (const attr of Array.from(node.attributes)) {
      const name = attr.name.toLowerCase();
      if (name.startsWith('on') || name === 'srcdoc') node.removeAttribute(attr.name);
      else if (URL_ATTRS.has(name) && !isSafeUrl(attr.value)) node.removeAttribute(attr.name);
    }
    if (node.tagName === 'A') {
      node.setAttribute('rel', 'noopener noreferrer nofollow');
      if (!node.getAttribute('target')) node.setAttribute('target', '_blank');
    }
  }
  for (const el of remove) el.remove();
  return doc.body.innerHTML;
}

function isSafeUrl(value: string): boolean {
  const v = value.trim().replace(/[\u0000-\u001F\s]+/g, '').toLowerCase();
  return !(v.startsWith('javascript:') || v.startsWith('vbscript:') || v.startsWith('data:'));
}

function isYoutube(src: string | null): boolean {
  if (!src) return false;
  try {
    const url = new URL(src, 'https://www.youtube.com');
    return url.protocol === 'https:' && YOUTUBE_HOSTS.test(url.hostname);
  } catch {
    return false;
  }
}

/** True when the HTML has no visible text and no embedded media. */
export function isHtmlEmpty(html: string | null | undefined): boolean {
  if (!html) return true;
  if (/<(img|iframe|table|hr)\b/i.test(html)) return false;
  return html.replace(/<[^>]*>/g, '').replace(/&nbsp;|\s/g, '') === '';
}
