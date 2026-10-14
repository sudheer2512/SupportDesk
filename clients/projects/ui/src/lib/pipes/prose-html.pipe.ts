import { Pipe, PipeTransform, SecurityContext, inject } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';

/** The only embeds rich text may carry (same allow-list as the server-side sanitiser). */
const EMBED_SRC = /^https:\/\/(www\.)?(youtube(-nocookie)?\.com\/embed\/[\w-]+|player\.vimeo\.com\/video\/\d+)(\?[\w=&%.-]*)?$/i;

/**
 * Rich text from the API (already sanitised server-side) for `[innerHTML]`, keeping YouTube/Vimeo embeds.
 * Angular's own sanitiser always strips `<iframe>`, so videos authored in the editor never showed in read views. This
 * pipe takes the embeds out, lets Angular sanitise everything else exactly as a plain `[innerHTML]` binding would, then
 * puts back iframes rebuilt from the validated https embed URL (sandboxed, lazy, strict referrer). Nothing else is
 * trusted. `<div class="sd-prose" [innerHTML]="article.bodyHtml | sdProseHtml"></div>`
 */
@Pipe({ name: 'sdProseHtml' })
export class SdProseHtmlPipe implements PipeTransform {
  private readonly sanitizer = inject(DomSanitizer);

  transform(html: string | null | undefined): SafeHtml | string {
    if (!html) return '';
    if (!/<iframe\b/i.test(html) || typeof DOMParser === 'undefined') return this.sanitizer.sanitize(SecurityContext.HTML, html) ?? '';

    const doc = new DOMParser().parseFromString(`<body>${html}</body>`, 'text/html');
    const nonce = Math.random().toString(36).slice(2, 10);
    const embeds: string[] = [];
    for (const frame of Array.from(doc.body.querySelectorAll('iframe'))) {
      const src = (frame.getAttribute('src') ?? '').trim();
      if (EMBED_SRC.test(src)) {
        // A plain-text marker survives Angular's sanitiser untouched; the nonce stops content from forging one.
        const marker = doc.createElement('p');
        marker.textContent = `sd-embed-${nonce}-${embeds.length}`;
        frame.replaceWith(marker);
        embeds.push(src);
      } else {
        frame.remove();
      }
    }
    let clean = this.sanitizer.sanitize(SecurityContext.HTML, doc.body.innerHTML) ?? '';
    embeds.forEach((src, i) => (clean = clean.replace(`<p>sd-embed-${nonce}-${i}</p>`, embedHtml(src))));
    return this.sanitizer.bypassSecurityTrustHtml(clean);
  }
}

function embedHtml(src: string): string {
  // EMBED_SRC admits no quotes, angle brackets or spaces; "&" is the only character that needs escaping.
  const url = src.replace(/&/g, '&amp;');
  return (
    `<iframe src="${url}" title="Embedded video" width="560" height="315" loading="lazy" allowfullscreen ` +
    `referrerpolicy="strict-origin-when-cross-origin" sandbox="allow-scripts allow-same-origin allow-presentation allow-popups" ` +
    `allow="encrypted-media; picture-in-picture; fullscreen"></iframe>`
  );
}
