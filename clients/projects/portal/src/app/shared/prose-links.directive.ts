import { Directive, inject } from '@angular/core';
import { Router } from '@angular/router';

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
    if (url.origin !== location.origin || !url.pathname.startsWith('/portal/')) return;
    event.preventDefault();
    void this.router.navigateByUrl(url.pathname.slice('/portal'.length) + url.search + url.hash);
  }
}
