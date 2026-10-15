import { Directive, inject } from '@angular/core';
import { Router } from '@angular/router';
import { appUrl } from '@sd/core';

/**
 * Keeps in-portal links inside server-rendered HTML (articles, posts, flow steps) in the SPA: a plain
 * left-click on `<a href="/portal/...">` navigates with the router instead of reloading the page.
 * `<div class="sd-prose" [innerHTML]="html" portalProseLinks></div>`
 */
@Directive({
  selector: '[portalProseLinks]',
  host: { '(click)': 'onClick($event)' },
})
export class ProseLinksDirective {
  private readonly router = inject(Router);

  protected onClick(event: MouseEvent): void {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    const anchor = (event.target as Element | null)?.closest?.('a');
    if (!anchor || (anchor.target && anchor.target !== '_self') || anchor.hasAttribute('download')) return;
    let url: URL;
    try {
      url = new URL(anchor.href, location.href);
    } catch {
      return;
    }
    // Portal links ("/portal/…", or "{virtual directory}/portal/…" under IIS) navigate inside the SPA.
    const portalBase = appUrl('portal/');
    if (url.origin !== location.origin || !url.pathname.startsWith(portalBase)) return;
    event.preventDefault();
    void this.router.navigateByUrl(url.pathname.slice(portalBase.length - 1) + url.search + url.hash);
  }
}
